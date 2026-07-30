using MelonLoader;
using Silica.AI;
using SilicaAdminMod;
using Si_RTS_AI.Suppression;
using System;
using System.Collections.Generic;
using System.Text;

namespace Si_RTS_AI
{
    /// <summary>
    /// Chat commands for Si_RTS_AI. Uses SilicaAdminMod's PlayerMethods.RegisterPlayerCommand
    /// (same pattern as Si_KingOfTheHill's /koh + /buy) instead of GameEvents.OnChatMessage,
    /// because the vanilla event doesn't fire for '/' messages — SilicaAdminMod's dispatcher
    /// swallows them first.
    ///
    /// Commands (all under /rtsai):
    ///   /rtsai status              — dump per-team override state + tallies (open to all)
    ///   /rtsai override on|off     — global override toggle (admin)
    ///   /rtsai override <team> on|off — per-team override (admin, substring match on team.name)
    ///
    /// Admin gating uses caller.CanAdminExecute(Power.Generic), same as KGT.
    /// </summary>
    internal static class Commands
    {
        internal static void Register()
        {
            PlayerMethods.RegisterPlayerCommand("rtsai", OnRtsAiCommand, true);
            MelonLogger.Msg("[RTSA] Registered /rtsai chat command.");
        }

        // args = the FULL chat line including the command token (SilicaAdminMod convention;
        // matches KGT's /koh handler). So parts[0] = "rtsai", parts[1] = subcommand.
        static void OnRtsAiCommand(Player? caller, string args)
        {
            var parts = (args ?? "").Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            string sub = parts.Length > 1 ? parts[1].ToLowerInvariant() : "status";

            switch (sub)
            {
                case "":
                case "status":
                    DumpStatus(caller);
                    return;

                case "override":
                    if (!IsAdmin(caller))
                    {
                        Reply(caller, "[RTSA] override requires admin (Power.Generic).");
                        return;
                    }
                    HandleOverride(caller, parts);
                    return;

                default:
                    Reply(caller, "[RTSA] usage: /rtsai [status | override [team] on|off]");
                    return;
            }
        }

        // parts layout (index 0 = "rtsai", index 1 = "override"):
        //   /rtsai override on            → parts = [rtsai, override, on]        len 3, filter=null
        //   /rtsai override off           → parts = [rtsai, override, off]       len 3, filter=null
        //   /rtsai override Sol on        → parts = [rtsai, override, Sol, on]   len 4, filter="Sol"
        //   /rtsai override Sol off       → parts = [rtsai, override, Sol, off]  len 4, filter="Sol"
        static void HandleOverride(Player? caller, string[] parts)
        {
            if (parts.Length < 3)
            {
                Reply(caller, "[RTSA] usage: /rtsai override [teamName] on|off");
                return;
            }

            string finalArg = parts[parts.Length - 1].ToLowerInvariant();
            bool? want = finalArg == "on" ? true : (finalArg == "off" ? false : (bool?)null);
            if (want == null)
            {
                Reply(caller, "[RTSA] override requires final arg 'on' or 'off'");
                return;
            }

            // Team filter is the token between "override" and the final on/off — present
            // only when parts.Length >= 4. Otherwise global (all managed teams).
            string? teamFilter = parts.Length >= 4 ? parts[2] : null;

            int matched = 0;
            foreach (var kv in AIManager.Commanders)
            {
                var team = kv.Key;
                if (team == null) continue;
                if (teamFilter != null && (team.name ?? "").IndexOf(teamFilter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                Phase31_Production.OverrideByTeam[team] = want.Value;
                matched++;
                Reply(caller, $"[RTSA] override.{team.name} = {(want.Value ? "ON" : "OFF")}");
            }
            if (matched == 0)
                Reply(caller, $"[RTSA] no AI-commanded teams matched filter='{teamFilter ?? "*"}' — nothing changed");
        }

        static void DumpStatus(Player? caller)
        {
            var sb = new StringBuilder();
            sb.AppendLine("[RTSA] status:");
            if (AIManager.Commanders.Count == 0)
            {
                sb.AppendLine("  (no AI commanders — either pre-round or all teams player-commanded)");
            }
            foreach (var kv in AIManager.Commanders)
            {
                var team = kv.Key;
                if (team == null) continue;
                Phase31_Production.OverrideByTeam.TryGetValue(team, out bool ov);
                if (!Phase31_Production.Tallies.TryGetValue(team, out var t))
                    t = new Phase31_Production.DecisionTally();
                sb.AppendLine($"  {team.name}: override={(ov ? "ON" : "OFF")} " +
                              $"samples={t.SamplesObserved} agree={t.Agreements} differ={t.Differed} " +
                              $"oursNotBuildable={t.OursNotBuildable} overridesApplied={t.OverridesApplied}");
            }
            Reply(caller, sb.ToString());
        }

        // ---- helpers ----

        static bool IsAdmin(Player? caller)
        {
            // Console (caller == null) always counts as admin.
            if (caller == null) return true;
            try { return caller.CanAdminExecute(Power.Generic); }
            catch { return false; }
        }

        static void Reply(Player? caller, string msg)
        {
            if (caller != null)
            {
                try { HelperMethods.SendChatMessageToPlayer(caller, msg); }
                catch { }
            }
            MelonLogger.Msg(msg);
        }
    }
}
