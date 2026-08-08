using MelonLoader;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Si_RTS_AI.Planning
{
    /// <summary>
    /// WHAT TO BUILD, ACCORDING TO 2,619 RECORDED GAMES.
    ///
    /// The production rule was "the most expensive thing this producer offers",
    /// on the theory that cost is the game's own statement about tier. It is,
    /// and tier is not the same as useful: at a Lesser Spawning Cyst that rule
    /// buys Dragonflies, which trade at 0.20 under commander AI. The first
    /// played round bought 26 of them.
    ///
    /// So the ordering now comes from what actually happened on the main server.
    /// Two numbers per unit, both measured with AI on BOTH sides of the fight,
    /// because that is the only mode this bot is ever in:
    ///
    ///   EFFECTIVENESS  kill/death. Firebug reads 4.46 in a player's hands and
    ///                  0.81 here — the aggregate figure is a measure of who was
    ///                  driving, not of the chassis.
    ///   COUNTERS       cash destroyed per cash lost against each class of enemy.
    ///                  Priced in cash rather than kills on purpose: a Scorpion
    ///                  kills Militia 367 to 1, which says infantry are cheap and
    ///                  nothing about countering. In cash that matchup is 0.97.
    ///
    /// DIVIDED BY CAP, NOT BY COST. Both caps are real and the bank runs 100k+
    /// unspent from minute twelve, so the question at a producer is what a SLOT
    /// is worth. On that measure Behemoth (2.42 over 2 cap) beats Colossus (1.18
    /// over 15) by fifteen to one, and Colossus is four times the price.
    ///
    /// A PRIOR, NOT AN ANSWER. It is a JSON file so a human can read it, argue
    /// with it, and edit it between rounds — and so the feedback loop can
    /// eventually rewrite it from what this bot measures itself. That is the
    /// reason it is a table and not a model: when the bot builds the wrong thing
    /// at minute fourteen you have to be able to read why.
    ///
    /// Missing file, missing unit or missing matchup all mean UNMEASURED rather
    /// than bad, and fall back to neutral. The mod must not refuse to build
    /// something merely because the archive never saw it.
    /// </summary>
    internal static class UnitPrior
    {
        const string PATH = "UserData/rtsai_units.json";

        internal class Entry
        {
            public int    Cost;
            public string CapType = "None";
            public int    Cap;
            public float  Effectiveness = 1f;
            public int    Built;
            public readonly Dictionary<string, float> Counters =
                new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        }

        static readonly Dictionary<string, Entry> _units =
            new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        static readonly Dictionary<string, string> _enemyClass =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        internal static bool Loaded { get; private set; }
        static DateTime _lastWrite;

        /// <summary>Neutral score for anything the archive never priced. Sits
        /// between the good units and the bad ones so an unmeasured chassis is
        /// tried rather than ignored, and does not beat a proven one.</summary>
        const float UNMEASURED = 0.5f;

        internal static void Reload()
        {
            try
            {
                if (!File.Exists(PATH))
                {
                    if (Loaded) MelonLogger.Msg("[MIL/PRIOR] rtsai_units.json removed — " +
                                                "falling back to most-expensive-option");
                    _units.Clear(); _enemyClass.Clear(); ArmyPlan.SetCurve(null); Loaded = false;
                    return;
                }
                var stamp = File.GetLastWriteTimeUtc(PATH);
                if (Loaded && stamp == _lastWrite) return;
                _lastWrite = stamp;

                var root = JObject.Parse(File.ReadAllText(PATH));
                _units.Clear(); _enemyClass.Clear();

                var us = root["units"] as JObject;
                if (us != null)
                    foreach (var p in us.Properties())
                    {
                        var o = p.Value as JObject;
                        if (o == null) continue;
                        var en = new Entry
                        {
                            Cost          = (int)   (o["cost"]          ?? 0),
                            CapType       = (string)(o["capType"]       ?? "None"),
                            Cap           = (int)   (o["cap"]           ?? 0),
                            Effectiveness = (float) (o["effectiveness"] ?? 1f),
                            Built         = (int)   (o["built"]         ?? 0),
                        };
                        var cs = o["counters"] as JObject;
                        if (cs != null)
                            foreach (var c in cs.Properties())
                                en.Counters[c.Name] = (float)c.Value;
                        _units[p.Name] = en;
                    }

                var curve = root["armyCurve"] as JObject;
                var pts = new Dictionary<string, int>();
                if (curve != null)
                    foreach (var c in curve.Properties())
                        pts[c.Name] = (int)c.Value;
                ArmyPlan.SetCurve(pts);

                var ec = root["enemyClasses"] as JObject;
                if (ec != null)
                    foreach (var p in ec.Properties())
                        _enemyClass[p.Name] = (string)p.Value;

                Loaded = _units.Count > 0;
                MelonLogger.Msg($"[MIL/PRIOR] loaded {PATH}: {_units.Count} units, " +
                                $"{_enemyClass.Count} enemy chassis classified, " +
                                $"armyCurve {(ArmyPlan.HasCurve ? "present" : "absent")}" +
                                (root["_source"]?["generated"] != null
                                    ? $" (generated {root["_source"]["generated"]})" : ""));
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[MIL/PRIOR] {PATH} could not be read ({ex.Message}) — " +
                                    "falling back to most-expensive-option");
                _units.Clear(); _enemyClass.Clear(); Loaded = false;
            }
        }

        /// <summary>Which class of problem an enemy chassis is, or null if the
        /// table has never heard of it.</summary>
        internal static string ClassOf(string enemyUnit) =>
            _enemyClass.TryGetValue(enemyUnit ?? "", out var c) ? c : null;

        /// <summary>
        /// What one more of this unit is worth right now, per cap point.
        ///
        /// The counter term is the average of this unit's exchange against the
        /// classes we can actually SEE, weighted by how much of each is out
        /// there. See nothing and it is neutral, which leaves raw effectiveness
        /// per cap deciding — and that is the right default, because it is what
        /// the unit does against whatever turns up.
        /// </summary>
        internal static float Score(string unitName, IDictionary<string, float> enemyMix,
                                    out string why)
        {
            why = "unmeasured";
            if (!_units.TryGetValue(unitName ?? "", out var en))
                return UNMEASURED;

            float fit = 1f;
            if (enemyMix != null && enemyMix.Count > 0 && en.Counters.Count > 0)
            {
                float wsum = 0f, acc = 0f;
                foreach (var kv in enemyMix)
                {
                    if (kv.Value <= 0f) continue;
                    // A class this unit was never priced against contributes its
                    // neutral 1.0 rather than dropping out, so a unit is not
                    // rewarded for having thin data.
                    float v = en.Counters.TryGetValue(kv.Key, out var c) ? c : 1f;
                    acc  += v * kv.Value;
                    wsum += kv.Value;
                }
                if (wsum > 0f) fit = acc / wsum;
            }

            int cap = Mathf.Max(1, en.Cap);
            float score = en.Effectiveness * fit / cap;
            why = $"eff {en.Effectiveness:F2} x fit {fit:F2} / cap {cap} = {score:F3}";
            return score;
        }

        internal static bool Knows(string unitName) => _units.ContainsKey(unitName ?? "");
    }
}
