using System;
using System.Reflection;
using HarmonyLib;
using MelonLoader;
using Silica;

namespace Si_RTS_AI.Faction
{
    /// <summary>
    /// STARTING CASH AS CONFIGURED, WHATEVER THE ROUND FLOW.
    ///
    /// Si_Resources sets each team's StartingResources in a postfix on
    /// MP_Strategy.SetTeamVersusMode and reads the team's first structure for a
    /// position. In the natural round flow (mode set by VersusAutoSelectMode or
    /// the vote, bases spawned later at round start) no structure exists yet:
    /// it warned "Could not determine starting position", threw in
    /// SpawnAtLocation and never reached the second team. The game's base
    /// spawn then applied the scene's own values: 2026-09-08 15:04, DrMuck's
    /// played round, Sol 40,000 and Centauri 25,000 instead of 10,500 each.
    /// Every test round had gone through the harness, which sets the mode with
    /// spawning on, so the bug never showed.
    ///
    /// After the bases are spawned this postfix re-applies the amounts from
    /// Si_Resources' own preference entries ([Silica] Resources_*_StartingAmount)
    /// to StartingResources and to the live balance. Nothing has been spent at
    /// that moment. Silent when Si_Resources is not loaded (no entries).
    /// </summary>
    internal static class StartingResourcesGuard
    {
        static int Configured(Team team)
        {
            string n = team?.name ?? "";
            string key = n.Contains("Alien") ? "Resources_Aliens_StartingAmount"
                       : n.Contains("Sol")   ? "Resources_Sol_StartingAmount"
                       : n.Contains("Cent")  ? "Resources_Centauri_StartingAmount" : null;
            if (key == null) return -1;
            try
            {
                var cat = MelonPreferences.GetCategory("Silica");
                var e = cat?.GetEntry<int>(key);
                return e != null ? e.Value : -1;
            }
            catch { return -1; }
        }

        static PropertyInfo _startProp;

        internal static void Apply(string reason)
        {
            try
            {
                _startProp ??= typeof(Team).GetProperty("StartingResources", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                var teams = Team.Teams;
                if (teams == null) return;
                for (int i = 0; i < teams.Count; i++)
                {
                    var team = teams[i];
                    if (team == null) continue;
                    int structures = 0; try { structures = team.Structures?.Count ?? 0; } catch { }
                    if (structures == 0) continue;                       // not in this round
                    int want = Configured(team);
                    if (want < 0) continue;
                    int start = -1, cash = team.TotalResources;
                    try { start = (int)(_startProp?.GetValue(team) ?? -1); } catch { }
                    if (start == want && cash == want) continue;
                    string setPath = "";
                    try { if (start != want && _startProp?.CanWrite == true) _startProp.SetValue(team, want); } catch (Exception ex) { setPath = " start-set failed: " + ex.Message; }
                    string cashPath = "";
                    if (cash != want)
                    {
                        try { cashPath = SuppressHumanAI.TrySetResourcesReflective(team, want) ?? "none"; } catch (Exception ex) { cashPath = "threw " + ex.Message; }
                    }
                    MelonLogger.Msg($"[CASH/GUARD] {reason}: {team.name} starting {start}->{want}, cash {cash}->{team.TotalResources}{(cashPath.Length > 0 ? " via " + cashPath : "")}{setPath}");
                    try { Si_RTS_AI.AppendToRound($"[CASH/GUARD] {reason} team={team.name} starting={start}->{want} cash={cash}->{team.TotalResources}"); } catch { }
                }
            }
            catch (Exception ex) { MelonLogger.Warning("[CASH/GUARD] threw: " + ex.Message); }
        }

        [HarmonyPatch(typeof(MP_Strategy), "SpawnBaseStructures")]
        static class Patch_SpawnBaseStructures
        {
            static void Postfix() => Apply("after base spawn");
        }
    }
}
