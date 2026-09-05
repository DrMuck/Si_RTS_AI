using MelonLoader;
using Si_RTS_AI.Perception;
using Si_RTS_AI.Perception.MapLayers;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Si_RTS_AI.Mil
{
    /// <summary>
    /// WALK TIME AND SAFE GROUND, AS ARRAYS.
    ///
    /// Everything the military layer used to answer with a straight line —
    /// how far is the front, how long until a force arrives, where should a
    /// producer stand so its units reach the fighting soonest — is a question
    /// about paths, and NarakaCity has walls. The game paths on the A*
    /// Pathfinding Project, so walkability is sampled from its graph ONCE per
    /// map, terrain slope from Unity's heightmap, and from then on every query
    /// is a Dijkstra over our own 40 m grid: 22,500 cells, a few milliseconds.
    ///
    /// PURE FUNCTIONS OVER ARRAYS. Nothing in Solve() touches Unity; the static
    /// layers are built on the main thread in small slices, the solves can run
    /// anywhere. That is the boundary MIL_V3_PLAN section 8 draws for the GPU.
    ///
    /// Flyers ignore walkability and slope; they get a flat field.
    /// </summary>
    internal static class Fields
    {
        const float SLOPE_COST     = 4f;      // extra cost per metre of rise per metre of run
        const float CELLS_PER_TICK = 900;     // static-layer sampling slice
        const float BAD_SLOPE      = 0.7f;    // rise/run above this is a wall to walkers

        static bool[]  _walkable = new bool[0];
        static float[] _height   = new float[0];
        static int     _built;                 // cells sampled so far
        static bool    _ready;
        static int     _unwalkable;
        static bool    _astarWarned;

        internal static bool Ready => _ready;
        internal static float UnwalkableShare => _walkable.Length == 0 ? 0f : _unwalkable / (float)_walkable.Length;

        internal static void ResetForNewRound()
        {
            _walkable = new bool[0]; _height = new float[0];
            _built = 0; _ready = false; _unwalkable = 0; _astarWarned = false;
            _cache.Clear();
        }

        // ---- static layers, built in slices --------------------------------------

        /// <summary>Call every tick; it samples a slice and returns once done.</summary>
        internal static void BuildTick()
        {
            if (_ready) return;
            int n = GridWorld.CellCount;
            if (_walkable.Length != n)
            {
                _walkable = new bool[n]; _height = new float[n];
                for (int i = 0; i < n; i++) _walkable[i] = true;
                _built = 0;
            }
            int end = Mathf.Min(n, _built + (int)CELLS_PER_TICK);
            Terrain[] terrains = null;
            try { terrains = Terrain.activeTerrains; } catch { }
            object astar = null;
            try { astar = AstarPath.active; } catch { astar = null; }

            for (int i = _built; i < end; i++)
            {
                var c = GridWorld.CellCenter(i % GridWorld.Width, i / GridWorld.Width);
                float h = 0f;
                if (terrains != null)
                    for (int t = 0; t < terrains.Length; t++)
                    {
                        var tr = terrains[t];
                        if (tr == null || tr.terrainData == null) continue;
                        var p = tr.transform.position; var s = tr.terrainData.size;
                        if (c.x < p.x || c.x > p.x + s.x || c.z < p.z || c.z > p.z + s.z) continue;
                        try { h = tr.SampleHeight(c) + p.y; } catch { }
                        break;
                    }
                _height[i] = h;

                if (astar != null)
                {
                    try
                    {
                        var probe = new Vector3(c.x, h, c.z);
                        var nn = AstarPath.active.GetNearest(probe, Pathfinding.NNConstraint.Walkable);
                        bool ok = nn.node != null;
                        if (ok)
                        {
                            float dx = nn.position.x - c.x, dz = nn.position.z - c.z;
                            ok = dx * dx + dz * dz <= GridWorld.CellSize * GridWorld.CellSize * 0.35f;
                        }
                        _walkable[i] = ok;
                        if (!ok) _unwalkable++;
                    }
                    catch (Exception ex)
                    {
                        if (!_astarWarned)
                        {
                            _astarWarned = true;
                            MelonLogger.Warning("[MIL/FIELDS] A* sampling failed, treating ground as walkable: " + ex.Message);
                        }
                        astar = null;
                    }
                }
            }
            _built = end;
            if (_built >= n)
            {
                _ready = true;
                MilLog.Msg($"[MIL/FIELDS] static layers built: {n} cells, {_unwalkable} unwalkable " +
                           $"({UnwalkableShare * 100f:F0}%), A* {(astar != null ? "sampled" : "unavailable")}");
            }
        }

        internal static float HeightAt(Vector3 p)
        {
            if (_height.Length == 0) return p.y;
            return _height[GridWorld.CellZ(p.z) * GridWorld.Width + GridWorld.CellX(p.x)];
        }

        internal static bool WalkableAt(Vector3 p)
        {
            if (_walkable.Length == 0) return true;
            return _walkable[GridWorld.CellZ(p.z) * GridWorld.Width + GridWorld.CellX(p.x)];
        }

        // ---- solving -----------------------------------------------------------

        /// <summary>A solved field: seconds to reach each cell from the seeds at
        /// 1 m/s (divide by speed), or +inf where unreachable.</summary>
        internal sealed class Field
        {
            public float[] Cost;      // metres-equivalent from nearest seed
            public bool Flying;
            public float At(Vector3 p) =>
                Cost[GridWorld.CellZ(p.z) * GridWorld.Width + GridWorld.CellX(p.x)];
            public float SecondsAt(Vector3 p, float speed) =>
                At(p) / Mathf.Max(0.5f, speed);
        }

        static readonly int[] DX = { 1, -1, 0, 0, 1, 1, -1, -1 };
        static readonly int[] DZ = { 0, 0, 1, -1, 1, -1, 1, -1 };

        /// <summary>
        /// Multi-source Dijkstra. Seeds carry an initial cost (metres). Extra
        /// per-cell penalty (metres-equivalent) lets the danger field shape a
        /// retreat or a rally choice.
        /// </summary>
        internal static Field Solve(IList<(Vector3 pos, float startM)> seeds, bool flying, float[] penalty = null)
        {
            int w = GridWorld.Width, hgt = GridWorld.Height, n = w * hgt;
            var cost = new float[n];
            for (int i = 0; i < n; i++) cost[i] = float.PositiveInfinity;
            var heap = new MinHeap(n);
            for (int s = 0; s < seeds.Count; s++)
            {
                int i = GridWorld.CellZ(seeds[s].pos.z) * w + GridWorld.CellX(seeds[s].pos.x);
                if (seeds[s].startM < cost[i]) { cost[i] = seeds[s].startM; heap.Push(i, cost[i]); }
            }
            bool haveStatic = _walkable.Length == n;
            float cs = GridWorld.CellSize, diag = cs * 1.41421f;
            while (heap.Count > 0)
            {
                heap.Pop(out int i, out float ci);
                if (ci > cost[i]) continue;
                int x = i % w, z = i / w;
                for (int k = 0; k < 8; k++)
                {
                    int nx = x + DX[k], nz = z + DZ[k];
                    if (nx < 0 || nz < 0 || nx >= w || nz >= hgt) continue;
                    int j = nz * w + nx;
                    float step = k < 4 ? cs : diag;
                    if (!flying && haveStatic)
                    {
                        if (!_walkable[j]) continue;
                        float rise = Mathf.Abs(_height[j] - _height[i]);
                        float grad = rise / step;
                        if (grad > BAD_SLOPE) continue;
                        step *= 1f + SLOPE_COST * grad;
                    }
                    if (penalty != null) step += penalty[j];
                    float nc = ci + step;
                    if (nc < cost[j]) { cost[j] = nc; heap.Push(j, nc); }
                }
            }
            return new Field { Cost = cost, Flying = flying };
        }

        internal static Field SolveFrom(Vector3 seed, bool flying, float[] penalty = null) =>
            Solve(new[] { (seed, 0f) }, flying, penalty);

        // A small per-tick cache so several callers asking about the same seed
        // in one tick share one solve.
        static readonly Dictionary<long, (float at, Field f)> _cache = new Dictionary<long, (float, Field)>();
        const float CACHE_S = 4f;

        static long KeyOf(Vector3 seed, bool flying) =>
            ((long)GridWorld.CellX(seed.x) << 20) | ((long)GridWorld.CellZ(seed.z) << 2) | (flying ? 1L : 0L);

        internal static Field Cached(Vector3 seed, bool flying)
        {
            long key = KeyOf(seed, flying);
            float now = Time.time;
            if (_cache.TryGetValue(key, out var e) && now - e.at < CACHE_S) return e.f;
            var f = SolveFrom(seed, flying);
            _cache[key] = (now, f);
            if (_cache.Count > 64) _cache.Clear();
            return f;
        }

        /// <summary>Seconds to walk from a to b at this speed. Straight line
        /// until the static layers exist; +inf when unreachable.</summary>
        internal static float WalkTimeBetween(Vector3 a, Vector3 b, float speed, bool flying = false)
        {
            speed = Mathf.Max(0.5f, speed);
            if (!_ready)
            {
                Vector3 d = b - a; d.y = 0f;
                return d.magnitude / speed;
            }
            var f = Cached(a, flying);
            return f.SecondsAt(b, speed);
        }

        // ---- danger, rally, front --------------------------------------------------

        /// <summary>Metres-equivalent penalty per cell from enemy reach: a cell
        /// inside a track's weapon reach costs as much as walking a long way
        /// around it. Built from Intel each call — tracks are few.</summary>
        internal static float[] DangerPenalty(float scaleM = 600f)
        {
            int w = GridWorld.Width, n = GridWorld.CellCount;
            var pen = new float[n];
            var tracks = Intel.Tracks;
            for (int t = 0; t < tracks.Count; t++)
            {
                var tr = tracks[t];
                var p = tr.Predicted();
                float r = Mathf.Max(tr.Reach, 200f) + 80f;
                int cx = GridWorld.CellX(p.x), cz = GridWorld.CellZ(p.z);
                int rc = Mathf.CeilToInt(r / GridWorld.CellSize);
                float weight = scaleM * Mathf.Clamp01(tr.Effective / 3000f) * tr.Confidence;
                for (int dz = -rc; dz <= rc; dz++)
                    for (int dx = -rc; dx <= rc; dx++)
                    {
                        int x = cx + dx, z = cz + dz;
                        if (x < 0 || z < 0 || x >= w || z >= GridWorld.Height) continue;
                        float d = Mathf.Sqrt(dx * dx + dz * dz) * GridWorld.CellSize;
                        if (d > r) continue;
                        pen[z * w + x] += weight * (1f - d / r);
                    }
            }
            return pen;
        }

        internal static float DangerAt(Vector3 p)
        {
            float d = 0f;
            var tracks = Intel.Tracks;
            for (int t = 0; t < tracks.Count; t++)
            {
                var tr = tracks[t]; var q = tr.Predicted();
                float dx = q.x - p.x, dz = q.z - p.z;
                float r = Mathf.Max(tr.Reach, 200f) + 80f;
                float dist = Mathf.Sqrt(dx * dx + dz * dz);
                if (dist > r) continue;
                d += tr.Effective * tr.Confidence * (1f - dist / r);
            }
            return d;
        }

        /// <summary>
        /// A rally point for a force heading to `objective` from `from`:
        /// on the walkable route, `standoffM` short of the objective, outside
        /// enemy reach. Walks back along the solved field from the objective.
        /// </summary>
        internal static Vector3 RallyFor(Vector3 from, Vector3 objective, float standoffM, bool flying)
        {
            Vector3 dir = from - objective; dir.y = 0f;
            float dist = dir.magnitude;
            if (dist <= standoffM + 1f) return from;
            Vector3 fallback = objective + dir.normalized * standoffM;

            if (!_ready) return fallback;
            // Sample candidates on a ring at standoffM around the objective and
            // pick the one nearest to `from` by field cost that is not dangerous.
            var f = Cached(from, flying);
            Vector3 best = fallback; float bestCost = float.MaxValue;
            for (int k = 0; k < 16; k++)
            {
                float a = k / 16f * 2f * Mathf.PI;
                var c = objective + new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)) * standoffM;
                if (!flying && !WalkableAt(c)) continue;
                float cost = f.At(c);
                if (float.IsInfinity(cost)) continue;
                cost += DangerAt(c) * 0.5f;
                if (cost < bestCost) { bestCost = cost; best = c; }
            }
            best.y = HeightAt(best);
            return best;
        }

        /// <summary>
        /// The cell among `candidates` that minimises the worst walk-time to any
        /// of `assets`, weighted. Used for the reserve's standing point.
        /// </summary>
        internal static Vector3 BestStandingPoint(IList<Vector3> candidates, IList<(Vector3 pos, float weight)> assets,
                                                  float speed, out float worstS)
        {
            worstS = float.PositiveInfinity;
            if (candidates.Count == 0) return Vector3.zero;
            if (!_ready || assets.Count == 0)
            {
                // Weighted centroid, no field.
                Vector3 c = Vector3.zero; float wsum = 0f;
                for (int i = 0; i < assets.Count; i++) { c += assets[i].pos * assets[i].weight; wsum += assets[i].weight; }
                if (wsum <= 0f) return candidates[0];
                c /= wsum;
                Vector3 best0 = candidates[0]; float bd = float.MaxValue;
                for (int i = 0; i < candidates.Count; i++)
                {
                    float d = (candidates[i] - c).sqrMagnitude;
                    if (d < bd) { bd = d; best0 = candidates[i]; }
                }
                return best0;
            }
            // One field per asset, read at each candidate.
            var fields = new Field[assets.Count];
            for (int a = 0; a < assets.Count; a++) fields[a] = Cached(assets[a].pos, false);
            Vector3 best = candidates[0]; float bestWorst = float.MaxValue;
            for (int i = 0; i < candidates.Count; i++)
            {
                float worst = 0f;
                for (int a = 0; a < assets.Count; a++)
                {
                    float s = fields[a].SecondsAt(candidates[i], speed) * assets[a].weight;
                    if (s > worst) worst = s;
                }
                worst += DangerAt(candidates[i]) * 0.01f;
                if (worst < bestWorst) { bestWorst = worst; best = candidates[i]; }
            }
            worstS = bestWorst;
            return best;
        }

        // ---- heap ------------------------------------------------------------

        sealed class MinHeap
        {
            int[] _idx; float[] _key; int _n;
            public MinHeap(int cap) { _idx = new int[cap + 8]; _key = new float[cap + 8]; }
            public int Count => _n;
            public void Push(int i, float k)
            {
                if (_n >= _idx.Length) { Array.Resize(ref _idx, _idx.Length * 2); Array.Resize(ref _key, _key.Length * 2); }
                int c = _n++;
                while (c > 0)
                {
                    int p = (c - 1) >> 1;
                    if (_key[p] <= k) break;
                    _idx[c] = _idx[p]; _key[c] = _key[p]; c = p;
                }
                _idx[c] = i; _key[c] = k;
            }
            public void Pop(out int i, out float k)
            {
                i = _idx[0]; k = _key[0];
                int last = --_n;
                if (last == 0) return;
                int li = _idx[last]; float lk = _key[last];
                int c = 0;
                while (true)
                {
                    int a = 2 * c + 1, b = a + 1, m = a;
                    if (a >= last) break;
                    if (b < last && _key[b] < _key[a]) m = b;
                    if (_key[m] >= lk) break;
                    _idx[c] = _idx[m]; _key[c] = _key[m]; c = m;
                }
                _idx[c] = li; _key[c] = lk;
            }
        }
    }
}
