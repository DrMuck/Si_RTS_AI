using MelonLoader;
using Silica;
using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Si_RTS_AI.Planning
{
    /// <summary>
    /// Per-map economic profile, built once at round start from the LIVE
    /// ResourceAreas, and the expansion parameters derived from it.
    ///
    /// Why this exists — NarakaCity, measured 2026-07-29:
    ///
    ///     sector   patches   nearest   median dist
    ///        N          6      216m         1383m
    ///       NW         23      802m         3218m
    ///        W         46      944m         3895m
    ///       SW         26      601m         3368m
    ///
    ///     Only 13 of 107 patches lie within 1500m of the Nest. W and NW hold
    ///     64% of the map and their patches sit ~3-4km out.
    ///
    /// A planner tuned on compact maps cannot use a map like that. TheMaw is
    /// 31 patches all close in; Naraka needs 4km chains, ~30 Nodes and 6000
    /// cash to reach the biotics that matter. One set of constants cannot
    /// serve both, which is what the user meant by map-dependent parameters.
    ///
    /// The parameters are DERIVED rather than hand-authored, so a rebalanced
    /// map or a moved spawn is picked up automatically (both are configurable
    /// via Si_MapBalance and UserData/Spawns). A per-map JSON may override any
    /// subset when the derivation is wrong for a specific map:
    ///
    ///     UserData/RTSA/mapprofiles/&lt;MapName&gt;.json
    ///     { "maxChainDepthM": 4200, "minChainRoi": 5.0, "expansionAggression": 1.4 }
    /// </summary>
    internal static class MapProfile
    {
        public static bool   Ready;
        public static string MapName = "";

        // ---- Raw shape ----
        public static int   PatchCount;
        public static long  TotalBiotics;
        public static float NearestPatchM, MedianPatchM, P90PatchM;
        public static int   PatchesWithin1500;
        /// <summary>Biotics per 45-degree sector around the Nest, index 0 = east.</summary>
        public static readonly long[] SectorBiotics = new long[8];
        public static readonly int[]  SectorPatches = new int[8];

        // ---- Derived expansion parameters ----
        /// <summary>How far out chain-building is worth planning toward.</summary>
        public static float MaxChainDepthM = 2000f;
        /// <summary>Biotics opened per cash spent, below which a chain isn't worth it.</summary>
        public static float MinChainRoi = 6f;
        /// <summary>Scales Phase 2/3 expansion appetite. 1.0 = compact map baseline.</summary>
        public static float ExpansionAggression = 1f;

        // ---- Cluster mode ----
        //
        // Some maps pack biotics into groups that ONE Bio Cache works as a
        // unit. IndustrialQuarter is the case in point: 172 patches in groups
        // spaced ~90m apart. There, "1-2 Cysts feed one cluster" and the right
        // Phase 1 is 2-4 CLUSTERS rather than 3-4 individual patches.
        //
        // The simulator's income model keys on a BC's NEAREST patch, so a
        // 4-pack looks exactly like a single patch and the whole strategy is
        // invisible to it. ClusterMode switches it to summing the patches
        // actually in harvest range.
        //
        // Gated on MEASURED cluster density rather than a map name, so it
        // survives rebalances and new maps — but calibrated so that on today's
        // map set only IndustrialQuarter crosses the line, which is the scope
        // the user asked for. Overridable per map via the profile JSON
        // ("clusterMode": 1 / 0).
        public static bool  ClusterMode;
        public static float MeanClusterSize;      // patches within CLUSTER_RADIUS_M, averaged
        // 100m, chosen by measurement across all 13 map dumps. Mean patches
        // within radius, per map:
        //
        //      radius:      40m    60m    80m   100m   120m
        //      IndustrialQ 1.01   1.02   1.02   2.10   2.31
        //      BlackIsle   1.00   1.03   1.24   1.61   2.00
        //      GreatErg    1.00   1.15   1.36   1.48   1.71
        //      everything else                  <=1.35
        //
        // Below 100m nothing separates — IQ's groups are ~90m apart, not the
        // ~30m assumed elsewhere in this codebase. At 100m IQ stands clear of
        // the field by 0.49, which is where the threshold goes.
        public const  float CLUSTER_RADIUS_M = 100f;
        const float CLUSTER_MODE_MIN_MEAN = 1.9f;

        public static void ResetForNewRound()
        {
            Ready = false;
            MapName = "";
            PatchCount = 0; TotalBiotics = 0;
            NearestPatchM = MedianPatchM = P90PatchM = 0f;
            PatchesWithin1500 = 0;
            Array.Clear(SectorBiotics, 0, 8);
            Array.Clear(SectorPatches, 0, 8);
            MaxChainDepthM = 2000f; MinChainRoi = 6f; ExpansionAggression = 1f;
            ClusterMode = false; MeanClusterSize = 0f;
            _positions.Clear(); _amounts.Clear(); Sites.Clear();
        }

        static readonly List<Vector3> _positions = new List<Vector3>();

        /// <summary>
        /// A harvest SITE — one or more patches close enough that a single Bio
        /// Cache works them as a unit. On spread maps a site is just a patch;
        /// on IndustrialQuarter it is the whole group. This is the unit the
        /// Opener plans in: "open on 3 sites" rather than "on 3 patches".
        /// </summary>
        public struct Site
        {
            public Vector3 Centroid;
            public int     Patches;
            public long    Biotics;
            public float   DistFromNest;
        }
        public static List<Site> Sites = new List<Site>();
        static readonly List<int> _amounts = new List<int>();

        static void BuildSites(Vector3 nest, List<int> amounts)
        {
            Sites.Clear();
            // Single-linkage grouping at the cluster radius. On a map where
            // ClusterMode is off this collapses to one site per patch, which
            // is exactly what we want there.
            float rSq = CLUSTER_RADIUS_M * CLUSTER_RADIUS_M;
            var used = new bool[_positions.Count];
            for (int i = 0; i < _positions.Count; i++)
            {
                if (used[i]) continue;
                var members = new List<int> { i };
                used[i] = true;
                if (ClusterMode)
                {
                    for (int j = 0; j < _positions.Count; j++)
                    {
                        if (used[j]) continue;
                        float dx = _positions[i].x - _positions[j].x;
                        float dz = _positions[i].z - _positions[j].z;
                        if (dx * dx + dz * dz > rSq) continue;
                        members.Add(j); used[j] = true;
                    }
                }
                Vector3 c = Vector3.zero; long bio = 0;
                for (int m = 0; m < members.Count; m++)
                {
                    c += _positions[members[m]];
                    bio += amounts[members[m]];
                }
                c /= members.Count;
                Sites.Add(new Site
                {
                    Centroid = c, Patches = members.Count, Biotics = bio,
                    DistFromNest = Mathf.Sqrt((c.x - nest.x) * (c.x - nest.x) +
                                              (c.z - nest.z) * (c.z - nest.z)),
                });
            }
            Sites.Sort((a, b) => a.DistFromNest.CompareTo(b.DistFromNest));
        }

        /// <summary>
        /// Build once, as soon as the Nest exists. Cheap — one pass over the
        /// ResourceAreas — and never repeated.
        /// </summary>
        public static void MaybeBuild(Team team, string mapName)
        {
            if (Ready || team == null) return;

            Vector3 nest = Vector3.zero;
            try
            {
                var structs = team.Structures;
                if (structs == null) return;
                for (int i = 0; i < structs.Count; i++)
                {
                    var s = structs[i];
                    if (s?.ObjectInfo == null || s.IsDestroyed) continue;
                    if (s.ObjectInfo.DisplayName == "Nest") { nest = s.transform.position; break; }
                }
            }
            catch { return; }
            if (nest == Vector3.zero) return;

            var dists = new List<float>();
            try
            {
                var all = ResourceArea.AllResourceAreas;
                if (all == null) return;
                var uType = team.UsableResource;
                for (int i = 0; i < all.Count; i++)
                {
                    var ra = all[i];
                    if (ra == null || ra.IsEmpty || ra.ResourceType != uType) continue;
                    var p = ra.SignalCenter;
                    int amount = ra.ResourceAmountCurrent;
                    float d = Mathf.Sqrt((p.x - nest.x) * (p.x - nest.x) + (p.z - nest.z) * (p.z - nest.z));
                    dists.Add(d);
                    _positions.Add(p); _amounts.Add(amount);
                    TotalBiotics += amount;
                    float ang = Mathf.Atan2(p.z - nest.z, p.x - nest.x);
                    if (ang < 0f) ang += 2f * Mathf.PI;
                    int sec = ((int)(ang / (Mathf.PI / 4f))) & 7;
                    SectorBiotics[sec] += amount;
                    SectorPatches[sec]++;
                }
            }
            catch { return; }
            if (dists.Count == 0) return;

            dists.Sort();
            PatchCount        = dists.Count;
            NearestPatchM     = dists[0];
            MedianPatchM      = dists[dists.Count / 2];
            P90PatchM         = dists[Mathf.Min(dists.Count - 1, (int)(dists.Count * 0.9f))];
            for (int i = 0; i < dists.Count; i++) if (dists[i] <= 1500f) PatchesWithin1500++;
            MapName = mapName ?? "";

            // Cluster density: mean number of patches within CLUSTER_RADIUS of
            // each patch, counting itself.
            if (_positions.Count > 0)
            {
                float rSq = CLUSTER_RADIUS_M * CLUSTER_RADIUS_M;
                long sum = 0;
                for (int i = 0; i < _positions.Count; i++)
                {
                    int n = 0;
                    for (int j = 0; j < _positions.Count; j++)
                    {
                        float dx = _positions[i].x - _positions[j].x;
                        float dz = _positions[i].z - _positions[j].z;
                        if (dx * dx + dz * dz <= rSq) n++;
                    }
                    sum += n;
                }
                MeanClusterSize = (float)sum / _positions.Count;
                // CLUSTER MODE IS AN OPT-IN, NOT A DERIVED PROPERTY — YET.
                //
                // The threshold alone would switch it on for any map that
                // happens to measure above 1.9, and what cluster mode does is
                // not cheap: it softens the direction penalty and ranks opener
                // candidates by biotics per metre instead of distance. Both were
                // derived from ONE measured map. IndustrialQuarter is the only
                // one with real clusters (2.10 against 1.61 next), and the only
                // one where clustering was shown to be better rather than
                // merely present.
                //
                // So it stays named until another map is measured (DrMuck,
                // 2026-08-09: "Cluster Mode should be only valid for Industrial
                // quarter yet"). The override file can still force it on for a
                // deliberate experiment.
                ClusterMode = MeanClusterSize >= CLUSTER_MODE_MIN_MEAN
                           && string.Equals(MapName, "IndustrialQuarter",
                                            StringComparison.OrdinalIgnoreCase);
            }

            BuildSites(nest, _amounts);
            Derive(dists);
            LoadOverride();

            Ready = true;
            MelonLogger.Msg(
                $"[MAPPROF] {MapName}: patches={PatchCount} biotics={TotalBiotics} " +
                $"nearest={NearestPatchM:F0}m median={MedianPatchM:F0}m p90={P90PatchM:F0}m " +
                $"within1500m={PatchesWithin1500} clusterSize={MeanClusterSize:F1} " +
                $"clusterMode={ClusterMode} sites={Sites.Count} -> maxChainDepth={MaxChainDepthM:F0}m " +
                $"minRoi={MinChainRoi:F1} aggression={ExpansionAggression:F2}");
        }

        static void Derive(List<float> sortedDists)
        {
            // Depth: plan out to where 90% of the biotics actually live. On a
            // compact map that lands near the existing 2000m default; on
            // Naraka it comes out past 4km, which is the whole point.
            // CEILING FROM THE MAP, NOT A MAGIC 5000.
            //
            // A fixed 5000m ceiling silently truncated the two biggest maps:
            // Badlands and GreatErg both compute p90 x 1.1 = ~5650m and were cut
            // to 5000m. Beyond that distance a patch is not even enumerated as a
            // candidate, so from a CORNER spawn on a 6000x6000 map the 5000m arc
            // cuts across as a diagonal — which is exactly what the user saw on
            // Badlands 2026-08-03: one half expanded, the other untouched,
            // divided north-west to south-east.
            //
            // The map's own diagonal is the honest ceiling: a chain should be
            // able to reach anywhere on the map, and nowhere further. Small maps
            // are unaffected — Citadel's diagonal is 4243m, well above what its
            // patches ask for.
            float mapDiagonalM = Perception.MapLayers.GridWorld.Width
                               * Perception.MapLayers.GridWorld.CellSize * 1.4142f;
            MaxChainDepthM = Mathf.Clamp(P90PatchM * 1.1f, 1500f, Mathf.Max(1500f, mapDiagonalM));

            // Aggression: how much of the map is out of comfortable reach. If
            // nearly everything is close, stay compact and let the beam's
            // normal income scoring run the show. If most of the value is far
            // out, the planner has to be willing to spend on chains to get it.
            float farFraction = PatchCount > 0
                ? 1f - (float)PatchesWithin1500 / PatchCount
                : 0f;
            ExpansionAggression = Mathf.Lerp(0.8f, 1.8f, farFraction);

            // ROI floor: a far map has to accept thinner returns, because the
            // alternative is not expanding at all. Compact maps can be picky.
            MinChainRoi = Mathf.Lerp(8f, 4f, farFraction);
        }

        static void LoadOverride()
        {
            try
            {
                string path = Path.Combine(Config.Paths.LogDir, "mapprofiles", MapName + ".json");
                if (!File.Exists(path)) return;
                string txt = File.ReadAllText(path);
                MaxChainDepthM      = ReadFloat(txt, "maxChainDepthM",      MaxChainDepthM);
                MinChainRoi         = ReadFloat(txt, "minChainRoi",         MinChainRoi);
                ClusterMode         = ReadFloat(txt, "clusterMode", ClusterMode ? 1f : 0f) >= 0.5f;
                ExpansionAggression = ReadFloat(txt, "expansionAggression", ExpansionAggression);
                MelonLogger.Msg($"[MAPPROF] applied per-map override from {path}");
            }
            catch (Exception ex) { MelonLogger.Warning("[MAPPROF] override read failed: " + ex.Message); }
        }

        // Deliberately not a JSON library dependency — three scalars, and the
        // file is hand-written by us.
        static float ReadFloat(string json, string key, float fallback)
        {
            int i = json.IndexOf("\"" + key + "\"", StringComparison.OrdinalIgnoreCase);
            if (i < 0) return fallback;
            int c = json.IndexOf(':', i);
            if (c < 0) return fallback;
            int e = c + 1;
            while (e < json.Length && (json[e] == ' ' || json[e] == '\t')) e++;
            int st = e;
            while (e < json.Length && (char.IsDigit(json[e]) || json[e] == '.' || json[e] == '-' || json[e] == '+')) e++;
            return float.TryParse(json.Substring(st, e - st),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float v) ? v : fallback;
        }
    }
}
