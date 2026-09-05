using UnityEngine;

namespace Si_RTS_AI.Perception
{
    /// <summary>
    /// A HOP IS MEASURED ON THE GROUND, NOT ON THE MAP.
    ///
    /// The game accepts a placement by the distance in the plane
    /// (ConstructionPreview.GetWithinBaseStructuresDistanceAndFogOfWar uses
    /// GameMath.Distance2D and counts unfinished sites as anchors), but a site
    /// only PROGRESSES while a functional structure is within
    /// MaximumBaseStructureDistance in three dimensions
    /// (ConstructionSite.GetFriendlyStructureNearby uses GameMath.Distance).
    /// So a node planned 121 m along a cliff can be placed and never start:
    /// NarakaCity 2026-09-05, node (1075,845) off the finished node (1195,860),
    /// twice in two rounds at the same spot, and the whole branch behind it
    /// wedged at 0%.
    ///
    /// Everything that steps a node chain therefore measures its hops here,
    /// with the terrain height sampled under both ends.
    /// </summary>
    internal static class Ground
    {
        static Terrain[] _terrains;
        static float _refreshAt;

        internal static void ResetForNewRound() { _terrains = null; _refreshAt = 0f; _shortenedLogged = 0; }

        static Terrain[] Terrains()
        {
            float now = Time.time;
            if (_terrains == null || now - _refreshAt > 60f)
            {
                try { _terrains = Terrain.activeTerrains; } catch { _terrains = null; }
                _refreshAt = now;
            }
            return _terrains;
        }

        /// <summary>Terrain height under (x,z); <paramref name="fallbackY"/> when
        /// there is no terrain there.</summary>
        internal static float HeightAt(float x, float z, float fallbackY)
        {
            var ts = Terrains();
            if (ts == null) return fallbackY;
            for (int t = 0; t < ts.Length; t++)
            {
                var tr = ts[t];
                if (tr == null) continue;
                try
                {
                    var td = tr.terrainData;
                    if (td == null) continue;
                    var p = tr.transform.position; var s = td.size;
                    if (x < p.x || x > p.x + s.x || z < p.z || z > p.z + s.z) continue;
                    return tr.SampleHeight(new Vector3(x, 0f, z)) + p.y;
                }
                catch { }
            }
            return fallbackY;
        }

        internal static float HeightAt(Vector3 p) => HeightAt(p.x, p.z, p.y);

        /// <summary>Squared distance in three dimensions with the terrain height
        /// sampled under both points. Both ends are sampled, never trusted:
        /// planner points carry the y of whatever they were derived from.</summary>
        internal static float Sq3(Vector3 a, Vector3 b)
        {
            float dx = a.x - b.x, dz = a.z - b.z;
            float dy = HeightAt(a) - HeightAt(b);
            return dx * dx + dz * dz + dy * dy;
        }

        internal static float Dist3(Vector3 a, Vector3 b) => Mathf.Sqrt(Sq3(a, b));

        /// <summary>Would a site at <paramref name="pos"/> progress off an anchor
        /// at <paramref name="anchor"/>? Centre to centre, which is stricter than
        /// the bounds-to-bounds test the game runs, so a yes here is a yes there.</summary>
        internal static bool WithinReach(Vector3 pos, Vector3 anchor, float reach)
        {
            float dx = pos.x - anchor.x, dz = pos.z - anchor.z;
            float flat = dx * dx + dz * dz;
            if (flat > reach * reach) return false;
            float dy = HeightAt(pos) - HeightAt(anchor);
            return flat + dy * dy <= reach * reach;
        }

        /// <summary>
        /// The point at most <paramref name="hopM"/> along the plane from
        /// <paramref name="from"/> toward <paramref name="to"/> whose distance
        /// from <paramref name="from"/> is also at most <paramref name="hopM"/>
        /// on the ground. The step is shortened, never redirected: on a slope
        /// the chain takes more hops, on a cliff it takes short ones, and the
        /// planned line is kept. Never shorter than a quarter hop, because a
        /// wall is a wall and the obstruction memory is the tool for that.
        ///
        /// SOLVED, NOT SHRUNK BY A FACTOR. The first build of this started at a
        /// full hop and cut it to 80% whenever the ground test failed, and a
        /// full hop fails that test for ANY rise at all, one metre included.
        /// Round seven on NarakaCity: median node spacing 87 m against 110 m
        /// the round before, 198 nodes by minute 20 against 126. DrMuck saw it
        /// on the map before the numbers did. So the step is the exact
        /// horizontal length that fits the rise measured at the candidate,
        /// sqrt(hop^2 - rise^2), re-measured where that lands.
        /// </summary>
        internal static Vector3 StepToward(Vector3 from, Vector3 to, float hopM)
        {
            float dx = to.x - from.x, dz = to.z - from.z;
            float len = Mathf.Sqrt(dx * dx + dz * dz);
            if (len < 1f) return from;
            float step = Mathf.Min(hopM, len);
            float ux = dx / len, uz = dz / len;
            float y0 = HeightAt(from);
            float minStep = hopM * 0.25f;
            float hop2 = hopM * hopM;
            Vector3 p = new Vector3(from.x + ux * step, from.y, from.z + uz * step);
            for (int i = 0; i < 4; i++)
            {
                float dy = HeightAt(p) - y0;
                if (step * step + dy * dy <= hop2 + 1f || step <= minStep) break;
                float fit = Mathf.Sqrt(Mathf.Max(hop2 - dy * dy, minStep * minStep));
                // Never lengthen on a re-measure, and always make progress.
                step = Mathf.Min(step - 1f, fit);
                if (step < minStep) step = minStep;
                p = new Vector3(from.x + ux * step, from.y, from.z + uz * step);
            }
            if (step < hopM * 0.85f && step < len && _shortenedLogged < 40)
            {
                _shortenedLogged++;
                float dyFinal = HeightAt(p) - y0;
                MelonLoader.MelonLogger.Msg($"[GROUND] hop from ({from.x:F0},{from.z:F0}) h{y0:F0} toward ({to.x:F0},{to.z:F0}) " +
                                            $"shortened {Mathf.Min(hopM, len):F0}->{step:F0} m, rise {dyFinal:F0} m");
            }
            return p;
        }

        static int _shortenedLogged;
    }
}
