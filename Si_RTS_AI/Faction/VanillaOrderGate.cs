using System;
using HarmonyLib;
using MelonLoader;
using Silica.AI;

namespace Si_RTS_AI.Faction
{
    /// <summary>
    /// STOP THE GAME'S OWN COMMANDER RE-TASKING UNITS ON A CO-OP TEAM.
    ///
    /// On a co-op alien team the AI holds the commander seat so it can run the
    /// economy. That seat also OWNS the army, so Silica's own commander keeps
    /// issuing move and attack orders to units the players are driving —
    /// DrMuck, watching a played round: Hunters and Behemoths ignoring the
    /// orders they were given, because vanilla re-tasked them a moment later.
    ///
    /// WHY NOT BLOCK BY UNIT. BattalionManager already blocks orders for units
    /// it owns, and that is the wrong tool here for two reasons. It is inert in
    /// co-op — Owns() requires military.execute, which is false — and more
    /// importantly a unit-based test cannot tell a PLAYER's order from the AI's.
    /// Blocking by unit on a played server would eat the players' own commands,
    /// which is worse than the problem.
    ///
    /// SO BLOCK BY ORIGIN. Vanilla's orders are issued from inside
    /// AICommander.Think. A player's are not — they arrive from the network
    /// handler, outside that window. A prefix/postfix pair around Think marks
    /// the window, and the order prefixes refuse anything raised inside it.
    /// Nothing else needs to know, and a player order is untouched by
    /// construction rather than by a check that might get it wrong.
    ///
    /// This mod's own unit orders are also safe: they are issued from OnUpdate
    /// and the planner tickers, never from inside Think. The one thing that
    /// does hang off Think is the observability postfix, which only reads
    /// cmd.Requests and issues nothing.
    ///
    /// Off by default. Turn on with "coopBlockVanillaUnitOrders": true in
    /// rtsai.json — it is live-reloaded, so it can go on and off mid-round.
    /// </summary>
    internal static class VanillaOrderGate
    {
        /// <summary>True while Silica's own commander is inside Think.</summary>
        static bool _inAiTick;
        internal static bool InAiTick => _inAiTick;

        /// <summary>Team whose commander is currently thinking — so a co-op
        /// alien team is protected without touching Sol or Centauri, whose
        /// commanders should keep working normally.</summary>
        static Team _thinkingTeam;

        internal static bool Enabled =>
            Planning.RtsaiConfig.Bool("coopBlockVanillaUnitOrders", false);

        static int _blockedMove, _blockedAttack;
        static float _lastReportAt;

        internal static void ResetForNewRound()
        {
            _inAiTick = false; _thinkingTeam = null;
            _blockedMove = 0; _blockedAttack = 0; _lastReportAt = 0f;
        }

        /// <summary>Only the team we hold the seat on. Sol and Centauri keep
        /// their commanders; this is about the seat the players share.</summary>
        static bool Protected(Team t)
        {
            if (t == null || _thinkingTeam == null) return false;
            if (!ReferenceEquals(t, _thinkingTeam)) return false;
            // NOT gated on AlienCommanderLock. It was, and that made the gate
            // inert in the co-op mode where it matters MOST: when a human holds
            // the seat, lockAlienCommander is off by definition. The game
            // normally disables its own commander for a human-held team, so
            // Think should not fire at all -- but "should not" is what the
            // player-orders-ignored report was about, and blocking by ORIGIN is
            // safe either way. A player's order never comes from inside Think.
            string n = t.name ?? "";
            return n.IndexOf("Alien", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        static bool ShouldBlock(Team t) => Enabled && _inAiTick && Protected(t);

        internal static void Report()
        {
            if (_blockedMove == 0 && _blockedAttack == 0) return;
            if (UnityEngine.Time.time - _lastReportAt < 30f) return;
            _lastReportAt = UnityEngine.Time.time;
            MelonLogger.Msg($"[COOP/GATE] blocked vanilla commander orders — " +
                            $"move={_blockedMove} attack={_blockedAttack} (cumulative)");
        }

        // ---- the window ------------------------------------------------

        [HarmonyPatch(typeof(AICommander), nameof(AICommander.Think))]
        static class Patch_Think_Window
        {
            static void Prefix(AICommander __instance)
            {
                _inAiTick = true;
                try { _thinkingTeam = __instance?.Team; } catch { _thinkingTeam = null; }
            }

            // A finalizer runs even if Think throws, so the flag cannot be left
            // stuck on — which would silently freeze every unit order on the
            // team for the rest of the round.
            static void Finalizer()
            {
                _inAiTick = false;
                _thinkingTeam = null;
            }
        }

        // ---- the doors -------------------------------------------------

        [HarmonyPatch(typeof(Unit), nameof(Unit.OnMoveOrder))]
        static class Patch_Unit_OnMoveOrder_Coop
        {
            static bool Prefix(Unit __instance)
            {
                try
                {
                    if (__instance == null) return true;
                    if (!ShouldBlock(__instance.Team)) return true;
                    _blockedMove++;
                    return false;
                }
                catch { return true; }
            }
        }

        [HarmonyPatch(typeof(AIGroup), nameof(AIGroup.OnAttackOrder))]
        static class Patch_AIGroup_OnAttackOrder_Coop
        {
            static bool Prefix(AIGroup __instance)
            {
                try
                {
                    if (__instance == null) return true;
                    var units = __instance.Units;
                    if (units == null || units.Count == 0) return true;
                    Team t = null;
                    for (int i = 0; i < units.Count && t == null; i++)
                        if (units[i] != null) t = units[i].Team;
                    if (!ShouldBlock(t)) return true;
                    _blockedAttack++;
                    return false;
                }
                catch { return true; }
            }
        }
    }
}
