using System;
using System.Collections.Generic;
using MelonLoader;
using Pathfinding;
using Silica.AI;
using UnityEngine;

namespace Si_RTS_AI.Perception
{
    /// <summary>
    /// THE GAME'S OWN GRAPH SAYS WHAT CAN WALK WHERE. Crimson Peak, 2026-09-07
    /// 13:02: 67 Behemoths spawned from cysts on the Nest plateau received 593
    /// attack orders on the Sol HQ and never got further than 320 m from their
    /// cyst; a 324k army stood at home for twenty minutes and the round timed
    /// out. Thirteen of them, and 36 smaller units, never left one cyst at all.
    /// The earlier win on the same map had its cysts south of the ridge and
    /// every Behemoth walked 900 to 1400 m. Units path on the A* graphs the map
    /// ships with, one per agent size, and our own structures cut them. Two
    /// points are mutually reachable for a unit when the nearest nodes on that
    /// unit's graph share an area. That is the check the game itself makes for
    /// placements; it costs two nearest-node lookups and is cached.
    /// </summary>
    internal static class Reach
    {
        const float SEARCH_M = 150f;
        const float CACHE_S  = 30f;
        const float MASKS_S  = 10f;

        static readonly Dictionary<long, (bool ok, float at)> _cache = new Dictionary<long, (bool, float)>();
        static readonly List<GraphMask> _masks = new List<GraphMask>();
        static readonly List<string> _maskNames = new List<string>();
        static float _masksAt = -999f;
        static int _warned;
        internal static int Refusals;

        internal static void ResetForNewRound()
        {
            _cache.Clear(); _masks.Clear(); _maskNames.Clear(); _masksAt = -999f; _warned = 0; Refusals = 0;
        }

        static bool TryMask(Unit u, out GraphMask mask)
        {
            mask = default;
            try
            {
                var agent = u.AIAgent;
                if (agent == null) return false;
                var pf = agent.AgentPathfinding;
                if (pf == null || pf.PathfindingSeeker == null) return false;
                mask = pf.PathfindingSeeker.graphMask;
                return true;
            }
            catch { return false; }
        }

        /// <summary>True unless the game's graph says this unit cannot walk there. Unknown means true.</summary>
        internal static bool CanReach(Unit u, Vector3 to)
        {
            if (u == null) return true;
            try { if (u.IsFlying) return true; } catch { }
            if (!TryMask(u, out var mask)) return true;
            Vector3 from; try { from = u.transform.position; } catch { return true; }
            return CanReach(mask, from, to);
        }

        internal static bool CanReach(GraphMask mask, Vector3 from, Vector3 to)
        {
            try
            {
                if (AstarPath.active == null) return true;
                long key = Key(mask, from, to);
                float now = Time.time;
                if (_cache.TryGetValue(key, out var c) && now - c.at < CACHE_S) return c.ok;
                // ONE GRAPH AT A TIME, WITH ROOM. GameAI.GetNearestNode returns
                // nothing unless the point sits within its distance tolerance of
                // the node (2 m by default) and its height tolerance (20 m); a base
                // centre or a unit a few metres off the mesh then reads as
                // unreachable, and on Naraka (14:29) every site and 56,000 unit
                // checks did. Area numbers are also per graph, so a mask spanning
                // two graphs is checked graph by graph: reachable on any one
                // graph is reachable; no node on any graph is unknown, not no.
                var graphs = AstarPath.active.data?.graphs;
                bool anyNode = false, ok = false;
                if (graphs != null)
                {
                    for (int g = 0; g < graphs.Length && !ok; g++)
                    {
                        var graph = graphs[g];
                        if (graph == null) continue;
                        int gi = (int)graph.graphIndex;
                        if (gi < 0 || gi > 30 || ((mask.value >> gi) & 1) == 0) continue;
                        // WALKABLE NODES ONLY. The game's own nearest-node helper
                        // does not constrain walkability, so a structure position
                        // (every candidate site) or a unit standing among nodes
                        // resolved to the unwalkable node under the building, whose
                        // area is nobody's: Naraka 15:26 still refused 9 of 9 sites.
                        var gm = GraphMask.FromGraphIndex(graph.graphIndex);
                        var a = NearestWalkable(from, gm);
                        if (a == null) continue;
                        var b = NearestWalkable(to, gm);
                        if (b == null) continue;
                        anyNode = true;
                        if (a.Area == b.Area) ok = true;
                    }
                }
                if (!anyNode) ok = true;
                if (_cache.Count > 4000) _cache.Clear();
                _cache[key] = (ok, now);
                return ok;
            }
            catch (Exception ex)
            {
                if (_warned++ < 3) MelonLogger.Warning("[REACH] query threw: " + ex.Message);
                return true;
            }
        }

        /// <summary>The distinct graph masks of the team's living ground units, refreshed every ten seconds.</summary>
        internal static List<GraphMask> GroundMasks(Team team)
        {
            float now = Time.time;
            if (now - _masksAt < MASKS_S) return _masks;
            _masksAt = now;
            _masks.Clear(); _maskNames.Clear();
            try
            {
                var units = team.Units;
                if (units == null) return _masks;
                for (int i = 0; i < units.Count; i++)
                {
                    var u = units[i];
                    if (u == null || u.ObjectInfo == null || u.IsDestroyed) continue;
                    string n = u.ObjectInfo.DisplayName ?? "";
                    if (n == "Shrimp" || n == "Queen") continue;
                    try { if (u.IsFlying) continue; } catch { }
                    if (!TryMask(u, out var m)) continue;
                    bool seen = false;
                    for (int k = 0; k < _masks.Count; k++) if (_masks[k].Equals(m)) { seen = true; break; }
                    if (!seen) { _masks.Add(m); _maskNames.Add(n); }
                }
            }
            catch { }
            return _masks;
        }

        /// <summary>True when every ground unit type the team fields can walk from one point to the other.</summary>
        internal static bool AllGroundCanReach(Team team, Vector3 from, Vector3 to, out string why)
        {
            why = "";
            var masks = GroundMasks(team);
            for (int k = 0; k < masks.Count; k++)
                if (!CanReach(masks[k], from, to)) { why = k < _maskNames.Count ? _maskNames[k] : "?"; return false; }
            return true;
        }

        static GraphNode NearestWalkable(Vector3 pos, GraphMask gm)
        {
            var constraint = new NearestNodeConstraint
            {
                graphMask = gm,
                area = -1,
                tags = -1,
                maxDistance = SEARCH_M,
                walkable = NearestNodeConstraint.WalkabilityConstraint.Walkable,
            };
            var nn = AstarPath.active.GetNearest(pos, constraint);
            var node = nn.node;
            if (node == null || !node.Walkable) return null;
            return node;
        }

        static long Key(GraphMask mask, Vector3 from, Vector3 to)
        {
            int m = mask.GetHashCode();
            int fx = Mathf.RoundToInt(from.x / 40f), fz = Mathf.RoundToInt(from.z / 40f);
            int tx = Mathf.RoundToInt(to.x / 40f),   tz = Mathf.RoundToInt(to.z / 40f);
            unchecked
            {
                long h = m;
                h = h * 1000003L + fx; h = h * 1000003L + fz; h = h * 1000003L + tx; h = h * 1000003L + tz;
                return h;
            }
        }
    }
}
