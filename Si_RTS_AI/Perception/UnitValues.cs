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

            _cost[unitName] = found;
            return found;
        }
    }
}
