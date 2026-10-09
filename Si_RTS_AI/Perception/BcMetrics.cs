using MelonLoader;
using Silica;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace Si_RTS_AI.Perception
{
    /// <summary>
    /// Passive per-BC / per-Refinery telemetry emitter for calibrating the BC-placement
    /// utility function. Fires every 30s per team; writes one JSON line per emission to
    /// UserData/RTSA/bc_metrics.jsonl containing:
    ///
    ///   { ts, map, roundT, team, cash, delta, bcCount, workerCount, patches, bcs:[...] }
    ///
    /// where each element of bcs is:
    ///
    ///   { x, z, workersInRange, patchesInRange, nearestPatchDist }
    ///
    /// Post-analysis: fit saturation and distance-decay curves by regressing
    /// team.delta on Σ per-bc features. Every soak automatically enriches the
    /// dataset — no scenario-mode needed for a first pass, though the scenario
    /// harness will give cleaner curves.
    /// </summary>
    internal static class BcMetrics
    {
        const float EMIT_INTERVAL_S  = 30f;
        /// <summary>
        /// How close a shrimp must be to count as working THIS Bio Cache.
        ///
        /// Was 500m, which counted each shrimp against every Bio Cache within
        /// half a kilometre — measured at 2.3 Bio Caches per shrimp on a real
        /// base, so 46 structures reported 425 "workers" for a team of 181.
        /// "21 sites have workers" then meant "21 sites are within 500m of
        /// somebody", which is nearly free on a clustered base and told us
        /// nothing. DrMuck, 2026-08-06: "500m in range is useless."
        ///
        /// 60m is what AlienShrimpProducer already uses for the same question
        /// when it decides whether a Bio Cache is at its worker cap, so the two
        /// now agree on what "belongs to this Bio Cache" means.
        /// </summary>
        const float WORKER_RANGE_M   = 60f;

        /// <summary>Patches this Bio Cache could plausibly be working. Matches
        /// Blueprint.BC_WORKS_M — wide enough to contain the game's placement
        /// slide, not so wide that every patch on the map counts.</summary>
        const float PATCH_RANGE_M    = 220f;

        // Per-team state.
        static readonly Dictionary<Team, float> _lastEmitAt = new Dictionary<Team, float>();
        static readonly Dictionary<Team, int>   _lastCash   = new Dictionary<Team, int>();

        internal static void TickAlien(Team team) => Tick(team, "Bio Cache", "Shrimp");
        internal static void TickHuman(Team team) => Tick(team, "Refinery", "Harvester");

        static void Tick(Team team, string bcDisplayName, string workerDisplayName)
        {
            if (team == null) return;
            float now = Time.time;
            if (_lastEmitAt.TryGetValue(team, out var last) && now - last < EMIT_INTERVAL_S) return;

            int cashNow;
            try { cashNow = team.TotalResources; } catch { return; }

            // First observation for this team — establish baseline, skip this emission.
            // Without it every first line reports a nonsense cash delta = current-cash.
            if (!_lastCash.TryGetValue(team, out var lastCash))
            {
                _lastCash[team]   = cashNow;
                _lastEmitAt[team] = now;
                return;
            }

            int delta = cashNow - lastCash;
            _lastCash[team]   = cashNow;
            _lastEmitAt[team] = now;

            // Snapshot the entities we need. Cheap — typical alien has ~10 BCs and
            // ~30 workers, and this only fires twice a minute.
            var bcPositions      = new List<Vector3>();
            var workerPositions  = new List<Vector3>();
            var patchPositions   = new List<Vector3>();
            try
            {
                var structs = team.Structures;
                if (structs != null)
                {
                    for (int i = 0; i < structs.Count; i++)
                    {
                        var s = structs[i];
                        if (s == null || s.ObjectInfo == null || s.IsDestroyed) continue;
                        if (!string.Equals(s.ObjectInfo.DisplayName, bcDisplayName, StringComparison.OrdinalIgnoreCase)) continue;
                        // The storage scaffold is not economy — a hundred spawned
                        // caches beside the Nest would otherwise read as a hundred
                        // idle sites and wreck every per-Bio-Cache number.
                        if (Faction.StorageBuffer.IsBufferPos(s.transform.position)) continue;
                        bcPositions.Add(s.transform.position);
                    }
                }
            } catch { }

            try
            {
                var units = team.Units;
                if (units != null)
                {
                    for (int i = 0; i < units.Count; i++)
                    {
                        var u = units[i];
                        if (u == null || u.ObjectInfo == null || u.IsDestroyed) continue;
                        if (!string.Equals(u.ObjectInfo.DisplayName, workerDisplayName, StringComparison.OrdinalIgnoreCase)) continue;
                        workerPositions.Add(u.transform.position);
                    }
                }
            } catch { }

            // Resource areas by type — same source the AlienConstruction planner uses.
            try
            {
                var all = ResourceArea.AllResourceAreas;
                if (all != null)
                {
                    var uType = team.UsableResource;
                    for (int i = 0; i < all.Count; i++)
                    {
                        var ra = all[i];
                        if (ra == null || ra.IsEmpty) continue;
                        if (ra.ResourceType != uType) continue;
                        patchPositions.Add(ra.SignalCenter);
                    }
                }
            } catch { }

            // Build the JSON line.
            var sb = new StringBuilder(256 + bcPositions.Count * 100);
            sb.Append('{');
            sb.Append("\"ts\":\"").Append(DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss")).Append("\",");
            sb.Append("\"map\":\"").Append(Esc(MapLayers.LayerReplay.CurrentMap)).Append("\",");
            sb.Append("\"roundT\":").Append(MapLayers.LayerReplay.CurrentRoundTime.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)).Append(',');
            sb.Append("\"team\":\"").Append(Esc(team.name)).Append("\",");
            sb.Append("\"cash\":").Append(cashNow).Append(',');
            sb.Append("\"delta\":").Append(delta).Append(',');
            sb.Append("\"windowS\":").Append(EMIT_INTERVAL_S.ToString("F0", System.Globalization.CultureInfo.InvariantCulture)).Append(',');
            sb.Append("\"bcCount\":").Append(bcPositions.Count).Append(',');
            sb.Append("\"workerCount\":").Append(workerPositions.Count).Append(',');
            sb.Append("\"patchCount\":").Append(patchPositions.Count).Append(',');
            // Self-describing, so rounds recorded either side of a radius change
            // cannot be silently averaged together.
            sb.Append("\"workerRangeM\":").Append(WORKER_RANGE_M.ToString("F0", System.Globalization.CultureInfo.InvariantCulture)).Append(',');
            sb.Append("\"patchRangeM\":").Append(PATCH_RANGE_M.ToString("F0", System.Globalization.CultureInfo.InvariantCulture)).Append(',');

            sb.Append("\"bcs\":[");
            for (int i = 0; i < bcPositions.Count; i++)
            {
                if (i > 0) sb.Append(',');
                var bp = bcPositions[i];
                int workersInRange  = CountWithin(bp, workerPositions, WORKER_RANGE_M);
                int patchesInRange  = CountWithin(bp, patchPositions,  PATCH_RANGE_M);
                float nearestPatch  = NearestDistance(bp, patchPositions);
                sb.Append('{')
                  .Append("\"x\":").Append(bp.x.ToString("F0", System.Globalization.CultureInfo.InvariantCulture)).Append(',')
                  .Append("\"z\":").Append(bp.z.ToString("F0", System.Globalization.CultureInfo.InvariantCulture)).Append(',')
                  .Append("\"workersInRange\":").Append(workersInRange).Append(',')
                  .Append("\"patchesInRange\":").Append(patchesInRange).Append(',')
                  .Append("\"nearestPatchDist\":").Append(nearestPatch.ToString("F0", System.Globalization.CultureInfo.InvariantCulture));
                // Per-Bio-Cache earnings: what was delivered here, and when the
                // first delivery landed. "deposited 0 and standing for minutes"
                // is the claimed-but-untapped case the expansion experiment is
                // trying to count.
                if (BcIncome.TryGet(bp, out long dep, out float firstT, out float builtT))
                    sb.Append(",\"deposited\":").Append(dep)
                      .Append(",\"firstDepositT\":").Append(firstT.ToString("F0", System.Globalization.CultureInfo.InvariantCulture))
                      .Append(",\"builtAtT\":").Append(builtT.ToString("F0", System.Globalization.CultureInfo.InvariantCulture));
                sb.Append('}');
            }
            sb.Append("]}");

            try
            {
                string dir = Config.Paths.LogDir;
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "bc_metrics.jsonl"), sb.ToString() + "\n");
            }
            catch (Exception ex) { MelonLogger.Warning($"[BCMETRICS] append threw: {ex.Message}"); }
        }

        static int CountWithin(Vector3 origin, List<Vector3> pts, float r)
        {
            float r2 = r * r; int n = 0;
            for (int i = 0; i < pts.Count; i++)
            {
                var d = pts[i] - origin;
                if (d.x * d.x + d.z * d.z <= r2) n++;
            }
            return n;
        }

        static float NearestDistance(Vector3 origin, List<Vector3> pts)
        {
            if (pts.Count == 0) return -1f;
            float best2 = float.MaxValue;
            for (int i = 0; i < pts.Count; i++)
            {
                var d = pts[i] - origin;
                float dsq = d.x * d.x + d.z * d.z;
                if (dsq < best2) best2 = dsq;
            }
            return Mathf.Sqrt(best2);
        }

        static string Esc(string s) => (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"");

        internal static void ResetForNewRound()
        {
            _lastEmitAt.Clear();
            _lastCash.Clear();
        }
    }
}
