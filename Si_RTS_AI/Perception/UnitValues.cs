using Silica;
using System;
using System.Collections.Generic;

namespace Si_RTS_AI.Perception
{
    /// <summary>
    /// What a unit is worth, in cash, read from the game.
    ///
    /// Two consumers wanted the same number for different reasons — the combat
    /// log prices losses so exchange ratios mean something, and the battalion
    /// manager measures a battalion's strength in value rather than bodies,
    /// because fifteen Crabs and fifteen Behemoths are not the same army. Same
    /// lookup, so it lives once.
    ///
    /// Live ConstructionData, like every other cost in this project: a balance
    /// mod changes these and a constant in our source could not know.
    ///
    /// TWO SOURCES, AND THE SECOND ONE MATTERS MORE THAN IT LOOKS. Asking what
    /// WE can build only prices units we can build. Read against the 661 rows
    /// combat.jsonl had collected by 2026-08-07, that left every enemy unit and
    /// every alien unit above our current tech at value 0 — Militia, Rifleman,
    /// Hunter, Shocker, both Quads, both Raiders — which is to say the exchange
    /// ratio the whole military layer is supposed to calibrate against was
    /// silently reading zero on one side of most engagements.
    ///
    /// So the fallback is the unit's own ObjectInfo.Cost, taken off a live unit
    /// of that name wherever it stands. It is the same field ThreatMap already
    /// prices enemy structures with, and it needs no permission to build.
    /// </summary>
    internal static class UnitValues
    {
        static readonly Dictionary<string, int> _cost =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        internal static void ResetForNewRound() => _cost.Clear();

        internal static int CostOf(string unitName)
        {
            if (string.IsNullOrEmpty(unitName)) return 0;
            if (_cost.TryGetValue(unitName, out int c)) return c;

            int found = 0;
            try
            {
                foreach (var kv in Silica.AI.AIManager.Commanders)
                {
                    var structs = kv.Key?.Structures;
                    if (structs == null) continue;
                    for (int i = 0; i < structs.Count && found == 0; i++)
                    {
                        var opts = structs[i]?.ConstructionOptions;
                        if (opts == null) continue;
                        foreach (var cd in opts)
                        {
                            if (cd?.ObjectInfo == null) continue;
                            if (!string.Equals(cd.ObjectInfo.DisplayName, unitName,
                                               StringComparison.OrdinalIgnoreCase)) continue;
                            try { found = cd.ResourceCost; } catch { }
                            break;
                        }
                    }
                    if (found > 0) break;
                }
            }
            catch { }

            if (found == 0) found = FromLiveUnit(unitName);

            // A zero that survived both lookups is cached anyway — retrying it
            // every engagement would walk every roster on the map for a unit
            // the game genuinely prices at nothing.
            _cost[unitName] = found;
            return found;
        }

        /// <summary>Cost off any live unit carrying that name, whoever owns it.
        /// This is how enemy units get priced at all.</summary>
        static int FromLiveUnit(string unitName)
        {
            try
            {
                foreach (var kv in Silica.AI.AIManager.Commanders)
                {
                    var units = kv.Key?.Units;
                    if (units == null) continue;
                    for (int i = 0; i < units.Count; i++)
                    {
                        var u = units[i];
                        if (u?.ObjectInfo == null) continue;
                        if (!string.Equals(u.ObjectInfo.DisplayName, unitName,
                                           StringComparison.OrdinalIgnoreCase)) continue;
                        try { return u.ObjectInfo.Cost; } catch { return 0; }
                    }
                }
            }
            catch { }
            return 0;
        }
    }
}
