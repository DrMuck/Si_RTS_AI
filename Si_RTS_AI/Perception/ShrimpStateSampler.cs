using MelonLoader;
using Silica;
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEngine;

namespace Si_RTS_AI.Perception
{
    /// <summary>
    /// Per-second sampler for controlled saturation/distance tests. For each managed
    /// team, emits one line per tick to UserData/RTSA/shrimp_states.jsonl containing:
    ///
    ///   { ts, roundT, team, cash, delta,
    ///     workers:[{name, x, z, stored, storedMax}, ...],
    ///     bcs:[{name, x, z, stored, storedMax}, ...] }
    ///
    /// Purpose: watch `stored` transition from 0 → carry_capacity while harvesting,
    /// then carry_capacity → 0 while depositing. The slopes give harvest and
    /// deposit rates directly. Team cash delta over the same window plus worker
    /// count gives the empirical saturation curve.
    ///
    /// Manual test protocol:
    ///   1. Suppress the alien AI (SuppressAlienAI planned or /rtsai override off).
    ///   2. Spawn 1 BC + 1..N shrimps at controlled distance from a biotics patch.
    ///   3. Let the sampler run for a couple of minutes.
    ///   4. Repeat for shrimp counts {1, 10, 15, 20} × distances {near, mid, far}.
    ///
    /// Fires from Si_RTS_AI.PeriodicTelemetryTick, so it runs at 1Hz regardless
    /// of whether a human or AI is in the commander seat.
    /// </summary>
    internal static class ShrimpStateSampler
    {
        const float SAMPLE_INTERVAL_S = 1f;

        /// <summary>
        /// Per-shrimp positions, once a second. Useful when characterising
        /// migration; 4.2 GB over a nineteen-hour soak otherwise, and none of
        /// the eco benchmarks read it. Off unless something asks for it.
        /// </summary>
        internal static bool Enabled;

        // Names that count as "workers" (resource carriers) and "banks" (deposit points).
        // Matches the same categories BcMetrics/AlienConstruction use, so alien
        // shrimps + human harvester variants both get logged.
        static readonly HashSet<string> WORKER_NAMES = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "Shrimp", "Harvester", "HoverHarvester", "HeavyHarvester" };
        static readonly HashSet<string> BANK_NAMES = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "Bio Cache", "BioCache", "Refinery" };

        // Reflection cache — reading ResourceHolders[0].AmountStored and MaxAmount
        // via reflection is idiomatic for how we've had to reach the storage state
        // (same reason the dumper couldn't use strongly-typed API for these).
        static PropertyInfo? _piResourceHolders;
        static PropertyInfo? _piAmountStored;
        static FieldInfo?    _fiMaxAmount;

        static readonly Dictionary<Team, float> _lastSampleAt = new Dictionary<Team, float>();
        static readonly Dictionary<Team, int>   _lastCash     = new Dictionary<Team, int>();

        internal static void ResetForNewRound()
        {
            _lastSampleAt.Clear();
            _lastCash.Clear();
        }

        internal static void Tick(Team team)
        {
            if (team == null) return;
            float now = Time.time;
            if (!Enabled) return;
            if (_lastSampleAt.TryGetValue(team, out var last) && now - last < SAMPLE_INTERVAL_S) return;
            _lastSampleAt[team] = now;

            int cash;
            try { cash = team.TotalResources; } catch { return; }
            int prevCash = _lastCash.TryGetValue(team, out var pc) ? pc : cash;
            int delta = cash - prevCash;
            _lastCash[team] = cash;

            var workers = new List<string>();
            var banks   = new List<string>();

            try
            {
                var units = team.Units;
                if (units != null)
                {
                    for (int i = 0; i < units.Count; i++)
                    {
                        var u = units[i];
                        if (u == null || u.ObjectInfo == null || u.IsDestroyed) continue;
                        string name = u.ObjectInfo.DisplayName ?? "";
                        if (!WORKER_NAMES.Contains(name)) continue;
                        Vector3 p = u.transform.position;
                        (int stored, int storedMax) = ReadHolderState(u);
                        workers.Add(FormatEntity(name, p, stored, storedMax));
                    }
                }
            }
            catch { }

            try
            {
                var structs = team.Structures;
                if (structs != null)
                {
                    for (int i = 0; i < structs.Count; i++)
                    {
                        var s = structs[i];
                        if (s == null || s.ObjectInfo == null || s.IsDestroyed) continue;
                        string name = s.ObjectInfo.DisplayName ?? "";
                        if (!BANK_NAMES.Contains(name)) continue;
                        Vector3 p = s.transform.position;
                        (int stored, int storedMax) = ReadHolderState(s);
                        banks.Add(FormatEntity(name, p, stored, storedMax));
                    }
                }
            }
            catch { }

            if (workers.Count == 0 && banks.Count == 0) return;   // nothing to log for this team yet

            var sb = new StringBuilder(128 + workers.Count * 60 + banks.Count * 60);
            sb.Append('{');
            sb.Append("\"ts\":\"").Append(DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss.fff")).Append("\",");
            sb.Append("\"roundT\":").Append(MapLayers.LayerReplay.CurrentRoundTime.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)).Append(',');
            sb.Append("\"team\":\"").Append(Esc(team.name)).Append("\",");
            sb.Append("\"cash\":").Append(cash).Append(',');
            sb.Append("\"delta\":").Append(delta).Append(',');
            sb.Append("\"workers\":[").Append(string.Join(",", workers)).Append("],");
            sb.Append("\"bcs\":[").Append(string.Join(",", banks)).Append(']');
            sb.Append('}');

            try
            {
                string dir = Path.Combine("UserData", "RTSA");
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "shrimp_states.jsonl"), sb.ToString() + "\n");
            }
            catch (Exception ex) { MelonLogger.Warning($"[SHRIMPSMP] append threw: {ex.Message}"); }
        }

        static string FormatEntity(string name, Vector3 pos, int stored, int storedMax)
        {
            return "{"
                + "\"name\":\"" + Esc(name) + "\","
                + "\"x\":"      + pos.x.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) + ","
                + "\"z\":"      + pos.z.ToString("F1", System.Globalization.CultureInfo.InvariantCulture) + ","
                + "\"stored\":" + stored + ","
                + "\"storedMax\":" + storedMax
                + "}";
        }

        // ResourceHolders[0] is the standard slot for both units and structures.
        // Returns (0, 0) if the entity has no storage or reflection fails — the
        // resulting log line still has meaningful position info.
        static (int stored, int max) ReadHolderState(object entity)
        {
            try
            {
                if (_piResourceHolders == null)
                {
                    _piResourceHolders = entity.GetType().GetProperty("ResourceHolders",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                }
                if (_piResourceHolders == null) return (0, 0);
                var list = _piResourceHolders.GetValue(entity) as System.Collections.IEnumerable;
                if (list == null) return (0, 0);
                object? holder = null;
                foreach (var h in list) { holder = h; break; }
                if (holder == null) return (0, 0);

                // Cache the AmountStored property and MaxAmount field on first access.
                if (_piAmountStored == null)
                {
                    _piAmountStored = holder.GetType().GetProperty("AmountStored",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                }
                if (_fiMaxAmount == null)
                {
                    _fiMaxAmount = holder.GetType().GetField("MaxAmount",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                }
                int stored = _piAmountStored != null ? (int)(_piAmountStored.GetValue(holder) ?? 0) : 0;
                int max    = _fiMaxAmount    != null ? (int)(_fiMaxAmount.GetValue(holder)    ?? 0) : 0;
                return (stored, max);
            }
            catch { return (0, 0); }
        }

        static string Esc(string s) => (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"");
    }
}
