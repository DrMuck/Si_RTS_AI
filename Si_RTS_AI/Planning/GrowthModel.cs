using MelonLoader;
using System.Collections.Generic;
using UnityEngine;

namespace Si_RTS_AI.Planning
{
    /// <summary>
    /// Where the base SHOULD grow, as a space-colonization problem.
    ///
    /// This is the algorithm procedural tree generators use (Runions et al.,
    /// "Modeling Trees with a Space Colonization Algorithm"), and the mapping is
    /// almost one to one:
    ///
    ///     attractor point  ->  a biotics patch no Bio Cache serves
    ///     tree node        ->  a Bio Cache or chain Node we already hold
    ///     influence radius ->  how far a patch pulls on the frontier
    ///     kill distance    ->  patch is served; stop attracting
    ///     growth direction ->  normalised sum of directions to attractors
    ///
    /// Each frontier structure looks at the unserved patches within its
    /// influence radius and extends toward their average direction. The useful
    /// property is emergent rather than coded: two clusters pulling on one node
    /// cancel laterally until the node is close enough for them to separate, so
    /// the front THICKENS toward dense ground and SPLITS when the ground splits.
    /// A branch, not a snake.
    ///
    /// Why this and not the bearing-sector bonus it is meant to replace: that
    /// term measures direction from the NEST, so its wedges are origin-relative
    /// and distance-blind — a patch 200m out and one 2,500m out on the same
    /// bearing compete for the same crowding count, and the sector boundaries
    /// fall on world axes for no reason connected to the map. Space colonization
    /// is computed from the FRONTIER; the Nest never enters it.
    ///
    /// SHADOW ONLY for now. It logs what it would do beside what the beam
    /// actually did, and changes nothing. The Opener was built the same way, and
    /// that is how we found its objective was ranking candidates by noise.
    ///
    /// The intended end state is not this replacing the beam. Space colonization
    /// decides WHERE the frontier should go — it has no notion of cost, cash or
    /// timing. The beam decides whether that is affordable now and in what
    /// order. Feeding these proposals in as candidates widens an option set that
    /// currently contains only already-reachable patches, which is the actual
    /// reason expansion snakes.
    /// </summary>
    internal static class GrowthModel
    {
        /// <summary>How far an unserved patch pulls on a frontier structure.</summary>
        const float INFLUENCE_M = 1200f;

        /// <summary>A patch this close to one of our structures counts as
        /// served and stops attracting — the algorithm's "kill distance".</summary>
        const float KILL_M = 260f;

        /// <summary>How far one growth step reaches.</summary>
        const float STEP_M = 320f;

        const int   MAX_PROPOSALS = 3;
        const float LOG_EVERY_S   = 20f;

        internal struct Proposal
        {
            public Vector3 From;        // frontier structure we grow from
            public Vector3 To;          // where the step lands
            public Vector3 Toward;      // the attractor cluster's centre of pull
            public int     Attractors;  // how many unserved patches pulled
            public long    Biotics;     // what they are worth
        }

        static float _lastLogAt;

        internal static void ResetForNewRound() { _lastLogAt = 0f; }

