using HarmonyLib;
using MelonLoader;
using Silica.AI;
using System;

namespace Si_RTS_AI.Faction
{
    /// <summary>
    /// v0.7.53 — Combat suppression for eco-only benchmark runs.
    ///
    /// When HeadlessTest_SuppressCombat is on (default: on for headless test runs),
    /// ALL AIGroup.OnAttackOrder calls are prefixed-suppressed. AI still MAKES combat
    /// decisions and forms groups, but no attack orders ever leave the box. Units
    /// don't chase enemies, don't shoot, don't scatter across the map to hunt.
    ///
    /// This lets us measure PURE eco potential (income rate over time) without the
    /// noise of combat destroying our own economy.
    ///
    /// The gate is a MelonPreferences flag from the existing HeadlessTest category
    /// (registered in TestHarness). If TestHarness isn't loaded / pref isn't set,
    /// defaults to false → no-op (safe for real games).
    ///
    /// Pref key: HeadlessTest_SuppressCombat (bool, default false)
    /// </summary>
    internal static class SuppressCombat
    {
        // Read once and cache — flipped at scene load by TestHarness. Cheap defaults.
        internal static bool Enabled = false;

        internal static int SuppressedAttackOrders;

        [HarmonyPatch(typeof(AIGroup), nameof(AIGroup.OnAttackOrder))]
        static class Patch_AIGroup_OnAttackOrder
        {
            static bool Prefix()
            {
                if (!Enabled) return true;    // allow original
                SuppressedAttackOrders++;
                return false;                 // suppress
            }
        }

        internal static void ResetForNewRound()
        {
            SuppressedAttackOrders = 0;
        }

        internal static string BuildRoundSummaryFragment()
        {
            if (SuppressedAttackOrders == 0) return "";
            return "--- Combat suppression (v0.7.53) ---\n" +
                   $"  AIGroup.OnAttackOrder suppressed: {SuppressedAttackOrders} times\n";
        }
    }
}
