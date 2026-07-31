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
    /// SHADOW FIRST. This reports and does not act. The two things it finds are
    /// what repair and looping will be built on:
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

        internal static void ResetForNewRound() { _lastReportAt = 0f; _repairAt = -1f; }

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
        const float MAX_BRIDGE_M   = 600f;
        const int   CANDIDATE_PAIRS = 24;
        const int   MIN_HOPS_SAVED  = 6;

        static void ReportLoopCandidates(List<Node> g, bool[] connected, System.Text.StringBuilder sb)
        {
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
                    if (gap > MAX_BRIDGE_M) continue;
                    pairs.Add((gap, a, b));
                }
            }
            if (pairs.Count == 0) return;
            pairs.Sort((x, y) => x.gap.CompareTo(y.gap));
            if (pairs.Count > CANDIDATE_PAIRS) pairs.RemoveRange(CANDIDATE_PAIRS, pairs.Count - CANDIDATE_PAIRS);

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

            for (int i = 0; i < pairs.Count; i++)
            {
                int a = pairs[i].a, b = pairs[i].b;
                int nodes = NodesToBridge(pairs[i].gap);

                if (connected[a] != connected[b])
                {
                    if (nodes < rnodes) { rnodes = nodes; ri = i; rgap = pairs[i].gap; }
                    continue;
                }
                if (!connected[a]) continue;                          // both already lost

                int hops = HopDistance(g, a, b);
                if (hops >= int.MaxValue) continue;                   // separate components
                if (hops < MIN_HOPS_SAVED) continue;                  // already well joined
                float score = hops / (float)Mathf.Max(1, nodes);      // cycle length per Node
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
            if (bi < 0) return;

            var A = g[pairs[bi].a].Pos; var B = g[pairs[bi].b].Pos;
            sb.Append(" | LOOP (").Append(A.x.ToString("F0")).Append(',').Append(A.z.ToString("F0"))
              .Append(")-(").Append(B.x.ToString("F0")).Append(',').Append(B.z.ToString("F0")).Append(')')
              .Append(" gap=").Append((int)bgap).Append("m nodes=").Append(bnodes)
              .Append(" closes=").Append(bhops).Append("hops");
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
              .Append(" singlePointsOfFailure=").Append(cuts.Count);
            if (orphans > 0)
                sb.Append(" firstOrphan=(").Append(firstOrphan.x.ToString("F0")).Append(',')
                  .Append(firstOrphan.z.ToString("F0")).Append(')');
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
