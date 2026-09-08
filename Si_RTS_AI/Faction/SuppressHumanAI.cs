using HarmonyLib;
using MelonLoader;
using Silica;
using Silica.AI;
using System;
using System.Reflection;
using UnityEngine;

namespace Si_RTS_AI.Faction
{
    /// <summary>
    /// Alien-eco-only soak mode. When Enabled:
    ///   1. HumanConstruction.HandleTick early-returns false (so Silica's stock
    ///      AIConstructionHandler.Think is ALSO skipped for humans).
    ///   2. TickPeriodic() runs every ~1s from TestHarness.Tick() and:
    ///        - Calls Unit.Suicide() on every non-HQ human unit (starter units
    ///          keep respawning otherwise since we can't hook the initial spawn
    ///          reliably; reactive purge is cheaper than a Harmony maze).
    ///        - Zeroes TotalResources via reflection on the same backing field
    ///          EcoRateSampler.TryDrain uses.
    ///   3. HQs are LEFT ALONE — the game may end the round for "team destroyed"
    ///      if we kill the last structure, and we want the round to run its
    ///      full ForceEndAfterMinutes span for benchmark comparison.
    ///
    /// Wired from TestHarness via the HeadlessTest_SuppressHumanAI preference.
    /// Sibling of SuppressCombat.
    /// </summary>
    internal static class SuppressHumanAI
    {
        public static bool Enabled;

        const float PURGE_INTERVAL_S = 1f;
        static float _lastPurgeAt;
        static FieldInfo? _totalResourcesField;
        static int _lifetimeUnitsKilled;

        static bool IsHumanTeamName(string n) =>
            !string.IsNullOrEmpty(n) &&
            !n.Contains("Alien") &&
            (n.Contains("Sol") || n.Contains("Cent") || n.Contains("Centauri"));