        /// <summary>
        /// Growth steps the frontier would take, best first. "Best" here means
        /// most attractors pulling — the direction with the most unclaimed
        /// ground behind it, which is the whole point of the model.
        /// </summary>
        internal static List<Proposal> Propose(EcoState s)
        {
            var result = new List<Proposal>();
            if (s == null) return result;

            var sites = MapProfile.Sites;
            if (sites == null || sites.Count == 0) return result;

            // ---- attractors: patches nothing of ours serves ----
            var attractors = new List<Vector3>();
            var attractorBiotics = new List<long>();
            float killSq = KILL_M * KILL_M;
            for (int i = 0; i < sites.Count; i++)
            {
                Vector3 c = sites[i].Centroid;
                if (NearAnyStructure(s, c, killSq)) continue;
                attractors.Add(c);
                attractorBiotics.Add(sites[i].Biotics);
            }
            if (attractors.Count == 0) return result;

            // ---- frontier: everything we hold can grow ----
            var frontier = new List<Vector3>();
            if (s.nestPos != Vector3.zero) frontier.Add(s.nestPos);
            for (int i = 0; i < s.bcs.Count; i++)   frontier.Add(s.bcs[i].pos);
            for (int i = 0; i < s.nodes.Count; i++) frontier.Add(s.nodes[i].pos);
            if (frontier.Count == 0) return result;

            float infSq = INFLUENCE_M * INFLUENCE_M;
            for (int fi = 0; fi < frontier.Count; fi++)
            {
                Vector3 from = frontier[fi];
                float dirX = 0f, dirZ = 0f;
                int pulled = 0;
                long worth = 0;
                float cx = 0f, cz = 0f;

                for (int ai = 0; ai < attractors.Count; ai++)
                {
                    float dx = attractors[ai].x - from.x, dz = attractors[ai].z - from.z;
                    float d2 = dx * dx + dz * dz;
                    if (d2 > infSq || d2 < 1f) continue;

                    // Nearest frontier structure wins the attractor, so two
                    // structures do not both grow toward the same patch.
                    if (!IsNearestFrontier(frontier, fi, attractors[ai])) continue;

                    float d = Mathf.Sqrt(d2);
                    dirX += dx / d; dirZ += dz / d;
                    cx += attractors[ai].x; cz += attractors[ai].z;
                    worth += attractorBiotics[ai];
                    pulled++;
                }
                if (pulled == 0) continue;

                float len = Mathf.Sqrt(dirX * dirX + dirZ * dirZ);
                if (len < 0.01f) continue;     // pulls cancelled — sits between clusters

                result.Add(new Proposal
                {
                    From = from,
                    To = new Vector3(from.x + dirX / len * STEP_M, from.y,
                                     from.z + dirZ / len * STEP_M),
                    Toward = new Vector3(cx / pulled, from.y, cz / pulled),
                    Attractors = pulled,
                    Biotics = worth,
                });
            }

            result.Sort((a, b) => b.Attractors != a.Attractors
                                ? b.Attractors.CompareTo(a.Attractors)
                                : b.Biotics.CompareTo(a.Biotics));
            if (result.Count > MAX_PROPOSALS) result.RemoveRange(MAX_PROPOSALS, result.Count - MAX_PROPOSALS);
            return result;
        }

        /// <summary>Log what the model would grow, for comparison against what
        /// the beam actually committed to. Costs nothing and changes nothing.</summary>
        internal static void LogShadow(EcoState s)
        {
            float now = Time.time;
            if (now - _lastLogAt < LOG_EVERY_S) return;
            _lastLogAt = now;

            var props = Propose(s);
            if (props.Count == 0) return;

            var sb = new System.Text.StringBuilder();
            sb.Append("[GROWTH/SHADOW]");
            for (int i = 0; i < props.Count; i++)
            {
                var p = props[i];
                sb.Append(" #").Append(i + 1)
                  .Append(" from(").Append(p.From.x.ToString("F0")).Append(',')
                  .Append(p.From.z.ToString("F0")).Append(')')
                  .Append("->(").Append(p.To.x.ToString("F0")).Append(',')
                  .Append(p.To.z.ToString("F0")).Append(')')
                  .Append(" pull=").Append(p.Attractors)
                  .Append(" toward(").Append(p.Toward.x.ToString("F0")).Append(',')
                  .Append(p.Toward.z.ToString("F0")).Append(')');
            }
            MelonLogger.Msg(sb.ToString());
        }

        static bool NearAnyStructure(EcoState s, Vector3 p, float r2)
        {
            for (int i = 0; i < s.bcs.Count; i++)
            {
                float dx = s.bcs[i].pos.x - p.x, dz = s.bcs[i].pos.z - p.z;
                if (dx * dx + dz * dz <= r2) return true;
            }
            return false;
        }

        static bool IsNearestFrontier(List<Vector3> frontier, int fi, Vector3 target)
        {
            float mine = SqDist(frontier[fi], target);
            for (int i = 0; i < frontier.Count; i++)
            {
                if (i == fi) continue;
                if (SqDist(frontier[i], target) < mine) return false;
            }
            return true;
        }

        static float SqDist(Vector3 a, Vector3 b)
        {
            float dx = a.x - b.x, dz = a.z - b.z;
            return dx * dx + dz * dz;
        }
    }
}
