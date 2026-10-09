using MelonLoader;
using Si_RTS_AI.Config;
using Silica.AI;
using SilicaAdminMod;
using System;
using System.Text;

namespace Si_RTS_AI.Chat
{
    /// <summary>
    /// /rtsai — the admin's handle on the mod. Registered through
    /// SilicaAdminMod's PlayerMethods.RegisterPlayerCommand (the vanilla chat
    /// event never sees '/' messages; the Admin Mod dispatcher swallows them).
    ///
    ///   /rtsai                      admin: open the menu · others: status
    ///   /rtsai on | off | auto      master switch (kept in state.json; auto = follow the config)
    ///   /rtsai status               switches, active config, per-team override tallies
    ///   /rtsai mil                  what the army thinks it is doing
    ///   /rtsai config [name]        list configs / activate one (live keys now, the rest next map)
    ///   /rtsai reload               re-read the active config from disk now
    ///   /rtsai log [cmd|round] on|off|auto   commander-action logging / round log file
    ///   /rtsai enable alien|sol|centauri on|off   per-faction switch until the next map load
    ///   /rtsai override [team] on|off         Phase 3.1 production override
    ///   /rtsai exit                 close the menu
    ///
    /// Admin gating uses caller.CanAdminExecute(Power.Generic); the console
    /// (caller == null) always counts as admin.
    /// </summary>
    internal static class Commands
    {
        internal static void Register()
        {
            try
            {
                PlayerMethods.RegisterPlayerCommand("rtsai", OnRtsAiCommand, true);
                Menu.RegisterShortcuts();
                MelonLogger.Msg("[RTSA] Registered /rtsai chat command (+ menu shortcuts /1-/20, /0, /back).");
            }
            catch (Exception ex) { MelonLogger.Warning("[RTSA] command registration failed: " + ex.Message); }
        }

        // args = the FULL chat line including the command token (SilicaAdminMod
        // convention), so parts[0] = "rtsai", parts[1] = subcommand.
        static void OnRtsAiCommand(Player? caller, string args)
        {
            var parts = (args ?? "").Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            string sub = parts.Length > 1 ? parts[1].ToLowerInvariant() : "";

            // A number while the menu is open is menu input.
            if (Menu.IsOpen(caller) && (sub == "back" || int.TryParse(sub, out _)))
            {
                Menu.Input(caller, sub);
                return;
            }

            switch (sub)
            {
                case "":
                case "menu":
                    if (IsAdmin(caller)) Menu.Open(caller);
                    else DumpStatus(caller);
                    return;

                case "exit":
                case "close":
                    Menu.Close(caller);
                    return;

                case "status":
                    DumpStatus(caller);
                    return;

                case "mil":
                case "military":
                    DumpMilitary(caller);
                    return;

                case "on":
                case "off":
                case "auto":
                    if (!RequireAdmin(caller, "on/off")) return;
                    SetEnabled(caller, sub == "auto" ? (bool?)null : sub == "on");
                    return;

                case "config":
                case "configs":
                    if (parts.Length < 3) { ListConfigs(caller); return; }
                    if (!RequireAdmin(caller, "config")) return;
                    SelectConfig(caller, parts[2]);
                    return;

                case "reload":
                    if (!RequireAdmin(caller, "reload")) return;
                    RtsaiConfig.Reload(force: true);
                    ModSwitches.Refresh("chat reload" + Who(caller));
                    Reply(caller, $"[RTSA] re-read {ConfigStore.ActiveName}.json — live keys applied, round-start keys at the next map");
                    return;

                case "log":
                case "logging":
                    if (parts.Length < 3) { ShowLogging(caller); return; }
                    if (!RequireAdmin(caller, "log")) return;
                    HandleLog(caller, parts);
                    return;

                case "enable":
                    if (!RequireAdmin(caller, "enable")) return;
                    HandleEnable(caller, parts);
                    return;

                case "override":
                    if (!RequireAdmin(caller, "override")) return;
                    HandleOverride(caller, parts);
                    return;

                default:
                    Reply(caller, "[RTSA] usage: /rtsai [on|off|auto | status | mil | config [name] | reload | log [cmd|round] on|off|auto | enable <alien|sol|centauri> on|off | override [team] on|off | exit]");
                    return;
            }
        }

        // ---- master switch ---------------------------------------------------

        internal static void SetEnabled(Player? caller, bool? want)
        {
            ConfigStore.SetEnabledOverride(want);
            ModSwitches.Refresh((want.HasValue ? (want.Value ? "chat on" : "chat off") : "chat auto") + Who(caller));
            string msg = want == null
                ? $"[RTSA] mod follows the config now: {(ModSwitches.Enabled ? "ON" : "OFF")} per {ConfigStore.ActiveName}.json"
                : (ModSwitches.Enabled
                    ? "[RTSA] RTS AI mod is ON — the layer commands the enabled factions whenever no player holds the seat"
                    : "[RTSA] RTS AI mod is OFF — vanilla commands every team; nothing is logged, patched or served");
            Reply(caller, msg);
        }

