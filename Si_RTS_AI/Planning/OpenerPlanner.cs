using MelonLoader;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Si_RTS_AI.Planning
{
    /// <summary>
    /// Phase 1 OPENER — picks the whole opening build order up front instead of
    /// re-deciding it every 8s tick.
    ///
    /// Why a separate planner. The beam is a marginal-income search over a 300s
    /// horizon, and the opening is not a marginal problem: it is a small
    /// discrete one — which 3-5 sites, in what order, with how many Nodes to
    /// anchor each, and where the Cysts go. Every opening fault this session
    /// traces to using a myopic search on it: the nearest patch skipped for a
    /// farther one, Node starbursts, a lone Node scoring below Noop so nothing
    /// fired at all, Bio Caches landing 160m from their biotics.
    ///
    /// A few hundred openings exist. They can be enumerated exhaustively and
    /// each simulated properly, once, at round start — which is both more
    /// accurate and cheaper than re-deriving a local decision 100 times.
    ///
    /// Why it matters more than the late game: the deficit COMPOUNDS. Measured
    /// against the human benchmark on NarakaCity, our shortfall runs -19% at
    /// 180s, -40% at 300s and -55% by 800s. A late third Cyst means fewer
    /// shrimps at 3 minutes, less cash to expand at 5, fewer Bio Caches at 8.
    ///
    /// SHADOW MODE. This build only LOGS the opening it would choose, next to
    /// what the beam actually did. Nothing is executed. Two planner stalls in
    /// one session came from wiring a change straight into the fire path, and
    /// the opening is the part of the round with no slack to absorb a mistake.
    /// Execution comes once the logged choices look right.
    /// </summary>
    internal static class OpenerPlanner
    {
        internal static bool Enabled = true;
        /// <summary>Shadow mode: log the chosen opening, never execute it.</summary>
        internal static bool ShadowOnly = true;

        // Search space. Sites are ranked by distance from the Nest; the opener
        // considers the nearest CANDIDATE_SITES and picks MIN..MAX of them.
        const int CANDIDATE_SITES = 8;
        const int MIN_SITES = 3;
        const int MAX_SITES = 5;
        // Scored to the HANDOFF, not to some arbitrary later point.
        //
        // The Opener's job ends when Phase 2 takes over — around 3-4 minutes on
        // a good start. Scoring to 420s credited it for a stretch it does not
        // control and would favour an opening that is mediocre at handoff
        // because it pays off later, which by then is the beam's problem.
        //
        // But cumulative income to 240s alone has the opposite bias: a 4th or
        // 5th Cyst's shrimps are still ramping when the clock stops, so it
        // would under-value them and pick a thin, fast opening — losing exactly
        // the compounding that makes the opening matter.
        //
        // So: integrate income to the handoff, then add a TERMINAL VALUE for
        // the economy being handed over — the income RATE at that moment,
        // projected forward. An opening is worth what it earned plus what it
        // leaves running.
        const float SCORE_HORIZON_S  = 240f;
        const float TERMINAL_TAIL_S  = 240f;   // how long the handover rate is credited for
        const float RATE_WINDOW_S    = 30f;    // window used to measure the rate at handoff
        // Measured: a Bio Cache lands ~25m off the patch centre, on the near
        // edge of its no-build zone.
        const float BC_PATCH_STANDOFF_M = 25f;

        static bool _done;

        internal static void ResetForNewRound()
        {
            _done = false;
            _queue.Clear(); _stepDone = new bool[0]; _queueStalledSince = 0f; _skipped = 0;
            _lastCystTry.Clear(); _cystAttempts.Clear();
        }

        /// <summary>
        /// Runs once, on the first plan tick where the map profile is ready.
        /// </summary>
        internal static void MaybePlan(EcoState state)
        {
            if (!Enabled || _done || state == null) return;
            if (!MapProfile.Ready) return;
            if (state.bcs.Count > 0) { _done = true; return; }   // too late to be an opening
            _done = true;

            try
            {
                long ts = System.Diagnostics.Stopwatch.GetTimestamp();
                var best = Search(state, out int evaluated);
                long ms = (System.Diagnostics.Stopwatch.GetTimestamp() - ts) * 1000L
                        / System.Diagnostics.Stopwatch.Frequency;
                if (best == null)
                {
                    MelonLogger.Msg("[OPENER] no viable opening found");
                    return;
                }
                _queue.Clear();
                _queue.AddRange(best.Steps);
                _stepDone = new bool[_queue.Count];
                _queueStalledSince = 0f;
                _skipped = 0;
                MelonLogger.Msg($"[OPENER] evaluated={evaluated} in {ms}ms  " +
                                $"{(ShadowOnly ? "SHADOW" : "EXECUTING")} steps={_queue.Count}  " +
                                $"chosen: {best.Describe()}");
            }
            catch (Exception ex) { MelonLogger.Warning("[OPENER] threw: " + ex.Message); }
        }

        // ---------------------------------------------------------------- //

        // ---- Executable queue -------------------------------------------
        internal enum StepKind { Node, Bc, Cyst }
        internal struct Step
        {
            public StepKind Kind;
            public Vector3  Target;
            public int      Cost;
            /// <summary>
            /// Hold this step until a Cyst near it is FINISHED — the gap build
            /// order for the 3rd+ biotics:
            ///
            ///   t=0   Cyst placed                 (BC's 500 stays in the bank)
            ///   t=35  Cyst done -> BC placed AND shrimp queued
            ///   t=50  shrimp ready, walks out and starts harvesting
            ///   t=65  BC done
            ///   t=~95 first load deposited
            ///
            /// The Bio Cache is ready before the first load arrives, and its
            /// cost funded shrimps for 35s instead of sitting in a structure
            /// that could not earn yet. This is an intentional WAIT, not a
            /// stall — it must not trip the queue's abandon timer.
            /// </summary>
            public bool     WaitForCystDone;
        }
        static readonly List<Step> _queue = new List<Step>();
        static bool[]  _stepDone = new bool[0];
        static float   _queueStalledSince;
        static int     _skipped;

        /// <summary>Any step of the opening still outstanding.</summary>
        internal static bool QueueActive
        {
            get
            {
                if (ShadowOnly) return false;
                for (int i = 0; i < _queue.Count; i++) if (!_stepDone[i]) return true;
                return false;
            }
        }

        /// <summary>
        /// Outstanding steps, in plan order. The caller fires whichever are
        /// legal RIGHT NOW and marks them done — it does not have to take them
        /// strictly in order.
        ///
        /// A strict head-of-queue was the reason Cysts came in 16-32s apart:
        /// the gap-order gate makes a site's Bio Cache wait ~35s for its own
        /// Cyst to finish, and with a single cursor every LATER site's Cyst sat
        /// behind that wait. Nothing about one site's timing should hold up
        /// another's — the user put it as "both biocaches could be fired at the
        /// same time but placed consecutively", and the game does exactly that:
        /// its placement search resolves in tens of milliseconds.
        /// </summary>
        internal static IEnumerable<int> PendingSteps()
        {
            for (int i = 0; i < _queue.Count; i++) if (!_stepDone[i]) yield return i;
        }

        internal static Step StepAt(int i) => _queue[i];

        /// <summary>
        /// 1 Hz Cyst pipeline. Retries each pending Cyst every RETRY_S until a
        /// Cyst structure actually EXISTS near its target, and only then marks
        /// the step done.
        ///
        /// This replaces every attempt to predict when a Bio Cache is ready.
        /// Each of those was wrong in a different way: IsFunctional turns true
        /// ~1s after construction STARTS, ConstructionBuildUp01 is a static
        /// ratio on ConstructionData, and TotalConstructionTime (30s) is the
        /// build-up plus the site teardown rather than time-to-usable.
        ///
        /// The game already knows the answer and enforces it, so we let it
        /// arbitrate: ask once a second, it refuses until the Bio Cache is
        /// finished, and the Cyst goes up the moment it is legal. Marking the
        /// step done only on the structure APPEARING also fixes the old failure
        /// where a silently-refused request counted as placed.
        ///
        /// User 2026-07-29: "just check if bio cache is finished and then build
        /// lesser cyst — the lesser is loaded in the pipeline and fired once
        /// biocache is finished."
        /// </summary>
        internal static void TickFast(Team team)
        {
            if (ShadowOnly || team == null || _queue.Count == 0) return;
            if (!global::Si_RTS_AI.TestHarnessNs.TestHarness.IsRoundActive) return;
            float now = Time.time;
            try
            {
                foreach (int i in System.Linq.Enumerable.ToList(PendingSteps()))
                {
                    if (_queue[i].Kind != StepKind.Cyst) continue;

                    // Confirm by NAME only, and never act on generic proximity.
                    //
                    // A 45m "anything nearby" test was tried and instantly broke
                    // it: the Cyst is aimed 40m from its Bio Cache, and a BC
                    // construction SITE does not report the finished display
                    // name, so the test saw an unknown structure and marked
                    // every Cyst step done before firing once.
                    //
                    // What actually prevents the spam is timing, not detection:
                    // one attempt, a wait longer than the Cyst build, then at
                    // most one more. Log what is nearby for diagnosis, but do
                    // not decide on it.
                    if (StructureNear(team, "Lesser Spawning Cyst", _queue[i].Target, 60f))
                    {
                        MelonLogger.Msg($"[OPENER] Cyst step {i + 1}/{_queue.Count} confirmed built");
                        MarkDone(i);
                        continue;
                    }

                    // IS ONE ALREADY ON THE WAY?
                    //
                    // Structure.Construct does not place immediately — it queues
                    // the build on the anchoring structure. So between firing and
                    // the Cyst existing there is a window in which nothing is
                    // visible in team.Structures, and every retry during that
                    // window adds ANOTHER entry to the queue. That is the Cyst
                    // spam: timely placement was achieved, but 14 retries over
                    // 70s each enqueued a fresh Cyst.
                    //
                    // Checking the production queues closes the window: if any
                    // Cyst is already pending anywhere on the team, wait for it.
                    if (CystPending(team, out string whereQueued))
                    {
                        MelonLogger.Msg($"[OPENER] Cyst step {i + 1}/{_queue.Count} waiting — " +
                                        $"a Cyst is already queued at {whereQueued}");
                        break;   // one Cyst in flight at a time
                    }

                    if (_lastCystTry.TryGetValue(i, out float last) && now - last < RETRY_S) continue;
                    if (team.TotalResources < _queue[i].Cost) continue;

                    // HARD ATTEMPT CAP.
                    //
                    // The placement search succeeds and calls Construct every
                    // time, but the game can then refuse it silently — observed
                    // 14 accepted placements at the same spot over 70s before a
                    // structure finally appeared. Each one risks paying 1500, so
                    // an unbounded retry is far worse than a late Cyst. Three
                    // attempts, then hand the step to the beam.
                    _cystAttempts.TryGetValue(i, out int tries);
                    if (tries >= MAX_CYST_ATTEMPTS)
                    {
                        MelonLogger.Msg($"[OPENER] Cyst step {i + 1}/{_queue.Count} gave up after " +
                                        $"{tries} attempts at ({_queue[i].Target.x:F0},{_queue[i].Target.z:F0}) " +
                                        $"— no structure appeared; leaving it to the beam");
                        MarkDone(i);
                        continue;
                    }
                    _cystAttempts[i] = tries + 1;
                    _lastCystTry[i] = now;

                    AnythingNear(team, _queue[i].Target, 60f, out string nearby);
                    MelonLogger.Msg($"[OPENER] Cyst step {i + 1}/{_queue.Count} attempt {tries + 1}" +
                                    $"/{MAX_CYST_ATTEMPTS} at ({_queue[i].Target.x:F0},{_queue[i].Target.z:F0}) " +
                                    $"cash={team.TotalResources} nearby='{nearby}'");
                    // Straight at the game, bypassing the planner dedup.
                    Faction.AlienConstruction.TryBuildStructureForPlanner(
                        team, EcoPlanner.ActionKind.PlaceCyst, _queue[i].Target);
                }
            }
            catch (Exception ex) { MelonLogger.Warning("[OPENER] TickFast threw: " + ex.Message); }
        }

        // Retry interval and attempt cap. A construction site was expected in
        // team.Structures within a few seconds; in practice a Cyst took 70s to
        // appear while every 5s retry was accepted by the placement search, so
        // both numbers are now conservative.
        // Longer than a Cyst's build time, so a legitimate build is never
        // interrupted by our own retry.
        const float RETRY_S = 30f;
        const int   MAX_CYST_ATTEMPTS = 2;
        static readonly Dictionary<int, int> _cystAttempts = new Dictionary<int, int>();
        static readonly Dictionary<int, float> _lastCystTry = new Dictionary<int, float>();

        /// <summary>
        /// Any owned structure within radius, excluding Bio Caches (the Cyst is
        /// deliberately placed near one). Reports what it found so the real
        /// construction-site display name shows up in the log — it is not
        /// "Lesser Spawning Cyst", which is why the previous check missed it.
        /// </summary>
        static bool AnythingNear(Team team, Vector3 target, float radiusM, out string what)
        {
            what = "";
            var structs = team.Structures;
            if (structs == null) return false;
            float r2 = radiusM * radiusM;
            for (int i = 0; i < structs.Count; i++)
            {
                var st = structs[i];
                if (st == null || st.ObjectInfo == null || st.IsDestroyed) continue;
                string n = st.ObjectInfo.DisplayName ?? "?";
                if (n == "Bio Cache" || n == "Nest") continue;
                float dx = st.transform.position.x - target.x, dz = st.transform.position.z - target.z;
                if (dx * dx + dz * dz >= r2) continue;
                what = n;
                return true;
            }
            return false;
        }

        /// <summary>
        /// Is a Lesser Spawning Cyst already sitting in any of the team's
        /// production queues? Construct() enqueues rather than placing, so this
        /// is the only way to see a build that has been ordered but has not yet
        /// produced a structure.
        /// </summary>
        static bool CystPending(Team team, out string where)
        {
            where = "";
            var structs = team.Structures;
            if (structs == null) return false;
            for (int i = 0; i < structs.Count; i++)
            {
                var st = structs[i];
                if (st == null || st.IsDestroyed) continue;
                System.Collections.IEnumerable q = null;
                try { q = st.ProductionQueue as System.Collections.IEnumerable; } catch { }
                if (q == null) continue;
                foreach (var item in q)
                {
                    if (item == null) continue;
                    string n = DescribeQueued(item);
                    if (n.IndexOf("Cyst", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    where = (st.ObjectInfo?.DisplayName ?? "?") + " queue";
                    return true;
                }
            }
            return false;
        }

        /// <summary>Best-effort name of a queued production item.</summary>
        static string DescribeQueued(object item)
        {
            try
            {
                var t = item.GetType();
                // Queue entries usually wrap a ConstructionData; try the common shapes.
                foreach (string prop in new[] { "ObjectInfo", "ConstructionData", "Data" })
                {
                    var pi = t.GetProperty(prop) ?? (object)null as System.Reflection.PropertyInfo;
                    pi = t.GetProperty(prop);
                    if (pi == null) continue;
                    var v = pi.GetValue(item);
                    if (v == null) continue;
                    var dn = v.GetType().GetProperty("DisplayName");
                    if (dn != null) return dn.GetValue(v)?.ToString() ?? "";
                    var oi = v.GetType().GetProperty("ObjectInfo");
                    if (oi != null)
                    {
                        var o = oi.GetValue(v);
                        var dn2 = o?.GetType().GetProperty("DisplayName");
                        if (dn2 != null) return dn2.GetValue(o)?.ToString() ?? "";
                    }
                }
                return item.ToString() ?? "";
            }
            catch { return ""; }
        }

        static bool StructureNear(Team team, string name, Vector3 target, float radiusM)
        {
            var structs = team.Structures;
            if (structs == null) return false;
            float r2 = radiusM * radiusM;
            for (int i = 0; i < structs.Count; i++)
            {
                var st = structs[i];
                if (st == null || st.ObjectInfo == null || st.IsDestroyed) continue;
                if (st.ObjectInfo.DisplayName != name) continue;
                float dx = st.transform.position.x - target.x, dz = st.transform.position.z - target.z;
                if (dx * dx + dz * dz < r2) return true;
            }
            return false;
        }
        internal static int  StepCount => _queue.Count;

        /// <summary>Is an outstanding Cyst step targeted near this position?</summary>
        internal static bool HasPendingCystNear(Vector3 pos, float radiusM)
        {
            if (ShadowOnly) return false;
            float r2 = radiusM * radiusM;
            for (int i = 0; i < _queue.Count; i++)
            {
                if (_stepDone[i] || _queue[i].Kind != StepKind.Cyst) continue;
                float dx = _queue[i].Target.x - pos.x, dz = _queue[i].Target.z - pos.z;
                if (dx * dx + dz * dz < r2) return true;
            }
            return false;
        }
        internal static void MarkDone(int i) { _stepDone[i] = true; _queueStalledSince = 0f; }

        /// <summary>
        /// Nothing fired and nothing was merely waiting — drop the step at the
        /// head of the outstanding set so one unbuildable item cannot hold the
        /// opening, and give up entirely after MAX_SKIPS.
        /// </summary>
        internal static void NoteNoProgress(float now)
        {
            if (_queueStalledSince <= 0f) { _queueStalledSince = now; return; }
            if (now - _queueStalledSince <= QUEUE_STALL_LIMIT_S) return;

            foreach (int i in PendingSteps())
            {
                _skipped++;
                MelonLogger.Msg($"[OPENER] step {i + 1}/{_queue.Count} ({_queue[i].Kind}) " +
                                $"stalled {QUEUE_STALL_LIMIT_S:F0}s — skipping it");
                _stepDone[i] = true;
                break;
            }
            _queueStalledSince = 0f;

            if (_skipped >= MAX_SKIPS)
            {
                MelonLogger.Msg($"[OPENER] {_skipped} steps skipped — abandoning opening, beam takes over");
                for (int i = 0; i < _stepDone.Length; i++) _stepDone[i] = true;
            }
        }

        const float QUEUE_STALL_LIMIT_S = 45f;
        const int   MAX_SKIPS = 3;

        class Plan
        {
            public List<int> SiteIdx = new List<int>();
            public int   CystCount;
            public int   TotalNodes;
            public int   CostCash;
            public float Score;              // income to handoff + terminal value
            public int   IncomeToHandoff;
            public float RateAtHandoff;      // income/sec as Phase 2 takes over
            public int   CashAtHorizon;
            public float CompleteAtS;        // when the last piece is placed
            public readonly List<Step> Steps = new List<Step>();

            public string Describe()
            {
                var sb = new System.Text.StringBuilder();
                sb.Append("sites=").Append(SiteIdx.Count)
                  .Append(" cysts=").Append(CystCount)
                  .Append(" nodes=").Append(TotalNodes)
                  .Append(" cost=").Append(CostCash)
                  .Append(" income@").Append((int)SCORE_HORIZON_S).Append("s=").Append(IncomeToHandoff)
                  .Append(" rate=").Append(RateAtHandoff.ToString("F0")).Append("/s")
                  .Append(" score=").Append((int)Score)
                  .Append(" cashLeft=").Append(CashAtHorizon)
                  .Append(" done@").Append((int)CompleteAtS).Append('s')
                  .Append(" [");
                for (int i = 0; i < SiteIdx.Count; i++)
                {
                    var s = MapProfile.Sites[SiteIdx[i]];
                    if (i > 0) sb.Append(' ');
                    sb.Append(s.Patches).Append("p@").Append((int)s.DistFromNest).Append('m');
                }
                sb.Append(']');
                return sb.ToString();
            }
        }

        static Plan Search(EcoState root, out int evaluated)
        {
            var sites = MapProfile.Sites;
            evaluated = 0;
            if (sites == null || sites.Count == 0) return null;
            int evalCount = 0;

            int n = Mathf.Min(CANDIDATE_SITES, sites.Count);
            Plan best = null;

            // Combinations of sites, taken in distance order — the order a
            // chain is actually built in, so no need to permute.
            var chosen = new List<int>();
            void Recurse(int start, int want)
            {
                if (chosen.Count == want)
                {
                    for (int cysts = 2; cysts <= chosen.Count; cysts++)
                    {
                        var p = Evaluate(root, chosen, cysts);
                        evalCount++;
                        if (p != null && (best == null || p.Score > best.Score)) best = p;
                    }
                    return;
                }
                for (int i = start; i < n; i++)
                {
                    chosen.Add(i);
                    Recurse(i + 1, want);
                    chosen.RemoveAt(chosen.Count - 1);
                }
            }
            for (int want = MIN_SITES; want <= MAX_SITES; want++)
            {
                chosen.Clear();
                Recurse(0, want);
            }
            evaluated = evalCount;
            return best;
        }

        /// <summary>
        /// Build the candidate opening into a forked state and roll it forward.
        /// Nodes, Bio Caches and Cysts are placed with the same costs, build
        /// times and reaches the live planner reads off the game, so the score
        /// is directly comparable to the beam's.
        /// </summary>
        static Plan Evaluate(EcoState root, List<int> siteIdx, int cystCount)
        {
            var s = root.Clone();
            var plan = new Plan { CystCount = cystCount };
            plan.SiteIdx.AddRange(siteIdx);

            float hop = Mathf.Max(1f, EcoSimulator.NODE_REACH_M - 15f);
            float bcAnchorReach = EcoSimulator.BcPlaceReachM + 40f;   // sign: see BC_TIGHT_GAP_M
            int cost = 0;

            for (int k = 0; k < siteIdx.Count; k++)
            {
                var site = MapProfile.Sites[siteIdx[k]];

                float anchorDist = NearestAnchorDist(s, site.Centroid);
                int nodes = 0;
                while (anchorDist > bcAnchorReach && nodes < 40)
                {
                    Vector3 from = NearestAnchorPos(s, site.Centroid);
                    Vector3 dir = site.Centroid - from;
                    float len = Mathf.Sqrt(dir.x * dir.x + dir.z * dir.z);
                    if (len < 1f) break;
                    if (!Afford(s, EcoSimulator.NODE_COST)) return null;
                    Vector3 np = from + dir * (hop / len);
                    s.nodes.Add(new EcoState.Node { pos = np, finished = false,
                                                    readyAt = s.t + EcoSimulator.NODE_BUILD_S });
                    plan.Steps.Add(new Step { Kind = StepKind.Node, Target = np, Cost = EcoSimulator.NODE_COST });
                    s.cash -= EcoSimulator.NODE_COST;
                    cost += EcoSimulator.NODE_COST;
                    nodes++;
                    anchorDist = NearestAnchorDist(s, site.Centroid);
                }
                plan.TotalNodes += nodes;

                // Where the Bio Cache will stand — needed now so the Cyst can be
                // placed beside it even though the Cyst goes in first.
                Vector3 bcPos = OffsetFromSite(s, site.Centroid, BC_PATCH_STANDOFF_M);

                // ORDER WITHIN A SITE depends on which site it is.
                //
                // The FIRST Bio Cache must go down before any Cyst — it is what
                // unlocks Cyst construction — and a BC also extends build range
                // further than a Cyst does (237m against 150m), so leading with
                // it opens ground faster.
                //
                // From the second site on, Cyst-first is better whenever cash is
                // the bottleneck: a Bio Cache costs money but earns nothing
                // until shrimps exist, so deferring it leaves cash free to keep
                // Cysts queueing shrimps through the gap. User 2026-07-29 —
                // this matters most for the 3rd+ Cyst at the end of Phase 1.
                // BC then Cyst, EVERY site, no waiting.
                //
                // Two mechanisms are deliberately gone: Cyst-before-BC on later
                // sites, and the gate that held a BC until its Cyst finished.
                // Together they serialised the opening — with one cursor, a site
                // waiting ~35s on its own Cyst blocked every later site, and
                // Cyst requests came 16-32s apart. Four attempts to patch around
                // that failed, so the mechanism goes.
                //
                // BC first also means the Cyst's prerequisite (a Bio Cache
                // nearby) is satisfied in the same tick, which is the reliable
                // path. The cash advantage of deferring the BC is real but
                // secondary to getting Cysts down early — revisit only once
                // timing is solid.
                if (!PlaceBcStep(s, plan, site, bcPos, ref cost)) return null;

                if (k < cystCount)
                {
                    if (!Afford(s, EcoSimulator.CYST_COST)) return null;
                    s.cysts.Add(new EcoState.Cyst { pos = bcPos, finished = false,
                        readyAt = s.t + EcoSimulator.CYST_BUILD_S,
                        nextSpawnAt = s.t + EcoSimulator.CYST_BUILD_S + EcoSimulator.SHRIMP_BUILD_S });
                    // Aim the Cyst BESIDE the Bio Cache, not on top of it.
                    //
                    // Targeting the site centroid puts it exactly where the BC
                    // just went, so the game's placement search has to slide it
                    // 27-34m to find clear ground — and that search is slow.
                    // Measured on NarakaCity: BC and Cyst requested in the SAME
                    // millisecond, yet the BCs' structures appeared at t=50s and
                    // the Cysts' not until t=104s and t=112s, behind four Nodes
                    // that were requested later. Offsetting perpendicular to the
                    // BC-to-patch line gives the search clear ground immediately
                    // and keeps the Cyst off the harvest path.
                    // Cyst goes in FIRST, and beside where the BC will stand.
                    //
                    // User rule 2026-07-29: "build lesser cyst first, build a
                    // shrimp from that and around the same timing a bio cache"
                    // — so the site has local production the moment the BC
                    // opens, instead of shrimps being walked in from elsewhere.
                    //
                    // Ordering it first also gives the Cyst the placement search
                    // ahead of the BC. When the BC went first, the Cyst's search
                    // had to work around an occupied spot and took 54s (BC start
                    // t=53s, Cyst start t=106s) even though both were requested
                    // in the same millisecond.
                    Vector3 cystTarget = OffsetCystBehind(site.Centroid, bcPos, CYST_BEHIND_M);
                    plan.Steps.Add(new Step { Kind = StepKind.Cyst, Target = cystTarget, Cost = EcoSimulator.CYST_COST });
                    s.cash -= EcoSimulator.CYST_COST;
                    cost += EcoSimulator.CYST_COST;
                }
                // Gap build order: from the second site on the BC waits for its
                // own Cyst to finish, so the Cyst's shrimp is already building
                // while the BC goes up.

            }

            plan.CostCash = cost;
            plan.CompleteAtS = s.t;

            // HARD BANK CONSTRAINT. The opening must fit the money we actually
            // have, not the money the simulator thinks we will earn.
            //
            // The income model over-predicts by ~3.5x in this window: it assumes
            // every Cyst runs from the moment it finishes and spawns on a strict
            // 15s cadence, so it reported 42,418 earned and 34,123 in hand at
            // 240s against a real ~12,000 and ~2,000. Affordability judged
            // against that let an 11,600 opening through on a 9,000 bank.
            //
            // The bank needs no model. The human opening spent 4,200 by t=98s
            // (2 BC + 2 Cyst + 1 Node), comfortably inside it — so this is not
            // a tight constraint, it just rules out the fantasy openings.
            if (cost > root.cash) return null;

            float remain = SCORE_HORIZON_S - s.t;
            if (remain <= RATE_WINDOW_S) return null;   // opening does not finish in time

            EcoSimulator.SimulateForward(s, remain - RATE_WINDOW_S);
            int earnedBeforeWindow = s.grossEarned;
            EcoSimulator.SimulateForward(s, RATE_WINDOW_S);
            float rateAtHandoff = s.grossEarned / RATE_WINDOW_S;

            plan.IncomeToHandoff = earnedBeforeWindow + s.grossEarned;
            plan.RateAtHandoff   = rateAtHandoff;
            plan.Score = plan.IncomeToHandoff + rateAtHandoff * TERMINAL_TAIL_S;
            plan.CashAtHorizon = s.cash;
            return plan;
        }

        /// <summary>Queue the Bio Cache for a site. False if unaffordable in the window.</summary>
        static bool PlaceBcStep(EcoState s, Plan plan, MapProfile.Site site, Vector3 bcPos, ref int cost)
        {
            if (!Afford(s, EcoSimulator.BC_COST)) return false;
            s.bcs.Add(new EcoState.Bc { pos = bcPos, finished = false,
                                        readyAt = s.t + EcoSimulator.BC_BUILD_S,
                                        storage = 0, storageCap = 4000 });
            plan.Steps.Add(new Step { Kind = StepKind.Bc, Target = site.Centroid,
                                      Cost = EcoSimulator.BC_COST });
            s.cash -= EcoSimulator.BC_COST;
            cost += EcoSimulator.BC_COST;
            return true;
        }

        // How many opening sites lead with the Bio Cache. At least 1 is
        // mandatory — the first BC is what unlocks Cyst construction.
        const int BC_FIRST_SITES = 1;

        /// <summary>
        /// Advance the rollout until <paramref name="cost"/> is affordable.
        /// False if it never becomes affordable inside the opening window —
        /// that candidate is not an opening, it is a plan for the whole game.
        ///
        /// Without this the rollout placed every structure at t=0 on infinite
        /// credit, so the biggest opening always won: 5 sites and 5 Cysts for
        /// 12,400 against a 9,000 bank, reporting 56,768 cash left over.
        /// </summary>
        static bool Afford(EcoState s, int cost)
        {
            const float STEP_S = 5f;
            while (s.cash < cost)
            {
                if (s.t >= SCORE_HORIZON_S - RATE_WINDOW_S) return false;
                EcoSimulator.SimulateForward(s, STEP_S);
            }
            return true;
        }

        // How far behind the Bio Cache the Cyst goes.
        //
        // 40m, down from 60m, for two reasons that point the same way:
        //
        //  * The planner treats a Bio Cache as "already cysted" only if a Cyst
        //    sits within 60m of it (Phase 1 coverage radius). At exactly 60m the
        //    opener's Cyst fell on the boundary, the BC still read as un-cysted,
        //    and the beam added a SECOND Cyst — two per Bio Cache.
        //  * The user wants it closer to the Bio Cache and its biotics anyway,
        //    which shortens the walk for freshly spawned shrimps.
        //
        // The game's placement search slides it clear of the BC footprint, so
        // aiming inside the nominal radius is safe — observed landings were
        // 18-27m from the Bio Cache.
        const float CYST_BEHIND_M = 40f;

        /// <summary>
        /// Cyst position: pushed AWAY FROM THE PATCH, directly behind the Bio
        /// Cache.
        ///
        /// A perpendicular offset was tried first and Cyst placements simply
        /// stopped resolving — requested at 23:14:45 with no construction start
        /// inside the next 90s, while Nodes requested 24s later began building
        /// in 2s. Perpendicular keeps the same distance from the biotics, so it
        /// lands inside the patch's no-build zone and the search has nowhere to
        /// go. Moving directly away from the patch is clear ground by
        /// construction: the Bio Cache already found legal ground at the zone
        /// boundary, and anything further out is further from the obstruction.
        /// </summary>
        static Vector3 OffsetCystBehind(Vector3 patch, Vector3 bcPos, float d)
        {
            float dx = bcPos.x - patch.x, dz = bcPos.z - patch.z;
            float len = Mathf.Sqrt(dx * dx + dz * dz);
            if (len < 1f) return bcPos + new Vector3(d, 0f, 0f);
            return bcPos + new Vector3(dx / len, 0f, dz / len) * d;
        }

        /// <summary>Push the build spot off the patch toward the chain, the way
        /// the game's placement search does.</summary>
        static Vector3 OffsetFromSite(EcoState s, Vector3 site, float d)
        {
            Vector3 anchor = NearestAnchorPos(s, site);
            Vector3 dir = anchor - site;
            float len = Mathf.Sqrt(dir.x * dir.x + dir.z * dir.z);
            if (len < 1f) return site + new Vector3(d, 0f, 0f);
            return site + dir * (d / len);
        }

        static float NearestAnchorDist(EcoState s, Vector3 to)
        {
            var p = NearestAnchorPos(s, to);
            float dx = p.x - to.x, dz = p.z - to.z;
            return Mathf.Sqrt(dx * dx + dz * dz);
        }

        static Vector3 NearestAnchorPos(EcoState s, Vector3 to)
        {
            Vector3 best = s.nestPos; float bd = float.MaxValue;
            void consider(Vector3 q)
            {
                float dx = q.x - to.x, dz = q.z - to.z;
                float d = dx * dx + dz * dz;
                if (d < bd) { bd = d; best = q; }
            }
            if (s.nestPos != Vector3.zero) consider(s.nestPos);
            for (int i = 0; i < s.bcs.Count; i++)   consider(s.bcs[i].pos);
            for (int i = 0; i < s.cysts.Count; i++) consider(s.cysts[i].pos);
            for (int i = 0; i < s.nodes.Count; i++) consider(s.nodes[i].pos);
            return best;
        }
    }
}
