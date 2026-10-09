using HarmonyLib;
using MelonLoader;
using Silica;
using Silica.AI;
using System;
using System.Collections.Generic;

namespace Si_RTS_AI.Faction
{
    /// <summary>
    /// v0.7.50 — Anti-Shrimp-Attack.
    ///
    /// Problem observed: stock AI keeps pulling harvester Shrimps into non-harvest
    /// groups. Once a Shrimp is in an offense/defense/explore group, that group's
    /// OnAttackOrder / SeekAndDestroy tasks issue attack orders to it and it wanders
    /// off its biotics patch. Result: eco quietly falls apart.
    ///
    /// Fix: intercept at TWO points so no matter how a Shrimp gets routed we keep it
    /// out of combat:
    ///
    ///   1. AIGroup.AddUnit(Unit) Prefix — refuse to add a Shrimp to any group whose
    ///      IsForHarvest is false. This is the front-door routing decision — if the
    ///      stock AIUnitHandler tries to move a Shrimp into a combat group we say no.
    ///
    ///   2. AIGroup.OnAttackOrder(Target, AgentMoveSpeed) Prefix — belt-and-braces
    ///      safety net. If any Shrimp somehow slipped into this group (a code path we
    ///      didn't cover, or a preset that changed IsForHarvest post-construction),
    ///      RemoveUnit it before the attack orders fan out. Non-Shrimp members of the
    ///      group still get their attack orders normally.
    ///
    /// API surface discovered via Mono.Cecil dump of SilicaCore.dll:
    ///   AIGroup.AddUnit(Unit)                → public bool
    ///   AIGroup.RemoveUnit(Unit)             → public bool
    ///   AIGroup.OnAttackOrder(Target, AMS)   → public void
    ///   AIGroup.Units                        → public List&lt;Unit&gt;
    ///   AIGroup.IsForHarvest                 → public bool { get; set; }
    ///
    /// Unit id: ObjectInfo.DisplayName == "Shrimp" (same test used in the rest of
    /// the mod — see Si_RTS_AI.cs OnUnitSpawned and AlienConstruction).
    ///
    /// Scope: applied to ALL teams. Shrimp is uniquely an Alien harvester unit, so
    /// non-Alien teams simply never see this trigger; no gate needed.
    /// </summary>
    internal static class AlienShrimpAntiAttack
    {
        const string SHRIMP_NAME = "Shrimp";

        // Round counters.
        internal static int BlockedAddUnit_NonHarvest;   // AddUnit refusals
        internal static int StrippedFromAttackOrder;     // OnAttackOrder-time removals
        internal static int BlockedMoveOrder_NonHarvest; // Move-order refusals

        // Per-task diagnostic: which task names are trying to move shrimps
        // off-harvest? User observed scatter despite Patch3 already blocking
        // non-AITask_Harvest moves. Suspect: some task name we don't recognize
        // is being used (idle-post-depletion, exploration, etc), OR the moves
        // come through a channel Patch3 doesn't hook (AIGroup batch orders?).
        // Log counts per task name so we can extend the allow-list surgically.
        internal static readonly System.Collections.Generic.Dictionary<string, int> BlockedMoveByTaskName
            = new System.Collections.Generic.Dictionary<string, int>();
        internal static readonly System.Collections.Generic.Dictionary<string, int> AllowedMoveByTaskName
            = new System.Collections.Generic.Dictionary<string, int>();

        // Bypass flag for OUR planner. When Si_RTS_AI.Planning.ShrimpRelocator
        // needs to redirect a shrimp (e.g. reroute off a depleting patch), it
        // sets this true just before calling OnMoveOrder and clears immediately
        // after. Patch3 below honors it — otherwise our own relocation orders
        // would be caught by the same "block non-harvest moves" filter that
        // stops vanilla scatter.
        [System.ThreadStatic] internal static bool PlannerOverride;

        // ---- Patch 1: REMOVED in v0.7.54 ----
        //
        // Observed 1191 refusals per NarakaCity round — over-aggressive. The stock AI
        // creates groups as generic-then-marks-harvest-later, so an early AddUnit
        // refusal permanently blocks shrimps from joining the eventual harvest group.
        // Result: no shrimps built, eco collapse.
        //
        // Correct approach: let shrimps be added to any group freely. Rely on the
        // OnAttackOrder Prefix (Patch 2 below) to strip them at the moment attack
        // orders would fan out. Shrimps get to stay in whatever group they were
        // routed to (usually harvest) and only lose membership when combat is about
        // to hit them.

        // ---- Patch 2: strip Shrimps out of any group about to issue an attack order ----
        [HarmonyPatch(typeof(AIGroup), nameof(AIGroup.OnAttackOrder))]
        static class Patch_AIGroup_OnAttackOrder
        {
            // Scratch list reused across calls — OnAttackOrder is not reentrant per
            // group and the whole game loop is single-threaded, so this is safe.
            static readonly List<Unit> _scratch = new List<Unit>(8);

            static void Prefix(AIGroup __instance)
            { if (!Config.ModSwitches.Enabled) return;
                try
                {
                    if (__instance == null) return;
                    var units = __instance.Units;
                    if (units == null || units.Count == 0) return;

                    _scratch.Clear();
                    for (int i = 0; i < units.Count; i++)
                    {
                        var u = units[i];
                        if (u != null && IsShrimp(u)) _scratch.Add(u);
                    }
                    if (_scratch.Count == 0) return;

                    for (int i = 0; i < _scratch.Count; i++)
                    {
                        try
                        {
                            if (__instance.RemoveUnit(_scratch[i]))
                                StrippedFromAttackOrder++;
                        }
                        catch { }
                    }
                    _scratch.Clear();
                }
                catch (Exception ex)
                {
                    MelonLogger.Warning("[RTSA/ShrimpNoAtk] OnAttackOrder prefix threw: " + ex.Message);
                }
            }
        }

