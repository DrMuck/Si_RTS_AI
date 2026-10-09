using MelonLoader;
using Si_RTS_AI.Config;
using SilicaAdminMod;
using System;
using System.Collections.Generic;

namespace Si_RTS_AI.Chat
{
    /// <summary>
    /// THE NUMBERED CHAT MENU BEHIND /rtsai — the same shape as Si_UnitBalance's !b:
    /// a page of numbered lines, /1../20 picks one, /0 exits, /back goes up.
    ///
    ///   === RTS AI ===
    ///   1. Mod: ON                 toggle the master switch
    ///   2. Config: public-play     pick another json from UserData/RTSAI/configs
    ///   3. Factions: ...           per-faction switches for this round
    ///   4. Logging: ...            commander actions / round log file
    ///   5. Status   6. Military   7. Reload config
    ///
    /// SHORTCUT CHAINING. The Admin Mod runs the FIRST handler registered under a
    /// command name, and Si_UnitBalance also owns /1-/20. So this mod registers
    /// its handlers ahead of any existing ones and forwards to them whenever the
    /// caller is not inside THIS menu — both menus keep working, whichever mod
    /// loaded first.
    /// </summary>
    internal static class Menu
    {
        enum Level { Root, Configs, Factions, Logging }

        class State
        {
            public Level Level = Level.Root;
            public string[] Configs = Array.Empty<string>();
        }

        // Keyed by PlayerID (SteamID) so the state survives Il2Cpp wrapper churn; the console is "console".
        static readonly Dictionary<string, State> _states = new Dictionary<string, State>();
        static readonly Dictionary<string, HelperMethods.CommandCallback> _ours = new Dictionary<string, HelperMethods.CommandCallback>(StringComparer.OrdinalIgnoreCase);

        const string PFX  = "<b><color=#DDE98C>[</color><color=#7DD4FF>RTSAI</color><color=#DDE98C>]</color></b> ";
        const string HDR  = "<color=#FFD700>";
        const string ITEM = "<color=#AAFFAA>";
        const string DIM  = "<color=#888888>";
        const string END  = "</color>";
        static string OnOff(bool b) => b ? "<color=#55FF55>ON</color>" : "<color=#FF5555>OFF</color>";

        static string Key(Player? p)
        {
            if (p == null) return "console";
            try { return p.PlayerID.ToString(); } catch { return p.PlayerName ?? "?"; }
        }

        internal static bool IsOpen(Player? p) => _states.ContainsKey(Key(p));

        internal static void Open(Player? p)
        {
            var st = new State();
            _states[Key(p)] = st;
            Send(p, PFX + HDR + "=== RTS AI ===" + END + " " + DIM + "(/1 ../9 pick · /0 exit · /back)" + END);
            Show(p, st);
        }

        internal static void Close(Player? p)
        {
            if (_states.Remove(Key(p))) Send(p, PFX + DIM + "menu closed." + END);
        }

        /// <summary>"1".."20", "0" or "back" from a shortcut or /rtsai N.</summary>
        internal static void Input(Player? p, string text)
        {
            if (!_states.TryGetValue(Key(p), out var st)) return;
            text = (text ?? "").Trim().ToLowerInvariant();
            if (text == "0" || text == "exit") { Close(p); return; }
            if (text == "back")
            {
                if (st.Level == Level.Root) { Close(p); return; }
                st.Level = Level.Root; Show(p, st); return;
            }
            if (!int.TryParse(text, out int n)) { Show(p, st); return; }

            try
            {
                switch (st.Level)
                {
                    case Level.Root:     Root(p, st, n); break;
                    case Level.Configs:  Configs(p, st, n); break;
                    case Level.Factions: Factions(p, st, n); break;
                    case Level.Logging:  Logging(p, st, n); break;
                }
            }
            catch (Exception ex) { MelonLogger.Warning("[RTSA] menu input threw: " + ex.Message); }
        }

        // ---- pages -----------------------------------------------------------

