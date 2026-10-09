using MelonLoader;

namespace Si_RTS_AI.Config
{
    /// <summary>
    /// THE SWITCHES, TYPED, AND PUSHED TO THE SUBSYSTEMS THAT OBEY THEM.
    ///
    /// Refresh() reads the active config (plus the chat overrides in state.json)
    /// into these statics and hands each value to the class that acts on it.
    /// It runs at start, at every map load, and after any chat change, so the
    /// subsystems never read the json themselves for a switch — one place
    /// decides, and the log says what was decided.
    ///
    /// Enabled is the MASTER. Off means the mod does nothing at all: no Harmony
    /// prefix runs its body, no tick does work, no file is written, the
    /// telemetry listener is down, the time scale is left alone. The one
    /// exception is serverFpsCap, a server setting that follows the config. The
    /// transition is handled by Si_RTS_AI.ApplyEnabled so held units are
    /// released and the round log closed.
    /// </summary>
    internal static class ModSwitches
    {
        // ---- master ----------------------------------------------------------
        public static bool Enabled      = true;
        /// <summary>[CMD]/[CASH]/[ORDER]/[SELECT]/[PILOT]/[TEAM] lines in the round log.</summary>
        public static bool CommanderLog = true;
        /// <summary>The per-round log file itself (and the Latest.log copy).</summary>
        public static bool RoundLog     = true;

        // ---- game-wide -------------------------------------------------------
        public static bool TestMode;
        public static bool SuppressCombat;
        public static bool EnemyBroke;
        public static bool AutoResourceDrain;
        public static int  TelemetryPort = 8765;

        // ---- planners --------------------------------------------------------
        public static bool ScoutEnabled    = true;
        public static int  ScoutMaxUnits   = 20;
        public static bool OpenerExecute   = true;
        public static bool EcoPlannerActive;

        // ---- harness (only meaningful while TestMode) ------------------------
        public static bool   AutoOverrideProduction;
        public static bool   PreventEmptyEndround   = true;
        public static bool   AutoStartRound         = true;
        public static int    AutoStartTimeoutSeconds = 45;
        public static bool   ForceAssignAICommanders = true;
        public static bool   FakeTeamJoin           = true;
        public static bool   AutoRotateMap;
        public static bool   ShrimpStateSampler;
        public static int    EndRoundAfterMinutes   = 10;
        public static int    RoundsPerMap           = 6;
        public static string MapRotation            = "NorthPolarCap,NarakaCity,WhisperingPlains";
        /// <summary>Tag on every benchmark row. Overwritten by the A/B rig per arm.</summary>
        public static string ConfigId               = "baseline";
        public static string ConfigCycle            = "";

        static bool  _first = true;
        static bool  _lastEnabled = true;
        static bool  _drainAnnounced;

