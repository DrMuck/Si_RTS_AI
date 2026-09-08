using MelonLoader;
using Silica;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Si_RTS_AI.Planning
{
    /// <summary>
    /// The structure network as a GRAPH: what is connected to a Nest, what is
    /// one loss away from being cut off, and where a loop would pay for itself.
    ///
    /// Everything alien hangs off a chain back to a Nest. Cut the chain and the
    /// whole branch decays — not just the Nodes, but the Bio Caches and Cysts
    /// riding on them. Players answer that by "circling" or "looping": joining
    /// two branches so either can carry the other if one is severed. None of
    /// that was modelled here; the planner laid chains and never looked at them
    /// again.
    ///
    /// NO LONGER SHADOW. This reported and did not act when it was written;
    /// EcoPlanner now consumes TryGetRepair and TryGetLoop and builds both. The
    /// stale "shadow" wording is worth naming because it nearly cost an answer:
    /// asked on 2026-08-12 whether a repair manager existed, reading this
    /// comment says no and reading the call sites says yes.
    ///
    /// The two things it finds:
    ///
    ///   ORPHANS           — already disconnected from every Nest, so already
    ///                       dying. Repair targets.
    ///   ARTICULATION      — a structure whose loss disconnects something behind
    ///     POINTS            it. These are exactly the places where a loop is
    ///                       worth building, found rather than guessed: they are
    ///                       the cut vertices of the graph (Hopcroft-Tarjan).
    ///
    /// Naming the cut vertices matters because "add redundancy" is otherwise
    /// unbounded — every extra link costs 200 and most buy nothing. A link that
    /// removes an articulation point provably converts a single point of failure
    /// into a loop; a link anywhere else does not.
    /// </summary>
    internal static class NodeManager
    {
        const float REPORT_EVERY_S = 30f;

        /// <summary>Slack over the expected build time before an order with no
        /// structure counts as stalled rather than merely slow.</summary>
        const float STALL_GRACE_S  = 20f;
        const float STALL_RADIUS_M = 60f;

        /// <summary>Seconds past the measured build time before a Node order
        /// that produced nothing is FORGOTTEN rather than merely reported.
        /// Longer than STALL_GRACE_S on purpose: reporting is a diagnostic and
        /// should be twitchy, dropping an order is an action and should not
        /// fire on something that is only slow. Well under
        /// AlienConstruction.ORDER_MEMORY_S (240s), which is how long the spot
        /// would otherwise stay un-retryable.</summary>
        const float STALL_CLEAR_AFTER_S = 60f;

        /// <summary>Stalled Node orders dropped this round, so the fix is
        /// visible in the round summary rather than only in the moment.</summary>
        internal static int StallsCleared { get; private set; }

        /// <summary>
        /// How many times one spot may be freed for a retry before we stop.
        ///
        /// WITHOUT THIS THE FIX IS AN INFINITE LOOP. Clearing a stalled order
        /// invites the planner to ask again; if the ground is genuinely
        /// unbuildable — blocked, contested, out of reach after a cut — it
        /// stalls again, gets cleared again, and the pair spin every thirty
        /// seconds spending cash on orders that can never complete. Three
        /// attempts is enough to ride out a transient (a chain being repaired
        /// behind it, a unit standing in the way) and few enough that a truly
        /// dead spot is abandoned within two minutes.
        /// </summary>
        const int   STALL_MAX_RETRIES = 3;
        const float STALL_MEMORY_M    = 40f;
        static readonly List<Vector3> _stallSpots  = new List<Vector3>(8);
        static readonly List<int>     _stallCounts = new List<int>(8);

        /// <summary>Times we have already freed this ground, so a spot that
        /// cannot be built is given up on instead of retried forever.</summary>
        static int StallRetriesAt(Vector3 pos)
        {
            float r2 = STALL_MEMORY_M * STALL_MEMORY_M;
            for (int i = 0; i < _stallSpots.Count; i++)
            {
                float dx = _stallSpots[i].x - pos.x, dz = _stallSpots[i].z - pos.z;
                if (dx * dx + dz * dz <= r2) return _stallCounts[i];
            }
            return 0;
        }

        static void NoteStallRetry(Vector3 pos)
        {
            float r2 = STALL_MEMORY_M * STALL_MEMORY_M;
            for (int i = 0; i < _stallSpots.Count; i++)
            {
                float dx = _stallSpots[i].x - pos.x, dz = _stallSpots[i].z - pos.z;
                if (dx * dx + dz * dz <= r2) { _stallCounts[i]++; return; }
            }
            _stallSpots.Add(pos); _stallCounts.Add(1);
        }
        static readonly List<Vector3> _orderScratch = new List<Vector3>(16);

        /// <summary>How far apart two structures can be and still chain. Read
        /// per kind, since a Bio Cache reaches further than a Node.</summary>
        static float ReachOf(string name)
        {
            if (name == "Node") return EcoSimulator.NODE_REACH_M;
            return EcoSimulator.BC_REACH_M;
        }

        class Node
        {
            public string Name = "";
            public Vector3 Pos;
            public bool IsNest;
            public readonly List<int> Adj = new List<int>();
        }

        static float _lastReportAt;

        // Latest repair need, for the eco planner to act on. Stale after
        // REPAIR_TTL_S so a fixed line stops being requested.
        const float REPAIR_TTL_S = 45f;
        static Vector3 _repairFrom, _repairTo;
        static Vector3 _loopFrom, _loopTo;
        static int     _loopNodes;
        static float   _loopAt = -1f;
        static float   _repairAt = -1f;

        /// <summary>
        /// Where a Node must be rebuilt to reconnect a severed branch.
        ///
        /// Repair is not expansion and must not wait behind it: everything past
        /// the break is ALREADY decaying, so the loss is running while we
        /// deliberate. Verified against the game's own signal — NarakaCity
        /// 2026-07-31, two Nodes killed, and graph reachability and the Decay
        /// component named the same structures, (2380,935) then (2340,750).
        /// </summary>
        internal static bool TryGetRepair(out Vector3 from, out Vector3 to)
        {
            from = _repairFrom; to = _repairTo;
            return _repairAt >= 0f && Time.time - _repairAt < REPAIR_TTL_S;
        }

        internal static void ResetForNewRound()
        {
            _lastReportAt = 0f; _repairAt = -1f; _loopAt = -1f; _commitAt = -1f;
            StallsCleared = 0;
            _stallSpots.Clear(); _stallCounts.Clear();
        }

        /// <summary>
        /// The loop worth closing, if any: a short bridge that turns a long
        /// dead-end into a ring.
        ///
        /// Deliberately the LOWEST priority claim on cash. A loop earns nothing
        /// — it is insurance against a branch being cut — so it must never
        /// compete with expansion or repair. It is only worth taking when there
        /// is genuine surplus, which mid-game there is: 56,000 to 79,000 sat
        /// unspent on NarakaCity 2026-08-02 while 82 of 174 structures were
        /// single points of failure.
        /// </summary>
        internal static bool TryGetLoop(out Vector3 from, out Vector3 to, out int nodes)
        {
            from = _loopFrom; to = _loopTo; nodes = _loopNodes;
            return _loopAt >= 0f && Time.time - _loopAt < REPAIR_TTL_S;
        }

        internal static void Tick(Team team)
        {
            if (team == null) return;
            if (!(team.name ?? "").Contains("Alien")) return;
            float now = Time.time;
            if (now - _lastReportAt < REPORT_EVERY_S) return;
            _lastReportAt = now;

            try { Report(team); }
            catch (Exception ex) { MelonLogger.Warning("[NODEMGR] threw: " + ex.Message); }
        }

        /// <summary>
        /// Is this structure decaying — i.e. has the game cut it off?
        ///
        /// Read by reflection for the same reason as the Queen check: we compile
        /// against netstandard reference stubs, so Il2Cpp generic GetComponent
        /// is not reliably available here. Absence of the component, or any
        /// interop failure, reports NOT decaying — a false "fine" costs a missed
        /// repair, a false "cut" would trigger repairs on a healthy network.
        /// </summary>
        internal static bool IsDecaying(Structure st)
        {
            try
            {
                var comps = st.GetComponents(typeof(Component));
                if (comps == null) return false;
                for (int i = 0; i < comps.Length; i++)
                {
                    var c = comps[i];
                    if (c == null) continue;
                    if (c.GetType().Name != "Decay") continue;
                    var p = c.GetType().GetProperty("enabled");
                    if (p == null) return false;
                    return p.GetValue(c, null) is bool b && b;
                }
            }
            catch { }
            return false;
        }

        static bool StructureNearPos(Team team, string name, Vector3 pos, float radiusM)
        {
            try
            {
                var structs = team?.Structures;
                if (structs == null) return false;
                float r2 = radiusM * radiusM;
                for (int i = 0; i < structs.Count; i++)
                {
                    var st = structs[i];
                    if (st == null || st.ObjectInfo == null || st.IsDestroyed) continue;
                    if ((st.ObjectInfo.DisplayName ?? "") != name) continue;
                    Vector3 q = st.transform.position;
                    float dx = q.x - pos.x, dz = q.z - pos.z;
                    if (dx * dx + dz * dz <= r2) return true;
                }
            }
            catch { }
            return false;
        }

        static List<Node> BuildGraph(Team team)
        {
            var g = new List<Node>();
            var structs = team.Structures;
            if (structs == null) return g;

            for (int i = 0; i < structs.Count; i++)
            {
                var st = structs[i];
                if (st == null || st.ObjectInfo == null || st.IsDestroyed) continue;
                string n = st.ObjectInfo.DisplayName ?? "";
                if (n != "Nest" && n != "Node" && n != "Bio Cache"
                    && n != "Lesser Spawning Cyst" && n != "Quantum Cortex") continue;
                g.Add(new Node { Name = n, Pos = st.transform.position, IsNest = n == "Nest" });
            }

            // Link anything within either party's reach — the chain is mutual.
            for (int a = 0; a < g.Count; a++)
                for (int b = a + 1; b < g.Count; b++)
                {
                    float dx = g[a].Pos.x - g[b].Pos.x, dz = g[a].Pos.z - g[b].Pos.z;
                    float d2 = dx * dx + dz * dz;
                    float r = Mathf.Max(ReachOf(g[a].Name), ReachOf(g[b].Name));
                    if (d2 > r * r) continue;
                    g[a].Adj.Add(b);
                    g[b].Adj.Add(a);
                }
            return g;
        }

        /// <summary>Nodes needed to bridge a gap, 0 if already in reach.</summary>
        static int NodesToBridge(float gapM)
        {
            float reach = EcoSimulator.NODE_REACH_M;
            if (gapM <= reach) return 0;
            float hop = Mathf.Max(1f, reach - 40f);      // same drift margin the chain uses
            return Mathf.CeilToInt((gapM - reach) / hop);
        }

        /// <summary>
        /// The cheapest worthwhile LOOP: a short bridge that closes a long
        /// cycle.
        ///
        /// Looping has to happen BEFORE anything is cut, so cut vertices alone
        /// are the wrong trigger — by the time one matters the branch is already
        /// severed. And it has to be cheap: every bridging Node costs 200, so a
        /// loop is only worth building where two branches already come close
        /// (user, 2026-07-31).
        ///
        /// The measure that captures both is the ratio of NETWORK distance to
        /// PHYSICAL distance. Two structures 200m apart but 14 hops apart in the
        /// graph are on different branches that nearly touch: one or two Nodes
        /// there converts a long dead-end into a ring. Two structures 200m apart
        /// and 2 hops apart are already effectively joined and a link buys
        /// nothing.
        ///
        /// Bounded work: only pairs within MAX_BRIDGE_M are considered, the
        /// closest CANDIDATE_PAIRS of those are scored, and each score is one
        /// BFS. This runs on the game thread at the report cadence.
        /// </summary>
        /// <summary>
        /// How far apart two branches may be and still be worth bridging —
        /// DERIVED FROM THE MAP, not fixed.
        ///
        /// A constant cannot be right here. The grid already measures each map's
        /// playable extent and it varies threefold: BlackIsle 2000m, Citadel and
        /// CrystalChasm 3000m, CrimsonPeak 4096m, Badlands and GreatErg 6000m.
        /// Branches radiate from the Nest, so on a big map neighbouring branches
        /// are simply further apart — which is why 600m only ever found pairs
        /// near the opener base, and why the user reported GreatErg "requires
        /// bigger loops" while small maps need nothing of the sort.
        ///
        /// Expressed as a fraction of extent, that becomes ~440m on BlackIsle
        /// and ~1320m on GreatErg without either being written down. The
        /// fraction is dimensionless and the only judgement left; everything
        /// with a unit comes from the map.
        /// </summary>
        const float BRIDGE_FRACTION_OF_MAP = 0.22f;

        static float MaxBridgeM
        {
            get
            {
                float extent = Perception.MapLayers.GridWorld.Width
                             * Perception.MapLayers.GridWorld.CellSize;
                // Never below a couple of Node hops, or nothing is bridgeable.
                return Mathf.Max(EcoSimulator.NODE_REACH_M * 3f, extent * BRIDGE_FRACTION_OF_MAP);
            }
        }
        /// <summary>How many pairs to look at before giving up, and how many
        /// worthwhile ones to find before settling. The first bounds the work,
        /// the second ends the search early when the answer is already clear.</summary>
        const int   MAX_PAIRS_EXAMINED = 250;
        const int   ENOUGH_CANDIDATES  = 12;

        /// <summary>How far past the base bridge range a well-populated branch
        /// may reach for help, and how many protected structures buy one extra
        /// multiple of it.</summary>
        const float MAX_BRIDGE_STRETCH  = 3f;
        const int   PROTECT_PER_STRETCH = 30;

        /// <summary>
        /// How many loops may be under construction at once.
        ///
        /// One at a time was the fix for loops that never finished, and it
        /// worked — but a map with several long dead-ends then closes them one
        /// after another, minutes apart. GreatErg 2026-08-03: southern branches
        /// and north-eastern branches both wanted closing and only one was ever
        /// in progress. Two lets independent rings proceed together while still
        /// forbidding the scatter that left seven nodes across four unfinished
        /// bridges.
        /// </summary>
        /// <summary>Whether to propose loop closures at all. Off: see the note
        /// at the bottom of the loop scan. Repair is unaffected.</summary>
        // LOOPS ARE BACK, FOR MAJOR BRANCHES ONLY. DrMuck's replays (2026-09-07):
        // Crimson Peak F5-F6 and C2-B1, Monument Valley E5, Naraka E5 — whole
        // branches lost for want of one bridge. The earlier objection stands for
        // spurs near spawn, so a loop must protect at least MIN_PROTECT
        // structures on its weaker side and cost at most MAX_LOOP_NODES nodes.
        const bool LOOPS_ENABLED   = true;
        const int  MIN_PROTECT     = 8;
        const int  MAX_LOOP_NODES  = 4;

        const int MAX_CONCURRENT_LOOPS = 2;
        const int   MIN_HOPS_SAVED  = 6;

        static void ReportLoopCandidates(List<Node> g, bool[] connected, System.Text.StringBuilder sb)
        {
            float maxBridge = MaxBridgeM;
            var subtree = SubtreeSizes(g);
            // A branch with a lot behind it justifies a longer reach for help.
            // Scanning to the widest possible bridge and letting the score
            // decide is cheaper than guessing one radius for every situation.
            float scanBridge = Mathf.Min(maxBridge * MAX_BRIDGE_STRETCH,
                                         Perception.MapLayers.GridWorld.Width
                                         * Perception.MapLayers.GridWorld.CellSize);
            var pairs = new List<(float gap, int a, int b)>();
            for (int a = 0; a < g.Count; a++)
            {
                if (g[a].Name != "Node" && !g[a].IsNest && g[a].Name != "Bio Cache") continue;
                for (int b = a + 1; b < g.Count; b++)
                {
                    if (g[b].Name != "Node" && !g[b].IsNest && g[b].Name != "Bio Cache") continue;
                    if (g[a].Adj.Contains(b)) continue;              // already joined
                    float dx = g[a].Pos.x - g[b].Pos.x, dz = g[a].Pos.z - g[b].Pos.z;
                    float gap = Mathf.Sqrt(dx * dx + dz * dz);
                    if (gap > scanBridge) continue;
                    pairs.Add((gap, a, b));
                }
            }
            if (pairs.Count == 0) return;
            pairs.Sort((x, y) => x.gap.CompareTo(y.gap));

            // REPAIR AND LOOP ARE DIFFERENT JOBS.
            //
            // A pair straddling the connected boundary is not a loop — bridging
            // it RECONNECTS something already dying, and its hop distance is
            // infinite, which made it score as infinitely valuable and win every
            // time. NarakaCity 2026-07-31, after two Nodes were killed:
            // "LOOP (2475,1165)-(2380,935) closes=2147483647hops".
            //
            // Split them. Repair is urgent and picked by CHEAPEST bridge back to
            // the live network; looping is preventive and picked by cycle length
            // per Node spent.
            float bestScore = 0f; int bi = -1, bhops = 0, bnodes = 0; float bgap = 0f;
            int ri = -1, rnodes = int.MaxValue; float rgap = 0f;

            // DO NOT STOP AT THE CLOSEST PAIRS.
            //
            // This used to score only the 24 physically closest pairs, and in a
            // dense base those are already NEIGHBOURS — one or two hops apart —
            // so every one failed the hop test and the search ended before
            // reaching anything worth bridging. NarakaCity 2026-08-02 reported
            // 82 cut vertices among 174 structures, a pure tree with no cycles
            // anywhere, and still proposed no loop. The filter was removing
            // exactly what it was looking for.
            //
            // Now it walks outward from the closest pair and stops once it has
            // found ENOUGH VALID candidates, not once it has looked at enough
            // pairs. BFS results are cached per source, so the extra work is a
            // few dozen searches rather than one per pair.
            var bfsCache = new Dictionary<int, int[]>();
            int examined = 0, valid = 0;

            // FINISH THE LOOP WE STARTED.
            //
            // The target was re-chosen from the current best every pass, so a
            // four-node bridge got one node, then the next pass preferred a
            // different pair and started another. NarakaCity 2026-08-02: seven
            // Nodes laid across four different pairs, not one ring closed, and
            // singlePointsOfFailure ROSE from 97 to 105 — every unfinished spur
            // is itself a new cut vertex, so half-built loops make the network
            // more fragile while spending money.
            //
            // A commitment is held until the ring actually closes, which is
            // measurable: once the bridge is in, the two ends are a short hop
            // apart instead of a long way round.
            if (TryResumeCommittedLoop(g, connected, bfsCache, sb)) return;

            for (int i = 0; i < pairs.Count && examined < MAX_PAIRS_EXAMINED && valid < ENOUGH_CANDIDATES; i++)
            {
                examined++;
                int a = pairs[i].a, b = pairs[i].b;
                int nodes = NodesToBridge(pairs[i].gap);

                if (connected[a] != connected[b])
                {
                    if (nodes < rnodes) { rnodes = nodes; ri = i; rgap = pairs[i].gap; }
                    continue;
                }
                if (!connected[a]) continue;                          // both already lost

                int hops = HopDistanceCached(g, a, b, bfsCache);
                if (hops >= MIN_HOPS_SAVED) valid++;
                if (hops >= int.MaxValue) continue;                   // separate components
                if (hops < MIN_HOPS_SAVED) continue;                  // already well joined

                // WHAT THE LOOP PROTECTS, NOT JUST HOW FAR ROUND IT IS.
                //
                // Both ends gain an alternative route, so the value is what
                // hangs off them. A long branch carrying most of the map now
                // outbids a cheap spur near the base, and earns a longer bridge
                // in proportion — the user's rule from 2026-08-03: "an expansion
                // line that has only one connection to nest within a long build
                // distance would need to find another branch".
                // WHAT WOULD ACTUALLY BE STRANDED IS THE SMALLER SIDE.
                //
                // This summed both subtrees, which is wrong twice over. Cutting
                // the single path between a and b does not orphan everything on
                // both sides — the Nest keeps one side and only the other is
                // lost, so the loop is worth what the SMALLER side holds.
                //
                // And because subtree counts what hangs below a node, the sum
                // is near-total for any pair close to the Nest and near-zero
                // out on the branches. That fed the distance allowance below,
                // so long bridges were permitted only near spawn and the
                // allowance collapsed to maxBridge everywhere else — which is
                // why loops were only ever CONSIDERED around the base. User
                // 2026-08-03 listed three wanted interconnects at 588m, 730m
                // and 776m, all mid-branch, none of which could be proposed.
                int protect = Mathf.Min(subtree[a], subtree[b]);
                if (protect < MIN_PROTECT || nodes > MAX_LOOP_NODES) continue;   // a spur, or too far round

                // No distance allowance any more. A bridge already pays for its
                // length in the score below, which divides protection by the
                // nodes the bridge costs, so a long one has to protect
                // proportionally more to win. A separate distance ceiling on
                // top of that just reimposed the hardcoded limit the score was
                // meant to replace. scanBridge remains as the sanity bound on
                // what is even examined.
                if (pairs[i].gap > scanBridge) continue;

                // Protection per Node spent, with hops kept as a floor test so a
                // pair that is already well connected is still ignored.
                float score = (hops * protect) / (float)Mathf.Max(1, nodes);
                if (score > bestScore)
                { bestScore = score; bi = i; bhops = hops; bnodes = nodes; bgap = pairs[i].gap; }
            }

            if (ri >= 0)
            {
                var C = g[pairs[ri].a].Pos; var D = g[pairs[ri].b].Pos;
                bool aLive = connected[pairs[ri].a];
                var live = aLive ? C : D; var lost = aLive ? D : C;
                _repairFrom = live; _repairTo = lost; _repairAt = Time.time;
                sb.Append(" | REPAIR from (").Append(live.x.ToString("F0")).Append(',')
                  .Append(live.z.ToString("F0")).Append(") to (").Append(lost.x.ToString("F0"))
                  .Append(',').Append(lost.z.ToString("F0")).Append(')')
                  .Append(" gap=").Append((int)rgap).Append("m nodes=").Append(rnodes);
            }
            // LOOPING IS OFF.
            //
            // It never earned its place. Across this week's rounds it produced
            // only single-node closures around spawn while singlePointsOfFailure
            // sat at 102, it was serialised to one commitment at a time, and no
            // ring was ever observed closing. Early loops are the worst of it:
            // nodes spent on redundancy a two-Bio-Cache base has no use for,
            // competing for cash with the expansion that would actually earn.
            //
            // REPAIR stays live — a branch cut off from the Nest is decaying and
            // must be reconnected, which is a different question from adding a
            // second route to something already connected.
            //
            // Kept as a switch rather than deleted: redundancy between MAJOR
            // BRANCHES is still wanted, but as part of the trunk/rib model where
            // there are named trunks to connect, not as an all-pairs scan over
            // every node on the map.
            if (bi < 0 || !LOOPS_ENABLED) { _loopAt = -1f; return; }

            var A = g[pairs[bi].a].Pos; var B = g[pairs[bi].b].Pos;
            _loopFrom = A; _loopTo = B; _loopNodes = bnodes; _loopAt = Time.time;
            _commitA = A; _commitB = B; _commitAt = Time.time;
            sb.Append(" | LOOP (").Append(A.x.ToString("F0")).Append(',').Append(A.z.ToString("F0"))
              .Append(")-(").Append(B.x.ToString("F0")).Append(',').Append(B.z.ToString("F0")).Append(')')
              .Append(" gap=").Append((int)bgap).Append("m nodes=").Append(bnodes)
              .Append(" closes=").Append(bhops).Append("hops");
        }

        /// <summary>
        /// Keep serving the loop already under construction.
        ///
        /// Returns true when a commitment is live and still unclosed, in which
        /// case it is republished unchanged and no new search runs. Released
        /// when the two ends come within MIN_HOPS_SAVED of each other — that IS
        /// the ring closing — or when the commitment ages out, so a bridge that
        /// cannot be built does not block every other loop for the round.
        /// </summary>
        /// <summary>
        /// How long one loop commitment holds the single loop slot.
        ///
        /// Was 240s, which is what serialised loop building: one commitment at
        /// a time for up to four minutes means about nine loops in a 36-minute
        /// round, and nine was exactly what the 2026-08-03 round produced
        /// against singlePointsOfFailure=102. Every other wanted bridge simply
        /// never got a turn — the user's pairs at 450m and 519m are 2-3 nodes
        /// of work that lost to a queue, not to a score.
        ///
        /// The TTL exists so a bridge that CANNOT be built does not block the
        /// slot for the round; it was never meant to be how long a normal
        /// bridge takes. A typical closure is 2-4 nodes: at CHAIN_HOPS_PER_CYCLE
        /// per 8s cycle that is ordered within two cycles and standing well
        /// inside a minute. 75s leaves generous headroom over that while
        /// cycling the slot roughly three times faster.
        ///
        /// This is a stopgap. The real fix is several concurrent commitments —
        /// MAX_CONCURRENT_LOOPS has been declared and unwired for weeks — and
        /// that belongs to the rings layer of the branch planner, not to
        /// another constant here.
        /// </summary>
        const float LOOP_COMMIT_TTL_S = 75f;
        static Vector3 _commitA, _commitB;
        static float   _commitAt = -1f;

        static bool TryResumeCommittedLoop(List<Node> g, bool[] connected,
                                           Dictionary<int, int[]> cache,
                                           System.Text.StringBuilder sb)
        {
            if (_commitAt < 0f) return false;
            if (Time.time - _commitAt > LOOP_COMMIT_TTL_S)
            {
                MelonLogger.Msg("[NODEMGR] loop commitment aged out — searching again");
                _commitAt = -1f; return false;
            }

            int a = NearestGraphIndex(g, _commitA), b = NearestGraphIndex(g, _commitB);
            if (a < 0 || b < 0 || !connected[a] || !connected[b]) { _commitAt = -1f; return false; }

            int hops = HopDistanceCached(g, a, b, cache);
            if (hops < MIN_HOPS_SAVED)
            {
                MelonLogger.Msg($"[NODEMGR] loop ({_commitA.x:F0},{_commitA.z:F0})-" +
                                $"({_commitB.x:F0},{_commitB.z:F0}) CLOSED — now {hops} hops apart");
                _commitAt = -1f;
                return false;
            }

            float gap = Vector3.Distance(_commitA, _commitB);
            _loopFrom = _commitA; _loopTo = _commitB;
            _loopNodes = NodesToBridge(gap); _loopAt = Time.time;
            sb.Append(" | LOOP(committed) (").Append(_commitA.x.ToString("F0")).Append(',')
              .Append(_commitA.z.ToString("F0")).Append(")-(").Append(_commitB.x.ToString("F0"))
              .Append(',').Append(_commitB.z.ToString("F0")).Append(')')
              .Append(" still=").Append(hops).Append("hops");
            return true;
        }

        /// <summary>Index of the graph vertex standing at this position.</summary>
        static int NearestGraphIndex(List<Node> g, Vector3 pos)
        {
            int best = -1; float bd = 60f * 60f;
            for (int i = 0; i < g.Count; i++)
            {
                float dx = g[i].Pos.x - pos.x, dz = g[i].Pos.z - pos.z;
                float d = dx * dx + dz * dz;
                if (d < bd) { bd = d; best = i; }
            }
            return best;
        }

        /// <summary>
        /// How many structures hang off each vertex, counted away from the Nest.
        ///
        /// This is what a loop is actually worth. A bridge that saves a spur of
        /// three structures and one that saves a forty-structure branch scored
        /// the same under hops-per-node, so the giant middle branch on
        /// NarakaCity 2026-08-03 — one connection to the Nest, everything behind
        /// it — never outbid a cheap cosmetic loop near the base.
        ///
        /// Computed from the BFS tree rooted at the Nest: process vertices in
        /// reverse discovery order and accumulate into the parent.
        /// </summary>
        static int[] SubtreeSizes(List<Node> g)
        {
            var size = new int[g.Count];
            var parent = new int[g.Count];
            var order = new List<int>(g.Count);
            for (int i = 0; i < g.Count; i++) { parent[i] = -1; size[i] = 1; }

            var q = new Queue<int>();
            var seen = new bool[g.Count];
            for (int i = 0; i < g.Count; i++)
                if (g[i].IsNest && !seen[i]) { seen[i] = true; q.Enqueue(i); }

            while (q.Count > 0)
            {
                int v = q.Dequeue();
                order.Add(v);
                var adj = g[v].Adj;
                for (int k = 0; k < adj.Count; k++)
                    if (!seen[adj[k]]) { seen[adj[k]] = true; parent[adj[k]] = v; q.Enqueue(adj[k]); }
            }
            for (int i = order.Count - 1; i >= 0; i--)
            {
                int v = order[i];
                if (parent[v] >= 0) size[parent[v]] += size[v];
            }
            return size;
        }

        /// <summary>Hops from one vertex to another, reusing the BFS from that
        /// source across every pair that shares it.</summary>
        static int HopDistanceCached(List<Node> g, int from, int to, Dictionary<int, int[]> cache)
        {
            if (!cache.TryGetValue(from, out var dist))
            {
                dist = new int[g.Count];
                for (int i = 0; i < g.Count; i++) dist[i] = -1;
                var q = new Queue<int>();
                dist[from] = 0; q.Enqueue(from);
                while (q.Count > 0)
                {
                    int v = q.Dequeue();
                    var adj = g[v].Adj;
                    for (int k = 0; k < adj.Count; k++)
                        if (dist[adj[k]] < 0) { dist[adj[k]] = dist[v] + 1; q.Enqueue(adj[k]); }
                }
                cache[from] = dist;
            }
            return dist[to] < 0 ? int.MaxValue : dist[to];
        }

        /// <summary>Hops between two vertices, -1 if unreachable.</summary>
        static int HopDistance(List<Node> g, int from, int to)
        {
            var dist = new int[g.Count];
            for (int i = 0; i < g.Count; i++) dist[i] = -1;
            var q = new Queue<int>();
            dist[from] = 0; q.Enqueue(from);
            while (q.Count > 0)
            {
                int v = q.Dequeue();
                if (v == to) return dist[v];
                var adj = g[v].Adj;
                for (int k = 0; k < adj.Count; k++)
                    if (dist[adj[k]] < 0) { dist[adj[k]] = dist[v] + 1; q.Enqueue(adj[k]); }
            }
            return int.MaxValue;                                     // different components
        }

        static void Report(Team team)
        {
            var g = BuildGraph(team);
            if (g.Count == 0) return;

            // ---- reachable from any Nest ----
            var seen = new bool[g.Count];
            var stack = new Stack<int>();
            int nests = 0;
            for (int i = 0; i < g.Count; i++)
                if (g[i].IsNest) { nests++; if (!seen[i]) { seen[i] = true; stack.Push(i); } }

            while (stack.Count > 0)
            {
                int v = stack.Pop();
                var adj = g[v].Adj;
                for (int k = 0; k < adj.Count; k++)
                    if (!seen[adj[k]]) { seen[adj[k]] = true; stack.Push(adj[k]); }
            }

            // THE GAME ALREADY SAYS SO.
            //
            // A structure cut off from a Nest has its Decay component switched
            // on — that is the authoritative statement, and it needs no graph.
            // Read it directly and report it beside our computed orphan count,
            // so the two can be compared: if they agree, detection can rely on
            // Decay alone and the graph is only needed for cut vertices.
            // User 2026-07-31: use it event-based to detect cut node lines.
            int decaying = 0;
            Vector3 firstDecaying = Vector3.zero;
            try
            {
                var structs = team.Structures;
                if (structs != null)
                    for (int i = 0; i < structs.Count; i++)
                    {
                        var st = structs[i];
                        if (st == null || st.IsDestroyed) continue;
                        if (!IsDecaying(st)) continue;
                        if (decaying == 0) firstDecaying = st.transform.position;
                        decaying++;
                    }
            }
            catch (Exception ex) { MelonLogger.Warning("[NODEMGR] decay read threw: " + ex.Message); }

            // ORDERED BUT NEVER BUILT.
            //
            // The graph is made of team.Structures, which holds only COMPLETED
            // structures — so a Node that was ordered and never started
            // building is invisible to it, and invisible to the Decay check
            // too, since there is no structure to carry a Decay component.
            // That is exactly the case seen on MonumentValley 2026-08-01: nodes
            // that never began because the chain ahead of them was cut before
            // their build progress started.
            //
            // The accepted Construct is the evidence. If an order is older than
            // the build should have taken and nothing stands there, it stalled.
            int stalled = 0, cleared = 0;
            Vector3 firstStalled = Vector3.zero;
            try
            {
                float build   = Perception.BuildTimeline.MeasuredTotalS("Node");
                float overdue = build + STALL_GRACE_S;
                _orderScratch.Clear();
                Faction.AlienConstruction.CollectOrdersOlderThan("Node", overdue, _orderScratch);
                for (int i = 0; i < _orderScratch.Count; i++)
                {
                    if (StructureNearPos(team, "Node", _orderScratch[i], STALL_RADIUS_M)) continue;
                    if (stalled == 0) firstStalled = _orderScratch[i];
                    stalled++;
                }

                // AND NOW DO SOMETHING ABOUT IT.
                //
                // Counting stalls was all this ever did: the number went into
                // the log line and nothing acted on it, so a Node ordered into
                // a chain that was cut before its build started stayed un-built
                // and — because WasOrderedNear answers yes for ORDER_MEMORY_S —
                // un-retryable. In the 2026-08-13 co-op round `stalled` climbed
                // 0 -> 9 over ten minutes while every REPAIR line went to a
                // different, orphaned node, and DrMuck stood on one of them
                // waiting for a repair that was never coming.
                //
                // FORGETTING IS THE ACTION, not re-ordering. Placement belongs
                // to whichever planner wanted the node; clearing the memory lets
                // it ask again on its next pass, or decide it no longer wants to,
                // and both of those are right. Issuing a replacement from here
                // would make this a second owner of a decision that already has
                // one.
                //
                // A LONGER FUSE THAN THE REPORT USES. Reporting at build+20s is
                // meant to be twitchy — it is a diagnostic. Clearing at
                // build+STALL_CLEAR_AFTER_S makes sure we are not dropping the
                // order for something that is merely slow and about to finish.
                float clearAfter = build + STALL_CLEAR_AFTER_S;
                _orderScratch.Clear();
                Faction.AlienConstruction.CollectOrdersOlderThan("Node", clearAfter, _orderScratch);
                for (int i = 0; i < _orderScratch.Count; i++)
                {
                    var at = _orderScratch[i];
                    if (StructureNearPos(team, "Node", at, STALL_RADIUS_M)) continue;

                    int tries = StallRetriesAt(at);
                    if (tries >= STALL_MAX_RETRIES)
                    {
                        // Left ORDERED on purpose. The order expiring naturally
                        // at ORDER_MEMORY_S is the backstop; re-freeing it here
                        // would restart the loop this cap exists to stop.
                        if (tries == STALL_MAX_RETRIES)
                        {
                            NoteStallRetry(at);   // tick past, so this logs once
                            MelonLogger.Warning($"[NODEMGR] Node at ({at.x:F0},{at.z:F0}) has " +
                                                $"stalled {STALL_MAX_RETRIES} times — giving up on " +
                                                $"that ground rather than ordering into it again");
                        }
                        continue;
                    }

                    int n = Faction.AlienConstruction.ForgetOrdersNear(
                                "Node", at, STALL_RADIUS_M);
                    if (n > 0)
                    {
                        cleared += n;
                        NoteStallRetry(at);
                        MelonLogger.Msg($"[NODEMGR] stalled Node at ({at.x:F0},{at.z:F0}) never " +
                                        $"started after {clearAfter:F0}s — order forgotten " +
                                        $"(attempt {tries + 1}/{STALL_MAX_RETRIES}), the spot is " +
                                        $"open to be planned again");
                    }
                }
                StallsCleared += cleared;
            }
            catch (Exception ex) { MelonLogger.Warning("[NODEMGR] stall check threw: " + ex.Message); }

            int orphans = 0;
            Vector3 firstOrphan = Vector3.zero;
            for (int i = 0; i < g.Count; i++)
                if (!seen[i]) { if (orphans == 0) firstOrphan = g[i].Pos; orphans++; }

            // ---- cut vertices: lose this and something behind it is severed ----
            var cuts = ArticulationPoints(g);

            var sb = new System.Text.StringBuilder();
            sb.Append("[NODEMGR] structures=").Append(g.Count)
              .Append(" nests=").Append(nests)
              .Append(" orphaned=").Append(orphans)
              .Append(" decaying=").Append(decaying)
              .Append(" stalled=").Append(stalled)
              .Append(StallsCleared > 0 ? " clearedThisRound=" + StallsCleared : "")
              .Append(" singlePointsOfFailure=").Append(cuts.Count);
            if (orphans > 0)
                sb.Append(" firstOrphan=(").Append(firstOrphan.x.ToString("F0")).Append(',')
                  .Append(firstOrphan.z.ToString("F0")).Append(')');
            if (stalled > 0)
                sb.Append(" firstStalled=(").Append(firstStalled.x.ToString("F0")).Append(',')
                  .Append(firstStalled.z.ToString("F0")).Append(')');
            if (decaying > 0)
                sb.Append(" firstDecaying=(").Append(firstDecaying.x.ToString("F0")).Append(',')
                  .Append(firstDecaying.z.ToString("F0")).Append(')');
            // The most valuable loop is the cut vertex carrying the most behind
            // it; for now report a few so the pattern is visible.
            int shown = 0;
            for (int i = 0; i < cuts.Count && shown < 3; i++, shown++)
                sb.Append(" cut@(").Append(g[cuts[i]].Pos.x.ToString("F0")).Append(',')
                  .Append(g[cuts[i]].Pos.z.ToString("F0")).Append(')');
            ReportLoopCandidates(g, seen, sb);
            MelonLogger.Msg(sb.ToString());
        }

        /// <summary>
        /// Hopcroft-Tarjan cut vertices. A vertex is a cut vertex when some
        /// child subtree has no back-edge above it — i.e. nothing else holds
        /// that subtree on. Iterative rather than recursive: a late-game network
        /// runs to hundreds of structures and this is on the game thread.
        /// </summary>
        static List<int> ArticulationPoints(List<Node> g)
        {
            int n = g.Count;
            var disc = new int[n];
            var low = new int[n];
            var parent = new int[n];
            var isCut = new bool[n];
            for (int i = 0; i < n; i++) { disc[i] = -1; parent[i] = -1; }

            int timer = 0;
            var it = new int[n];                 // adjacency cursor per vertex
            var stack = new Stack<int>();

            for (int s = 0; s < n; s++)
            {
                if (disc[s] != -1) continue;
                int rootChildren = 0;
                disc[s] = low[s] = timer++;
                stack.Push(s);

                while (stack.Count > 0)
                {
                    int v = stack.Peek();
                    if (it[v] < g[v].Adj.Count)
                    {
                        int to = g[v].Adj[it[v]++];
                        if (to == parent[v]) continue;
                        if (disc[to] != -1)
                        {
                            if (disc[to] < low[v]) low[v] = disc[to];   // back edge
                        }
                        else
                        {
                            parent[to] = v;
                            disc[to] = low[to] = timer++;
                            stack.Push(to);
                            if (v == s) rootChildren++;
                        }
                    }
                    else
                    {
                        stack.Pop();
                        int p = parent[v];
                        if (p != -1)
                        {
                            if (low[v] < low[p]) low[p] = low[v];
                            // Nothing behind v reaches above p, so p holds it on
                            // alone. Root handled separately below.
                            if (low[v] >= disc[p] && p != s) isCut[p] = true;
                        }
                    }
                }
                if (rootChildren > 1) isCut[s] = true;   // root splits two branches
            }

            var outList = new List<int>();
            for (int i = 0; i < n; i++) if (isCut[i]) outList.Add(i);
            return outList;
        }
    }
}
