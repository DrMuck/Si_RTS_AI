using System;
using System.Linq;
using MelonLoader;
using Si_RTS_AI.Config;

namespace Si_RTS_AI.Chat
{
    /// <summary>
    /// What the Si_ModUI server mod reads through reflection (public static string ModUIData()):
    /// the /rtsai sub-commands as buttons, since the Admin Mod registry only knows the bare "/rtsai".
    /// Entries marked a=true are admin-only; the ModUI server never sends them to players without the power.
    /// "ch" lists [value,label] choices; with "v" (the current value) ModUI draws the entry as a switch row
    /// (one button per choice, the current one highlighted, a click sends text + " " + value).
    /// "info" lines are shown above the command list.
    /// </summary>
    public static class ModUIHook
    {
        public static string? ModUIData()
        {
            try
            {
                string mode = ConfigStore.EnabledOverride.HasValue ? (ConfigStore.EnabledOverride.Value ? "on" : "off") : "auto";
                string cmdLog = ConfigStore.CommanderLogOverride.HasValue ? (ConfigStore.CommanderLogOverride.Value ? "on" : "off") : "auto";
                string roundLog = ConfigStore.RoundLogOverride.HasValue ? (ConfigStore.RoundLogOverride.Value ? "on" : "off") : "auto";
                bool overrideOn = false;
                try { overrideOn = Suppression.Phase31_Production.OverrideByTeam.Values.Any(v => v); } catch { }

                var x = global::ModUI.ModUIExport.New()
                    .Info($"RTS AI mod {(ModSwitches.Enabled ? "ON" : "OFF")} (switch {mode}) | config {ConfigStore.ActiveName} | " +
                          $"Alien {OnOff(Faction.FactionControl.AlienEnabled)}, Sol {OnOff(Faction.FactionControl.SolEnabled)}, Centauri {OnOff(Faction.FactionControl.CentauriEnabled)}")
                    .Info($"commander log {OnOff(ModSwitches.CommanderLog)} ({cmdLog}) | round log {OnOff(ModSwitches.RoundLog)} ({roundLog})" + (overrideOn ? " | production override ON" : ""))
                    // players
                    .Action("/rtsai", "RTS AI", "Status for players; the interactive chat menu for admins.")
                    .Action("/rtsai status", "Status", "Switches, active config and per-team override tallies.")
                    .Action("/rtsai mil", "Military report", "What the AI army thinks it is doing right now.")
                    .Action("/rtsai configs", "List configs", "The config presets on the server; * marks the active one.")
                    .Action("/rtsai log", "Logging state", "Commander-action logging and round log switches.")
                    // admin switches
                    .Switch("/rtsai", "RTS AI mod", "ON: the AI commands every enabled faction whenever no player holds the seat. OFF: vanilla commands every team. AUTO: follow the active config.", admin: true, current: mode, ("on", "ON"), ("off", "OFF"), ("auto", "AUTO"));
                var configs = new System.Collections.Generic.List<(string, string)>();
                try { foreach (var name in ConfigStore.List()) configs.Add((name, name)); }
                catch { }
                x.Switch("/rtsai config", "Active config", "Switch to another preset. Economy and military keys apply at the next map load.", admin: true, current: ConfigStore.ActiveName, configs.ToArray())
                 .OnOff("/rtsai enable alien", "Alien AI", "Until the next map load; the config decides again then.", admin: true, on: Faction.FactionControl.AlienEnabled)
                 .OnOff("/rtsai enable sol", "Sol AI", "Until the next map load; the config decides again then.", admin: true, on: Faction.FactionControl.SolEnabled)
                 .OnOff("/rtsai enable centauri", "Centauri AI", "Until the next map load; the config decides again then.", admin: true, on: Faction.FactionControl.CentauriEnabled)
                 .OnOff("/rtsai override", "Production override", "Phase 3.1 production override for all AI-commanded teams (this round).", admin: true, on: overrideOn)
                 .Switch("/rtsai log cmd", "Commander log", "Log every commander action. AUTO follows the config.", admin: true, current: cmdLog, ("on", "ON"), ("off", "OFF"), ("auto", "AUTO"))
                 .Switch("/rtsai log round", "Round log file", "Write the round log file. AUTO follows the config.", admin: true, current: roundLog, ("on", "ON"), ("off", "OFF"), ("auto", "AUTO"))
                 // admin actions
                 .Action("/rtsai reload", "Reload config", "Re-read the active config from disk; live keys apply now, the rest at the next map.", admin: true)
                 .Action("/rtsai menu", "Chat menu", "The interactive /rtsai menu (numbered options).", admin: true);
                return x.Build();
            }
            catch (Exception ex)
            {
                MelonLogger.Warning("[RTSA] ModUIData failed: " + ex.Message);
                return null;
            }
        }

        static string OnOff(bool b) => b ? "on" : "off";
    }
}