        internal static void Refresh(string reason)
        {
            bool wasEnabled = _lastEnabled;

            Enabled      = ConfigStore.EnabledOverride      ?? RtsaiConfig.Bool("enabled",      true);
            CommanderLog = ConfigStore.CommanderLogOverride ?? RtsaiConfig.Bool("commanderLog", true);
            RoundLog     = ConfigStore.RoundLogOverride     ?? RtsaiConfig.Bool("roundLog",     true);

            TestMode          = RtsaiConfig.Bool("testMode",          false);
            SuppressCombat    = RtsaiConfig.Bool("suppressCombat",    false);
            EnemyBroke        = RtsaiConfig.Bool("enemyBroke",        false);
            AutoResourceDrain = RtsaiConfig.Bool("autoResourceDrain", false);
            TelemetryPort     = RtsaiConfig.Int ("telemetryPort",     8765);

            ScoutEnabled     = RtsaiConfig.Bool("scout.enabled",    true);
            ScoutMaxUnits    = RtsaiConfig.Int ("scout.maxUnits",   20);
            OpenerExecute    = RtsaiConfig.Bool("openerExecute",    true);
            EcoPlannerActive = RtsaiConfig.Bool("ecoPlannerActive", false);

            AutoOverrideProduction  = RtsaiConfig.Bool("harness.autoOverrideProduction",  false);
            PreventEmptyEndround    = RtsaiConfig.Bool("harness.preventEmptyEndround",    true);
            AutoStartRound          = RtsaiConfig.Bool("harness.autoStartRound",          true);
            AutoStartTimeoutSeconds = RtsaiConfig.Int ("harness.autoStartTimeoutSeconds", 45);
            ForceAssignAICommanders = RtsaiConfig.Bool("harness.forceAssignAICommanders", true);
            FakeTeamJoin            = RtsaiConfig.Bool("harness.fakeTeamJoin",            true);
            AutoRotateMap           = RtsaiConfig.Bool("harness.autoRotateMap",           false);
            ShrimpStateSampler      = RtsaiConfig.Bool("harness.shrimpStateSampler",      false);
            MapRotation             = RtsaiConfig.Str ("harness.mapRotation",             "NorthPolarCap,NarakaCity,WhisperingPlains");
            ConfigId                = RtsaiConfig.Str ("harness.configId",                "baseline");
            EndRoundAfterMinutes    = RtsaiConfig.Int ("endRoundAfterMinutes",            10);
            RoundsPerMap            = RtsaiConfig.Int ("roundsPerMap",                    6);
            ConfigCycle             = RtsaiConfig.Str ("configCycle",                     "");

            // ---- push to the subsystems ----
            Faction.FactionControl.AlienEnabled    = RtsaiConfig.Bool("factions.alien",    true);
            Faction.FactionControl.SolEnabled      = RtsaiConfig.Bool("factions.sol",      false);
            Faction.FactionControl.CentauriEnabled = RtsaiConfig.Bool("factions.centauri", false);

            Planning.ScoutPlanner.Enabled      = ScoutEnabled;
            Planning.ScoutPlanner.MaxScouts    = ScoutMaxUnits;
            Planning.OpenerPlanner.ShadowOnly  = !OpenerExecute;
            Planning.EcoPlanner.ExecutionEnabled = EcoPlannerActive;

            // The soak hacks only ever apply inside test mode, and never with the
            // mod off: leaving either on while a human plays means the round dies
            // mid-match and the aliens never shoot back.
            Faction.SuppressCombat.Enabled  = Enabled && TestMode && SuppressCombat;
            Faction.SuppressHumanAI.Enabled = Enabled && TestMode && EnemyBroke;

            // autoResourceDrain DESTROYS MONEY and the only way to see it is a
            // sawtooth in the cash trace, so it announces itself once.
            bool drain = Enabled && AutoResourceDrain;
            if (drain != Perception.EcoRateSampler.AutoDrainEnabled || !_drainAnnounced)
            {
                _drainAnnounced = true;
                MelonLogger.Msg(drain
                    ? "[RTSA/CONFIG] autoResourceDrain ON — team cash is cut to 70% of capacity whenever it passes 75%."
                    : "[RTSA/CONFIG] autoResourceDrain OFF — cash is left alone.");
            }
            Perception.EcoRateSampler.AutoDrainEnabled = drain;
            Perception.ShrimpStateSampler.Enabled = Enabled && ShrimpStateSampler;

            // Telemetry listener follows the master switch and the port.
            bool wantTelemetry = Enabled && TelemetryPort > 0 && TelemetryPort < 65536;
            if (wantTelemetry && Perception.TelemetryServer.IsRunning && Perception.TelemetryServer.Port != TelemetryPort)
                Perception.TelemetryServer.Stop();
            if (wantTelemetry && !Perception.TelemetryServer.IsRunning)
                Perception.TelemetryServer.Start(TelemetryPort);
            else if (!wantTelemetry && Perception.TelemetryServer.IsRunning)
                Perception.TelemetryServer.Stop();

            MelonLogger.Msg($"[RTSA/CONFIG] switches ({reason}): mod={(Enabled ? "ON" : "OFF")} " +
                            $"alien={(Faction.FactionControl.AlienEnabled ? "on" : "off")} sol={(Faction.FactionControl.SolEnabled ? "on" : "off")} cent={(Faction.FactionControl.CentauriEnabled ? "on" : "off")} " +
                            $"testMode={TestMode} ecoAssist={Planning.EcoPlannerConfig.EcoAssistWithHumanCommander} ecoPlannerActive={EcoPlannerActive} " +
                            $"scout={ScoutEnabled}/{ScoutMaxUnits} opener={(OpenerExecute ? "execute" : "shadow")} " +
                            $"commanderLog={CommanderLog} roundLog={RoundLog} telemetry={(wantTelemetry ? TelemetryPort.ToString() : "off")}");

            _lastEnabled = Enabled;
            if (_first || wasEnabled != Enabled)
            {
                _first = false;
                Si_RTS_AI.ApplyEnabled(Enabled, reason);
            }
        }
    }
}