        static void Show(Player? p, State st)
        {
            switch (st.Level)
            {
                case Level.Root:
                    Send(p, PFX + ITEM + "1." + END + " Mod: " + OnOff(ModSwitches.Enabled) +
                            (ConfigStore.EnabledOverride.HasValue ? DIM + " (chat override)" + END : "") +
                            "   " + ITEM + "2." + END + " Config: " + HDR + ConfigStore.ActiveName + END);
                    Send(p, PFX + ITEM + "3." + END + " Factions: Alien " + OnOff(Faction.FactionControl.AlienEnabled) +
                            " · Sol " + OnOff(Faction.FactionControl.SolEnabled) +
                            " · Cent " + OnOff(Faction.FactionControl.CentauriEnabled));
                    Send(p, PFX + ITEM + "4." + END + " Logging: commander " + OnOff(ModSwitches.CommanderLog) +
                            " · round log " + OnOff(ModSwitches.RoundLog));
                    Send(p, PFX + ITEM + "5." + END + " Status   " + ITEM + "6." + END + " Military   " +
                            ITEM + "7." + END + " Reload config   " + ITEM + "0." + END + " Exit");
                    break;

                case Level.Configs:
                    st.Configs = ConfigStore.List();
                    Send(p, PFX + HDR + "Config" + END + " " + DIM + Paths.ConfigDir + END);
                    if (st.Configs.Length == 0) Send(p, PFX + DIM + "no configs found." + END);
                    for (int i = 0; i < st.Configs.Length && i < 20; i++)
                    {
                        bool active = string.Equals(st.Configs[i], ConfigStore.ActiveName, StringComparison.OrdinalIgnoreCase);
                        Send(p, PFX + ITEM + (i + 1) + "." + END + " " + (active ? HDR + st.Configs[i] + END + DIM + "  (active)" + END : st.Configs[i]));
                    }
                    Send(p, PFX + DIM + "pick a number to activate · /back" + END);
                    break;

                case Level.Factions:
                    Send(p, PFX + HDR + "Factions" + END + " " + DIM + "(this round; the config applies again at the next map)" + END);
                    Send(p, PFX + ITEM + "1." + END + " Alien " + OnOff(Faction.FactionControl.AlienEnabled) +
                            "   " + ITEM + "2." + END + " Sol " + OnOff(Faction.FactionControl.SolEnabled) +
                            "   " + ITEM + "3." + END + " Centauri " + OnOff(Faction.FactionControl.CentauriEnabled));
                    Send(p, PFX + DIM + "pick a number to toggle · /back" + END);
                    break;

                case Level.Logging:
                    Send(p, PFX + HDR + "Logging" + END);
                    Send(p, PFX + ITEM + "1." + END + " Commander actions " + OnOff(ModSwitches.CommanderLog) +
                            (ConfigStore.CommanderLogOverride.HasValue ? DIM + " (chat override)" + END : DIM + " (config)" + END));
                    Send(p, PFX + ITEM + "2." + END + " Round log file " + OnOff(ModSwitches.RoundLog) +
                            (ConfigStore.RoundLogOverride.HasValue ? DIM + " (chat override)" + END : DIM + " (config)" + END));
                    Send(p, PFX + ITEM + "3." + END + " Clear overrides (follow the config)   " + DIM + "/back" + END);
                    break;
            }
        }

        static void Root(Player? p, State st, int n)
        {
            switch (n)
            {
                case 1: Commands.SetEnabled(p, !ModSwitches.Enabled); Show(p, st); break;
                case 2: st.Level = Level.Configs; Show(p, st); break;
                case 3: st.Level = Level.Factions; Show(p, st); break;
                case 4: st.Level = Level.Logging; Show(p, st); break;
                case 5: Commands.DumpStatus(p); break;
                case 6: Commands.DumpMilitary(p); break;
                case 7:
                    RtsaiConfig.Reload(force: true);
                    ModSwitches.Refresh("menu reload");
                    Send(p, PFX + "re-read " + HDR + ConfigStore.ActiveName + ".json" + END + DIM + " — live keys applied, the rest at the next map" + END);
                    Show(p, st);
                    break;
                default: Show(p, st); break;
            }
        }

