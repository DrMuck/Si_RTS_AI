using MelonLoader;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;

namespace Si_RTS_AI.Mil
{
    /// <summary>
    /// THE PHYSICS TABLE, READ FROM THE BALANCE MOD'S OWN DUMP.
    ///
    /// Si_UnitBalance rewrites `UserData/UnitBalance_cfg/Si_UnitBalance_Dump.json`
    /// every time it applies a configuration, so it is the live, modded truth
    /// for cost, health, damage, cooldown, speed and cap weight — the absolute
    /// half of "fit dimensionless behaviour, read absolute physics"
    /// (MIL_V2_ARCHITECTURE section 2). Nothing here is hardcoded per unit.
    ///
    /// Where a live object is at hand its ObjectInfo wins (UnitValues,
    /// UnitCaps already do that); this table is for names we cannot touch —
    /// enemy chassis we have only seen, units above our tech — and for the two
    /// numbers ObjectInfo does not carry at all: damage per second and speed.
    ///
    /// DPS is the best of the weapon slots the dump knows about. Several
    /// chassis report zero in every slot (infantry weapons are not in the
    /// dump); those fall back to cost/10, labelled, so a raze estimate is
    /// crude rather than absent.
    /// </summary>
    internal static class UnitStats
    {
        const string PATH = "UserData/UnitBalance_cfg/Si_UnitBalance_Dump.json";

        internal class Stats
        {
            public string Name = "";
            public string Team = "";
            public bool   IsStructure;
            public int    Cost;
            public float  Hp;
            public float  Dps;
            public bool   DpsMeasured;
            public float  MoveSpeed;
            public float  FlySpeed;
            public bool   Flyer;
            public string CapType = "None";
            public int    CapValue;
            public int    MinTier = -1;
            public float  Range;
        }

        static readonly Dictionary<string, Stats> _byName =
            new Dictionary<string, Stats>(StringComparer.OrdinalIgnoreCase);
        static DateTime _lastWrite;
        internal static bool Loaded { get; private set; }

        internal static void Reload()
        {
            try
            {
                if (!File.Exists(PATH))
                {
                    if (Loaded) MilLog.Msg("[MIL/STATS] balance dump gone — falling back to cost/10 dps");
                    _byName.Clear(); Loaded = false; return;
                }
                var stamp = File.GetLastWriteTimeUtc(PATH);
                if (Loaded && stamp == _lastWrite) return;
                _lastWrite = stamp;

                var root = JObject.Parse(File.ReadAllText(PATH));
                var units = root["units"] as JArray;
                if (units == null) return;
                _byName.Clear();
                int measured = 0;
                foreach (var u in units)
                {
                    var o = u as JObject; if (o == null) continue;
                    var s = new Stats
                    {
                        Name        = (string)(o["name"] ?? ""),
                        Team        = (string)(o["team"] ?? ""),
                        IsStructure = (bool)(o["is_structure"] ?? false),
                        Cost        = (int)(o["cost"] ?? 0),
                        Hp          = (float)(o["hp"] ?? 0f),
                        MoveSpeed   = (float)(o["move_speed"] ?? 0f),
                        FlySpeed    = (float)(o["fly_speed"] ?? 0f),
                        CapType     = (string)(o["unit_cap_type"] ?? "None"),
                        CapValue    = (int)(o["unit_cap_value"] ?? 0),
                        MinTier     = (int)(o["min_tier"] ?? -1),
                        Range       = (float)(o["atk_range"] ?? 0f),
                    };
                    if (string.IsNullOrEmpty(s.Name)) continue;
                    float dps = 0f;
                    dps = Math.Max(dps, Rate(o, "atk_damage", "atk_cooldown"));
                    dps = Math.Max(dps, Rate(o, "atk2_damage", "atk2_cooldown"));
                    dps = Math.Max(dps, Rate(o, "vt_impact_dmg", "vt_fire_interval"));
                    dps = Math.Max(dps, Rate(o, "vt2_impact_dmg", "vt2_fire_interval"));
                    // Ground moves at MoveSpeed; the flyers are the chassis whose
                    // air speed is the real one. 30 m/s and twice the ground
                    // figure separates Wasp/Squid/Dragonfly/Firebug from the
                    // generic 16 every walker carries.
                    s.Flyer = s.FlySpeed >= 30f && s.FlySpeed > 2f * s.MoveSpeed;
                    if (dps > 0f) { s.Dps = dps; s.DpsMeasured = true; measured++; }
                    else s.Dps = Math.Max(1f, s.Cost / 10f);
                    _byName[s.Name] = s;
                }
                Loaded = _byName.Count > 0;
                MilLog.Msg($"[MIL/STATS] loaded {PATH}: {_byName.Count} entries, dps measured for {measured}");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[MIL/STATS] {PATH} unreadable ({ex.Message})");
            }
        }

        static float Rate(JObject o, string dmgKey, string cdKey)
        {
            float d = (float)(o[dmgKey] ?? 0f), c = (float)(o[cdKey] ?? 0f);
            return d > 0f && c > 0.01f ? d / c : 0f;
        }

        internal static bool TryGet(string name, out Stats s) =>
            _byName.TryGetValue(name ?? "", out s);

        internal static float DpsOf(string name)
        {
            if (_byName.TryGetValue(name ?? "", out var s)) return s.Dps;
            int cost = 0;
            try { cost = Perception.UnitValues.CostOf(name); } catch { }
            return Math.Max(1f, cost / 10f);
        }

        internal static float HpOf(string name) =>
            _byName.TryGetValue(name ?? "", out var s) && s.Hp > 0f ? s.Hp : 1000f;

        /// <summary>Speed the unit actually travels at — air speed for flyers.</summary>
        internal static float SpeedOf(string name)
        {
            if (_byName.TryGetValue(name ?? "", out var s))
                return s.Flyer ? s.FlySpeed : (s.MoveSpeed > 0f ? s.MoveSpeed : 9f);
            return 9f;
        }

        internal static bool IsFlyer(string name) =>
            _byName.TryGetValue(name ?? "", out var s) && s.Flyer;

        internal static int CapWeightOf(string name) =>
            _byName.TryGetValue(name ?? "", out var s) ? s.CapValue : 0;

        internal static string CapTypeOf(string name) =>
            _byName.TryGetValue(name ?? "", out var s) ? s.CapType : "None";

        internal static IEnumerable<Stats> All() => _byName.Values;
    }
}
