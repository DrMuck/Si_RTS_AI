using MelonLoader;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace Si_RTS_AI.Config
{
    /// <summary>
    /// WHICH CONFIG IS ACTIVE, AND THE FEW THINGS SET FROM CHAT.
    ///
    /// UserData/RTSAI/configs/ holds any number of json configurations
    /// (public-play, coop-eco, headless-alien-only, ...). Exactly one is active;
    /// its name is kept in UserData/RTSAI/state.json together with the overrides
    /// an admin sets from chat (mod on/off, commander logging). state.json is the
    /// only file the mod writes, and it is tiny, so the configs themselves stay
    /// hand-edited and never get rewritten from memory.
    ///
    /// Precedence for a switch: chat override in state.json > active config >
    /// built-in default. Clearing the override from chat hands the switch back
    /// to the config.
    ///
    /// First start on a server that still has the pre-0.94 layout
    /// (UserData/rtsai.json plus three MelonPreferences sections) imports both
    /// into configs/migrated.json and activates it, so nothing changes
    /// behaviourally until somebody picks another config.
    /// </summary>
    internal static class ConfigStore
    {
        class State
        {
            public string ActiveConfig = "";
            public bool?  Enabled;
            public bool?  CommanderLog;
            public bool?  RoundLog;
        }

        static State _state = new State();
        static bool _inited;

        internal static string ActiveName       => _state.ActiveConfig ?? "";
        internal static string ActiveConfigPath => string.IsNullOrEmpty(ActiveName) ? "" : Paths.ConfigFile(ActiveName);

        internal static bool? EnabledOverride      => _state.Enabled;
        internal static bool? CommanderLogOverride => _state.CommanderLog;
        internal static bool? RoundLogOverride     => _state.RoundLog;

        internal static void SetEnabledOverride(bool? v)      { _state.Enabled = v;      SaveState(); }
        internal static void SetCommanderLogOverride(bool? v) { _state.CommanderLog = v; SaveState(); }
        internal static void SetRoundLogOverride(bool? v)     { _state.RoundLog = v;     SaveState(); }

        /// <summary>Create the folder, import an old layout, read state.json and
        /// make sure something is active. Idempotent.</summary>
        internal static void Init()
        {
            if (_inited) return;
            _inited = true;
            try { Directory.CreateDirectory(Paths.ConfigDir); } catch (Exception ex) { MelonLogger.Warning("[RTSAI/CONFIG] cannot create " + Paths.ConfigDir + ": " + ex.Message); }
            try { Migrate(); } catch (Exception ex) { MelonLogger.Warning("[RTSAI/CONFIG] migration threw: " + ex.Message); }
            LoadState();
            EnsureActive();
            MelonLogger.Msg($"[RTSAI/CONFIG] folder {Path.GetFullPath(Paths.Root)} — configs: {string.Join(", ", List())} — active: {ActiveName}" +
                            (_state.Enabled.HasValue ? $" — chat override enabled={_state.Enabled}" : "") +
                            (_state.CommanderLog.HasValue ? $" commanderLog={_state.CommanderLog}" : "") +
                            (_state.RoundLog.HasValue ? $" roundLog={_state.RoundLog}" : ""));
        }

        /// <summary>Config names (file names without .json), sorted. Files
        /// starting with '_' are skipped so notes can live beside the configs.</summary>
        internal static string[] List()
        {
            var names = new List<string>();
            try
            {
                if (Directory.Exists(Paths.ConfigDir))
                    foreach (var f in Directory.GetFiles(Paths.ConfigDir, "*.json"))
                    {
                        string n = Path.GetFileNameWithoutExtension(f);
                        if (!string.IsNullOrEmpty(n) && !n.StartsWith("_")) names.Add(n);
                    }
            }
            catch (Exception ex) { MelonLogger.Warning("[RTSAI/CONFIG] listing failed: " + ex.Message); }
            names.Sort(StringComparer.OrdinalIgnoreCase);
            return names.ToArray();
        }

        internal static bool Exists(string name) =>
            !string.IsNullOrEmpty(name) && File.Exists(Paths.ConfigFile(name));

        /// <summary>Make <paramref name="name"/> the active config and load it.
        /// Live keys apply at once, round-start keys at the next map load.</summary>
        internal static bool Select(string name, out string error)
        {
            error = "";
            if (!Exists(name)) { error = $"no config '{name}' in {Paths.ConfigDir}"; return false; }
            // Resolve the on-disk spelling so the state file and the log agree.
            foreach (var n in List()) if (string.Equals(n, name, StringComparison.OrdinalIgnoreCase)) { name = n; break; }
            _state.ActiveConfig = name;
            SaveState();
            RtsaiConfig.Reload(force: true);
            ModSwitches.Refresh("config '" + name + "' selected");
            MelonLogger.Msg($"[RTSAI/CONFIG] active config -> {name}");
            return true;
        }

        // ---- state.json ------------------------------------------------------

        static void LoadState()
        {
            try
            {
                if (!File.Exists(Paths.StateFile)) return;
                var j = JObject.Parse(File.ReadAllText(Paths.StateFile));
                _state.ActiveConfig = j.Value<string>("activeConfig") ?? "";
                _state.Enabled      = j["enabled"]?.Type      == JTokenType.Boolean ? j.Value<bool>("enabled")      : (bool?)null;
                _state.CommanderLog = j["commanderLog"]?.Type == JTokenType.Boolean ? j.Value<bool>("commanderLog") : (bool?)null;
                _state.RoundLog     = j["roundLog"]?.Type     == JTokenType.Boolean ? j.Value<bool>("roundLog")     : (bool?)null;
            }
            catch (Exception ex) { MelonLogger.Warning("[RTSAI/CONFIG] state.json unreadable (" + ex.Message + ") — starting fresh"); }
        }

        static void SaveState()
        {
            try
            {
                var j = new JObject
                {
                    ["_readme"] = "Written by Si_RTS_AI. activeConfig names a file in configs/; the other keys are chat overrides (absent = the config decides).",
                    ["activeConfig"] = _state.ActiveConfig ?? ""
                };
                if (_state.Enabled.HasValue)      j["enabled"]      = _state.Enabled.Value;
                if (_state.CommanderLog.HasValue) j["commanderLog"] = _state.CommanderLog.Value;
                if (_state.RoundLog.HasValue)     j["roundLog"]     = _state.RoundLog.Value;
                Directory.CreateDirectory(Paths.Root);
                // JToken.ToString() without the Formatting overload: the server's
                // Newtonsoft lacks ToString(Formatting) (MissingMethodException).
                File.WriteAllText(Paths.StateFile, j.ToString());
            }
            catch (Exception ex) { MelonLogger.Warning("[RTSAI/CONFIG] could not write state.json: " + ex.Message); }
        }

        static void EnsureActive()
        {
            if (Exists(ActiveName)) return;
            var all = List();
            if (all.Length == 0)
            {
                WriteDefaultConfig();
                all = List();
            }
            string pick = "";
            foreach (var n in all) if (string.Equals(n, "public-play", StringComparison.OrdinalIgnoreCase)) pick = n;
            if (pick.Length == 0 && all.Length > 0) pick = all[0];
            if (!string.IsNullOrEmpty(ActiveName))
                MelonLogger.Warning($"[RTSAI/CONFIG] state.json names '{ActiveName}' but no such config exists — using '{pick}'");
            _state.ActiveConfig = pick;
            SaveState();
        }

        static void WriteDefaultConfig()
        {
            try
            {
                var j = new JObject
                {
                    ["_readme"] = new JArray(
                        "Generated because UserData/RTSAI/configs/ was empty. Every key is optional;",
                        "see configs/KEYS.md in the repo for the full list. Pick a config in game",
                        "with /rtsai, or edit this file - it is re-read at every map load."),
                    ["enabled"] = true,
                    ["factions"] = new JObject { ["alien"] = true, ["sol"] = false, ["centauri"] = false },
                    ["testMode"] = false,
                    ["commanderLog"] = true,
                    ["military"] = new JObject { ["enabled"] = false }
                };
                File.WriteAllText(Paths.ConfigFile("default"), j.ToString());
                MelonLogger.Msg("[RTSAI/CONFIG] wrote configs/default.json");
            }
            catch (Exception ex) { MelonLogger.Warning("[RTSAI/CONFIG] could not write default.json: " + ex.Message); }
        }

        // ---- migration from the pre-0.94 layout -------------------------------

        /// <summary>Old preference key -> json key (dotted).</summary>
        static readonly Dictionary<string, string> PrefToJson = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            // [HeadlessTest]
            { "TestMode",                              "testMode" },
            { "EnemyBroke",                            "enemyBroke" },
            { "AutoResourceDrain",                     "autoResourceDrain" },
            { "RTSAI_Alien",                           "factions.alien" },
            { "RTSAI_Sol",                             "factions.sol" },
            { "RTSAI_Centauri",                        "factions.centauri" },
            { "ScoutEnabled",                          "scout.enabled" },
            { "ScoutMaxUnits",                         "scout.maxUnits" },
            { "OpenerExecute",                         "openerExecute" },
            { "HeadlessTest_EcoPlannerActive",         "ecoPlannerActive" },
            { "HeadlessTest_AutoOverrideRtsai",        "harness.autoOverrideProduction" },
            { "HeadlessTest_PreventEmptyEndround",     "harness.preventEmptyEndround" },
            { "HeadlessTest_EndRoundAfterMinutes",     "endRoundAfterMinutes" },
            { "HeadlessTest_AutoStartRound",           "harness.autoStartRound" },
            { "HeadlessTest_AutoStartTimeoutSeconds",  "harness.autoStartTimeoutSeconds" },
            { "HeadlessTest_ForceAssignAICommanders",  "harness.forceAssignAICommanders" },
            { "HeadlessTest_FakeTeamJoin",             "harness.fakeTeamJoin" },
            { "HeadlessTest_SuppressCombat",           "suppressCombat" },
            { "HeadlessTest_TelemetryPort",            "telemetryPort" },
            { "HeadlessTest_MapRotation",              "harness.mapRotation" },
            { "HeadlessTest_AutoRotateMap",            "harness.autoRotateMap" },
            { "HeadlessTest_ConfigId",                 "harness.configId" },
            { "HeadlessTest_ConfigCycle",              "configCycle" },
            { "HeadlessTest_RoundsPerMap",             "roundsPerMap" },
            { "HeadlessTest_ShrimpStateSampler",       "harness.shrimpStateSampler" },
            // [Si_RTS_AI_EcoPlanner]
            { "Phase2CystCoverageRadiusM",             "eco.phase2CystCoverageRadiusM" },
            { "Phase2CystMinClusterPatches",           "eco.phase2CystMinClusterPatches" },
            { "Phase2CystTargetFarthestInCluster",     "eco.phase2CystTargetFarthestInCluster" },
            { "Phase2MaxUncystedBcQueue",              "eco.phase2MaxUncystedBcQueue" },
            { "Phase1MinTappedPatches",                "eco.phase1MinTappedPatches" },
            { "EcoAssistWithHumanCommander",           "ecoAssistWithHumanCommander" },
            // [Si_RTS_AI_Blueprint]
            { "BlueprintDrivesPhase2",                 "blueprintDrivesPhase2" },
            { "ReplanIntervalS",                       "replanIntervalS" },
            { "MaxSitesPerPlan",                       "blueprint.maxSitesPerPlan" },
            { "MaxCystsPerPlan",                       "maxCystsPerPlan" },
            { "CystStaffedEnough",                     "blueprint.cystStaffedEnough" },
            { "CystRelocationSpacings",                "blueprint.cystRelocationSpacings" },
            { "NoCystsFromShrimpCount",                "blueprint.noCystsFromShrimpCount" },
            { "PersistPlans",                          "blueprint.persistPlans" },
            { "CystStrategyAuto",                      "cystStrategyAuto" },
            { "WorkersByTenMinutes",                   "workersByTenMinutes" },
            { "WorkerCapPerBioCache",                  "workerCapPerBioCache" },
            { "ProducerPerSites",                      "producerPerSites" },
        };

        static readonly string[] PrefSections = { "HeadlessTest", "Si_RTS_AI_EcoPlanner", "Si_RTS_AI_Blueprint" };
        static readonly string[] DataFiles    = { "rtsai_units.json", "mil_doctrine.json", "commander_compositions.csv" };

        static void Migrate()
        {
            // Data files: copy (not move) into the folder so release.py and the
            // old paths keep working until the originals are cleaned up by hand.
            foreach (var f in DataFiles)
            {
                string old = Path.Combine(Paths.UserData, f), now = Path.Combine(Paths.Root, f);
                try
                {
                    if (File.Exists(old) && !File.Exists(now))
                    {
                        File.Copy(old, now);
                        MelonLogger.Msg($"[RTSAI/CONFIG] copied {old} -> {now}");
                    }
                }
                catch (Exception ex) { MelonLogger.Warning($"[RTSAI/CONFIG] copying {f}: {ex.Message}"); }
            }

            // The old single file is imported once, as configs/migrated.json, even
            // when presets are already in the folder. It becomes the active config
            // only if nothing is active yet, so behaviour does not change underneath
            // a server that has already chosen.
            if (!File.Exists(Paths.LegacyConfig)) return; // fresh server: default.json will be written if needed
            if (Exists("migrated")) return;              // imported before

            JObject root;
            try { root = JObject.Parse(File.ReadAllText(Paths.LegacyConfig)); }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[RTSAI/CONFIG] {Paths.LegacyConfig} unreadable ({ex.Message}) — not migrated");
                return;
            }

            int imported = 0;
            string cfgPath = Path.Combine(Paths.UserData, "MelonPreferences.cfg");
            if (File.Exists(cfgPath))
            {
                try
                {
                    foreach (var kv in ReadPrefSections(File.ReadAllText(cfgPath)))
                    {
                        if (!PrefToJson.TryGetValue(kv.Key, out string jsonKey)) continue;
                        if (SetIfAbsent(root, jsonKey, kv.Value)) imported++;
                    }
                }
                catch (Exception ex) { MelonLogger.Warning("[RTSAI/CONFIG] reading MelonPreferences.cfg for migration: " + ex.Message); }
            }

            root.AddFirst(new JProperty("_migrated", new JArray(
                $"Imported {DateTime.Now:yyyy-MM-dd HH:mm} from UserData/rtsai.json plus the [HeadlessTest], " +
                "[Si_RTS_AI_EcoPlanner] and [Si_RTS_AI_Blueprint] sections of MelonPreferences.cfg.",
                "Keys that were already in rtsai.json kept their value; " + imported + " came from the preferences.",
                "Those three sections are no longer read; they can be deleted from MelonPreferences.cfg.")));

            string dest = Paths.ConfigFile("migrated");
            File.WriteAllText(dest, root.ToString());
            LoadState();
            bool activate = string.IsNullOrEmpty(_state.ActiveConfig) || !Exists(_state.ActiveConfig);
            if (activate) { _state.ActiveConfig = "migrated"; SaveState(); }
            MelonLogger.Msg($"[RTSAI/CONFIG] MIGRATED {Paths.LegacyConfig} (+{imported} preference values) -> {dest}" +
                            (activate ? "; it is the active config. " : " (not activated: '" + _state.ActiveConfig + "' already is). ") +
                            "The old rtsai.json is left in place and no longer read.");
        }

        /// <summary>key = value pairs from the three sections this mod used to own.</summary>
        static IEnumerable<KeyValuePair<string, JToken>> ReadPrefSections(string text)
        {
            string section = "";
            var line = new Regex(@"^\s*([A-Za-z0-9_]+)\s*=\s*(.+?)\s*$");
            foreach (var raw in text.Split('\n'))
            {
                string l = raw.TrimEnd('\r');
                var t = l.Trim();
                if (t.StartsWith("[") && t.EndsWith("]")) { section = t.Substring(1, t.Length - 2); continue; }
                if (Array.IndexOf(PrefSections, section) < 0) continue;
                if (t.StartsWith("#")) continue;
                var m = line.Match(l);
                if (!m.Success) continue;
                yield return new KeyValuePair<string, JToken>(m.Groups[1].Value, ParsePrefValue(m.Groups[2].Value));
            }
        }

        static JToken ParsePrefValue(string v)
        {
            v = v.Trim();
            if (v.Length >= 2 && v[0] == '"' && v[v.Length - 1] == '"') return v.Substring(1, v.Length - 2);
            if (bool.TryParse(v, out bool b)) return b;
            if (int.TryParse(v, out int i)) return i;
            if (double.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double d)) return d;
            return v;
        }

        /// <summary>Set a dotted key, creating objects on the way, unless it is already present.</summary>
        static bool SetIfAbsent(JObject root, string dotted, JToken value)
        {
            var parts = dotted.Split('.');
            JObject cur = root;
            for (int i = 0; i < parts.Length - 1; i++)
            {
                if (!(cur[parts[i]] is JObject next))
                {
                    if (cur[parts[i]] != null) return false;   // a scalar sits where an object should be: leave it
                    next = new JObject();
                    cur[parts[i]] = next;
                }
                cur = next;
            }
            string last = parts[parts.Length - 1];
            if (cur[last] != null) return false;
            cur[last] = value;
            return true;
        }
    }
}
