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

        internal static void ResetForNewRound() { _lastReportAt = 0f; }

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
              .Append(" singlePointsOfFailure=").Append(cuts.Count);
            if (orphans > 0)
                sb.Append(" firstOrphan=(").Append(firstOrphan.x.ToString("F0")).Append(',')
                  .Append(firstOrphan.z.ToString("F0")).Append(')');
            // The most valuable loop is the cut vertex carrying the most behind
            // it; for now report a few so the pattern is visible.
            int shown = 0;
            for (int i = 0; i < cuts.Count && shown < 3; i++, shown++)
                sb.Append(" cut@(").Append(g[cuts[i]].Pos.x.ToString("F0")).Append(',')
                  .Append(g[cuts[i]].Pos.z.ToString("F0")).Append(')');
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
