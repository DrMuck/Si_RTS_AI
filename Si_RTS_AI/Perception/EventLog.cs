using HarmonyLib;
using MelonLoader;
using Silica;
using Silica.AI;
using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace Si_RTS_AI.Perception
{
    /// <summary>
    /// OBSERVABILITY OF WHAT THE GAME DOES: AI commander ticks, player and AI
    /// unit orders, selections, pilot changes, team changes, spawns and losses.
    /// Everything here reads; nothing here orders or places.
    ///
    /// Sources:
    ///   - a Harmony postfix on AICommander.Think ([AI] lines + the [RTSA/OBS]
    ///     console summary every LOG_EVERY_N_TICKS ticks per team)
    ///   - GameEvents delegates, re-hooked at every scene because Silica clears
    ///     them on scene transition
    ///
    /// The commander-attributed lines ([ORDER] [SELECT] [PILOT] [TEAM]) are
    /// gated on ModSwitches.CommanderLog; the tallies behind the round summary
    /// keep counting either way. Spawn/loss lines and layer invalidation always
    /// run, because the planners read them.
    /// </summary>
    internal static class EventLog
    {
        // Log a summary line every N Think() calls per team. Stock ThinkInterval is
        // ~3s, so at LOG_EVERY_N_TICKS = 5 that's one summary every ~15s per team.
        const int LOG_EVERY_N_TICKS = 5;

        static readonly Dictionary<Team, TeamObservation> _obs = new Dictionary<Team, TeamObservation>();
        static bool _hooked;

        class TeamObservation
        {
            public string TeamName = "?";
            public int TickCount;
            public int TotalRequests;
            public Dictionary<EAIRequestType, int> ReqTypeCount = new Dictionary<EAIRequestType, int>();
            public Dictionary<EAITaskType, int>    TaskCount    = new Dictionary<EAITaskType, int>();
            public long PrioritySum;
            public int PriorityCount;
            public int MaxPriority = int.MinValue;
            public int MinPriority = int.MaxValue;
        }

        static bool CommanderLinesOn => Config.ModSwitches.Enabled && Config.ModSwitches.CommanderLog;

        internal static void ClearForNewRound()
        {
            _obs.Clear();
            Phase2.ClearForNewRound();
        }

        internal static string BuildRoundSummary() => Phase2.BuildRoundSummary();

        // ==================== AI COMMANDER TICKS ====================

        [HarmonyPatch(typeof(AICommander), nameof(AICommander.Think))]
        static class Patch_AICommander_Think
        {
            static void Postfix(AICommander __instance)
            {
                if (!Config.ModSwitches.Enabled) return;
                try { OnCommanderThought(__instance); } catch (Exception ex)
                {
                    MelonLogger.Warning($"[RTSA] AI-Postfix threw: {ex.Message}");
                }
            }
        }

        static void OnCommanderThought(AICommander cmd)
        {
            if (cmd == null || cmd.Team == null || !Si_RTS_AI.SceneReady) return;

            if (!_obs.TryGetValue(cmd.Team, out var ob))
            {
                ob = new TeamObservation { TeamName = ResolveTeamName(cmd.Team) };
                _obs[cmd.Team] = ob;
            }

            ob.TickCount++;
            var reqs = cmd.Requests;
            if (reqs == null) return;

            var tickReqType = new Dictionary<EAIRequestType, int>();
            var tickTask    = new Dictionary<EAITaskType, int>();
            int tickPrioSum = 0, tickPrioCount = 0, tickPrioMax = int.MinValue, tickPrioMin = int.MaxValue;
            int n = reqs.Count;
            for (int i = 0; i < n; i++)
            {
                var r = reqs[i];
                if (r == null) continue;

                Bump(tickReqType, r.Type);
                Bump(ob.ReqTypeCount, r.Type);
                if (r.Type == EAIRequestType.Unit)
                {
                    Bump(tickTask, r.UnitTask);
                    Bump(ob.TaskCount, r.UnitTask);
                }

                tickPrioSum += r.Priority; tickPrioCount++;
                if (r.Priority > tickPrioMax) tickPrioMax = r.Priority;
                if (r.Priority < tickPrioMin) tickPrioMin = r.Priority;

                ob.PrioritySum += r.Priority; ob.PriorityCount++;
                if (r.Priority > ob.MaxPriority) ob.MaxPriority = r.Priority;
                if (r.Priority < ob.MinPriority) ob.MinPriority = r.Priority;
            }
            ob.TotalRequests += n;

            if (!CommanderLinesOn) return;
            string line =
                $"[AI] [{ob.TeamName}] tick={ob.TickCount} reqs={n} " +
                $"type={{{FormatDict(tickReqType)}}} " +
                $"task={{{FormatDict(tickTask)}}} " +
                $"prio(min/avg/max)={(tickPrioCount > 0 ? tickPrioMin.ToString() : "-")}/" +
                $"{(tickPrioCount > 0 ? (tickPrioSum / (float)tickPrioCount).ToString("F1") : "-")}/" +
                $"{(tickPrioCount > 0 ? tickPrioMax.ToString() : "-")} " +
                $"groups={(cmd.Groups != null ? cmd.Groups.Count : 0)}";
            Core.RoundLog.Append(line);

            if (ob.TickCount % LOG_EVERY_N_TICKS == 0)
                MelonLogger.Msg($"[RTSA/OBS] {line}");
        }

        // ==================== GAME EVENTS ====================

        internal static void Hook()
        {
            if (_hooked) return;
            try
            {
#if GAME_MAIN
                GameEvents.OnUnitReceivedAttackOrder -= OnUnitReceivedAttackOrderMain;
                GameEvents.OnUnitReceivedAttackOrder += OnUnitReceivedAttackOrderMain;
                GameEvents.OnUnitReceivedMoveOrder   -= OnUnitReceivedMoveOrderMain;
                GameEvents.OnUnitReceivedMoveOrder   += OnUnitReceivedMoveOrderMain;
                GameEvents.OnUnitReceivedStopOrder   -= OnUnitReceivedStopOrderMain;
                GameEvents.OnUnitReceivedStopOrder   += OnUnitReceivedStopOrderMain;
#else
                GameEvents.OnObjectReceivedAttackOrder -= OnUnitReceivedAttackOrder;
                GameEvents.OnObjectReceivedAttackOrder += OnUnitReceivedAttackOrder;
                GameEvents.OnObjectReceivedMoveOrder   -= OnUnitReceivedMoveOrder;
                GameEvents.OnObjectReceivedMoveOrder   += OnUnitReceivedMoveOrder;
                GameEvents.OnObjectReceivedStopOrder   -= OnUnitReceivedStopOrder;
                GameEvents.OnObjectReceivedStopOrder   += OnUnitReceivedStopOrder;
#endif
                GameEvents.OnPlayerSelectUnit        -= OnPlayerSelectUnit;
                GameEvents.OnPlayerSelectUnit        += OnPlayerSelectUnit;
                GameEvents.OnPlayerChangedTeam       -= OnPlayerChangedTeam;
                GameEvents.OnPlayerChangedTeam       += OnPlayerChangedTeam;
                GameEvents.OnPlayerChangedUnit       -= OnPlayerChangedUnit;
                GameEvents.OnPlayerChangedUnit       += OnPlayerChangedUnit;

                GameEvents.OnStructureSpawned        -= OnStructureSpawned;
                GameEvents.OnStructureSpawned        += OnStructureSpawned;
                GameEvents.OnUnitSpawned             -= OnUnitSpawned;
                GameEvents.OnUnitSpawned             += OnUnitSpawned;
                GameEvents.OnStructureDestroyed      -= OnStructureDestroyed;
                GameEvents.OnStructureDestroyed      += OnStructureDestroyed;
                GameEvents.OnUnitDestroyed           -= OnUnitDestroyed;
                GameEvents.OnUnitDestroyed           += OnUnitDestroyed;

                _hooked = true;
                MelonLogger.Msg("[RTSA] GameEvents hooked (orders + selections + spawns).");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[RTSA] HookGameEvents failed: {ex.Message}");
            }
        }

        /// <summary>Drop our delegates (mod switched off mid-round). Harmless if not hooked.</summary>
        internal static void Unhook()
        {
            try
            {
#if GAME_MAIN
                GameEvents.OnUnitReceivedAttackOrder -= OnUnitReceivedAttackOrderMain;
                GameEvents.OnUnitReceivedMoveOrder   -= OnUnitReceivedMoveOrderMain;
                GameEvents.OnUnitReceivedStopOrder   -= OnUnitReceivedStopOrderMain;
#else
                GameEvents.OnObjectReceivedAttackOrder -= OnUnitReceivedAttackOrder;
                GameEvents.OnObjectReceivedMoveOrder   -= OnUnitReceivedMoveOrder;
                GameEvents.OnObjectReceivedStopOrder   -= OnUnitReceivedStopOrder;
#endif
                GameEvents.OnPlayerSelectUnit   -= OnPlayerSelectUnit;
                GameEvents.OnPlayerChangedTeam  -= OnPlayerChangedTeam;
                GameEvents.OnPlayerChangedUnit  -= OnPlayerChangedUnit;
                GameEvents.OnStructureSpawned   -= OnStructureSpawned;
                GameEvents.OnUnitSpawned        -= OnUnitSpawned;
                GameEvents.OnStructureDestroyed -= OnStructureDestroyed;
                GameEvents.OnUnitDestroyed      -= OnUnitDestroyed;
            }
            catch { }
            _hooked = false;
        }

        /// <summary>Scene transitions clear the delegates without telling us.</summary>
        internal static void MarkUnhooked() { _hooked = false; }

        // Attribution rules — a unit's order can come from three sources:
        //   1. PILOTED  — unit.ControlledBy != null → someone in first-person mode is
        //                 personally driving the unit, and the order came from them.
        //   2. COMMANDER-PLAYER — team has no AI commander enabled (a human is the
        //                 team's RTS commander) → the order was issued top-down by
        //                 the human commander clicking select + right-click.
        //   3. COMMANDER-AI — team has an AI commander enabled → the order was
        //                 emitted by the AI's Think() → group routing.
        static string OrderTime()
        {
            try { return MapLayers.LayerReplay.CurrentRoundTime.ToString("F0"); } catch { return "-"; }
        }
        static string AttributeSource(Unit unit)
        {
            try
            {
                var actor = unit?.ControlledBy;
                if (actor != null) return $"piloted={SafeName(actor)}";

                var team = unit?.Team;
                if (team != null && !AIManager.IsCommanderEnabled(team))
                {
                    string tag = CommanderLog.CommanderTag(team);
                    return tag.Length > 0 ? "commander=player " + tag : "commander=player";
                }
            }
            catch { }
            return "commander=ai";
        }

#if GAME_MAIN
        static void OnUnitReceivedAttackOrderMain(Unit u, Target target) => OnUnitReceivedAttackOrder(u, target);
        static void OnUnitReceivedMoveOrderMain(Unit u, Vector3 destination) => OnUnitReceivedMoveOrder(u, destination);
        static void OnUnitReceivedStopOrderMain(Unit u) => OnUnitReceivedStopOrder(u);
#endif
        static void OnUnitReceivedAttackOrder(BaseGameObject obj, Target target)
        {
            var unit = obj as Unit;
            if (unit == null) return;
            var team = unit.Team;
            string src = AttributeSource(unit);
            var p2 = Phase2.Get(team);
            if      (src.StartsWith("piloted"))          p2.OrdersPiloted_Attack++;
            else if (src.StartsWith("commander=player")) p2.OrdersCommanderPlayer_Attack++;
            else                                         p2.OrdersCommanderAi_Attack++;

            if (!CommanderLinesOn) return;
            string tgtDesc = "?";
            try { tgtDesc = target?.ToString() ?? "?"; } catch { }
            Core.RoundLog.Append(
                $"[ORDER] t={OrderTime()} {src} team={ResolveTeamName(team)} " +
                $"unit={UnitDisplay(unit)} kind=attack tgt={tgtDesc}");
        }

        static void OnUnitReceivedMoveOrder(BaseGameObject obj, Vector3 destination)
        {
            var unit = obj as Unit;
            if (unit == null) return;
            var team = unit.Team;
            string src = AttributeSource(unit);
            var p2 = Phase2.Get(team);
            if      (src.StartsWith("piloted"))          p2.OrdersPiloted_Move++;
            else if (src.StartsWith("commander=player")) p2.OrdersCommanderPlayer_Move++;
            else                                         p2.OrdersCommanderAi_Move++;

            if (!CommanderLinesOn) return;
            Core.RoundLog.Append(
                $"[ORDER] t={OrderTime()} {src} team={ResolveTeamName(team)} " +
                $"unit={UnitDisplay(unit)} kind=move " +
                $"dst=({destination.x:F0},{destination.y:F0},{destination.z:F0})");
        }

        static void OnUnitReceivedStopOrder(BaseGameObject obj)
        {
            var unit = obj as Unit;
            if (unit == null) return;
            var team = unit.Team;
            string src = AttributeSource(unit);
            var p2 = Phase2.Get(team);
            if      (src.StartsWith("piloted"))          p2.OrdersPiloted_Stop++;
            else if (src.StartsWith("commander=player")) p2.OrdersCommanderPlayer_Stop++;
            else                                         p2.OrdersCommanderAi_Stop++;

            if (!CommanderLinesOn) return;
            Core.RoundLog.Append(
                $"[ORDER] t={OrderTime()} {src} team={ResolveTeamName(team)} " +
                $"unit={UnitDisplay(unit)} kind=stop");
        }

        static void OnPlayerSelectUnit(Player player, Unit oldUnit, Unit newUnit)
        {
            if (player == null || !CommanderLinesOn) return;
            Core.RoundLog.Append(
                $"[SELECT] t={OrderTime()} player={SafeName(player)} team={ResolveTeamName(player.Team)} " +
                $"unit={(newUnit != null ? UnitDisplay(newUnit) : "-")} " +
                $"(was: {(oldUnit != null ? UnitDisplay(oldUnit) : "-")})");
        }

        static void OnPlayerChangedUnit(Player player, Unit oldUnit, Unit newUnit)
        {
            if (player == null || !CommanderLinesOn) return;
            Core.RoundLog.Append(
                $"[PILOT] t={OrderTime()} player={SafeName(player)} team={ResolveTeamName(player.Team)} " +
                $"unit={(newUnit != null ? UnitDisplay(newUnit) : "-")} " +
                $"(was: {(oldUnit != null ? UnitDisplay(oldUnit) : "-")})");
        }

        static void OnPlayerChangedTeam(Player player, Team oldTeam, Team newTeam)
        {
            if (player == null || !CommanderLinesOn) return;
            Core.RoundLog.Append(
                $"[TEAM] t={OrderTime()} player={SafeName(player)} " +
                $"{ResolveTeamName(oldTeam)} -> {ResolveTeamName(newTeam)}");
        }

        static void OnStructureSpawned(Structure structure)
        {
            if (structure == null || structure.ObjectInfo == null) return;
            var team = structure.Team;
            string name = structure.ObjectInfo.DisplayName ?? "?";

            var p2 = Phase2.Get(team);
            p2.StructuresSpawned++;
            Bump(p2.StructureBuiltByName, name);

            // Layer invalidation — only on layer-relevant structure types so we
            // don't rebuild after every random Alien creature spawn.
            if (string.Equals(name, "Bio Cache",            StringComparison.OrdinalIgnoreCase)) MapLayers.AlienEcoLayers.OnBcChanged();
            if (string.Equals(name, "Lesser Spawning Cyst", StringComparison.OrdinalIgnoreCase)) MapLayers.AlienEcoLayers.OnCystChanged();
            if (string.Equals(name, "Refinery",             StringComparison.OrdinalIgnoreCase)) MapLayers.HumanEcoLayers.OnRefineryChanged();
            if (string.Equals(name, "Headquarters",         StringComparison.OrdinalIgnoreCase)) MapLayers.HumanEcoLayers.OnHqChanged();

            Vector3 sp = Vector3.zero;
            try { sp = structure.transform.position; } catch { }
            Core.RoundLog.Append($"[SPAWN_S] t={OrderTime()} team={ResolveTeamName(team)} structure={name} at=({sp.x:F0},{sp.z:F0})");
            // Measure how far the game's placement search moved this from where
            // the planner asked for it — sets the chain-anchor margin.
            try { Planning.EcoPlanner.NoteStructureSpawned(team, name, sp); } catch { }
        }

        static void OnUnitSpawned(Unit unit)
        {
            if (unit == null || unit.ObjectInfo == null) return;
            var team = unit.Team;
            string name = unit.ObjectInfo.DisplayName ?? "?";
            var p2 = Phase2.Get(team);
            p2.UnitsSpawned++;
            Bump(p2.UnitBuiltByName, name);
            Core.RoundLog.Append($"[SPAWN_U] t={OrderTime()} team={ResolveTeamName(team)} unit={name}");
        }

        // Loss tracking. Keeps the log quiet (deaths are already in Silica's HL
        // log) but rolls counts into the round summary.
        static void OnStructureDestroyed(Structure structure, GameObject instigator)
        {
            if (structure == null || structure.ObjectInfo == null) return;
            var p2 = Phase2.Get(structure.Team);
            p2.StructuresLost++;
            string name = structure.ObjectInfo.DisplayName ?? "?";
            Bump(p2.StructureLostByName, name);

            if (string.Equals(name, "Bio Cache",            StringComparison.OrdinalIgnoreCase)) MapLayers.AlienEcoLayers.OnBcChanged();
            if (string.Equals(name, "Lesser Spawning Cyst", StringComparison.OrdinalIgnoreCase)) MapLayers.AlienEcoLayers.OnCystChanged();
            if (string.Equals(name, "Refinery",             StringComparison.OrdinalIgnoreCase)) MapLayers.HumanEcoLayers.OnRefineryChanged();
            if (string.Equals(name, "Headquarters",         StringComparison.OrdinalIgnoreCase)) MapLayers.HumanEcoLayers.OnHqChanged();
        }

        static void OnUnitDestroyed(Unit unit, GameObject instigator)
        {
            if (unit == null || unit.ObjectInfo == null) return;
            var p2 = Phase2.Get(unit.Team);
            p2.UnitsLost++;
            Bump(p2.UnitLostByName, unit.ObjectInfo.DisplayName ?? "?");
        }

        // ==================== HELPERS ====================

        static void Bump<TKey>(Dictionary<TKey, int> d, TKey k) where TKey : notnull
        {
            d[k] = d.TryGetValue(k, out int v) ? v + 1 : 1;
        }

        static string FormatDict<TKey>(Dictionary<TKey, int> d) where TKey : notnull
        {
            if (d.Count == 0) return "";
            var sb = new StringBuilder(); bool first = true;
            foreach (var kv in d)
            {
                if (!first) sb.Append(',');
                sb.Append(kv.Key).Append(':').Append(kv.Value);
                first = false;
            }
            return sb.ToString();
        }

        static string ResolveTeamName(Team team)
        {
            try { if (team != null && !string.IsNullOrEmpty(team.name)) return team.name; } catch { }
            return "Team?";
        }

        static string UnitDisplay(Unit u)
        {
            try { return u?.ObjectInfo?.DisplayName ?? "?"; } catch { return "?"; }
        }

        static string SafeName(Player p)
        {
            try { return p?.PlayerName ?? "?"; } catch { return "?"; }
        }
    }
}