        // ---- configs -----------------------------------------------------------

        static void ListConfigs(Player? caller)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"[RTSA] configs in {Paths.ConfigDir}:");
            foreach (var n in ConfigStore.List())
                sb.AppendLine((string.Equals(n, ConfigStore.ActiveName, StringComparison.OrdinalIgnoreCase) ? "  * " : "    ") + n);
            sb.Append("  /rtsai config <name> to activate (admin)");
            Reply(caller, sb.ToString());
        }

        internal static void SelectConfig(Player? caller, string name)
        {
            if (ConfigStore.Select(name, out string err))
            {
                MelonLogger.Msg($"[RTSA] config '{ConfigStore.ActiveName}' activated{Who(caller)}");
                Reply(caller, $"[RTSA] config -> {ConfigStore.ActiveName}: mod {(ModSwitches.Enabled ? "ON" : "OFF")}, " +
                              $"alien {OnOff(Faction.FactionControl.AlienEnabled)} sol {OnOff(Faction.FactionControl.SolEnabled)} cent {OnOff(Faction.FactionControl.CentauriEnabled)}, " +
                              $"testMode {ModSwitches.TestMode}. Economy/military keys apply at the next map load.");
            }
            else Reply(caller, "[RTSA] " + err);
        }

        // ---- logging -------------------------------------------------------------

        static void ShowLogging(Player? caller)
        {
            Reply(caller, $"[RTSA] logging: commander actions {OnOff(ModSwitches.CommanderLog)}" +
                          (ConfigStore.CommanderLogOverride.HasValue ? " (chat override)" : " (config)") +
                          $", round log {OnOff(ModSwitches.RoundLog)}" +
                          (ConfigStore.RoundLogOverride.HasValue ? " (chat override)" : " (config)") +
                          $" — file {Core.RoundLog.Path}. /rtsai log [cmd|round] on|off|auto");
        }

        static void HandleLog(Player? caller, string[] parts)
        {
            // /rtsai log on|off|auto          -> commander logging
            // /rtsai log cmd|round on|off|auto
            string which = "cmd", val;
            if (parts.Length >= 4) { which = parts[2].ToLowerInvariant(); val = parts[3].ToLowerInvariant(); }
            else val = parts[2].ToLowerInvariant();
            bool? want = val == "on" ? true : val == "off" ? false : val == "auto" ? (bool?)null : (bool?)null;
            if (val != "on" && val != "off" && val != "auto") { Reply(caller, "[RTSA] usage: /rtsai log [cmd|round] on|off|auto"); return; }
            if (which == "cmd" || which == "commander") SetCommanderLog(caller, want);
            else if (which == "round" || which == "file") SetRoundLog(caller, want);
            else Reply(caller, "[RTSA] usage: /rtsai log [cmd|round] on|off|auto");
        }

        internal static void SetCommanderLog(Player? caller, bool? want)
        {
            ConfigStore.SetCommanderLogOverride(want);
            ModSwitches.Refresh("chat commander log" + Who(caller));
            Reply(caller, $"[RTSA] commander action logging {OnOff(ModSwitches.CommanderLog)}" + (want == null ? " (following the config)" : ""));
        }

        internal static void SetRoundLog(Player? caller, bool? want)
        {
            ConfigStore.SetRoundLogOverride(want);
            ModSwitches.Refresh("chat round log" + Who(caller));
            Reply(caller, $"[RTSA] round log file {OnOff(ModSwitches.RoundLog)}" + (want == null ? " (following the config)" : ""));
        }

        // ---- factions ------------------------------------------------------------

        // /rtsai enable <alien|sol|centauri> on|off — per-faction switch for this round.
        static void HandleEnable(Player? caller, string[] parts)
        {
            if (parts.Length < 4)
            {
                Reply(caller, "[RTSA] usage: /rtsai enable <alien|sol|centauri> on|off");
                return;
            }
            string finalArg = parts[3].ToLowerInvariant();
            bool? want = finalArg == "on" ? true : (finalArg == "off" ? false : (bool?)null);
            if (want == null) { Reply(caller, "[RTSA] enable requires final arg 'on' or 'off'"); return; }
            SetFaction(caller, parts[2], want.Value);
        }

        internal static void SetFaction(Player? caller, string factionKey, bool on)
        {
            if (Faction.FactionControl.TrySetByKey(factionKey, on, out string canonical))
            {
                MelonLogger.Msg($"[RTSA] faction {canonical} {OnOff(on)}{Who(caller)} (until the next map load; the config decides then)");
                Reply(caller, $"[RTSA] {canonical} {OnOff(on)} for this round (the config applies again at the next map)");
            }
            else Reply(caller, $"[RTSA] unknown faction '{factionKey}' — use alien | sol | centauri");
        }

        // ---- Phase 3.1 production override ------------------------------------------

        //   /rtsai override on            → all AI-commanded teams
        //   /rtsai override Sol off       → one team (substring match)
        static void HandleOverride(Player? caller, string[] parts)
        {
            if (parts.Length < 3) { Reply(caller, "[RTSA] usage: /rtsai override [teamName] on|off"); return; }
            string finalArg = parts[parts.Length - 1].ToLowerInvariant();
            bool? want = finalArg == "on" ? true : (finalArg == "off" ? false : (bool?)null);
            if (want == null) { Reply(caller, "[RTSA] override requires final arg 'on' or 'off'"); return; }
            string? teamFilter = parts.Length >= 4 ? parts[2] : null;

            int matched = 0;
            foreach (var kv in AIManager.Commanders)
            {
                var team = kv.Key;
                if (team == null) continue;
                if (teamFilter != null && (team.name ?? "").IndexOf(teamFilter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                Suppression.Phase31_Production.OverrideByTeam[team] = want.Value;
                matched++;
                Reply(caller, $"[RTSA] override.{team.name} = {OnOff(want.Value)}");
            }
            if (matched == 0)
                Reply(caller, $"[RTSA] no AI-commanded teams matched filter='{teamFilter ?? "*"}' — nothing changed");
        }

        // ---- status --------------------------------------------------------------

        internal static void DumpStatus(Player? caller)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"[RTSA] v0.94.0 mod {OnOff(ModSwitches.Enabled)}" +
                          (ConfigStore.EnabledOverride.HasValue ? " (chat override)" : "") +
                          $" · config {ConfigStore.ActiveName} · testMode {ModSwitches.TestMode}");
            sb.AppendLine($"  factions: Alien={OnOff(Faction.FactionControl.AlienEnabled)} " +
                          $"Sol={OnOff(Faction.FactionControl.SolEnabled)} " +
                          $"Centauri={OnOff(Faction.FactionControl.CentauriEnabled)} · " +
                          $"ecoAssist={Planning.EcoPlannerConfig.EcoAssistWithHumanCommander} lockAlienSeat={Faction.AlienCommanderLock.Enabled} " +
                          $"scouts={ModSwitches.ScoutEnabled}/{ModSwitches.ScoutMaxUnits} military={Planning.MilitaryConfig.Enabled}");
            sb.AppendLine($"  logging: commander {OnOff(ModSwitches.CommanderLog)} · round log {OnOff(ModSwitches.RoundLog)} · telemetry {(Perception.TelemetryServer.IsRunning ? Perception.TelemetryServer.Port.ToString() : "off")}");
            if (AIManager.Commanders.Count == 0)
                sb.AppendLine("  (no AI commanders — either pre-round or all teams player-commanded)");
            foreach (var kv in AIManager.Commanders)
            {
                var team = kv.Key;
                if (team == null) continue;
                Suppression.Phase31_Production.OverrideByTeam.TryGetValue(team, out bool ov);
                if (!Suppression.Phase31_Production.Tallies.TryGetValue(team, out var t))
                    t = new Suppression.Phase31_Production.DecisionTally();
                sb.AppendLine($"  {team.name}: override={OnOff(ov)} " +
                              $"samples={t.SamplesObserved} agree={t.Agreements} differ={t.Differed} " +
                              $"oursNotBuildable={t.OursNotBuildable} overridesApplied={t.OverridesApplied}");
            }
            Reply(caller, sb.ToString().TrimEnd());
        }

        /// <summary>
        /// What the army thinks it is doing, from inside the game. The military
        /// layer's log lines are 20 and 30 seconds apart, which is right for a
        /// soak and useless when somebody is playing against it and wants to know
        /// why nothing came to defend the north.
        /// </summary>
        internal static void DumpMilitary(Player? caller)
        {
            var cfg = new StringBuilder();
            cfg.AppendLine("[RTSA] military:");
            if (!ModSwitches.Enabled) { cfg.Append("  the mod is OFF"); Reply(caller, cfg.ToString()); return; }
            if (!Planning.MilitaryConfig.Enabled)
            {
                cfg.Append("  OFF — set \"military\": { \"enabled\": true } in the active config (takes effect at the next map load).");
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
            Reply(caller, cfg.ToString().TrimEnd());
        }

        // ---- helpers ---------------------------------------------------------------

        internal static string OnOff(bool b) => b ? "ON" : "OFF";
        static string Who(Player? caller) => caller != null ? $" (by {caller.PlayerName})" : " (console)";

        internal static bool IsAdmin(Player? caller)
        {
            if (caller == null) return true;   // console
            try { return caller.CanAdminExecute(Power.Generic); }
            catch { return false; }
        }

        static bool RequireAdmin(Player? caller, string what)
        {
            if (IsAdmin(caller)) return true;
            Reply(caller, $"[RTSA] {what} requires admin (Power.Generic).");
            return false;
        }

        internal static void Reply(Player? caller, string msg)
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
