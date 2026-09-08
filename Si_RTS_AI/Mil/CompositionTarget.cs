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

        // PER FACTION. The first Sol round under the mod (2026-09-07 20:12) queued
        // Barrage Trucks and Commandos by the hundred: only the alien rows were
        // read, so Sol ran on the fitted values alone (Commando 6.2, Barrage Truck
        // above every tank). Tables are keyed by the faction column of the CSV.
        static readonly Dictionary<string, Dictionary<string, float>> _targetByFaction =
            new Dictionary<string, Dictionary<string, float>>(StringComparer.OrdinalIgnoreCase);
        static string _faction = "Alien";
        static Dictionary<string, float> _targetShare = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        static readonly Dictionary<string, int>   _cost        = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        static readonly Dictionary<string, int>   _ours        = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        static int _oursTotal;
        static bool _loaded, _tried;

        internal static bool Loaded => _loaded;

        internal static void ResetForNewRound()
        {
            _ours.Clear(); _oursTotal = 0; _oursSupport = 0;
            if (!_tried) Load();
        }

        /// <summary>Select the table for the team being produced for ("Alien", "Sol", "Centauri" from the team name).</summary>
        internal static void UseTeam(Team team)
        {
            string n = team?.name ?? "";
            string f = n.IndexOf("Sol", StringComparison.OrdinalIgnoreCase) >= 0 ? "Sol"
                     : n.IndexOf("Cent", StringComparison.OrdinalIgnoreCase) >= 0 ? "Centauri" : "Alien";
            if (f == _faction) return;
            // Counts are not wiped on a faction switch: the military context swaps
            // this class's fields per team, so each team keeps its own tally.
            _faction = f;
            _targetShare = _targetByFaction.TryGetValue(f, out var t) ? t : new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        }

        static void Load()
        {
            _tried = true;
            try
            {
                if (!File.Exists(PATH)) { MelonLogger.Msg("[MIL/COMP] no " + PATH + " — composition target off"); return; }
                var rawByFaction = new Dictionary<string, Dictionary<string, float>>(StringComparer.OrdinalIgnoreCase);
                foreach (var line in File.ReadAllLines(PATH))
                {
                    var f = line.Split(',');
                    if (f.Length < 4 || f[0] == "faction") continue;
                    string faction = f[0].Trim(); string unit = f[1].Trim();
                    if (unit == "Shrimp" || unit == "Queen" || unit.IndexOf("Harvester", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                    if (!float.TryParse(f[3], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float perRound)) continue;
                    if (int.TryParse(f[2], out int cost)) _cost[unit] = cost;
                    if (!rawByFaction.TryGetValue(faction, out var raw)) rawByFaction[faction] = raw = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
                    raw[unit] = perRound;
                }
                foreach (var fk in rawByFaction)
                {
                    // CASH SHARES, NOT UNIT COUNTS. Count shares made a Sol round queue 48
                    // Commandos and 33 Light Strikers against 9 Hover Tanks (2026-09-07
                    // 22:04): a 40-cash Rifleman weighed the same as a 4,000-cash Railgun.
                    // The target is what strong commanders SPEND on each chassis.
                    float total = 0f;
                    foreach (var kv in fk.Value) total += kv.Value * Mathf.Max(1, CostOf(kv.Key));
                    if (total <= 0f) continue;
                    var shares = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
                    var sb = new System.Text.StringBuilder($"[MIL/COMP] {fk.Key} target mix by cash from top-quartile commanders:");
                    foreach (var kv in fk.Value)
                    {
                        float sh = kv.Value * Mathf.Max(1, CostOf(kv.Key)) / total;
                        shares[kv.Key] = sh;
                        sb.Append(' ').Append(kv.Key).Append(' ').Append((sh * 100f).ToString("F0")).Append('%');
                    }
                    _targetByFaction[fk.Key] = shares;
                    MelonLogger.Msg(sb.ToString());
                }
                if (_targetByFaction.Count == 0) return;
                _targetShare = _targetByFaction.TryGetValue(_faction, out var cur) ? cur : _targetShare;
                _loaded = true;
            }
            catch (Exception ex) { MelonLogger.Warning("[MIL/COMP] load threw: " + ex.Message); }
        }

        /// <summary>Record one of our own picks, weighted by cash.</summary>
        internal static void NoteQueued(string unit)
        {
            if (string.IsNullOrEmpty(unit)) return;
            int c = Mathf.Max(1, CostOf(unit));
            _ours.TryGetValue(unit, out int n); _ours[unit] = n + c; _oursTotal += c;
            if (IsSupport(unit)) _oursSupport += c;
        }
        static int CostOf(string unit) => _cost.TryGetValue(unit ?? "", out int c) ? c : 0;
        // SUPPORT IS CAPPED. Repair rigs, trucks, transports and artillery support
        // the line, they are not the line: 22 Repair Rigs in one round (DrMuck).
        static readonly HashSet<string> SUPPORT = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "Repair Rig", "Repair Truck", "Barrage Truck", "AA Truck", "Flak Car", "Platoon Hauler", "Squad Transport", "Troop Transport", "Pulse Truck", "Shuttle", "Freighter", "Dropship" };
        const float SUPPORT_SHARE_MAX = 0.10f;
        static int _oursSupport;
        internal static bool IsSupport(string unit) => SUPPORT.Contains(unit ?? "");

        /// <summary>Multiplier on the pick score: above 1 while we field less of this unit than the target, below 1 (floored) when more; heavies never eased.</summary>
        internal static float Multiplier(string unit, int cost)
        {
            if (string.IsNullOrEmpty(unit)) return 1f;
            if (IsSupport(unit) && _oursTotal > 0 && _oursSupport / (float)_oursTotal >= SUPPORT_SHARE_MAX) return 0.2f;
            if (!_loaded) return 1f;
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
