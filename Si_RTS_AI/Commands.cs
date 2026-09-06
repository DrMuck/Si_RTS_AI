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

                case "enable":
                    if (!IsAdmin(caller))
                    {
                        Reply(caller, "[RTSA] enable requires admin (Power.Generic).");
                        return;
                    }
                    HandleEnable(caller, parts);
                    return;

                case "mil":
                case "military":
                    DumpMilitary(caller);
                    return;

                case "on":
                case "off":
                    // The plain switch for a public server: "/rtsai off" hands the
                    // aliens back to the vanilla commander mid-game, "/rtsai on"
                    // takes them back. Admin only.
                    if (!IsAdmin(caller))
                    {
                        Reply(caller, "[RTSA] on/off requires admin (Power.Generic).");
                        return;
                    }
                    {
                        bool on = sub == "on";
                        global::Si_RTS_AI.Faction.FactionControl.TrySetByKey("alien", on, out _);
                        string msg = on ? "[RTSA] RTS alien AI is ON - the layer commands the aliens when no player holds the seat"
                                        : "[RTSA] RTS alien AI is OFF - the vanilla commander is in charge of the aliens";
                        Reply(caller, msg);
                        MelonLogger.Msg(msg + (caller != null ? $" (by {caller.PlayerName})" : ""));
                    }
                    return;

                default:
                    Reply(caller, "[RTSA] usage: /rtsai [on | off | status | mil | override [team] on|off | enable <alien|sol|centauri> on|off]");
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

        // /rtsai enable <alien|sol|centauri> on|off — toggle per-faction master switch.
        static void HandleEnable(Player? caller, string[] parts)
        {
            if (parts.Length < 4)
            {
                Reply(caller, "[RTSA] usage: /rtsai enable <alien|sol|centauri> on|off");
                return;
            }
            string factionKey = parts[2];
            string finalArg   = parts[3].ToLowerInvariant();
            bool? want = finalArg == "on" ? true : (finalArg == "off" ? false : (bool?)null);
            if (want == null)
            {
                Reply(caller, "[RTSA] enable requires final arg 'on' or 'off'");
                return;
            }
            if (global::Si_RTS_AI.Faction.FactionControl.TrySetByKey(factionKey, want.Value, out string canonical))
                Reply(caller, $"[RTSA] RTSAI_{canonical} = {(want.Value ? "ON" : "OFF")}");
            else
                Reply(caller, $"[RTSA] unknown faction '{factionKey}' — use alien | sol | centauri");
        }

        static void DumpStatus(Player? caller)
        {
            var sb = new StringBuilder();
            sb.AppendLine("[RTSA] status:");
            sb.AppendLine($"  factions: Alien={(global::Si_RTS_AI.Faction.FactionControl.AlienEnabled ? "ON" : "OFF")} " +
                          $"Sol={(global::Si_RTS_AI.Faction.FactionControl.SolEnabled ? "ON" : "OFF")} " +
                          $"Centauri={(global::Si_RTS_AI.Faction.FactionControl.CentauriEnabled ? "ON" : "OFF")}");
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

        /// <summary>
        /// What the army thinks it is doing, from inside the game. The military
        /// layer's log lines are 20 and 30 seconds apart, which is right for a
        /// soak and useless when somebody is playing against it and wants to know
        /// why nothing came to defend the north.
        /// </summary>
        static void DumpMilitary(Player? caller)
        {
            var cfg = new StringBuilder();
            cfg.AppendLine("[RTSA] military:");
            if (!Planning.MilitaryConfig.Enabled)
            {
                cfg.AppendLine("  OFF — set \"military\": { \"enabled\": true } in UserData/rtsai.json " +
                               "(takes effect at the next map load).");
                Reply(caller, cfg.ToString());
                return;
            }

            cfg.AppendLine($"  execute={Planning.MilitaryConfig.Execute} " +
                           $"produce={Planning.MilitaryConfig.Produce} " +
                           $"offence={Planning.MilitaryConfig.Offence}");
            cfg.AppendLine($"  army={Mil.Objectives.ArmyEff:F0} eff ({Mil.Objectives.ArmyCash} cash) " +
                           $"enemy~{Mil.Intel.EnemyEffective:F0} eff tracks={Mil.Intel.Tracks.Count} " +
                           $"known={Mil.Intel.KnownStructureCount} bases={Mil.Intel.Bases.Count} hqs={Mil.Intel.KnownEnemyHqs()}");
            cfg.AppendLine($"  units commanded: {Mil.Forces.UnitsCommanded()} (reserve {Mil.Forces.ReserveUnits}u " +
                           $"{Mil.Forces.ReserveEff:F0} eff at ({Mil.Forces.ReservePoint.x:F0},{Mil.Forces.ReservePoint.z:F0})), " +
                           $"produced this round: {Mil.ProductionV3.QueuedThisRound}");
            if (Mil.Objectives.NextOffensivePrice > 0f)
                cfg.AppendLine($"  building toward {Mil.Objectives.NextOffensivePrice:F0} eff for {Mil.Objectives.NextOffensiveWhat}");

            var objectives = Mil.Objectives.Portfolio;
            if (objectives.Count == 0) cfg.AppendLine("  no objectives");
            foreach (var o in objectives)
            {
                if (o.Status == Mil.Objectives.Status.Done || o.Status == Mil.Objectives.Status.Failed) continue;
                cfg.AppendLine($"  {o.Kind}#{o.Id} {o.Status} need {o.RequiredEff:F0} have {o.AssignedEff:F0} " +
                               $"pWin {o.PWin:F2} @({o.Where.x:F0},{o.Where.z:F0}) - {o.Note}");
            }
            foreach (var f in Mil.Forces.Active)
            {
                var c = Mil.Forces.Centroid(f);
                cfg.AppendLine($"  force {f.Name} {f.Phase} {f.Units.Count}u {f.Eff:F0} eff at ({c.x:F0},{c.z:F0})");
            }

            Reply(caller, cfg.ToString());
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
