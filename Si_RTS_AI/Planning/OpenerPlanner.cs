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

        /// <summary>
        /// May one site take a SECOND Cyst instead of the opening reaching for
        /// another site? Live from rtsai.json, default off — see the comment at
        /// the cyst-count loop in Search for why. Set "openerDoubleCyst": true
        /// to score doubled openings again.
        /// </summary>
        internal static bool DoubleCystEnabled => RtsaiConfig.Bool("openerDoubleCyst", false);

        // Search space. Sites are ranked by distance from the Nest; the opener
        // considers the nearest CANDIDATE_SITES and picks MIN..MAX of them.
        const int CANDIDATE_SITES = 8;
        const int MIN_SITES = 3;
        const int MAX_SITES = 5;
        /// <summary>How many candidate openings to log alongside the winner.</summary>
        const int RUNNERS_UP = 6;

        /// <summary>
        /// Score cost of one chain Node, derived rather than guessed.
        ///
        /// From the same round's candidates: the 4-cyst opening earned 49,368 by
        /// the handoff against 46,473 for an otherwise similar 3-cyst one, so
        /// 1,500 cash of Cyst bought ~2,895 score — about 1.93 score per cash.
        /// A Node costs 200, hence ~386. Rounded to 400.
        ///
        /// Nodes earn nothing themselves; they only buy reach. Charging for them
        /// is what stops the planner treating an 8-node chain as free.
        /// </summary>
        const float NODE_SCORE_COST = 400f;

        /// <summary>
        /// Two sites within this bearing of each other (degrees, measured from
        /// the Nest) count as facing the same direction. A quadrant — the
        /// natural unit of "the same side of the base".
        ///
        /// 40 degrees was too narrow to discriminate. Measured NarakaCity: the
        /// dominant pair under a 40 degree window was the 216m and 682m sites
        /// at 11.7 degrees apart, and those two appear in EVERY competitive
        /// opening — so the term scored nearly the same for all of them and the
        /// only differentiating pair contributed 0.165. At 90 degrees the
        /// northern set reads 2.00 against 1.18 for the set that includes the
        /// southern tap, which is the distinction that was wanted.
        /// </summary>
        const float REDUNDANT_ANGLE_DEG = 90f;

        /// <summary>
        /// Score charged for a fully redundant pair of sites — two taps on the
        /// same bearing from the Nest — tapering to zero at REDUNDANT_ANGLE_DEG.
        ///
        /// This replaces a mean-pairwise-DISTANCE bonus, which was actively
        /// harmful: deleting a nearby site RAISES the mean distance, so the term
        /// paid 9,880 to drop the closest 213m biotics and take two distant ones
        /// with no Cyst at all. Income fell from 49,368 to 46,417 and the plan
        /// still won. Any spread term expressed as a REWARD has that failure
        /// mode. Expressed as a PENALTY it cannot: adding a site can never
        /// improve this term, only its income can.
        ///
        /// Sized against the measured value of a Cyst — 1,500 cash of Cyst
        /// bought ~2,895 score — so two sites sharing a direction are worth
        /// appreciably less than two that do not, but never less than having
        /// one fewer site.
        /// </summary>
        const float REDUNDANT_DIR_PENALTY = 2000f;

        /// <summary>Score per unit of biotics the chosen sites still hold. See
        /// the derivation where it is applied.</summary>
        const float RESERVE_VALUE_PER_BIOTIC = 0.04f;
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
            /// The site this step is chaining toward. Node targets are re-derived
            /// from live structure positions at fire time, and that needs to know
            /// where the chain is headed — Target alone is a plan-time guess that
            /// the placement search invalidates.
            /// </summary>
            public Vector3  Goal;
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
        static float   _lastWaitLogAt;
        static float   _waitingForCashAt = -999f;
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
        /// Cash the opening still needs for producers it has planned but not
        /// placed.
        ///
        /// A Lesser Cyst is a shrimp FACTORY, so spending its 1,500 on twelve
        /// shrimps instead is a trade of everything that factory would have
        /// produced for one batch now. That trade was being made silently:
        /// NarakaCity 2026-08-05, step 13 of 13 — the fourth Cyst — waited
        /// fifty seconds with cash oscillating between 630 and 1,940 because
        /// every income tick went into a shrimp, then gave up and skipped
        /// itself. DrMuck saw the same thing from the other side: "I don't see
        /// a 4th cyst being placed early as well... maybe even conflicts with
        /// the opener planner."
        ///
        /// So the producer holds this back. It is only ever the opening's own
        /// planned Cysts, and it clears as they go up.
        /// </summary>
        internal static int PendingCystCash
        {
            get
            {
                if (ShadowOnly) return 0;
                int n = 0;
                for (int i = 0; i < _queue.Count; i++)
                    if (!_stepDone[i] && _queue[i].Kind == StepKind.Cyst) n++;
                return n > 0 ? EcoSimulator.CYST_COST : 0;
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

        /// <summary>
        /// Cash the committed opening still needs. Zero once the queue is done.
        ///
        /// The opening is a costed plan — Evaluate rejects any candidate whose
        /// total exceeds the bank — so a reservation taken out from under it
        /// mid-way breaks a commitment that was already checked. Exposed so the
        /// tech reserve can hold only genuine surplus while the opening runs.
        /// </summary>
        internal static int OutstandingCost
        {
            get
            {
                if (_queue.Count == 0) return 0;
                int total = 0;
                foreach (int i in PendingSteps()) total += _queue[i].Cost;
                return total;
            }
        }

        internal static Step StepAt(int i) => _queue[i];

        /// <summary>
        /// Move a step's target. Node steps plan a straight hop chain from
        /// ASSUMED structure positions, but the game's placement search relocates
        /// every structure by tens of metres, so by the second hop the planned
        /// point can be out of reach of anything real. NarakaCity 2026-07-30:
        /// node planned at (2388,1972), the Bio Cache it chained from actually
        /// landed at (2250,1865) — a 175m gap against a 150m node reach, so the
        /// step was refused every tick for 4.5 minutes and the site's Bio Cache
        /// stalled out behind it.
        /// </summary>
        internal static void RetargetStep(int i, Vector3 pos)
        {
            if (i < 0 || i >= _queue.Count) return;
            var st = _queue[i];
            st.Target = pos;
            _queue[i] = st;
        }

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

            // PAUSE, DO NOT SPEND, WHILE THE QUEEN IS AWAY.
            //
            // AlienConstruction refuses the placement, but that refusal is too
            // deep: the attempt counter and the retry timer are incremented
            // BEFORE the call, so a Queen out of the Nest silently burns each
            // Cyst step's 10-attempt budget and the step then gives up for
            // good — permanent damage from a temporary condition.
            //
            // Returning here freezes the opening instead: no attempts, no retry
            // timers, no stall accounting. It resumes exactly where it was when
            // she docks.
            if (!Perception.QueenStatus.CanBuild(team)) return;

            float now = Time.time;
            try { TickChain(team); }
            catch (Exception ex) { MelonLogger.Warning("[OPENER] chain threw: " + ex.Message); }
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
                    // Already ORDERED? Construct returns Success the moment the
                    // order is accepted, well before a structure appears — that
                    // is ground truth and ends the duplicate problem. The old
                    // check looked for a finished structure by name, missed the
                    // gap, and let a second Cyst be built 30s later.
                    float dedupR = CystDedupRadiusFor(i);
                    if (Faction.AlienConstruction.WasOrderedNear(
                            "Lesser Spawning Cyst", _queue[i].Target, dedupR)
                        || Perception.BuildTimeline.IsBuildingNear(
                            "Lesser Spawning Cyst", _queue[i].Target, dedupR)
                        || StructureNear(team, "Lesser Spawning Cyst", _queue[i].Target,
                                         Mathf.Min(60f, dedupR)))
                    {
                        MelonLogger.Msg($"[OPENER] Cyst step {i + 1}/{_queue.Count} confirmed ordered");
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
                        // If this never logs, the production-queue reflection is
                        // not matching and duplicate protection rests entirely
                        // on the one-per-tick rule and the retry interval.
                        MelonLogger.Msg($"[OPENER] Cyst step {i + 1}/{_queue.Count} waiting — " +
                                        $"a Cyst is already queued at {whereQueued}");
                        break;   // one Cyst in flight at a time
                    }

                    // Don't spend an attempt before Cysts are even UNLOCKED.
                    //
                    // The unlock is "the team has a finished Bio Cache" — any
                    // one, anywhere. Cysts anchor to the Nest (every placement
                    // log says anchor=Nest), so proximity to a particular BC is
                    // not the requirement; that was my mistake, and it is also
                    // what makes rule 3.2 possible at all.
                    //
                    // Previously both attempts were burned at t~26s before any
                    // BC existed, the cap gave up, and the Cyst only arrived
                    // when the beam re-proposed it — 49s after its Bio Cache was
                    // ready.
                    // Wait for THIS site's own Bio Cache, not merely any BC.
                    //
                    // A Cyst is placed against the nearest structure that can
                    // build one. Fire it while only the Nest qualifies and it
                    // anchors there — the logs show anchor=Nest, anchorDist=149m,
                    // pinned at the Nest's 150m reach and therefore unable to sit
                    // near biotics 213m out. Once this site's Bio Cache is up it
                    // becomes the nearest valid anchor and the Cyst lands beside
                    // its own patch, which is what the user is asking for.
                    // THE CYST UNLOCK IS GLOBAL, NOT PER SITE.
                    //
                    // One finished Bio Cache anywhere on the map allows Cysts
                    // (user, 2026-07-30). Gating each Cyst on a completed Bio
                    // Cache within 140m of its own target therefore bought
                    // nothing from the second site on — the first Bio Caches had
                    // been up for minutes — and cost the full ~30s of the local
                    // Bio Cache's construction at every later site.
                    //
                    // The Cyst still goes exactly where it went before, beside
                    // its patch, so no walk distance is traded away. Only the
                    // wait is dropped. If the spot is not yet in build range the
                    // game refuses it and the 1Hz pipeline retries, which is the
                    // same arbitration that fixed the original Cyst delay.
                    // THIS SITE'S BIO CACHE GOES FIRST — rule 3.2b.
                    //
                    // The plan already queues Bc before Cyst at every site, but
                    // ordering in the queue does not decide who fires: Cysts run
                    // here at 1 Hz while Bc steps fire on the beam's 8s tick, so
                    // whenever both become legal the Cyst wins by up to 8
                    // seconds. NarakaCity 2026-07-30 v0.8.52: the 3rd Cyst went
                    // up at (2375,820) with its Bio Cache still unbuilt, which is
                    // exactly the 1,500-before-500 spend 3.2b exists to stop.
                    if (!SiteBcDone(team, _queue[i].Goal))
                    {
                        // WAITING FOR MONEY IS NOT A STALL.
                        //
                        // This path runs on the 1Hz Cyst tick and the 8s plan
                        // tick could not see it, so a Cyst saving up read as
                        // "nothing fired", the stall timer ran, and the fourth
                        // Cyst of the opening skipped itself after 45 seconds
                        // with cash oscillating just under what it needed.
                        // DrMuck: "give finishing the opener more weight."
                        _waitingForCashAt = now;
                        if (now - _lastWaitLogAt > 5f)
                        {
                            _lastWaitLogAt = now;
                            MelonLogger.Msg($"[OPENER] Cyst step {i + 1}/{_queue.Count} waiting: " +
                                            $"this site's Bio Cache is not placed yet");
                        }
                        continue;
                    }

                    if (!AnyBcReadyCached(team, out string bcVia))
                    {
                        // Name the gate. A Cyst arrived 29s after its Bio Cache
                        // was ready with nothing in the log to say why, and I am
                        // not guessing at it again.
                        if (now - _lastWaitLogAt > 5f)
                        {
                            _lastWaitLogAt = now;
                            float rt = 0f;
                            try { rt = Perception.MapLayers.LayerReplay.CurrentRoundTime; } catch { }
                            MelonLogger.Msg($"[OPENER] Cyst step {i + 1}/{_queue.Count} waiting: " +
                                            $"no finished BC anywhere yet; target ({_queue[i].Target.x:F0}," +
                                            $"{_queue[i].Target.z:F0}) at roundT={rt:F0}s " +
                                            $"(usable {Perception.BuildTimeline.MeasuredBuildUpS("Bio Cache"):F0}s after start)");
                        }
                        continue;
                    }
                    if (_lastCystTry.TryGetValue(i, out float last) && now - last < RETRY_S) continue;

                    // Keep enough behind to feed the producers we already have.
                    int floor = ShrimpFloor(team);
                    if (team.TotalResources < _queue[i].Cost + floor)
                    {
                        if (now - _lastWaitLogAt > 5f)
                        {
                            _lastWaitLogAt = now;
                            MelonLogger.Msg($"[OPENER] Cyst step {i + 1}/{_queue.Count} waiting: " +
                                            $"cash={team.TotalResources} < {_queue[i].Cost}+{floor} " +
                                            $"(keeping shrimp production funded)");
                        }
                        continue;
                    }

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
                    // A queued search is still resolving — waiting is not an
                    // attempt. Counting it would spend the attempt budget on
                    // requests that were never issued.
                    if (Faction.AlienConstruction.SearchInFlightNear(
                            "Lesser Spawning Cyst", _queue[i].Target)) continue;

                    _cystAttempts[i] = tries + 1;
                    _lastCystTry[i] = now;

                    AnythingNear(team, _queue[i].Target, 60f, out string nearby);
                    MelonLogger.Msg($"[OPENER] Cyst step {i + 1}/{_queue.Count} BC ready via={bcVia}");
                    MelonLogger.Msg($"[OPENER] Cyst step {i + 1}/{_queue.Count} attempt {tries + 1}" +
                                    $"/{MAX_CYST_ATTEMPTS} at ({_queue[i].Target.x:F0},{_queue[i].Target.z:F0}) " +
                                    $"cash={team.TotalResources} nearby='{nearby}'");
                    // Straight at the game, bypassing the planner dedup.
                    //
                    // DECLARE A DELIBERATE PAIR. AlienConstruction refuses a
                    // second Cyst within 90m of one already ordered, and it is
                    // right to: queued searches stack. But the opener's doubled
                    // Cyst IS a second Cyst beside the first, 70m away by
                    // construction, so it was refused every time and never once
                    // built. Distance cannot separate the two cases -- a search
                    // for the SAME target can slide just as far -- so the caller
                    // says which it meant. HasCystSibling is the same test that
                    // shrinks this step's own dedup radius, so the two guards
                    // cannot disagree about what a pair is.
                    Faction.AlienConstruction.TryBuildStructureForPlanner(
                        team, EcoPlanner.ActionKind.PlaceCyst, _queue[i].Target,
                        deliberatePair: HasCystSibling(i));

                    // ONE PER TICK. Every pending Cyst becomes legal at the same
                    // instant — when the first Bio Cache finishes and the unlock
                    // fires — so without this the whole queue goes out in one
                    // frame and several Cysts get built at once. At 1Hz they now
                    // go down consecutively, a second apart.
                    return;
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
        // Back to the conservative values that produced good first-Cyst timing.
        //
        // 5s/8 attempts was tried on the theory that CystPending() would stop
        // duplicates. It did not — that check reflects over production-queue
        // entries whose shape is unverified, so it may simply never match. With
        // the unlock gate in place an attempt is only made when it can actually
        // succeed, so one try is normally enough and a slow retry is pure
        // insurance rather than the mechanism.
        // Quick retry is safe now that WasOrderedNear() gives ground truth on
        // whether the order was accepted — a refusal costs a second, a success
        // can never be repeated.
        const float RETRY_S = 3f;
        const int   MAX_CYST_ATTEMPTS = 10;
        static readonly Dictionary<int, int> _cystAttempts = new Dictionary<int, int>();
        static readonly Dictionary<int, float> _lastCystTry = new Dictionary<int, float>();

        /// <summary>
        /// Any owned structure within radius, excluding Bio Caches (the Cyst is
        /// deliberately placed near one). Reports what it found so the real
        /// construction-site display name shows up in the log — it is not
        /// "Lesser Spawning Cyst", which is why the previous check missed it.
        /// </summary>
        /// <summary>
        /// Extend the node chain toward the next Bio Cache that cannot reach —
        /// at 1 Hz, for the same reason the Cyst pipeline runs here.
        ///
        /// On the beam's 8s tick a chain cost 24s per hop against a 12s Node
        /// build: place, hold the next tick because the Node is still building,
        /// hold again, place. NarakaCity 2026-07-30, the southern chain took
        /// 113s for four hops (14:21:34, 14:22:15, 14:22:39, 14:23:03, then the
        /// Bio Cache at 14:23:27), which threw away the whole timing advantage
        /// of picking the southern site in the first place.
        ///
        /// No explicit wait is needed here. The hop is measured from the nearest
        /// FINISHED structure, and team.Structures only lists finished ones, so
        /// while the previous Node is still building the computed point does not
        /// move and the already-ordered check suppresses it. The moment it
        /// finishes the point advances and the next hop goes out within a
        /// second.
        /// </summary>
        static void TickChain(Team team)
        {
            _hopsThisTick.Clear();
            foreach (int i in PendingSteps())
            {
                if (_queue[i].Kind != StepKind.Bc) continue;
                Vector3 goal = _queue[i].Target;

                // STOP AT THE LIMIT. AN EXTRA HOP IS NOT FREE MARGIN.
                //
                // v0.73.3 shortened this to BcPlaceReachM - 40 on the theory
                // that the Bio Cache stalled for want of reach: the chain stops
                // measuring against NearestFinished, which counts accepted
                // orders, while CanPlaceBcTightNow needs a FINISHED anchor, so
                // the chain could stop with the Bio Cache only 15m inside.
                //
                // The next round measured it and the theory was wrong. The
                // southern node was ordered at 10:49:40.6 and did not COMPLETE
                // until 10:50:38.7 — 58s, sixth in a node queue that retires
                // about one every ten seconds — and the Bio Cache was requested
                // at 10:50:50.9, the first eco tick after. Reach was never the
                // constraint and the executor added no latency at all.
                //
                // So an extra hop would not have bought margin, it would have
                // put one more node in front of the Bio Cache and delayed it
                // another ten seconds. Back to the limit.
                float reach = EcoSimulator.BcPlaceReachM + 40f;
                if (!NearestFinished(team, goal, out Vector3 from, out float gap)) return;
                if (gap <= reach) continue;          // this one can build; try the next

                float hop = Mathf.Max(1f, EcoSimulator.NODE_REACH_M - 40f);

                // A BIO CACHE ABOUT TO GO UP MAY ANCHOR THIS CHAIN FOR FREE.
                //
                // Sites near the Nest need no nodes at all — the patch is inside
                // build range — and once such a Bio Cache exists it anchors the
                // chain onward. Hopping from the Nest toward the 3rd or 4th site
                // before placing it spends nodes on ground that Bio Cache would
                // have covered for nothing (user, 2026-07-30).
                //
                // Only when it genuinely helps, which is the user's own caveat:
                // the Bio Cache must sit at least one full hop closer to the goal
                // than the anchor we would otherwise use — i.e. predominantly in
                // the direction of the expansion, not merely somewhere nearby.
                // And only when it can actually be built right now, so a site
                // that is itself unreachable cannot deadlock the chain.
                if (BetterAnchorComing(team, from, goal, hop, out Vector3 bcPos))
                {
                    if (Time.time - _lastWaitLogAt > 10f)
                    {
                        _lastWaitLogAt = Time.time;
                        MelonLogger.Msg($"[OPENER] holding chain toward ({goal.x:F0},{goal.z:F0}) — " +
                                        $"a Bio Cache at ({bcPos.x:F0},{bcPos.z:F0}) anchors it " +
                                        $"{Vector3.Distance(from, goal) - Vector3.Distance(bcPos, goal):F0}m closer");
                    }
                    continue;
                }

                Vector3 dir = goal - from;
                float len = Mathf.Sqrt(dir.x * dir.x + dir.z * dir.z);
                if (len < 1f) return;
                Vector3 np = from + dir * (Mathf.Min(hop, len) / len);

                // Already on the way? While the previous hop builds, `from` has
                // not moved, so np is that same point and this holds.
                if (Faction.AlienConstruction.WasOrderedNear("Node", np, 70f)) continue;
                if (team.TotalResources < EcoSimulator.NODE_COST) return;

                // SEPARATE CHAINS MAY RUN TOGETHER; PARALLEL ONES MAY NOT.
                //
                // Serving strictly one chain per tick made the northern site
                // wait for the southern chain to finish. But letting every goal
                // hop freely is what produced two chains running side by side
                // 60-75m apart, because both far sites lay north and hopped off
                // the same anchor. The distinction is whether the hops actually
                // diverge: goals in genuinely different directions produce hops
                // far apart and can proceed at once.
                bool tooClose = false;
                for (int h = 0; h < _hopsThisTick.Count; h++)
                {
                    float dx = _hopsThisTick[h].x - np.x, dz = _hopsThisTick[h].z - np.z;
                    if (dx * dx + dz * dz < CHAIN_SEPARATION_M * CHAIN_SEPARATION_M)
                    { tooClose = true; break; }
                }
                if (tooClose) continue;

                MelonLogger.Msg($"[OPENER] step {i + 1}/{_queue.Count} chain node at " +
                                $"({np.x:F0},{np.z:F0}) toward ({goal.x:F0},{goal.z:F0}) " +
                                $"gap={gap:F0}m cash={team.TotalResources}");
                Faction.AlienConstruction.TryBuildStructureForPlanner(
                    team, EcoPlanner.ActionKind.PlaceNode, np);
                _hopsThisTick.Add(np);
                if (_hopsThisTick.Count >= MAX_CHAINS_AT_ONCE) return;
            }
        }

        /// <summary>Two chain hops closer than this are the same chain drawn
        /// twice, not two chains.</summary>
        const float CHAIN_SEPARATION_M = 200f;
        const int   MAX_CHAINS_AT_ONCE = 2;
        static readonly List<Vector3> _hopsThisTick = new List<Vector3>(4);

        /// <summary>
        /// Nearest structure that can anchor a NODE. team.Structures carries a
        /// structure only once construction has ended, so it supplies the
        /// finished ones; accepted orders supply the ones still going up, which
        /// anchor a node just as well.
        /// </summary>
        /// <summary>
        /// Is a Bio Cache from our own plan about to make this chain cheaper?
        ///
        /// True only when a pending Bio Cache step is at least one hop closer to
        /// the goal than the anchor we would use now, AND is itself already
        /// placeable — a site we cannot reach yet must never hold up a chain,
        /// which is how the old WaitForBetterAnchor could stall.
        /// </summary>
        static bool BetterAnchorComing(Team team, Vector3 from, Vector3 goal,
                                       float hop, out Vector3 bcPos)
        {
            bcPos = Vector3.zero;
            float fromGap = Vector3.Distance(from, goal);
            float reach = EcoSimulator.BcPlaceReachM + 40f;

            for (int j = 0; j < _queue.Count; j++)
            {
                if (_queue[j].Kind != StepKind.Bc) continue;
                Vector3 t = _queue[j].Target;

                // NOT _stepDone — that means REQUESTED, not placed.
                //
                // TryFireAction queues an async placement search and marks the
                // step done immediately, so between that instant and Construct
                // returning Success the Bio Cache is in neither the step state
                // nor the order record. NarakaCity 2026-07-31: the plan issued
                // at 14:27:16.023, the chain hopped off the NEST at .035, and
                // the Bio Cache that should have anchored it landed at .213 —
                // 178ms too late to be seen, so two nodes went in beside it.
                //
                // Skip only Bio Caches that ALREADY anchor, since NearestFinished
                // counts those; everything else is still "coming".
                if (Faction.AlienConstruction.WasOrderedNear("Bio Cache", t, 200f)) continue;
                if (StructureNear(team, "Bio Cache", t, 200f)) continue;
                if (Vector3.Distance(t, goal) > fromGap - hop) continue;   // not toward the goal
                if (!NearestFinished(team, t, out _, out float d) || d > reach) continue;  // not buildable yet
                bcPos = t;
                return true;
            }
            return false;
        }

        static bool NearestFinished(Team team, Vector3 to, out Vector3 pos, out float dist)
        {
            pos = Vector3.zero; dist = float.MaxValue;
            var structs = team.Structures;
            if (structs == null) return false;
            // BRANCH FROM A BASE STRUCTURE UNLESS A NODE IS GENUINELY CLOSER.
            //
            // Picking the nearest anchor to the goal lets a chain start from
            // another chain's TIP whenever that tip is even slightly closer,
            // which produces a dog-leg and hangs one branch off another.
            // NarakaCity 2026-08-01: the western chain toward (1744,1478)
            // started from the north-west chain's node at 608m instead of the
            // Bio Cache at 668m — 60m closer, less than a single 110m hop, so it
            // bought nothing and cost a detour. It also made the western branch
            // die with the northern one, which is the fragility the NodeManager
            // is meant to remove rather than create.
            //
            // So Nest and Bio Caches are preferred outright; a Node has to beat
            // the best of them by a full hop to be worth branching from.
            float baseDist = float.MaxValue; Vector3 basePos = Vector3.zero;
            float nodeDist = float.MaxValue; Vector3 nodePos = Vector3.zero;
            for (int i = 0; i < structs.Count; i++)
            {
                var st = structs[i];
                if (st == null || st.ObjectInfo == null || st.IsDestroyed) continue;
                string n = st.ObjectInfo.DisplayName ?? "";
                if (n != "Nest" && n != "Bio Cache" && n != "Node" && n != "Lesser Spawning Cyst") continue;
                Vector3 q = st.transform.position;
                float dx = q.x - to.x, dz = q.z - to.z;
                float d = Mathf.Sqrt(dx * dx + dz * dz);
                if (n == "Node") { if (d < nodeDist) { nodeDist = d; nodePos = q; } }
                else             { if (d < baseDist) { baseDist = d; basePos = q; } }
            }
            float hopM = Mathf.Max(1f, EcoSimulator.NODE_REACH_M - 40f);
            if (baseDist < float.MaxValue && nodeDist >= baseDist - hopM)
            { dist = baseDist; pos = basePos; }
            else if (nodeDist < float.MaxValue)
            { dist = nodeDist; pos = nodePos; }
            else if (baseDist < float.MaxValue)
            { dist = baseDist; pos = basePos; }
            // Structures that are ordered and far enough along to anchor, but
            // not yet listed as complete. A Node measured 20s to completion
            // against a 12s build-up, so waiting for the list costs ~8s a hop.
            // Anchors for a NODE need no age at all: a Bio Cache or Node
            // anchors from the moment it is placed (user, 2026-07-30). The
            // build-up wait that used to be applied here was ~8s a hop of pure
            // loss, and before that it was a full completion wait.
            _anchorScratch.Clear();
            Faction.AlienConstruction.CollectOrdersOlderThan("Node", 0f, _anchorScratch);
            Faction.AlienConstruction.CollectOrdersOlderThan("Bio Cache", 0f, _anchorScratch);
            for (int i = 0; i < _anchorScratch.Count; i++)
            {
                float dx = _anchorScratch[i].x - to.x, dz = _anchorScratch[i].z - to.z;
                float d = Mathf.Sqrt(dx * dx + dz * dz);
                if (d < dist) { dist = d; pos = _anchorScratch[i]; }
            }

            return dist < float.MaxValue;
        }

        static readonly List<Vector3> _anchorScratch = new List<Vector3>(16);

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

        /// <summary>
        /// Does the team have at least one Bio Cache that has finished? That is
        /// the Cyst unlock. Judged by elapsed time since the structure appeared,
        /// against BuildUpTime read from the game — no property on Structure
        /// reports completion reliably (IsFunctional flips ~1s after start).
        /// </summary>
        /// <summary>A finished Bio Cache within radius of this spot.</summary>
        static bool BcReadyNear(Team team, Vector3 pos, float radiusM)
        {
            float roundT;
            try { roundT = Perception.MapLayers.LayerReplay.CurrentRoundTime; } catch { return false; }
            return Perception.BuildTimeline.BcCompleteNear(pos, radiusM, roundT);
        }

        /// <summary>
        /// Has the Bio Cache step for this site already been placed? Steps carry
        /// the site centroid in Goal, so the pair can be matched without extra
        /// bookkeeping. True when no Bc step exists for the site, so a stray
        /// Cyst is never deadlocked by a missing partner.
        /// </summary>
        static bool SiteBcDone(Team team, Vector3 goal)
        {
            bool hasBcStep = false;
            for (int j = 0; j < _queue.Count; j++)
            {
                if (_queue[j].Kind != StepKind.Bc) continue;
                float dx = _queue[j].Goal.x - goal.x, dz = _queue[j].Goal.z - goal.z;
                if (dx * dx + dz * dz > 1f) continue;
                hasBcStep = true;
            }
            if (!hasBcStep) return true;   // nothing to wait for

            // GROUND TRUTH, NOT STEP STATE. _stepDone means REQUESTED, not
            // placed: the Bio Cache step is marked done the moment its request
            // goes out, which released the Cyst while the Bio Cache's own
            // placement search was still running. NarakaCity 2026-07-30 v0.8.53:
            // the 4th Cyst went up at 15:39:13 and its Bio Cache only landed at
            // 15:41:05, 112s later.
            return Faction.AlienConstruction.WasOrderedNear("Bio Cache", goal, 200f)
                || StructureNear(team, "Bio Cache", goal, 200f);
        }

        /// <summary>
        /// Cash that must survive placing a Cyst, so buying one never halts
        /// shrimp production at the Cysts already running.
        ///
        /// A Cyst costs 1,500 against a shrimp's 160. Spending down to zero for
        /// a fourth producer stops the three that are already earning — measured
        /// NarakaCity 2026-07-30: the 4th Cyst took cash 1,500 -> 0 and the
        /// site's own Bio Cache could not be afforded for another 112s. The
        /// floor scales with how many Cysts are actually producing, since that
        /// is what the reserve has to feed.
        /// </summary>
        static int ShrimpFloor(Team team)
        {
            int producers = 0;
            try
            {
                var structs = team?.Structures;
                if (structs != null)
                    for (int i = 0; i < structs.Count; i++)
                    {
                        var st = structs[i];
                        if (st == null || st.ObjectInfo == null || st.IsDestroyed) continue;
                        if ((st.ObjectInfo.DisplayName ?? "") == "Lesser Spawning Cyst") producers++;
                    }
            }
            catch { }
            return producers * EcoSimulator.SHRIMP_COST;
        }

        /// <summary>Any finished Bio Cache on the team — the actual Cyst
        /// prerequisite. Reports which signal answered, for the log.</summary>
        static bool AnyBcReadyCached(Team team, out string via)
        {
            if (AnyBcReady(team)) { via = "anyBc"; return true; }
            via = "none";
            return false;
        }

        static bool AnyBcReady(Team team)
        {
            float roundT;
            try { roundT = Perception.MapLayers.LayerReplay.CurrentRoundTime; } catch { return false; }
            var structs = team.Structures;
            if (structs == null) return false;
            for (int i = 0; i < structs.Count; i++)
            {
                var st = structs[i];
                if (st == null || st.ObjectInfo == null || st.IsDestroyed) continue;
                if (st.ObjectInfo.DisplayName != "Bio Cache") continue;
                if (Perception.BuildTimeline.BcCompleteNear(st.transform.position, 30f, roundT))
                    return true;
            }
            return false;
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
            // Saving up for a step is progress. See _waitingForCashAt.
            if (now - _waitingForCashAt < 3f) { _queueStalledSince = 0f; return; }
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
            public float SpreadM;            // mean pairwise distance between sites
            public float NodePenalty;        // score cost of the chain
            public float SpreadBonus;
            public long  BioticsTapped;
            public float ReserveBonus;
            public bool  DoubledCyst;
            /// <summary>Index into SiteIdx of the site taking the second Cyst.</summary>
            public int   DoubledAt;
            public float TerminalValue;
            /// <summary>Rollout time at which each site finished being placed —
            /// the number the tail's rate projection silently assumed was 0 for
            /// every site regardless of how far away it was.</summary>
            public readonly List<float> SiteAtS = new List<float>();
            public readonly List<Step> Steps = new List<Step>();

            public string Describe()
            {
                var sb = new System.Text.StringBuilder();
                sb.Append("sites=").Append(SiteIdx.Count)
                  .Append(" cysts=").Append(CystCount).Append(DoubledCyst ? "(2x@" + (int)MapProfile.Sites[SiteIdx[DoubledAt]].DistFromNest + "m)" : "")
                  .Append(" nodes=").Append(TotalNodes)
                  .Append(" cost=").Append(CostCash)
                  .Append(" income@").Append((int)SCORE_HORIZON_S).Append("s=").Append(IncomeToHandoff)
                  .Append(" rate=").Append(RateAtHandoff.ToString("F0")).Append("/s")
                  .Append(" tail=").Append((int)TerminalValue)
                  .Append(" sameDir=").Append(SpreadM.ToString("F2"))
                  .Append(" -nodes=").Append((int)NodePenalty)
                  .Append(" -sameDir=").Append((int)(-SpreadBonus))
                  .Append(" +reserve=").Append((int)ReserveBonus)
                  .Append(" score=").Append((int)Score)
                  .Append(" cashLeft=").Append(CashAtHorizon)
                  .Append(" done@").Append((int)CompleteAtS).Append('s')
                  .Append(" [");
                for (int i = 0; i < SiteIdx.Count; i++)
                {
                    var s = MapProfile.Sites[SiteIdx[i]];
                    if (i > 0) sb.Append(' ');
                    sb.Append(s.Patches).Append("p@").Append((int)s.DistFromNest).Append('m');
                    if (i < SiteAtS.Count) sb.Append('/').Append((int)SiteAtS[i]).Append('s');
                }
                sb.Append(']');
                return sb.ToString();
            }
        }

        /// <summary>
        /// How much of the opening is aimed the same way. Sums, over every pair
        /// of chosen sites, how close their bearings from the Nest are — 1.0 for
        /// an identical bearing, tapering to 0 at REDUNDANT_ANGLE_DEG apart.
        ///
        /// Bearings, not distances. Distance-based spread rewarded abandoning
        /// close biotics; bearing redundancy cannot, and it is the thing that
        /// actually costs us — two sites in one quadrant share a shrimp pool and
        /// produce the relocation churn seen when 682m NW and 802m W were both
        /// tapped, while a southern tap opens ground nothing else covers.
        /// </summary>
        /// <summary>
        /// The direction penalty, softened on cluster maps.
        ///
        /// On a map with no directional choice at all the term is harmless —
        /// every candidate carries the same redundancy, so it becomes a constant
        /// offset and cancels out of a purely relative comparison. The case that
        /// does need care is a map where clustering is genuinely BETTER: packed
        /// patches share a node chain and give shrimps short walks, and pushing
        /// the opener apart there costs income for nothing.
        ///
        /// IndustrialQuarter is that map — the only one measured with real
        /// clusters (MeanClusterSize 2.10 against 1.61 next). There a Site
        /// already aggregates several patches, so two Sites in one quadrant is a
        /// rich cluster rather than a redundant direction, and the penalty is
        /// scaled down by how clustered the map actually is.
        /// </summary>
        static float RedundancyPenalty()
        {
            if (!MapProfile.ClusterMode) return REDUNDANT_DIR_PENALTY;
            float size = Mathf.Max(1f, MapProfile.MeanClusterSize);
            return REDUNDANT_DIR_PENALTY / size;
        }

        static float DirectionRedundancy(List<int> siteIdx, Vector3 nest)
        {
            if (siteIdx.Count < 2) return 0f;
            var bearing = new float[siteIdx.Count];
            for (int i = 0; i < siteIdx.Count; i++)
            {
                Vector3 c = MapProfile.Sites[siteIdx[i]].Centroid;
                bearing[i] = Mathf.Atan2(c.x - nest.x, c.z - nest.z) * Mathf.Rad2Deg;
            }
            float total = 0f;
            for (int i = 0; i < siteIdx.Count; i++)
                for (int j = i + 1; j < siteIdx.Count; j++)
                {
                    float d = Mathf.Abs(Mathf.DeltaAngle(bearing[i], bearing[j]));
                    if (d < REDUNDANT_ANGLE_DEG)
                        total += 1f - d / REDUNDANT_ANGLE_DEG;
                }
            return total;
        }

        /// <summary>
        /// Biotics still in the ground under this plan's Bio Caches at handoff.
        /// The ceiling on anything the terminal tail can credit.
        /// </summary>
        /// <summary>
        /// Which of the chosen sites hosts the second Cyst.
        ///
        /// SEARCHING FOR THIS DID NOT WORK, SO IT IS A RULE.
        ///
        /// v0.77.0 scored the doubled Cyst on each of the two nearest sites and
        /// let the rollout pick. The rollout cannot see the thing that matters:
        /// the value of a doubled Cyst is partly that its shrimps RELOCATE onto
        /// ground tapped later, and "later" is mostly past the 240s horizon the
        /// opening is scored over. So the two hosts came out within noise and
        /// the choice was being made on rounding.
        ///
        /// The rule instead, and it is one the blueprint already applies to its
        /// own producers: extra production goes where migration will NOT reach.
        /// The site nearest the next tap feeds it for free on a short walk, so
        /// doubling there buys ground that was covered anyway; the site
        /// furthest from it is the one whose shrimps would otherwise have
        /// nowhere better to go.
        ///
        /// DrMuck, 2026-08-10, on [213m 215m 600m]: the 215m site at
        /// (2388,1450) is 741m from the southern tap against the 213m site's
        /// 537m, so 215m takes the pair — "do the double spawner at the
        /// furthest biotics from that".
        ///
        /// The last site is excluded as a host: it comes online last, so
        /// doubling there lands both Cysts late.
        /// </summary>
        static int DoublingHost(List<int> siteIdx)
        {
            if (siteIdx.Count < 2) return 0;
            Vector3 lastTap = MapProfile.Sites[siteIdx[siteIdx.Count - 1]].Centroid;
            int best = 0; float bestD = -1f;
            for (int k = 0; k < siteIdx.Count - 1; k++)
            {
                Vector3 c = MapProfile.Sites[siteIdx[k]].Centroid;
                float dx = c.x - lastTap.x, dz = c.z - lastTap.z;
                float d = dx * dx + dz * dz;
                if (d > bestD) { bestD = d; best = k; }
            }
            return best;
        }

        static float RemainingUnderPlan(EcoState s)
        {
            // Same radius the shrimp grouping uses to decide which patches a
            // Bio Cache actually serves.
            const float SERVICE_M = 200f;
            float radSq = SERVICE_M * SERVICE_M;
            float total = 0f;
            for (int pi = 0; pi < s.patches.Count; pi++)
            {
                if (s.patches[pi].remaining <= 0) continue;
                for (int bi = 0; bi < s.bcs.Count; bi++)
                {
                    float dx = s.bcs[bi].pos.x - s.patches[pi].pos.x;
                    float dz = s.bcs[bi].pos.z - s.patches[pi].pos.z;
                    if (dx * dx + dz * dz > radSq) continue;
                    total += s.patches[pi].remaining;
                    break;                       // count each patch once
                }
            }
            return total;
        }

        static Plan Search(EcoState root, out int evaluated)
        {
            var sites = MapProfile.Sites;
            evaluated = 0;
            if (sites == null || sites.Count == 0) return null;
            int evalCount = 0;

            // CANDIDATES BY DISTANCE, EXCEPT WHERE CLUSTERS MAKE THAT WRONG.
            //
            // The worth ranking below was adopted on the belief that it was a
            // no-op off cluster maps: "on NarakaCity all 107 hold 22,000, so
            // worth/distance collapses to distance and this changes nothing."
            // That reading came from headless TEST MODE, which never applies
            // UserData\Spawns — the live map runs Si_MapBalance's 42,000 with
            // per-patch overrides, so worth/distance does NOT collapse and the
            // ranking quietly reorders the pool on the only map that matters.
            //
            // So rank by distance, which is what the opener can actually see
            // from spawn (DrMuck, 2026-08-09), and keep the worth ranking for
            // the case it was really built for — genuine clusters.
            //
            // Sites arrive sorted by distance, and taking the nearest
            // CANDIDATE_SITES is fine on a map where every patch holds the same
            // amount.
            //
            // It fails badly where clusters exist. IndustrialQuarter has 172
            // patches in 123 sites at clusterSize 2.1, and the nearest 8 covers
            // a tiny radius: the opener took a 3-patch cluster at 21m and then
            // SINGLE patches at 666m, 900m and 983m, each needing its own
            // ~110m-per-hop chain — 17 Nodes for 5 Bio Caches. A richer cluster
            // further out was never rejected; it was never a candidate.
            //
            // Ranking by biotics per metre puts a 4-patch cluster at 900m
            // (88,000 / 900 = 98) ahead of a lone patch at 666m (22,000 / 666 =
            // 33), which is the trade actually worth making: one Bio Cache, one
            // chain, four patches.
            var pool = new List<int>(sites.Count);
            for (int i = 0; i < sites.Count; i++) pool.Add(i);
            if (MapProfile.ClusterMode)
            {
                // Cluster map. A Site here already aggregates several patches,
                // so biotics per metre is asking "how much ground does one Bio
                // Cache and one chain open" — the trade IndustrialQuarter turns
                // on. Ranking it by distance is what put 17 Nodes under 5 Bio
                // Caches there.
                pool.Sort((a, b) =>
                {
                    float va = sites[a].Biotics / Mathf.Max(1f, sites[a].DistFromNest);
                    float vb = sites[b].Biotics / Mathf.Max(1f, sites[b].DistFromNest);
                    return vb.CompareTo(va);
                });
            }
            else
            {
                // Scattered map. One patch per Site, so worth per metre is just
                // tonnage per metre, and tonnage is not something the opener can
                // see from spawn — distance is.
                pool.Sort((a, b) => sites[a].DistFromNest.CompareTo(sites[b].DistFromNest));
            }
            int n = Mathf.Min(CANDIDATE_SITES, pool.Count);
            pool.RemoveRange(n, pool.Count - n);
            // Back into distance order: the recursion builds combinations in the
            // order a chain is actually laid, so it must not permute.
            pool.Sort((a, b) => sites[a].DistFromNest.CompareTo(sites[b].DistFromNest));
            Plan best = null;

            // Keep the runners-up. Two candidate sets can be identical in cost
            // and node count and still score apart — NarakaCity 2026-07-30,
            // {214m,216m,682m,848m} beat {214m,216m,601m,682m} with both at
            // 4 nodes / 2800, so the margin is entirely in the rollout and was
            // invisible. Ties go to whichever is enumerated first, so a win
            // means a strictly higher score; this shows by how much.
            var top = new List<Plan>(RUNNERS_UP + 1);
            Plan bestDoubled = null, bestSingle = null;
            void Consider(Plan p)
            {
                int at = top.Count;
                while (at > 0 && top[at - 1].Score < p.Score) at--;
                if (at >= RUNNERS_UP) return;
                top.Insert(at, p);
                if (top.Count > RUNNERS_UP) top.RemoveAt(top.Count - 1);
            }

            // Combinations of sites, taken in distance order — the order a
            // chain is actually built in, so no need to permute.
            var chosen = new List<int>();
            void Recurse(int start, int want)
            {
                if (chosen.Count == want)
                {
                    // Allow ONE site to take a second Cyst. Reaching a further
                    // biotics costs a Bio Cache, a Cyst and its node chain —
                    // 3,000 on the last NarakaCity plan — while a second Cyst on
                    // ground already held costs 1,500 and no nodes at all. Early
                    // on the binding constraint is shrimp PRODUCTION, not patch
                    // capacity: measured 3 shrimps against a per-patch ceiling of
                    // 18, so doubling production where we already stand may beat
                    // paying for reach. User idea, 2026-07-31.
                    // DOUBLING IS OFF BY DEFAULT BECAUSE IT HAS NEVER WORKED.
                    //
                    // AlienConstruction.IsDuplicateNow refuses a Cyst within
                    // DUP_RADIUS_M (90m) of another, and says why: "Cysts and
                    // Bio Caches are one-per-patch". A doubled Cyst is placed at
                    // +/-CYST_BESIDE_M around one Bio Cache, so the pair is 70m
                    // apart and the second is always refused. NarakaCity
                    // 2026-08-10 12:29: step 10 attempted ten times at
                    // (2688,1138) with 4,400 in the bank and gave up.
                    //
                    // The opener nonetheless SCORED the doubling — the doubled
                    // plan beat the 3-Cyst variant by 1277 — so it was choosing
                    // openings on a producer the executor cannot place. Off
                    // until the one-per-patch rule is deliberately revisited;
                    // openerDoubleCyst in rtsai.json turns it back on.
                    int cystMax = chosen.Count + (DoubleCystEnabled ? 1 : 0);
                    for (int cysts = 2; cysts <= cystMax; cysts++)
                    {
                        var p = Evaluate(root, chosen, cysts, DoublingHost(chosen));
                        evalCount++;
                        if (p == null) continue;
                        Consider(p);
                        if (p.DoubledCyst) { if (bestDoubled == null || p.Score > bestDoubled.Score) bestDoubled = p; }
                        else               { if (bestSingle  == null || p.Score > bestSingle.Score)  bestSingle  = p; }
                        if (best == null || p.Score > best.Score) best = p;
                    }
                    return;
                }
                for (int i = start; i < n; i++)
                {
                    chosen.Add(pool[i]);
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

            for (int i = 0; i < top.Count; i++)
                MelonLogger.Msg($"[OPENER/ALT] #{i + 1} {top[i].Describe()}");

            // The doubled-Cyst option, always shown even when it loses — it was
            // added to answer a specific question and a silent absence from the
            // top six answers nothing.
            if (bestDoubled != null)
                MelonLogger.Msg($"[OPENER/2X] best doubled: {bestDoubled.Describe()}" +
                                $" | vs best single {(int)(bestSingle?.Score ?? 0)}" +
                                $" ({(int)(bestDoubled.Score - (bestSingle?.Score ?? 0))})");
            else
                MelonLogger.Msg("[OPENER/2X] no doubled-Cyst plan was affordable");

            // The best opening that taps a site the winner ignored. This is the
            // "why not that patch?" question in one line, rather than a guess.
            for (int pi = 0; pi < n; pi++)
            {
                int sIdx = pool[pi];
                if (best != null && best.SiteIdx.Contains(sIdx)) continue;
                Plan bestWith = null;
                for (int i = 0; i < top.Count; i++)
                    if (top[i].SiteIdx.Contains(sIdx)
                        && (bestWith == null || top[i].Score > bestWith.Score)) bestWith = top[i];
                var site = sites[sIdx];
                if (bestWith != null)
                    MelonLogger.Msg($"[OPENER/SKIP] site@{(int)site.DistFromNest}m best-with-it " +
                                    $"score={(int)bestWith.Score} vs winner {(int)(best?.Score ?? 0)} " +
                                    $"({(int)(bestWith.Score - (best?.Score ?? 0))})");
                else
                    MelonLogger.Msg($"[OPENER/SKIP] site@{(int)site.DistFromNest}m not in any " +
                                    $"top-{RUNNERS_UP} opening");
            }
            return best;
        }

        /// <summary>
        /// Build the candidate opening into a forked state and roll it forward.
        /// Nodes, Bio Caches and Cysts are placed with the same costs, build
        /// times and reaches the live planner reads off the game, so the score
        /// is directly comparable to the beam's.
        /// </summary>
        static Plan Evaluate(EcoState root, List<int> siteIdx, int cystCount, int doubleAt = 0)
        {
            var s = root.Clone();
            var plan = new Plan { CystCount = cystCount };
            plan.SiteIdx.AddRange(siteIdx);
            _layoutEarned = 0f;

            float hop = Mathf.Max(1f, EcoSimulator.NODE_REACH_M - 15f);
            float bcAnchorReach = EcoSimulator.BcPlaceReachM + 40f;   // sign: see BC_TIGHT_GAP_M
            int cost = 0;

            for (int k = 0; k < siteIdx.Count; k++)
            {
                var site = MapProfile.Sites[siteIdx[k]];

                // A CHAIN IS SERIAL. ORDERING A HOP EARLY BUYS NOTHING.
                //
                // Measured on NarakaCity 2026-08-10, six links across two
                // chains, every one exact to under 20ms:
                //
                //     usableAt = max(orderTime, anchorUsableAt) + Total
                //
                //   (2475,1165) ordered 38.700  ready  58.718   anchor: Nest
                //   (2415,1040) ordered 39.807  ready  78.731 = 58.718 + 20
                //   (2380, 935) ordered 40.619  ready  98.733 = 78.731 + 20
                //   (2370,1535) ordered 39.614  ready  88.952 = 68.940 + 20
                //   (2330,1640) ordered 40.615  ready 108.956 = 88.952 + 20
                //   (2290,1745) ordered 41.625  ready 128.974 = 108.956 + 20
                //
                // The rollout charged one second per hop, which is what
                // ORDERING costs and is beside the point — all six went out
                // within three seconds of each other and the last was not
                // usable for another 87. A five-hop chain is a hundred seconds,
                // and until this landed a long shared chain looked nearly free,
                // which is why the opener started preferring to string four
                // sites along one bearing (DrMuck: "the opener looks strange
                // now! In particular the noding").
                float anchorDist = NearestAnchorDist(s, site.Centroid);
                NearestAnchor(s, site.Centroid, out float anchorReady);
                int nodes = 0;
                while (anchorDist > bcAnchorReach && nodes < 40)
                {
                    Vector3 from = NearestAnchor(s, site.Centroid, out anchorReady);
                    Vector3 dir = site.Centroid - from;
                    float len = Mathf.Sqrt(dir.x * dir.x + dir.z * dir.z);
                    if (len < 1f) break;
                    if (!Afford(s, EcoSimulator.NODE_COST + GroundFloor(s, plan, cystCount))) return null;
                    Advance(s, NODE_HOP_ORDER_S);
                    Vector3 np = from + dir * (hop / len);
                    // Starts when its anchor is up, not when it is ordered.
                    anchorReady = Mathf.Max(s.t, anchorReady) + EcoSimulator.NODE_BUILD_S;
                    s.nodes.Add(new EcoState.Node { pos = np, finished = false,
                                                    readyAt = anchorReady });
                    plan.Steps.Add(new Step { Kind = StepKind.Node, Target = np,
                                              Goal = site.Centroid, Cost = EcoSimulator.NODE_COST });
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
                // Rule 3.2: from the THIRD site on, the Cyst is queued BEFORE
                // its Bio Cache.
                //
                // Legal because the Cyst unlock is "any finished BC on the
                // team", not "a BC at this site" — by the third site the first
                // two are long since up. The BC costs 500 and earns nothing
                // until shrimps exist, so ordering the Cyst first gets shrimp
                // production started earlier at that site.
                //
                // Only the ORDER changes. The queue is unordered at fire time,
                // so this cannot serialise the opening the way the old
                // wait-for-Cyst GATE did.
                //
                // Open question the user raised: the Cyst may end up slightly
                // further from the biotics this way. Whether the extra walk
                // outweighs earlier shrimps is untested — worth measuring.
                bool cystFirstHere = k >= CYST_FIRST_FROM_SITE;
                if (cystFirstHere && k < cystCount)
                {
                    if (!AdvanceUntilBcFinished(s)) return null;
                    if (!Afford(s, EcoSimulator.CYST_COST)) return null;
                    Vector3 ct = OffsetCystBeside(site.Centroid, bcPos, CYST_BESIDE_M);
                    s.cysts.Add(new EcoState.Cyst { pos = ct, finished = false,
                        readyAt = s.t + EcoSimulator.CYST_BUILD_S,
                        nextSpawnAt = s.t + EcoSimulator.CYST_BUILD_S + EcoSimulator.SHRIMP_BUILD_S });
                    plan.Steps.Add(new Step { Kind = StepKind.Cyst, Target = ct,
                                              Goal = site.Centroid, Cost = EcoSimulator.CYST_COST });
                    s.cash -= EcoSimulator.CYST_COST;
                    cost += EcoSimulator.CYST_COST;
                }

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
                // The Bio Cache needs a FINISHED anchor -- the game refuses it
                // otherwise, which is what CanPlaceBcTightNow reports at fire
                // time. NarakaCity 2026-08-10: the southern chain's last node
                // completed at 10:50:38.7 and the Bio Cache was requested at
                // 10:50:50.9, the very next eco tick. So the wait is real, and
                // the rollout has to sit through it rather than place on top of
                // a node that is still going up.
                if (anchorReady > s.t) Advance(s, anchorReady - s.t);

                if (!PlaceBcStep(s, plan, site, bcPos, ref cost,
                                 GroundFloor(s, plan, cystCount))) return null;

                if (k < cystCount && !cystFirstHere)
                {
                    if (!AdvanceUntilBcFinished(s)) return null;
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
                    Vector3 cystTarget = OffsetCystBeside(site.Centroid, bcPos, CYST_BESIDE_M);
                    plan.Steps.Add(new Step { Kind = StepKind.Cyst, Target = cystTarget,
                                              Goal = site.Centroid, Cost = EcoSimulator.CYST_COST });
                    s.cash -= EcoSimulator.CYST_COST;
                    cost += EcoSimulator.CYST_COST;
                }
                // Gap build order: from the second site on the BC waits for its
                // own Cyst to finish, so the Cyst's shrimp is already building
                // while the BC goes up.

                plan.SiteAtS.Add(s.t);
            }

            // The extra Cyst, when this candidate doubles up. It goes on the
            // FIRST site: nearest, already reachable, and the place where extra
            // production compounds for the longest.
            plan.DoubledCyst = cystCount > siteIdx.Count;
            if (plan.DoubledCyst && siteIdx.Count > 0)
            {
                if (!AdvanceUntilBcFinished(s)) return null;
                if (!Afford(s, EcoSimulator.CYST_COST)) return null;
                // WHICH SITE TAKES THE SECOND CYST IS A CHOICE, NOT SLOT 0.
                //
                // It was hardcoded to the nearest site and never compared
                // against the alternative. DrMuck, 2026-08-10: the second
                // nearest may be better because its shrimps can relocate onto
                // untapped ground while a further site is still being tapped --
                // the doubled Cyst is a shrimp SOURCE, so where those shrimps
                // can go next is part of its value, not just what it stands on.
                int dIdx = Mathf.Clamp(doubleAt, 0, siteIdx.Count - 1);
                plan.DoubledAt = dIdx;
                var site0 = MapProfile.Sites[siteIdx[dIdx]];
                Vector3 bc0 = OffsetFromSite(s, site0.Centroid, BC_PATCH_STANDOFF_M);
                // Mirror the usual offset so the two Cysts do not contend for
                // the same ground in the placement search.
                Vector3 t2 = OffsetCystBeside(site0.Centroid, bc0, -CYST_BESIDE_M);
                s.cysts.Add(new EcoState.Cyst { pos = t2, finished = false,
                    readyAt = s.t + EcoSimulator.CYST_BUILD_S,
                    nextSpawnAt = s.t + EcoSimulator.CYST_BUILD_S + EcoSimulator.SHRIMP_BUILD_S });
                plan.Steps.Add(new Step { Kind = StepKind.Cyst, Target = t2,
                                          Goal = site0.Centroid, Cost = EcoSimulator.CYST_COST });
                s.cash -= EcoSimulator.CYST_COST;
                cost += EcoSimulator.CYST_COST;
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

            // _layoutEarned is what came in WHILE the opening was going down.
            // Nonzero from v0.73 on, because layout now takes time.
            plan.IncomeToHandoff = (int)_layoutEarned + earnedBeforeWindow + s.grossEarned;
            plan.RateAtHandoff   = rateAtHandoff;

            // WHY income+rate ALONE CANNOT CHOOSE.
            //
            // Measured NarakaCity 2026-07-30: all 546 candidate openings scored
            // within 59 points of each other on 130,848 — 0.045%. The winner was
            // therefore noise. Two reasons, both fixed below:
            //
            //  * rate * TERMINAL_TAIL_S is ~81,600 of the score, 62% of the
            //    total, and every opening converges to nearly the same rate by
            //    the handoff. Most of the objective was a constant.
            //  * Nodes and cash had ZERO weight. An 8-node plan scored the same
            //    as a 4-node one, because the income model over-predicts ~3.5x
            //    and leaves ~38,000 in the modelled bank — so the simulator
            //    never feels the scarcity that produced real InsufficientResource
            //    refusals in the same round.
            plan.SpreadM     = DirectionRedundancy(siteIdx, root.nestPos);
            plan.NodePenalty = NODE_SCORE_COST * plan.TotalNodes;
            plan.SpreadBonus = -RedundancyPenalty() * plan.SpreadM;

            // WHAT THE SITES STILL HOLD.
            //
            // Income to the handoff cannot see a cluster's advantage: in the
            // first 240s the limit is how many shrimps exist, not how much
            // biotics is reachable, so a 3-patch site out-earns a 1-patch site
            // by well under one percent. IndustrialQuarter 2026-07-30: the plan
            // taking the western 3-patch cluster had the HIGHEST income of every
            // candidate (43,894) and still lost by 1,335, because the node and
            // direction penalties are charged per SITE — a cluster pays a lone
            // patch's costs for three times the ground.
            //
            // So credit what a site still holds. Rated at a small fraction of
            // its eventual cash value: 1,500 cash of Cyst bought ~2,895 score,
            // about 1.93 per cash, and unmined biotics is valued here at 0.04 —
            // roughly two percent of that — because it is far off and depends on
            // shrimps that may never be built.
            long bioticsTapped = 0;
            for (int i = 0; i < siteIdx.Count; i++) bioticsTapped += MapProfile.Sites[siteIdx[i]].Biotics;
            plan.BioticsTapped = bioticsTapped;
            plan.ReserveBonus  = RESERVE_VALUE_PER_BIOTIC * bioticsTapped;

            // THE TAIL CANNOT OUTLAST THE RESOURCE.
            //
            // Terminal value was rate-at-handoff projected flat for 240s, which
            // assumes the ground keeps paying. It does not: a site worked by
            // twice as many shrimps drains in half the time. At realistic early
            // counts a 22,000 patch lasts 280-670s, so depletion falls OUTSIDE
            // the 240s scoring window and was invisible — precisely the risk in
            // doubling up on one patch (user, 2026-07-31: "one disadvantage of
            // double cyst is faster depletion").
            //
            // So cap the tail by what is actually left. Earning `rate` for
            // TERMINAL_TAIL_S is only possible while there is resource to earn
            // it from; beyond that the income stops regardless of how many
            // shrimps are standing there.
            float remainingAtHandoff = RemainingUnderPlan(s);
            plan.TerminalValue = Mathf.Min(rateAtHandoff * TERMINAL_TAIL_S, remainingAtHandoff);

            plan.Score = plan.IncomeToHandoff
                       + plan.TerminalValue
                       - plan.NodePenalty
                       + plan.SpreadBonus
                       + plan.ReserveBonus;
            plan.CashAtHorizon = s.cash;
            return plan;
        }

        /// <summary>
        /// Ground yields to the opening's own unplaced Cysts.
        ///
        /// Mirrors the executor exactly: TryFireAction refuses a PlaceBc or
        /// PlaceNode whose payment would drop cash below OpenerPlanner
        /// .PendingCystCash, and logs it as "reserve1500". The rollout did not
        /// model that floor, so it costed openings the executor would then
        /// refuse — NarakaCity 2026-08-09, the 4th site's Bio Cache was refused
        /// four times over 24s for exactly this reason while the plan had
        /// declared the whole 9,000 affordable at t=0.
        ///
        /// A Cyst placement is exempt, as it is in the executor — the floor
        /// exists to protect Cysts, not to block them.
        ///
        /// THE SECOND TERM IS WHAT A CYST COSTS AFTER YOU HAVE BOUGHT IT.
        ///
        /// A Cyst is 1,500 once and then 160 per shrimp for as long as it runs,
        /// so a 4-Cyst opening carries an ongoing draw a 2-Cyst opening does
        /// not — and buying ground with that money is what stops the producers
        /// (DrMuck, 2026-08-09: "you need to consider the upfront costs to pump
        /// out shrimps from more cysts"). The executor already holds it back as
        /// min(cysts,10) x SHRIMP_COST, logged as "reserve640" at four Cysts;
        /// the rollout costed openings as though it did not, so a plan with more
        /// Cysts looked no more expensive to keep running than one with fewer.
        /// </summary>
        static int GroundFloor(EcoState s, Plan plan, int cystCount)
        {
            int placed = 0;
            for (int i = 0; i < plan.Steps.Count; i++)
                if (plan.Steps[i].Kind == StepKind.Cyst) placed++;
            int pendingCyst   = placed < cystCount ? EcoSimulator.CYST_COST : 0;
            int shrimpReserve = Mathf.Min(s.cysts.Count, 10) * EcoSimulator.SHRIMP_COST;
            return Mathf.Max(pendingCyst, shrimpReserve);
        }

        /// <summary>Queue the Bio Cache for a site. False if unaffordable in the window.</summary>
        static bool PlaceBcStep(EcoState s, Plan plan, MapProfile.Site site, Vector3 bcPos,
                                ref int cost, int groundFloor)
        {
            if (!Afford(s, EcoSimulator.BC_COST + groundFloor)) return false;
            s.bcs.Add(new EcoState.Bc { pos = bcPos, finished = false,
                                        readyAt = s.t + EcoSimulator.BC_BUILD_S,
                                        storage = 0, storageCap = 4000 });
            plan.Steps.Add(new Step { Kind = StepKind.Bc, Target = site.Centroid,
                                      Goal = site.Centroid, Cost = EcoSimulator.BC_COST });
            s.cash -= EcoSimulator.BC_COST;
            cost += EcoSimulator.BC_COST;
            return true;
        }

        // How many opening sites lead with the Bio Cache. At least 1 is
        // mandatory — the first BC is what unlocks Cyst construction.
        const int BC_FIRST_SITES = 1;

        /// <summary>
        /// Site index from which the Cyst is queued BEFORE its Bio Cache.
        ///
        /// Now never: EVERY site leads with its Bio Cache.
        ///
        /// This does not make rule 3.2 wrong. Considered at one site on its own
        /// it holds — a Bio Cache costs money and earns nothing until shrimps
        /// exist, so deferring it does free cash there. What it was weighed
        /// without is two other mechanisms:
        ///
        ///  * PRICE. A Cyst is 1,500 against the Bio Cache's 500. Ordering it
        ///    first at the 3rd or 4th site takes cash away from shrimp
        ///    production at the Cysts ALREADY running, which is where the money
        ///    was earning. NarakaCity 2026-07-30: cash fell 3,800 -> 1,700 ->
        ///    200 and stayed there for the rest of Phase 1.
        ///  * PLACEMENT REACH. Placement needs a FINISHED anchor. At a new site
        ///    the only finished structure is the node chain, which stops short
        ///    of the patch — the southern Cyst landed at (2375,820) against a
        ///    patch at (2307,714). The Bio Cache stands beside the patch, so
        ///    anchoring the Cyst off it puts the Cyst nearer the biotics.
        ///
        /// So Bio-Cache-first is both cheaper at the moment it matters and
        /// better placed. The cost is the Cyst arriving ~30s later.
        /// </summary>
        const int CYST_FIRST_FROM_SITE = int.MaxValue;

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
                Advance(s, STEP_S);
            }
            return true;
        }

        /// <summary>
        /// Income earned while the opening is being LAID OUT, as opposed to
        /// after it. Accumulated here because EcoSimulator.SimulateForward
        /// zeroes s.grossEarned on entry, so it reports only the last window it
        /// ran — every earlier window is lost unless it is banked as it goes.
        ///
        /// Harmless until v0.73: layout took no time, so Afford never advanced
        /// the clock and there was never more than one window. The moment
        /// layout is sequenced this becomes the difference between scoring an
        /// opening's whole 240s and scoring only its last few seconds.
        /// </summary>
        static float _layoutEarned;

        /// <summary>Run the rollout forward, banking what it earns.</summary>
        static void Advance(EcoState s, float dt)
        {
            if (dt <= 0f) return;
            EcoSimulator.SimulateForward(s, dt);
            _layoutEarned += s.grossEarned;
        }

        /// <summary>
        /// Hold until some Bio Cache has FINISHED building.
        ///
        /// The game gates Cyst construction on a finished Bio Cache anywhere on
        /// the team — TickFast's "no finished BC anywhere yet" is that refusal
        /// observed from the outside. The rollout placed Cysts at t=0 regardless,
        /// which is not a placement the game would ever have accepted.
        /// </summary>
        static bool AdvanceUntilBcFinished(EcoState s)
        {
            const float STEP_S = 2f;
            while (true)
            {
                for (int i = 0; i < s.bcs.Count; i++) if (s.bcs[i].finished) return true;
                if (s.bcs.Count == 0) return false;               // nothing coming
                if (s.t >= SCORE_HORIZON_S - RATE_WINDOW_S) return false;
                Advance(s, STEP_S);
            }
        }

        /// <summary>
        /// Wall-clock cost of ordering one node hop.
        ///
        /// Not the 12s build time: TickChain measures its hop from the nearest
        /// structure INCLUDING accepted orders (see NearestFinished), so the
        /// chain does not wait for each node to finish. Measured on NarakaCity
        /// 2026-08-09, the north-west chain placed hops at 22:24:22, :23, :24,
        /// :25, :26, :27 and :30 — one per second, seven of them.
        /// </summary>
        const float NODE_HOP_ORDER_S = 1f;

        // Lateral offset from the PATCH, perpendicular to the BC direction —
        // far enough to clear the Bio Cache footprint, close enough that the
        // Cyst ends up level with it rather than behind it.
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
        const float CYST_BESIDE_M = 35f;
        /// <summary>Radius for "a Cyst is already coming here". Wide because the
        /// placement search relocates a request by up to ~100m, so the order
        /// that lands is not at the point we asked for.</summary>
        const float CYST_DEDUP_M = 130f;

        /// <summary>Below this the guard is useless, so a plan that packs Cysts
        /// tighter than this gets no dedup protection between them at all.</summary>
        const float CYST_DEDUP_MIN_M = 20f;

        /// <summary>
        /// The dedup radius for one Cyst step, shrunk so it cannot reach a
        /// SIBLING step's target.
        ///
        /// CYST_DEDUP_M is 130m because a placement can slide ~100m, and
        /// CYST_BESIDE_M is 35m so a doubled Cyst sits beside its Bio Cache on
        /// the same patch. Mirrored, that pair is 70m apart — well inside 130 —
        /// so the first of the pair always confirmed the second and the second
        /// was never built. Both constants are right on their own and were
        /// written for opposite purposes; nothing reconciled them.
        ///
        /// Measured NarakaCity 2026-08-10: step 2's Cyst ordered at 12:07:23,
        /// step 10 — the doubled Cyst on the SAME site — marked "confirmed
        /// ordered" at 12:07:25 without ever being attempted. The blueprint's
        /// first Cyst was 12:13:05, five and a half minutes later, so this was
        /// the opener colliding with itself. Cost: the 4th producer arrived at
        /// t=403s instead of t=110s, on a plan the opener only chose BECAUSE it
        /// doubled (it beat the 3-Cyst variant by 1277).
        ///
        /// Half the distance to the nearest other Cyst step: wide enough to
        /// absorb a slide when nothing is near, never wide enough to swallow a
        /// deliberate neighbour. No special case for the doubled step — any
        /// plan that puts two Cysts close gets the same protection.
        /// </summary>
        /// <summary>Does another Cyst step in this plan sit close enough that
        /// the two are a deliberate pair rather than a duplicate? Same measure
        /// CystDedupRadiusFor uses, so the opener's dedup and the construction
        /// guard agree by construction.</summary>
        static bool HasCystSibling(int i) => CystDedupRadiusFor(i) < CYST_DEDUP_M;

        static float CystDedupRadiusFor(int i)
        {
            float r = CYST_DEDUP_M;
            Vector3 t = _queue[i].Target;
            for (int j = 0; j < _queue.Count; j++)
            {
                if (j == i || _queue[j].Kind != StepKind.Cyst) continue;
                float dx = _queue[j].Target.x - t.x, dz = _queue[j].Target.z - t.z;
                float d = Mathf.Sqrt(dx * dx + dz * dz);
                if (d > 1f) r = Mathf.Min(r, d * 0.5f);
            }
            return Mathf.Max(CYST_DEDUP_MIN_M, r);
        }

        /// <summary>
        /// Cyst position: BESIDE THE PATCH, level with the Bio Cache.
        ///
        /// Aimed at the patch and offset perpendicular to the BC-to-patch line,
        /// so the placement search slides it out to the no-build boundary on
        /// that side — landing about as close to the biotics as the Bio Cache
        /// is, rather than behind it.
        ///
        /// It was previously pushed 40m AWAY from the patch, which put the Bio
        /// Cache between the Cyst and the biotics. That offset was guarding
        /// against a reach limit that does not exist: a Cyst was observed
        /// placed 618m from its anchor with result=Success, because only the
        /// NEST can build Cysts and the placement is validated against the whole
        /// base network, not against that one anchor. An earlier perpendicular
        /// attempt did fail, but that was the unlock problem (no finished Bio
        /// Cache yet), not the position — I mis-attributed it at the time.
        ///
        /// User 2026-07-30: "the first 2 starter cysts can be placed closer to
        /// biotics because they anchor to the already finished biocache."
        /// </summary>
        static Vector3 OffsetCystBeside(Vector3 patch, Vector3 bcPos, float d)
        {
            float dx = bcPos.x - patch.x, dz = bcPos.z - patch.z;
            float len = Mathf.Sqrt(dx * dx + dz * dz);
            if (len < 1f) return patch + new Vector3(d, 0f, 0f);
            Vector3 perp = new Vector3(-dz / len, 0f, dx / len);   // 90 deg in XZ
            return patch + perp * d;
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
            => NearestAnchor(s, to, out _);

        /// <summary>
        /// Nearest anchor AND the time it becomes usable. The readyAt is the
        /// half that was missing: a chain hop cannot start building until the
        /// thing it hangs off has finished, so the anchor's clock is the hop's
        /// start.
        /// </summary>
        static Vector3 NearestAnchor(EcoState s, Vector3 to, out float readyAt)
        {
            Vector3 best = s.nestPos; float bd = float.MaxValue; float bReady = 0f;
            void consider(Vector3 q, bool finished, float ready)
            {
                float dx = q.x - to.x, dz = q.z - to.z;
                float d = dx * dx + dz * dz;
                if (d < bd) { bd = d; best = q; bReady = finished ? 0f : ready; }
            }
            if (s.nestPos != Vector3.zero) consider(s.nestPos, true, 0f);
            for (int i = 0; i < s.bcs.Count; i++)   consider(s.bcs[i].pos,   s.bcs[i].finished,   s.bcs[i].readyAt);
            for (int i = 0; i < s.cysts.Count; i++) consider(s.cysts[i].pos, s.cysts[i].finished, s.cysts[i].readyAt);
            for (int i = 0; i < s.nodes.Count; i++) consider(s.nodes[i].pos, s.nodes[i].finished, s.nodes[i].readyAt);
            readyAt = bReady;
            return best;
        }
    }
}
