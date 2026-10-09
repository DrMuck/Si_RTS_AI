using HarmonyLib;
using MelonLoader;
using Silica.AI;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Si_RTS_AI
{
    /// <summary>
    /// Phase 2 observability additions:
    ///   1. Per-team accumulators for structures spawned, units spawned, structures
    ///      destroyed, and handler-level metrics (see HandlerMetrics).
    ///   2. Per-tick handler observability — postfixes on AIConstructionHandler.Think()
    ///      and AIUnitHandler.Think() to log what the AI THINKS it can build and what
    ///      preset composition it wants. Explains WHY the AI stalls out on 3 refineries
    ///      + 0 factories (last round's key finding).
    ///   3. Round-end summary block appended when a new scene loads. Answers questions
    ///      like: "which team peaked at what tech tier", "how many orders per unit
    ///      produced", "how often did AIConstructionHandler have zero BuildableStructures
    ///      because it was resource-starved".
    ///
    /// All observability, still no gameplay change.
    /// </summary>
    internal static class Phase2
    {
        // Per-team accumulators — reset in ClearForNewRound(), summary flushed by
        // WriteRoundSummary().
        static readonly Dictionary<Team, RoundStats> _stats = new Dictionary<Team, RoundStats>();

        internal class RoundStats
        {
            public string TeamName = "?";

            // Structures (from GameEvents.OnStructureSpawned/Destroyed).
            public int StructuresSpawned, StructuresLost;
            public Dictionary<string, int> StructureBuiltByName = new Dictionary<string, int>();
            public Dictionary<string, int> StructureLostByName  = new Dictionary<string, int>();

            // Units (from GameEvents.OnUnitSpawned/Destroyed).
            public int UnitsSpawned, UnitsLost;
            public Dictionary<string, int> UnitBuiltByName = new Dictionary<string, int>();
            public Dictionary<string, int> UnitLostByName  = new Dictionary<string, int>();

            // Orders (from GameEvents.OnUnitReceivedAttack/Move/StopOrder — attributed
            // per team in the callback). Split by kind + attribution.
            public int OrdersCommanderAi_Move, OrdersCommanderAi_Attack, OrdersCommanderAi_Stop;
            public int OrdersCommanderPlayer_Move, OrdersCommanderPlayer_Attack, OrdersCommanderPlayer_Stop;
            public int OrdersPiloted_Move, OrdersPiloted_Attack, OrdersPiloted_Stop;

            // Handler-level per-tick observations (average → sample count).
            // Construction handler.
            public int ConstructionTicksObserved;
            public long CumulBuildableStructures;      // sum of BuildableStructures.Count each tick
            public long CumulBuildableUnits;           // sum of BuildableUnits.Count each tick
            public long CumulBuildableTechUpgrades;    // sum of BuildableTechTierUpgrades.Count each tick
            public long CumulMissingResources;         // sum of LastMissingResources each tick (how starved on average)
            public int  TicksWithZeroBuildableStructs; // ticks where the AI couldn't build ANYTHING (bad — means resource starved or tech-locked)

            // Unit handler.
            public int UnitHandlerTicksObserved;
            public long CumulCombatPresets;
            public long CumulExplorationPresets;
            public long CumulHarvestPresets;
        }

        // ==== Called by main mod on scene load ====

        internal static void ClearForNewRound()
        {
            _stats.Clear();
        }

        // ==== Accessor for main-mod callbacks ====

        internal static RoundStats Get(Team team)
        {
            if (team == null) return _stubStats;   // don't drop null-team events, just bucket them
            if (!_stats.TryGetValue(team, out var s))
            {
                s = new RoundStats { TeamName = ResolveTeamName(team) };
                _stats[team] = s;
            }
            return s;
        }
        static readonly RoundStats _stubStats = new RoundStats { TeamName = "Team?" };

        // ==== Handler observability (Harmony postfixes) ====

        // AIConstructionHandler.Think() runs INSIDE AICommander.Think(), one per tick.
        // After it runs, its public lists (BuildableStructures/Units/TechUpgrades) plus
        // LastMissingResources describe what the AI evaluated this tick.
        [HarmonyPatch(typeof(AIConstructionHandler), nameof(AIConstructionHandler.Think))]
        internal static class Patch_AIConstructionHandler_Think
        {
            static void Postfix(AIConstructionHandler __instance)
            { if (!Config.ModSwitches.Enabled) return;
                try
                {
                    var team = __instance?.Commander?.Team;
                    if (team == null) return;
                    var s = Get(team);
                    s.ConstructionTicksObserved++;
                    int nStructs = __instance.BuildableStructures?.Count ?? 0;
                    int nUnits   = __instance.BuildableUnits?.Count ?? 0;
                    int nTech    = __instance.BuildableTechTierUpgrades?.Count ?? 0;
                    s.CumulBuildableStructures += nStructs;
                    s.CumulBuildableUnits      += nUnits;
                    s.CumulBuildableTechUpgrades += nTech;
                    s.CumulMissingResources    += __instance.LastMissingResources;
                    if (nStructs == 0) s.TicksWithZeroBuildableStructs++;
                }
                catch (Exception ex) { MelonLogger.Warning("[RTSA/P2] Construction postfix threw: " + ex.Message); }
            }
        }

        [HarmonyPatch(typeof(AIUnitHandler), nameof(AIUnitHandler.Think))]
        internal static class Patch_AIUnitHandler_Think
        {
            static void Postfix(AIUnitHandler __instance)
            { if (!Config.ModSwitches.Enabled) return;
                try
                {
                    var team = __instance?.Commander?.Team;
                    if (team == null) return;
                    var s = Get(team);
                    s.UnitHandlerTicksObserved++;
                    s.CumulCombatPresets       += __instance.CombatPresets?.Count      ?? 0;
                    s.CumulExplorationPresets  += __instance.ExplorationPresets?.Count ?? 0;
                    s.CumulHarvestPresets      += __instance.HarvestPresets?.Count     ?? 0;
                }
                catch (Exception ex) { MelonLogger.Warning("[RTSA/P2] Unit postfix threw: " + ex.Message); }
            }
        }

        // ==== Round summary emitter — called from OnSceneWasLoaded BEFORE clearing ====

        internal static string BuildRoundSummary()
        {
            if (_stats.Count == 0) return "";

            var sb = new StringBuilder();
            sb.AppendLine("");
            sb.AppendLine("========== ROUND SUMMARY ==========");
            foreach (var kv in _stats.OrderBy(k => k.Value.TeamName))
            {
                var s = kv.Value;
                sb.AppendLine($"--- {s.TeamName} ---");
                sb.AppendLine($"  structures: built={s.StructuresSpawned} lost={s.StructuresLost}");
                sb.AppendLine($"    built:    {FormatTopN(s.StructureBuiltByName, 12)}");
                sb.AppendLine($"    lost:     {FormatTopN(s.StructureLostByName, 12)}");
                sb.AppendLine($"  units:      built={s.UnitsSpawned} lost={s.UnitsLost}");
                sb.AppendLine($"    built:    {FormatTopN(s.UnitBuiltByName, 12)}");
                sb.AppendLine($"    lost:     {FormatTopN(s.UnitLostByName, 12)}");

                int cAi     = s.OrdersCommanderAi_Move + s.OrdersCommanderAi_Attack + s.OrdersCommanderAi_Stop;
                int cPlayer = s.OrdersCommanderPlayer_Move + s.OrdersCommanderPlayer_Attack + s.OrdersCommanderPlayer_Stop;
                int cPilot  = s.OrdersPiloted_Move + s.OrdersPiloted_Attack + s.OrdersPiloted_Stop;
                int total   = cAi + cPlayer + cPilot;
                sb.AppendLine($"  orders:     total={total}  commander(ai)={cAi} commander(player)={cPlayer} piloted={cPilot}");
                sb.AppendLine($"    breakdown: cmdAI(m/a/s)={s.OrdersCommanderAi_Move}/{s.OrdersCommanderAi_Attack}/{s.OrdersCommanderAi_Stop}  " +
                              $"cmdPlayer(m/a/s)={s.OrdersCommanderPlayer_Move}/{s.OrdersCommanderPlayer_Attack}/{s.OrdersCommanderPlayer_Stop}  " +
                              $"piloted(m/a/s)={s.OrdersPiloted_Move}/{s.OrdersPiloted_Attack}/{s.OrdersPiloted_Stop}");
                if (s.UnitsSpawned > 0)
                    sb.AppendLine($"  micro/unit: {total / (float)s.UnitsSpawned:F1} orders per unit produced");

                if (s.ConstructionTicksObserved > 0)
                {
                    float ticks = s.ConstructionTicksObserved;
                    sb.AppendLine($"  construction handler:  ticks={s.ConstructionTicksObserved}");
                    sb.AppendLine($"    avg BuildableStructures/tick = {s.CumulBuildableStructures / ticks:F1}");
                    sb.AppendLine($"    avg BuildableUnits/tick      = {s.CumulBuildableUnits / ticks:F1}");
                    sb.AppendLine($"    avg BuildableTechUpgrades    = {s.CumulBuildableTechUpgrades / ticks:F1}");
                    sb.AppendLine($"    avg MissingResources/tick    = {s.CumulMissingResources / ticks:F0}");
                    sb.AppendLine($"    ticks with 0 buildable structures = {s.TicksWithZeroBuildableStructs} ({s.TicksWithZeroBuildableStructs * 100f / ticks:F0}%)");
                }
                if (s.UnitHandlerTicksObserved > 0)
                {
                    float ticks = s.UnitHandlerTicksObserved;
                    sb.AppendLine($"  unit handler: ticks={s.UnitHandlerTicksObserved}  " +
                                  $"avg presets combat/explore/harvest = " +
                                  $"{s.CumulCombatPresets / ticks:F1}/" +
                                  $"{s.CumulExplorationPresets / ticks:F1}/" +
                                  $"{s.CumulHarvestPresets / ticks:F1}");
                }
                sb.AppendLine("");
            }
            sb.AppendLine("========== END ROUND SUMMARY ==========");
            return sb.ToString();
        }

        static string FormatTopN(Dictionary<string, int> d, int n)
        {
            if (d == null || d.Count == 0) return "-";
            var sb = new StringBuilder(); bool first = true;
            foreach (var kv in d.OrderByDescending(x => x.Value).Take(n))
            {
                if (!first) sb.Append(", ");
                sb.Append(kv.Key).Append('×').Append(kv.Value);
                first = false;
            }
            return sb.ToString();
        }

        static string ResolveTeamName(Team team)
        {
            try { if (team != null && !string.IsNullOrEmpty(team.name)) return team.name; } catch { }
            return "Team?";
        }
    }
}