        internal static void TickPeriodic()
        {
            if (!Enabled) return;
            if (Time.time - _lastPurgeAt < PURGE_INTERVAL_S) return;
            _lastPurgeAt = Time.time;

            try
            {
                var cmds = AIManager.Commanders;
                if (cmds == null) return;
                foreach (var kv in cmds)
                {
                    var team = kv.Key;
                    if (team == null) continue;
                    if (!IsHumanTeamName(team.name)) continue;
                    PurgeUnits(team);
                    TryZeroCash(team);
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[RTSA/SuppressHumanAI] TickPeriodic threw: {ex.Message}");
            }
        }

        static void PurgeUnits(Team team)
        {
            var units = team.Units;
            if (units == null) return;
            int killed = 0;
            // Iterate forward but be defensive — Suicide() removes from the list.
            for (int i = units.Count - 1; i >= 0; i--)
            {
                Unit u;
                try { u = units[i]; } catch { continue; }
                if (u == null) continue;
                bool destroyed;
                try { destroyed = u.IsDestroyed; } catch { destroyed = false; }
                if (destroyed) continue;
                try { u.Suicide(); killed++; } catch { }
            }
            if (killed > 0)
            {
                _lifetimeUnitsKilled += killed;
                MelonLogger.Msg($"[RTSA/SuppressHumanAI] Purged {killed} unit(s) from {team.name} (lifetime={_lifetimeUnitsKilled}).");
            }
        }

        static bool _cashDiagLogged;
        static void TryZeroCash(Team team)
        {
            if (_totalResourcesField == null)
            {
                // The real backing field turned out to be m_StoredResources (dumped via
                // GetFields at runtime — Il2Cpp does NOT emit the C# auto-property backing
                // name for interop types). Try it first; keep the others as fallbacks in
                // case the game rev renames.
                foreach (var name in new[] { "m_StoredResources", "<TotalResources>k__BackingField", "m_TotalResources", "_totalResources" })
                {
                    var fi = typeof(Team).GetField(name,
                        BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                    if (fi != null && fi.FieldType == typeof(int)) { _totalResourcesField = fi; break; }
                }
                if (_totalResourcesField == null)
                {
                    if (!_cashDiagLogged)
                    {
                        _cashDiagLogged = true;
                        // Dump every field on Team so we can find the actual backing
                        // storage name. Il2Cpp interop occasionally renames or mangles
                        // backing fields differently than plain C#.
                        var sb = new System.Text.StringBuilder();
                        foreach (var f in typeof(Team).GetFields(
                            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public))
                        {
                            sb.Append(f.FieldType.Name).Append(' ').Append(f.Name).Append("; ");
                        }
                        MelonLogger.Warning($"[RTSA/SuppressHumanAI] Cash-zero: no field match. All Team fields: {sb}");
                    }
                    return;
                }
            }
            try
            {
                int cur = team.TotalResources;
                if (cur == 0) return;
                // The Il2Cpp binary exposes several setter paths that C# interop hides:
                //   1. set_TotalResources(int)   — property setter on the native side
                //   2. SetResources(int)         — public method that also drives the setter
                //   3. AddResources(int)         — negative amount deducts
                // Reflection can invoke any of them by name. Try each in order until one
                // takes; cache the winner so we don't re-resolve every tick.
                string usedPath = TrySetResourcesReflective(team, 0);
                if (usedPath == null && _totalResourcesField != null)
                {
                    _totalResourcesField.SetValue(team, 0);
                    usedPath = "field:" + _totalResourcesField.Name;
                }
                int after = team.TotalResources;
                if (!_cashDiagLogged)
                {
                    _cashDiagLogged = true;
                    MelonLogger.Msg($"[RTSA/SuppressHumanAI] Cash-zero: {team.name} {cur}→{after} via {usedPath ?? "NONE"}.");
                }
            }
            catch (Exception ex)
            {
                if (!_cashDiagLogged)
                {
                    _cashDiagLogged = true;
                    MelonLogger.Warning($"[RTSA/SuppressHumanAI] Cash-zero threw: {ex.Message}");
                }
            }
        }

        static MethodInfo? _setterMi;
        static string? _setterName;
        internal static string? TrySetResourcesReflective(Team team, int value)
        {
            if (_setterMi != null)
            {
                try { _setterMi.Invoke(team, new object[] { value }); return _setterName; }
                catch { }
            }
            // First-choice: game's own "resources <team> <delta>" console command.
            // Same path EcoRateSampler.TryDrain uses — bypasses every Il2Cpp trap
            // that made the earlier reflection attempts silently no-op. We ask for
            // a NEGATIVE delta to land cash at `value` (typically 0 for suppress).
            int deltaNeeded = value - team.TotalResources;
            if (deltaNeeded != 0)
            {
                if (global::Si_RTS_AI.Perception.EcoRateSampler.TryMutateTeamResources(team, deltaNeeded, out string path)
                    && team.TotalResources == value)
                {
                    _setterName = "console:" + path;
                    return _setterName;
                }
            }

            // Silent method-scan fallback — iterate methods without AccessTools.Method,
            // whose C# reflection API logs a warning on every miss. Direct GetMethods
            // gives us the same coverage without the noise.
            foreach (var name in new[] { "SetResources", "set_TotalResources" })
            {
                foreach (var mi in typeof(Team).GetMethods(
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    if (mi.Name != name) continue;
                    var pars = mi.GetParameters();
                    if (pars.Length != 1 || pars[0].ParameterType != typeof(int)) continue;
                    try
                    {
                        mi.Invoke(team, new object[] { value });
                        if (team.TotalResources == value)
                        {
                            _setterMi = mi; _setterName = name;
                            return name;
                        }
                    }
                    catch { }
                }
            }
            // Silent AddResources fallback.
            MethodInfo? add = null;
            foreach (var mi in typeof(Team).GetMethods(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (mi.Name != "AddResources") continue;
                var pars = mi.GetParameters();
                if (pars.Length == 1 && pars[0].ParameterType == typeof(int)) { add = mi; break; }
            }
            if (add != null)
            {
                try
                {
                    int delta = value - team.TotalResources;
                    add.Invoke(team, new object[] { delta });
                    _setterMi = add; _setterName = "AddResources";
                    return "AddResources";
                }
                catch { }
            }
            return null;
        }

        internal static void ResetForNewRound()
        {
            _lastPurgeAt        = 0f;
            _lifetimeUnitsKilled = 0;
        }
    }
}