        static void Configs(Player? p, State st, int n)
        {
            if (n < 1 || n > st.Configs.Length) { Show(p, st); return; }
            Commands.SelectConfig(p, st.Configs[n - 1]);
            st.Level = Level.Root;
            Show(p, st);
        }

        static void Factions(Player? p, State st, int n)
        {
            switch (n)
            {
                case 1: Commands.SetFaction(p, "alien",    !Faction.FactionControl.AlienEnabled); break;
                case 2: Commands.SetFaction(p, "sol",      !Faction.FactionControl.SolEnabled); break;
                case 3: Commands.SetFaction(p, "centauri", !Faction.FactionControl.CentauriEnabled); break;
            }
            Show(p, st);
        }

        static void Logging(Player? p, State st, int n)
        {
            switch (n)
            {
                case 1: Commands.SetCommanderLog(p, !ModSwitches.CommanderLog); break;
                case 2: Commands.SetRoundLog(p, !ModSwitches.RoundLog); break;
                case 3: Commands.SetCommanderLog(p, null); Commands.SetRoundLog(p, null); break;
            }
            Show(p, st);
        }

        // ---- shortcuts -------------------------------------------------------

        internal static void RegisterShortcuts()
        {
            var names = new List<string> { "0", "back" };
            for (int i = 1; i <= 20; i++) names.Add(i.ToString());
            int chained = 0;
            foreach (var name in names)
            {
                string n = name;
                HelperMethods.CommandCallback cb = (player, args) => OnShortcut(n, player, args);
                _ours[n] = cb;
                try
                {
                    // Ours goes FIRST: the dispatcher stops at the first name match.
                    var list = PlayerMethods.PlayerCommands;
                    var existing = new List<PlayerCommand>();
                    if (list != null)
                    {
                        foreach (var c in list)
                            if (c != null && string.Equals(c.CommandName, n, StringComparison.OrdinalIgnoreCase)) existing.Add(c);
                        foreach (var c in existing) list.Remove(c);
                    }
                    PlayerMethods.RegisterPlayerCommand(n, cb, true);
                    if (list != null) foreach (var c in existing) { list.Add(c); chained++; }
                }
                catch (Exception ex) { MelonLogger.Warning($"[RTSA] shortcut /{n}: {ex.Message}"); }
            }
            if (chained > 0) MelonLogger.Msg($"[RTSA] menu shortcuts chained ahead of {chained} existing handler(s) from other mods");
        }

        static void OnShortcut(string name, Player? player, string args)
        {
            if (IsOpen(player))
            {
                if (!Commands.IsAdmin(player)) { Close(player); return; }
                Input(player, name);
                return;
            }
            // Not in our menu: hand the command to whoever else owns it (Si_UnitBalance's !b).
            try
            {
                var list = PlayerMethods.PlayerCommands;
                if (list == null) return;
                _ours.TryGetValue(name, out var mine);
                // Snapshot first: a handler may register more commands while running.
                var others = new List<PlayerCommand>();
                foreach (var c in list)
                    if (c != null && string.Equals(c.CommandName, name, StringComparison.OrdinalIgnoreCase) && !ReferenceEquals(c.PlayerCommandCallback, mine))
                        others.Add(c);
                foreach (var c in others)
                {
                    try { c.PlayerCommandCallback?.Invoke(player, args); }
                    catch (Exception ex) { MelonLogger.Warning($"[RTSA] chained /{name} handler threw: {ex.Message}"); }
                }
            }
            catch { }
        }

        static void Send(Player? p, string msg)
        {
            if (p == null) { MelonLogger.Msg(System.Text.RegularExpressions.Regex.Replace(msg, "<[^>]+>", "")); return; }
            try { HelperMethods.SendChatMessageToPlayer(p, msg); } catch { }
        }
    }
}