        static bool IsShrimp(Unit u)
        {
            try { return string.Equals(u.ObjectInfo?.DisplayName, SHRIMP_NAME, StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
        }

        // ---- Patch 3: block move orders on shrimps unless they're currently harvesting ----
        //
        // Observed 156 vanilla-AI move orders on shrimps per NorthPolarCap round
        // sent to consistent scatter targets like (22,1114) and (-730,-288) — those
        // are the game's Explore / Prospect / Sentry / SeekAndDestroy tasks
        // dispatching shrimps as makeshift scouts. Result: 25% of shrimps end up
        // >400m from any BC and stop contributing to eco.
        //
        // Fix: prefix Unit.OnMoveOrder. If the unit is a Shrimp and its CurrentTask
        // is anything other than AITask_Harvest, refuse the move. This keeps the
        // harvest cycle intact (Task=Harvest → moves allowed for the biotics↔BC
        // loop) while blocking every scatter path we've seen.
#if GAME_MAIN
        [HarmonyPatch(typeof(Unit), nameof(Unit.OnMoveOrder))]
        static class Patch_Unit_OnMoveOrder
        {
            static bool Prefix(Unit __instance)
            { if (!Config.ModSwitches.Enabled) return true;
                bool __result = false;
                try
                {
                    var unit = __instance;
                    if (unit == null || !IsShrimp(unit)) return true;
#else
        [HarmonyPatch(typeof(AIOrderProcessor), nameof(AIOrderProcessor.IssueOrder))]
        static class Patch_Unit_OnMoveOrder
        {
            static bool Prefix(AIOrderProcessor __instance, OrderDefinition definition, ref bool __result)
            { if (!Config.ModSwitches.Enabled) return true;
                try
                {
                    if (!OrderCompat.IsMoveOrder(definition)) return true;
                    var unit = __instance.OwnerUnit();
                    if (unit == null || !IsShrimp(unit)) return true;
#endif
                    if (PlannerOverride) return true;
                    // Human-commander opt-out. When a real player takes over the
                    // alien team the AI commander is disabled — in that case
                    // let ALL orders through so the player can command their
                    // own shrimps without our anti-scatter filter interfering.
                    try
                    {
                        var t = unit.Team;
                        if (t != null && !Silica.AI.AIManager.IsCommanderEnabled(t)) return true;
                        if (t != null && !FactionControl.IsEnabled(t)) return true;   // switched off: vanilla commands
                    }
                    catch { }
                    var task = unit.CurrentTask;
                    string taskName = task?.GetType().Name ?? "(null)";
                    if (taskName == "AITask_Harvest")
                    {
                        AllowedMoveByTaskName.TryGetValue(taskName, out int a);
                        AllowedMoveByTaskName[taskName] = a + 1;
                        return true;
                    }
                    BlockedMoveOrder_NonHarvest++;
                    BlockedMoveByTaskName.TryGetValue(taskName, out int b);
                    BlockedMoveByTaskName[taskName] = b + 1;
                    __result = false;
                    return false;
                }
                catch (Exception ex)
                {
                    MelonLogger.Warning("[RTSA/ShrimpNoAtk] OnMoveOrder prefix threw: " + ex.Message);
                    return true;   // fall through to stock on our own bug
                }
            }
        }

        // ---- Round wiring ----
        internal static void ResetForNewRound()
        {
            BlockedAddUnit_NonHarvest    = 0;
            StrippedFromAttackOrder      = 0;
            BlockedMoveOrder_NonHarvest  = 0;
            BlockedMoveByTaskName.Clear();
            AllowedMoveByTaskName.Clear();
        }

        internal static string BuildRoundSummaryFragment()
        {
            if (BlockedAddUnit_NonHarvest == 0 && StrippedFromAttackOrder == 0 && BlockedMoveOrder_NonHarvest == 0) return "";
            var sb = new System.Text.StringBuilder();
            sb.Append("--- Shrimp anti-attack (v0.7.50) ---\n")
              .Append($"  AddUnit refused (non-harvest group): {BlockedAddUnit_NonHarvest}\n")
              .Append($"  Stripped at OnAttackOrder time:      {StrippedFromAttackOrder}\n")
              .Append($"  Move orders refused (non-harvest):   {BlockedMoveOrder_NonHarvest}\n");
            if (BlockedMoveByTaskName.Count > 0)
            {
                sb.Append("  Blocked-move task-name breakdown:\n");
                var sorted = new System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<string, int>>(BlockedMoveByTaskName);
                sorted.Sort((a, b) => b.Value.CompareTo(a.Value));
                for (int i = 0; i < sorted.Count && i < 10; i++)
                    sb.Append($"    {sorted[i].Key}: {sorted[i].Value}\n");
            }
            if (AllowedMoveByTaskName.Count > 0)
            {
                sb.Append("  Allowed-move task-name breakdown:\n");
                foreach (var kv in AllowedMoveByTaskName)
                    sb.Append($"    {kv.Key}: {kv.Value}\n");
            }
            return sb.ToString();
        }
    }
}
