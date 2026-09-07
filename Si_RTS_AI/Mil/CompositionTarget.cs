using System;
using System.Collections.Generic;
using System.IO;
using MelonLoader;
using UnityEngine;

namespace Si_RTS_AI.Mil
{
    /// <summary>
    /// WHAT STRONG COMMANDERS FIELD, AS A TARGET MIX. The production pick is an
    /// argmax over doctrine value, so the best-valued chassis of a pool is the
    /// only one ever queued: 97 to 135 Behemoths a round and no Scorpion at
    /// all, while the top quartile of human commanders in the replay archive
    /// fields about 36 Behemoths, 19 Scorpions and 3 Colossi (DrMuck,
    /// 2026-09-07: "no Scorps and Colossus were built at all").
    ///
    /// This reads the archive's per-round counts for the top quartile and turns
    /// them into shares. A unit fielded below its share gets its pick score
    /// lifted, up to LIFT_MAX; a unit fielded above it is eased down, but never
    /// below EASE_MIN, and heavies (cost at or above HEAVY_COST) are never
    /// eased at all: commanders may build far more Colossi than the archive
    /// average, and a ceiling on heavies would be wrong (DrMuck). The target is
    /// a prior, not a rule; the learning loop is expected to replace it with
    /// what more games show.
    ///
    /// Source: UserData/commander_compositions.csv, columns
    /// faction,unit,cost,per_round_top,... — only the alien rows are read.
    /// </summary>
    internal static class CompositionTarget
    {
        const string PATH      = "UserData/commander_compositions.csv";
        const float  LIFT_MAX  = 2.5f;
        const float  EASE_MIN  = 0.5f;
        const int    HEAVY_COST = 3000;
        const int    MIN_SAMPLE = 8;     // our own picks before shares mean anything

        static readonly Dictionary<string, float> _targetShare = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        static readonly Dictionary<string, int>   _cost        = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        static readonly Dictionary<string, int>   _ours        = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        static int _oursTotal;
        static bool _loaded, _tried;

        internal static bool Loaded => _loaded;

        internal static void ResetForNewRound()
        {
            _ours.Clear(); _oursTotal = 0;
            if (!_tried) Load();
        }

        static void Load()
        {
            _tried = true;
            try
            {
                if (!File.Exists(PATH)) { MelonLogger.Msg("[MIL/COMP] no " + PATH + " — composition target off"); return; }
                float total = 0f;
                var raw = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
                foreach (var line in File.ReadAllLines(PATH))
                {
                    var f = line.Split(',');
                    if (f.Length < 4 || !string.Equals(f[0], "Alien", StringComparison.OrdinalIgnoreCase)) continue;
                    string unit = f[1].Trim();
                    if (unit == "Shrimp" || unit == "Queen") continue;
                    if (!float.TryParse(f[3], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float perRound)) continue;
                    if (int.TryParse(f[2], out int cost)) _cost[unit] = cost;
                    raw[unit] = perRound; total += perRound;
                }
                if (total <= 0f) return;
                var sb = new System.Text.StringBuilder("[MIL/COMP] target mix from top-quartile commanders:");
                foreach (var kv in raw)
                {
                    _targetShare[kv.Key] = kv.Value / total;
                    sb.Append(' ').Append(kv.Key).Append(' ').Append((kv.Value / total * 100f).ToString("F0")).Append('%');
                }
                _loaded = true;
                MelonLogger.Msg(sb.ToString());
            }
            catch (Exception ex) { MelonLogger.Warning("[MIL/COMP] load threw: " + ex.Message); }
        }

        /// <summary>Record one of our own picks.</summary>
        internal static void NoteQueued(string unit)
        {
            if (string.IsNullOrEmpty(unit)) return;
            _ours.TryGetValue(unit, out int n); _ours[unit] = n + 1; _oursTotal++;
        }

        /// <summary>Multiplier on the pick score: above 1 while we field less of this unit than the target, below 1 (floored) when more; heavies never eased.</summary>
        internal static float Multiplier(string unit, int cost)
        {
            if (!_loaded || string.IsNullOrEmpty(unit)) return 1f;
            if (!_targetShare.TryGetValue(unit, out float target) || target <= 0f) return 1f;
            if (_oursTotal < MIN_SAMPLE) return Mathf.Clamp(target * 10f, 1f, LIFT_MAX);   // early: lift the rarer chassis a little
            _ours.TryGetValue(unit, out int n);
            float ours = n / (float)_oursTotal;
            if (ours <= 0f) return LIFT_MAX;
            float m = target / ours;
            if (m >= 1f) return Mathf.Min(m, LIFT_MAX);
            if (cost >= HEAVY_COST) return 1f;
            return Mathf.Max(m, EASE_MIN);
        }

        internal static string Summary()
        {
            if (_oursTotal == 0) return "";
            var sb = new System.Text.StringBuilder();
            foreach (var kv in _ours) sb.Append(kv.Key).Append('×').Append(kv.Value).Append(' ');
            return sb.ToString();
        }
    }
}
