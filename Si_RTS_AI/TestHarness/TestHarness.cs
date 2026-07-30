using HarmonyLib;
using MelonLoader;
using Silica.AI;
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace Si_RTS_AI.TestHarnessNs
{
    /// <summary>
    /// Headless test harness — lets us drive a Silica dedicated server through a full
    /// round with zero human players so we can regression-test the AI slice in CI/soak.
    ///
    /// All behavior gated behind MelonPreferences category "HeadlessTest":
    ///   HeadlessTest_Enable                (bool, default false)  master switch
    ///   HeadlessTest_AutoOverrideRtsai     (bool, default true)   flip Phase31 override on for every AI-commanded team on scene load
    ///   HeadlessTest_PreventEmptyEndround  (bool, default true)   Harmony-patch GameMode.set_NoPlayersTime to keep NoPlayersTime pinned at 0 (stock ends the round when empty for 300s)
    ///   HeadlessTest_EndRoundAfterMinutes  (int,  default 10, clamped [1..120]) after this many wall-clock minutes since scene load, force-end the round via GameMode.CurrentGameMode.EndRound()
    ///   HeadlessTest_AutoStartRound        (bool, default true)   headless auto-start: call MP_Strategy.SetTeamVersusMode(VersusAutoSelectMode, preventSpawning:false) ONCE per scene, then passively wait for Silica's own pre-round countdown (GameModeExt.Timer) to transition MissionState INIT->STARTED naturally. If the natural transition doesn't happen within HeadlessTest_AutoStartTimeoutSeconds, force-flip as a last-resort fallback.
    ///   HeadlessTest_AutoStartTimeoutSeconds (int, default 45, clamped [15..300]) how long to wait for the natural INIT->STARTED transition after SetTeamVersusMode before falling back to a force-flip.
    ///   HeadlessTest_ForceAssignAICommanders (bool, default true) belt-and-braces: iterate MP_Strategy.TeamSetups and ensure each active empty-commander team has an AICommander registered and enabled via AIManager.AddCommander + EnableCommander. With v0.7.34's passive AutoStart this is largely redundant (the natural STARTED transition also fires EnableCommander per team via OnMissionStateChanged) but the calls are idempotent so it's kept as a safety net. Retries per tick until every active team succeeds or FORCE_ASSIGN_TIMEOUT_S elapses.
    ///
    /// Wiring points (in Si_RTS_AI.cs):
    ///   OnInitializeMelon → TestHarness.Init()
    ///   OnSceneWasLoaded  → TestHarness.OnSceneLoaded(sceneName)  AFTER existing state resets
    ///   OnUpdate          → TestHarness.Tick()
    /// </summary>
    internal static class TestHarness
    {
        const string CAT = "HeadlessTest";

        // How long ForceAssignAICommanders will retry before giving up (per scene).
        // Keep this small — if TeamSetups aren't populated within 10s, something upstream
        // is wrong and we're better off logging the failure than looping forever.
        const float FORCE_ASSIGN_TIMEOUT_S = 10f;

        // Pref entries — populated in Init(); nullable-annotated so we can guard against
        // Init not having run (defensive; wiring guarantees it does).
        static MelonPreferences_Category? _cat;
        // Removed 2026-07-09 as duplicates of TestMode/EnemyBroke:
        //   _enable          → replaced by _testMode
        //   _suppressHumanAI → replaced by _enemyBroke
        static MelonPreferences_Entry<bool>? _autoOverride;
        static MelonPreferences_Entry<bool>? _preventEmptyEndround;
        static MelonPreferences_Entry<int>?  _endRoundAfterMinutes;
        static MelonPreferences_Entry<bool>? _autoStartRound;
        static MelonPreferences_Entry<int>?  _autoStartTimeoutSeconds;
        static MelonPreferences_Entry<bool>? _forceAssignAICommanders;
        static MelonPreferences_Entry<bool>? _fakeTeamJoin;
        static MelonPreferences_Entry<bool>? _suppressCombat;
        // User 2026-07-09 (beta branch): clearer names.
        // TestMode replaces HeadlessTest_Enable (whether the harness runs at all).
        // EnemyBroke replaces HeadlessTest_SuppressHumanAI (whether Human teams
        // get their cash zeroed + starter units purged for alien-eco-only soak).
        // Both default false — normal gameplay. If either is unset in the .cfg,
        // fall back to the older HeadlessTest_* key for backwards compat.
        static MelonPreferences_Entry<bool>? _testMode;
        static MelonPreferences_Entry<bool>? _enemyBroke;
        static MelonPreferences_Entry<bool>? _autoResourceDrain;
        // Per-faction master switch — user 2026-07-09. Alien default true,
        // humans default false → out of the box, our mod only manages Alien.
        static MelonPreferences_Entry<bool>? _rtsaiAlien;
        static MelonPreferences_Entry<bool>? _rtsaiSol;
        static MelonPreferences_Entry<bool>? _rtsaiCentauri;
        // Basic Military Manager — user 2026-07-09. Opt-in for now.
        static MelonPreferences_Entry<bool>?  _militaryEnabled;
        static MelonPreferences_Entry<int>?   _militaryCriticalMass;
        static MelonPreferences_Entry<float>? _militaryCrabFraction;
        static MelonPreferences_Entry<bool>?  _scoutEnabled;
        static MelonPreferences_Entry<int>?   _scoutMaxUnits;
        static MelonPreferences_Entry<bool>?  _openerExecute;
        static MelonPreferences_Entry<int>?  _telemetryPort;
        static MelonPreferences_Entry<string>? _mapRotation;
        static MelonPreferences_Entry<string>? _configId;
        static MelonPreferences_Entry<bool>?   _ecoPlannerActive;

        // Per-scene runtime state.
        static bool  _sceneActive;          // did OnSceneLoaded accept this scene?
        static float _sceneLoadedAt;        // Time.time at scene load
        static bool  _autoOverrideApplied;  // one-shot per scene
        static bool  _forceEndRoundFired;   // one-shot per scene — don't spam EndRound() every tick
        static bool  _autoStartFired;       // one-shot per scene — set when STARTED observed OR fallback fires
        static bool  _forceAssignFired;     // one-shot per scene — set when every active team is assigned OR timeout
        static bool  _fakeTeamJoinFired;    // one-shot per scene — set once the FakeTeamJoin sim has run
        static float _fakeTeamJoinArmedAt;  // Time.time when MinimalAutoStart fired — we wait a bit after that
        static Player? _fakeJoinPlayer;     // synthetic Player produced by Player.AddPlayer, cached for logging

        // AutoStart passive-wait state (v0.7.34):
        //   _autoStartVsModeSet   — true once SetTeamVersusMode has been successfully invoked this scene
        //   _autoStartVsModeSetAt — Time.time timestamp of that call (baseline for the timeout clock)
        //   _lastLoggedMissionState / _haveLastLoggedMissionState — tracks state transitions so
        //                            we log ONCE per change (not every tick)
        //   _nextTimerLogAt       — throttles the "waiting… GameModeTimer=..." heartbeat to
        //                            avoid log spam while the countdown ticks down
        static bool  _autoStartVsModeSet;
        static float _autoStartVsModeSetAt;
        static GameModeExt.EMissionState _lastLoggedMissionState;
        static bool  _haveLastLoggedMissionState;

        // Public accessor for other subsystems that must gate on round
        // activity — the planner, shrimp relocator, etc. Reading this
        // instead of touching game internals avoids threading + interop
        // hazards. Set from Tick() on the main thread each tick.
        internal static bool IsRoundActive { get; private set; }
        static float _nextTimerLogAt;

        // Cached values so the Harmony prefix (which doesn't easily reach the entry
        // instances during PatchAll) can read them lock-free.
        static bool _cachedEnable;
        static bool _cachedPreventEmptyEndround;

        // ================================================================
        // Init — set up prefs + register the Harmony patch. Idempotent.
        // ================================================================
        internal static void Init()
        {
            try
            {
                _cat = MelonPreferences.CreateCategory(CAT, "Si_RTS_AI Headless Test");
                _autoOverride         = _cat.CreateEntry("HeadlessTest_AutoOverrideRtsai",    true,  "Auto-flip Phase31 production override on for every AI-commanded team");
                _preventEmptyEndround = _cat.CreateEntry("HeadlessTest_PreventEmptyEndround", true,  "Prevent stock's 300s no-players auto-endround by pinning NoPlayersTime at 0");
                _endRoundAfterMinutes = _cat.CreateEntry("HeadlessTest_EndRoundAfterMinutes", 10,    "Force-end the round after this many minutes since scene load (clamped 1..120)");
                _autoStartRound       = _cat.CreateEntry("HeadlessTest_AutoStartRound",       true,  "Headless auto-start: call SetTeamVersusMode(VersusAutoSelectMode) once per scene, then passively wait for Silica's own pre-round countdown to transition MissionState INIT->STARTED naturally. Force-flip fallback only if the natural transition times out.");
                _autoStartTimeoutSeconds = _cat.CreateEntry("HeadlessTest_AutoStartTimeoutSeconds", 45, "Seconds to wait after SetTeamVersusMode for the natural INIT->STARTED transition before falling back to a force-flip (clamped 15..300)");
                _forceAssignAICommanders = _cat.CreateEntry("HeadlessTest_ForceAssignAICommanders", true, "Belt-and-braces: iterate MP_Strategy.TeamSetups and ensure each active empty-commander team has an AICommander registered + enabled via AIManager.AddCommander/EnableCommander. Idempotent — the natural STARTED transition also does this via OnMissionStateChanged.");
                _fakeTeamJoin         = _cat.CreateEntry("HeadlessTest_FakeTeamJoin",         true,  "v0.7.38: on a fully-headless server (0 clients), simulate a player picking a team AFTER SetTeamVersusMode to unblock the round-start transition. Live modding experience says a real client's team pick is the natural trigger for MissionState INIT->STARTED — this replays that server-side by synthesizing a Player via Player.AddPlayer(NetworkID, name), pushing them onto the first active human team, and calling OnPlayerJoinedBase + NetworkLayer.SendPlayerSelectTeam + AIManager.AddCommander/EnableCommander. One-shot per scene.");
                _suppressCombat       = _cat.CreateEntry("HeadlessTest_SuppressCombat",      true,  "v0.7.53: prefix-suppress AIGroup.OnAttackOrder unconditionally. AI still forms combat groups internally but no attack orders leave the box. Lets us measure pure eco potential without combat destroying economy. Turn OFF for real gameplay.");
                _telemetryPort        = _cat.CreateEntry("HeadlessTest_TelemetryPort",       8765,  "v0.7.56: HTTP port for the browser-based layers viewer to poll (localhost only). Set 0 to disable the listener. Endpoints: /catalog, /layer/{team}/{name}, /state.");
                _testMode             = _cat.CreateEntry("TestMode",   false, "Enable the headless test harness. When true, bot auto-joins to start the round and the round is force-ended after N minutes. When false: normal gameplay (harness dormant).");
                _enemyBroke           = _cat.CreateEntry("EnemyBroke", false, "Suppress human enemy AI. Only takes effect when TestMode=true. When true: Human teams (Sol/Centauri) have their cash zeroed + starter units purged every ~1s for alien-eco-only benchmarks. When false: humans play normally.");
                _autoResourceDrain    = _cat.CreateEntry("AutoResourceDrain", false, "When true: EcoRateSampler auto-drains team cash down to 70% cap once it exceeds 75% (min floor 20k). Only useful for benchmark measurement — soak runs want continuous income growth for cumulIncome tracking. When false: cash caps normally (default for regular gameplay).");
                _rtsaiAlien           = _cat.CreateEntry("RTSAI_Alien",     true,  "Master switch — enable Si_RTS_AI's decisions for Alien team. When false, stock game AI runs for Alien.");
                _rtsaiSol             = _cat.CreateEntry("RTSAI_Sol",       false, "Master switch — enable Si_RTS_AI's decisions for Human Sol team. When false, stock game AI runs for Sol.");
                _rtsaiCentauri        = _cat.CreateEntry("RTSAI_Centauri",  false, "Master switch — enable Si_RTS_AI's decisions for Human Centauri team. When false, stock game AI runs for Centauri.");
                _militaryEnabled      = _cat.CreateEntry("MilitaryEnabled",           false, "Basic MilitaryManager: perception (threats + HVT targets), production (Crab at fraction of Cysts), and army coordination (attack only at critical mass). Opt-in; requires SuppressCombat=false for actual attack orders.");
                _militaryCriticalMass = _cat.CreateEntry("MilitaryCriticalMassSize",  15,    "Minimum combat unit count before MilitaryManager orders an attack. Below this, army stays grouped at rally point.");
                _militaryCrabFraction = _cat.CreateEntry("MilitaryCystCrabFraction",  0.25f, "Fraction of eligible Cysts dedicated to Crab production (rest keep making Shrimps). 0.25 = every 4th Cyst.");
                _scoutEnabled         = _cat.CreateEntry("ScoutEnabled",                  true,  "ScoutPlanner: conscript up to 2 starter Crabs as dedicated scouts and sweep a star of waypoints outward from the Nest, snapping each waypoint to the nearest undiscovered biotics patch. Reveals ground for FoW-gated BC placement. When false, Crabs stay under vanilla AI control.");
                _scoutMaxUnits        = _cat.CreateEntry("ScoutMaxUnits",                 20,    "How many tier-0 units (Crab / Squid) ScoutPlanner fields. Each owns one arm of a uniform star from the Nest — 20 arms = one every 18 degrees. Existing units are conscripted first; the rest are built as cheap Crabs on a slow trickle.");
                _openerExecute        = _cat.CreateEntry("OpenerExecute",                 true,  "Phase 1 OpenerPlanner drives the opening build order instead of the beam. False = shadow mode (logs its chosen opening, builds nothing). Abandons itself if a step stalls 45s.");
                _mapRotation          = _cat.CreateEntry("HeadlessTest_MapRotation",         "NorthPolarCap,NarakaCity,WhisperingPlains", "Comma-separated map names to cycle through for soak testing. Logged at scene load and echoed at round end — auto-cycling isn't wired yet, so restart the server with the next map name in this list to rotate. Guards against overfitting AI tuning to a single map.");
                _configId             = _cat.CreateEntry("HeadlessTest_ConfigId",            "baseline", "Free-form tag identifying the AI-config version this soak run represents (e.g. 'baseline', 'utility_bc_v1'). Written to each benchmark row so 'python analyze.py | group_by(configId)' can diff A/B eco.");
                _ecoPlannerActive     = _cat.CreateEntry("HeadlessTest_EcoPlannerActive",    false,   "Rolling-horizon eco planner action-execution switch. OFF (default): planner runs in shadow mode, logging [PLAN] recommendations only. ON: planner fires the winning action via HelperMethods.SpawnAtLocation when expected_gain exceeds the min-conviction threshold. Independent of AutoOverrideRtsai — works even when a human is commanding the alien team.");

                RefreshCache();
                // Keep the cache in sync if an admin edits the file mid-session.
                MelonPreferences.OnPreferencesLoaded.Subscribe((_) => RefreshCache());

                MelonLogger.Msg($"[RTSA/HT] TestHarness prefs registered under category '{CAT}'. Enable={_cachedEnable} AutoOverride={_autoOverride.Value} PreventEmpty={_cachedPreventEmptyEndround} EndAfter={_endRoundAfterMinutes.Value}min AutoStart={_autoStartRound.Value} AutoStartTimeout={ClampTimeoutSeconds(_autoStartTimeoutSeconds.Value)}s ForceAssignAI={_forceAssignAICommanders.Value} FakeTeamJoin={_fakeTeamJoin.Value} TelemetryPort={_telemetryPort.Value}");

                // Start telemetry HTTP listener whenever a port is configured.
                // Decoupled from TestMode 2026-07-09 — the LayersViewer is
                // independent observability that runs during normal gameplay
                // too. Set TelemetryPort=0 in the .cfg to disable.
                int port = _telemetryPort?.Value ?? 0;
                if (port > 0 && port < 65536)
                    global::Si_RTS_AI.Perception.TelemetryServer.Start(port);
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[RTSA/HT] TestHarness.Init failed: {ex}");
            }
        }

        static void RefreshCache()
        {
            bool testModeOn = _testMode?.Value ?? false;
            _cachedEnable                = testModeOn;
            _cachedPreventEmptyEndround  = _preventEmptyEndround?.Value ?? false;
            // Push suppress-combat flag to the Harmony-patch reader.
            global::Si_RTS_AI.Faction.SuppressCombat.Enabled =
                testModeOn && (_suppressCombat?.Value ?? false);
            // EnemyBroke only takes effect while TestMode is on — outside
            // test mode we always leave humans alone regardless of pref.
            bool enemyBroke = _enemyBroke?.Value ?? false;
            global::Si_RTS_AI.Faction.SuppressHumanAI.Enabled = testModeOn && enemyBroke;
            // AutoResourceDrain toggles the EcoRateSampler's saturation drain.
            // Independent of TestMode — a soak measurement without the harness
            // may still want the drain to keep income measurable past cap.
            global::Si_RTS_AI.Perception.EcoRateSampler.AutoDrainEnabled =
                _autoResourceDrain?.Value ?? false;
            // Per-faction master switches. Alien defaults true; humans false.
            global::Si_RTS_AI.Faction.FactionControl.AlienEnabled    = _rtsaiAlien?.Value    ?? true;
            global::Si_RTS_AI.Faction.FactionControl.SolEnabled      = _rtsaiSol?.Value      ?? false;
            global::Si_RTS_AI.Faction.FactionControl.CentauriEnabled = _rtsaiCentauri?.Value ?? false;
            // MilitaryManager knobs.
            global::Si_RTS_AI.Faction.MilitaryManager.Enabled                   = _militaryEnabled?.Value      ?? false;
            global::Si_RTS_AI.Faction.MilitaryManager.CriticalMassSize          = _militaryCriticalMass?.Value ?? 15;
            global::Si_RTS_AI.Faction.MilitaryManager.CystCrabProductionFraction = _militaryCrabFraction?.Value ?? 0.25f;
            global::Si_RTS_AI.Planning.ScoutPlanner.Enabled    = _scoutEnabled?.Value  ?? true;
            global::Si_RTS_AI.Planning.ScoutPlanner.MaxScouts  = _scoutMaxUnits?.Value ?? 20;
            global::Si_RTS_AI.Planning.OpenerPlanner.ShadowOnly = !(_openerExecute?.Value ?? true);
            // Wire the eco-planner execution switch. Independent of _enable —
            // the planner should be able to run its execution path even in a
            // regular game where the test harness master switch is off.
            global::Si_RTS_AI.Planning.EcoPlanner.ExecutionEnabled =
                _ecoPlannerActive?.Value ?? false;
        }

        // ================================================================
        // Scene load — arm timers and (later) apply the auto-override.
        // ================================================================
        internal static void OnSceneLoaded(string sceneName)
        {
            RefreshCache();
            _sceneActive               = false;
            _autoOverrideApplied       = false;
            _forceEndRoundFired        = false;
            _autoStartFired            = false;
            _forceAssignFired          = false;
            _fakeTeamJoinFired         = false;
            _fakeTeamJoinArmedAt       = 0f;
            _fakeJoinPlayer            = null;
            _autoStartVsModeSet        = false;
            _autoStartVsModeSetAt      = 0f;
            _haveLastLoggedMissionState = false;
            _lastLoggedMissionState    = default;
            _nextTimerLogAt            = 0f;

            // Publish _sceneActive independent of TestMode so Tick() can
            // still poll MissionState → IsRoundActive for other subsystems
            // (EcoPlanner bails when !IsRoundActive). Harness-specific
            // automation stays gated on _cachedEnable inside Tick().
            if (!IsGameplayScene(sceneName))
            {
                MelonLogger.Msg($"[RTSA/HT] Scene '{sceneName}' skipped (not a gameplay scene).");
                return;
            }
            _sceneActive    = true;
            _sceneLoadedAt  = Time.time;

            if (!_cachedEnable) return;
            int mins    = ClampMinutes(_endRoundAfterMinutes?.Value ?? 10);
            int autoTo  = ClampTimeoutSeconds(_autoStartTimeoutSeconds?.Value ?? 45);
            bool supHumanAI = global::Si_RTS_AI.Faction.SuppressHumanAI.Enabled;
            string cfgId   = _configId?.Value ?? "-";
            MelonLogger.Msg($"[RTSA/HT] Armed for scene '{sceneName}'. Force-end in {mins} min. AutoOverride={_autoOverride?.Value == true}. PreventEmptyEndround={_cachedPreventEmptyEndround}. AutoStartRound={_autoStartRound?.Value == true} (timeout {autoTo}s). ForceAssignAICommanders={_forceAssignAICommanders?.Value == true}. FakeTeamJoin={_fakeTeamJoin?.Value == true}. SuppressHumanAI={supHumanAI} ConfigId='{cfgId}'");

            // Log the map-rotation hint. Auto-cycling isn't wired yet (Silica's
            // NextMap/StartVoteForNextMap APIs exist but need per-map wiring); this
            // just names the next map you'd expect to see so it's easy to spot
            // if the server got stuck on one map instead of rotating.
            var rot = ParseMapRotation();
            if (rot.Count > 0)
            {
                int idx = rot.FindIndex(m => string.Equals(m, sceneName, StringComparison.OrdinalIgnoreCase));
                string next = rot[(idx < 0 ? 0 : (idx + 1) % rot.Count)];
                MelonLogger.Msg($"[RTSA/HT] MapRotation: current='{sceneName}' (index {idx}), configured={string.Join(",", rot)}. Restart with map='{next}' for next round to rotate.");
            }
        }

        static List<string> ParseMapRotation()
        {
            var raw = _mapRotation?.Value ?? "";
            var list = new List<string>();
            foreach (var p in raw.Split(','))
            {
                var s = p?.Trim();
                if (!string.IsNullOrEmpty(s)) list.Add(s);
            }
            return list;
        }

        static bool IsGameplayScene(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            string ls = s.ToLowerInvariant();
            // Silica non-gameplay scenes we've seen: MainMenu, Loading, Intro, Splash,
            // Startup, Boot. Match on substring to be safe against exact-name drift.
            if (ls.Contains("mainmenu") ||
                ls.Contains("loading")  ||
                ls.Contains("intro")    ||
                ls.Contains("splash")   ||
                ls.Contains("startup")  ||
                ls.Contains("boot")     ||
                ls.Contains("dontdestroy"))
                return false;
            return true;
        }

        static int ClampMinutes(int m)
        {
            if (m < 1)   return 1;
            if (m > 120) return 120;
            return m;
        }

        static int ClampTimeoutSeconds(int s)
        {
            if (s < 15)  return 15;
            if (s > 300) return 300;
            return s;
        }

        // ================================================================
        // Tick — called from Si_RTS_AI.OnUpdate.
        // ================================================================
        internal static void Tick()
        {
            if (!_sceneActive) return;

            // MissionState observation MUST run whether TestMode is on or off,
            // because IsRoundActive is what EcoPlanner (and other subsystems)
            // gate on. Decoupled from _cachedEnable 2026-07-11 — previously
            // TestMode=false left IsRoundActive stuck at false, so Alien AI
            // silently did nothing on production servers.
            LogNaturalStartProgress();

            if (!_cachedEnable) return;
            if (!_autoStartFired && _autoStartRound?.Value == true)
                TryMinimalAutoStart();

            // Alien-eco-only soak: periodically purge human units + zero cash so
            // starter units and any auto-produced units don't wander around
            // corrupting the alien-side eco measurement.
            global::Si_RTS_AI.Faction.SuppressHumanAI.TickPeriodic();

            // (0b) v0.7.38: after MinimalAutoStart has fired and TeamsVersus is set,
            // simulate a player picking a team to unblock the natural round-start.
            // Ground truth: on a fully headless server, SetTeamVersusMode alone does NOT
            // trigger MissionState INIT->STARTED — a client's team pick does. Replay
            // that server-side by synthesizing a Player and pushing it onto a team.
            if (!_fakeTeamJoinFired && _fakeTeamJoin?.Value == true && _autoStartFired)
                TryFakeTeamJoin();

            // (1) auto-override: try each tick until at least one commander exists, then
            // flip Phase31.OverrideByTeam=true for every AI-commanded team, once.
            if (!_autoOverrideApplied && _autoOverride?.Value == true)
            {
                try
                {
                    var cmds = AIManager.Commanders;
                    if (cmds != null && cmds.Count > 0)
                    {
                        int flipped = 0;
                        foreach (var kv in cmds)
                        {
                            var team = kv.Key;
                            if (team == null) continue;
                            Suppression.Phase31_Production.OverrideByTeam[team] = true;
                            flipped++;
                        }
                        _autoOverrideApplied = true;
                        MelonLogger.Msg($"[RTSA/HT] AutoOverride: flipped Phase31 override=true on {flipped} team(s).");
                    }
                }
                catch (Exception ex)
                {
                    // Retry next tick — most likely AIManager not ready yet.
                    MelonLogger.Warning($"[RTSA/HT] AutoOverride attempt failed (will retry): {ex.Message}");
                }
            }

            // (2) force-end after N minutes
            if (!_forceEndRoundFired)
            {
                int mins = ClampMinutes(_endRoundAfterMinutes?.Value ?? 10);
                float thresholdSeconds = mins * 60f;
                float elapsed = Time.time - _sceneLoadedAt;
                if (elapsed >= thresholdSeconds)
                {
                    _forceEndRoundFired = true;
                    ForceEndRound(elapsed);
                }
            }
        }

        // Minimal auto-start (v0.7.37): after 3s, call SetTeamVersusMode ONCE from
        // config's VersusAutoSelectMode value. Nothing else. Log what we did.
        static void TryMinimalAutoStart()
        {
            try
            {
                float elapsed = Time.time - _sceneLoadedAt;
                if (elapsed < 3f) return;   // let scene settle

                var gm = GameMode.CurrentGameMode;
                if (gm == null) return;
                var mp = gm as MP_Strategy;
                if (mp == null)
                {
                    _autoStartFired = true;
                    MelonLogger.Msg($"[RTSA/HT] MinimalAutoStart: not MP_Strategy — {gm.GetType().Name}. Nothing to do.");
                    return;
                }

                if (mp.TeamsVersus != GameModeExt.ETeamsVersus.NONE)
                {
                    _autoStartFired = true;
                    // TeamsVersus was set upstream (e.g. by AutoTeamsSelect). We still want
                    // FakeTeamJoin to try — arm its clock too.
                    _fakeTeamJoinArmedAt = Time.time;
                    MelonLogger.Msg($"[RTSA/HT] MinimalAutoStart: TeamsVersus already {mp.TeamsVersus}. Skipping SetTeamVersusMode; arming FakeTeamJoin anyway.");
                    return;
                }

                var vsMode = ReadVersusAutoSelectMode();
                mp.SetTeamVersusMode(vsMode, preventSpawning: false);
                _autoStartFired = true;
                // Arm the FakeTeamJoin clock so (0b) waits ~2s for TeamSetups to populate
                // before iterating them — SetTeamVersusMode's internal setup work is async.
                _fakeTeamJoinArmedAt = Time.time;
                MelonLogger.Msg($"[RTSA/HT] MinimalAutoStart: called SetTeamVersusMode({vsMode}) at t+{elapsed:F1}s. Now observing whether the round transitions to STARTED naturally.");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[RTSA/HT] MinimalAutoStart threw: {ex.Message}");
            }
        }

        // ================================================================
        // TryFakeTeamJoin — v0.7.38
        //
        // Ground truth from live testing: SetTeamVersusMode alone makes teams *visible*
        // in the client UI when a real player joins, but does NOT trigger the round-start
        // (MissionState INIT->STARTED) transition on a fully-headless (0-client) server.
        // The natural trigger is a client's team-pick — REQUEST_JOIN_TEAM RPC processed
        // server-side. We can't drive an RPC without a network peer, so we replay the
        // server-side effect directly:
        //
        //   1. Synthesize a Player via Player.AddPlayer(NetworkID, name) — Player has a
        //      public 2-arg static factory that registers the player in the game's
        //      internal registry (probed live against the shipped SilicaCore.dll).
        //   2. Push that Player onto the first active human team (setup.Commander is
        //      settable to a Player, per StrategyTeamSetup public field probe).
        //   3. Fire the notification cascade that a real team-pick would:
        //        - GameMode.OnPlayerJoinedBase(player)     [instance, 1-arg, public]
        //        - NetworkLayer.SendPlayerSelectTeam(p,t)  [static, public — same call
        //                                                   Si_BasicTeamBalance.SwapTeam uses]
        //   4. As a belt-and-braces, also run the v0.7.33 AI-commander bring-up path
        //      (AIManager.AddCommander + EnableCommander) for every active empty-commander
        //      team, but INLINE here so we're not entangled with v0.7.33's own
        //      TryForceAssignAICommanders (which we're not touching).
        //
        // Approach ordering per task brief:
        //   - Approach 3 (AIManager.AddCommander + EnableCommander) — safest, all public APIs
        //   - Approach 1 (setup.Commander = fake Player)             — fills the "team has a
        //     commander" slot the round-start gate probably reads
        //   - Approach 2 (OnPlayerJoinedBase + SendPlayerSelectTeam) — mimics the real
        //     server-side handler cascade
        //
        // API accessibility (probed against C:\Users\schwe\Projects\Si_RTS_AI\include\
        // netstandard2.1\SilicaCore.dll via _probe.cs compile-error technique — no
        // reflection needed for any of the below):
        //   Player                     : new Player() ctor is public                       [compiled]
        //   Player.AddPlayer(NetworkID, string)                                             [compiled — returns Player]
        //   Player.Team           setter public                                             [compiled]
        //   Player.PlayerName     setter public                                             [compiled]
        //   NetworkID(ulong) ctor public                                                    [compiled]
        //   MP_Strategy.TeamSetups                                                          [known public]
        //   StrategyTeamSetup.Team / Commander / AICommanderSettings public fields          [compiled]
        //   MP_Strategy.GetTeamSetupActive(setup)                                           [known public]
        //   GameMode.OnPlayerJoinedBase(Player) instance, 1-arg                             [compiled]
        //   NetworkLayer.SendPlayerSelectTeam(Player, Team) static public                   [compiled — matches
        //                                                                                    Si_BasicTeamBalance.SwapTeam]
        //   AIManager.AddCommander / EnableCommander / GetCommander / IsCommanderEnabled   [known public]
        //
        // Ship status: shipped based on API introspection (all public, no reflection).
        // NOT confirmed live at ship time — server-side round-transition observation is
        // the caller's responsibility. If MissionState still doesn't transition to
        // STARTED after this fires, next steps in order of likelihood:
        //   a) Player.AddPlayer may need PlayerChannel wiring the ctor doesn't do — try
        //      also setting via reflection AccessTools.Field(typeof(Player),"PlayerChannel")
        //      to a non-zero value.
        //   b) The gate may specifically want NumConnectedPlayers>0, which our synthetic
        //      Player may not increment. If so, probe MP_Strategy for a UpdatePlayerCount
        //      or set_PlayerCount and drive it explicitly.
        //   c) The gate may be inside MP_Strategy.UpdateGameLoop, which observation
        //      showed *doesn't tick* on 0-client servers. Fake-join might not wake it —
        //      may need to trigger UpdateGameLoop via a Harmony transpiler or by
        //      forcing NetworkLayer.LocalPlayerPresent.
        // ================================================================
        static void TryFakeTeamJoin()
        {
            // Wait ~2s after MinimalAutoStart fired so SetTeamVersusMode's internal
            // TeamSetups population (async in SpawnSideAITeams) has settled.
            const float POST_VSMODE_DELAY_S = 2f;
            if (_fakeTeamJoinArmedAt <= 0f) return;
            float sinceArm = Time.time - _fakeTeamJoinArmedAt;
            if (sinceArm < POST_VSMODE_DELAY_S) return;

            try
            {
                var gm = GameMode.CurrentGameMode;
                if (gm == null) return;

                var mp = gm as MP_Strategy;
                if (mp == null)
                {
                    _fakeTeamJoinFired = true;
                    MelonLogger.Msg($"[RTSA/HT] FakeTeamJoin: skipping — CurrentGameMode is {gm.GetType().Name}, not MP_Strategy.");
                    return;
                }

                // Guard: only fire while we're still in pre-round INIT and a vs-mode is set.
                var state = mp.MissionState;
                if (state == GameModeExt.EMissionState.STARTED)
                {
                    _fakeTeamJoinFired = true;
                    float t0 = Time.time - _sceneLoadedAt;
                    MelonLogger.Msg($"[RTSA/HT] FakeTeamJoin: MissionState already STARTED at t+{t0:F1}s — natural transition beat us to it. Nothing to fake.");
                    return;
                }
                if (mp.TeamsVersus == GameModeExt.ETeamsVersus.NONE)
                {
                    // Not ready yet — retry until either vs-mode gets set or we give up.
                    // Cap retries with the same 10s soft timeout the other helpers use.
                    if (sinceArm >= 10f)
                    {
                        _fakeTeamJoinFired = true;
                        MelonLogger.Warning($"[RTSA/HT] FakeTeamJoin: giving up — TeamsVersus still NONE after {sinceArm:F1}s. SetTeamVersusMode likely failed upstream.");
                    }
                    return;
                }

                var setups = mp.TeamSetups;
                if (setups == null || setups.Count == 0)
                {
                    if (sinceArm >= 10f)
                    {
                        _fakeTeamJoinFired = true;
                        MelonLogger.Warning($"[RTSA/HT] FakeTeamJoin: giving up — TeamSetups still empty after {sinceArm:F1}s.");
                    }
                    return;
                }

                // Step 1 (Approach 3): AIManager.AddCommander + EnableCommander for every
                // active empty-commander team. This is the "each team has *a* commander,
                // AI is fine" path — the game's OnMissionStateChanged does this on the
                // natural STARTED transition, but on a 0-client server that transition
                // never fires, so we prime it here.
                var joinTargets = new List<StrategyTeamSetup>();
                int aiAssigned = 0, aiAlready = 0, aiFailed = 0, inactive = 0, playerHeld = 0;
                foreach (var setup in setups)
                {
                    if (setup == null) continue;
                    var t = setup.Team;
                    if (t == null) continue;

                    bool active;
                    try { active = mp.GetTeamSetupActive(setup); }
                    catch { active = false; }
                    if (!active) { inactive++; continue; }

                    // Track the first active team as the fake-join target — we don't
                    // stomp a team that already has a real player-commander.
                    if (setup.Commander == null)
                        joinTargets.Add(setup);
                    else
                        playerHeld++;

                    try
                    {
                        var existing = AIManager.GetCommander(t, ignoreDisabled: true);
                        if (existing == null)
                        {
                            var settings = setup.AICommanderSettings ?? t.DefaultAICommanderSettings;
                            if (settings != null)
                                AIManager.AddCommander(t, settings);
                        }
                        if (!AIManager.IsCommanderEnabled(t))
                        {
                            AIManager.EnableCommander(t, true);
                            aiAssigned++;
                        }
                        else aiAlready++;
                    }
                    catch (Exception ex)
                    {
                        aiFailed++;
                        MelonLogger.Warning($"[RTSA/HT] FakeTeamJoin: AI bring-up for team={SafeTeamShortName(t)} threw: {ex.Message}");
                    }
                }

                // Step 2 (Approach 1 + 2): synthesize one Player and push it onto the
                // first empty-commander active team. Fake-join into just one team is
                // enough because the round-start gate only needs to see "someone picked
                // a team" per the ground truth, not "every team has a picker".
                string joinedTeamName = "-";
                bool sentSelect = false;
                bool calledOnJoined = false;
                bool setCommanderOk = false;
                if (joinTargets.Count > 0)
                {
                    var target = joinTargets[0];
                    var targetTeam = target.Team;

                    // Bogus but stable "NetworkID" — the ctor takes a ulong. Use a
                    // recognizable sentinel so the log clearly attributes any downstream
                    // behavior to us and not a real client.
                    // 0x515C_A_B07_ED = SilicaBotEd, a stable-but-obviously-fake sentinel
                    // Steam64 value. Real Steam64s live around 76561198e9; this sits
                    // outside that band so it can never collide with a genuine client.
                    const ulong BOGUS_ID = 0x515CAB07EDUL;
                    Player? synth = null;
                    try
                    {
                        var nid = new NetworkID(BOGUS_ID);
                        synth = Player.AddPlayer(nid, "RTSA_HeadlessBot");
                    }
                    catch (Exception ex)
                    {
                        MelonLogger.Warning($"[RTSA/HT] FakeTeamJoin: Player.AddPlayer(NetworkID, string) threw: {ex.Message}. Falling back to raw new Player().");
                    }
                    if (synth == null)
                    {
                        // Fallback path — bare Player without registration. Approaches 1 & 2
                        // may still work (setup.Commander is a plain Player-typed field,
                        // NetworkLayer.SendPlayerSelectTeam just takes a Player reference).
                        try
                        {
                            synth = new Player();
                            synth.PlayerName = "RTSA_HeadlessBot";
                        }
                        catch (Exception ex)
                        {
                            MelonLogger.Warning($"[RTSA/HT] FakeTeamJoin: new Player() also threw: {ex.Message}. Aborting fake-join.");
                        }
                    }

                    if (synth != null)
                    {
                        _fakeJoinPlayer = synth;
                        joinedTeamName = SafeTeamShortName(targetTeam);

                        try { synth.Team = targetTeam; } catch (Exception ex)
                        { MelonLogger.Warning($"[RTSA/HT] FakeTeamJoin: setting Player.Team threw: {ex.Message}"); }

                        // v0.7.39: the synthetic player must join AS A REGULAR INFANTRY
                        // (FPS side), NOT take the commander seat. Setting setup.Commander
                        // = synth (previously approach 1) makes the fake bot become the
                        // commander, which blocks AI takeover of that team. Skip it and
                        // only do the "select team" RPC — same as Si_BasicTeamBalance's
                        // player.Team = team + SendPlayerSelectTeam flow.
                        setCommanderOk = false;   // preserved for log symmetry

                        // Approach 2: fire the two calls Si_BasicTeamBalance's team-swap
                        // path does. OnPlayerJoinedBase raises the OnPlayerJoined event
                        // (m_Invoke_OnPlayerJoined is in the DLL) so any downstream
                        // subscriber that unblocks on player-count deltas gets fed.
                        try { gm.OnPlayerJoinedBase(synth); calledOnJoined = true; }
                        catch (Exception ex)
                        { MelonLogger.Warning($"[RTSA/HT] FakeTeamJoin: GameMode.OnPlayerJoinedBase(synth) threw: {ex.Message}"); }

                        try { NetworkLayer.SendPlayerSelectTeam(synth, targetTeam); sentSelect = true; }
                        catch (Exception ex)
                        { MelonLogger.Warning($"[RTSA/HT] FakeTeamJoin: NetworkLayer.SendPlayerSelectTeam threw: {ex.Message}"); }
                    }
                }

                _fakeTeamJoinFired = true;
                float elapsed = Time.time - _sceneLoadedAt;
                float dt = Time.time - _fakeTeamJoinArmedAt;
                MelonLogger.Msg(
                    $"[RTSA/HT] FakeTeamJoin fired at t+{elapsed:F1}s ({dt:F1}s post-vsMode). " +
                    $"MissionState={state} TeamsVersus={mp.TeamsVersus}. " +
                    $"AI bring-up: assigned={aiAssigned} already={aiAlready} failed={aiFailed} inactive={inactive} player-held={playerHeld}. " +
                    $"Fake-join into '{joinedTeamName}': setCommander={setCommanderOk} onJoinedBase={calledOnJoined} sendSelectTeam={sentSelect} " +
                    $"synth-player={(_fakeJoinPlayer != null ? "created" : "MISSING")}. " +
                    $"Watch server log for MissionState transition to STARTED; if it doesn't happen the fake-join didn't satisfy the round-start gate.");
            }
            catch (Exception ex)
            {
                // Non-terminal — retry next tick unless we've blown the soft-timeout.
                var real = (ex as TargetInvocationException)?.InnerException ?? ex;
                MelonLogger.Warning($"[RTSA/HT] FakeTeamJoin threw (will retry unless timed out): {real.Message}");
                float since = Time.time - _fakeTeamJoinArmedAt;
                if (since >= 10f)
                {
                    _fakeTeamJoinFired = true;
                    MelonLogger.Warning($"[RTSA/HT] FakeTeamJoin: giving up after {since:F1}s of retries.");
                }
            }
        }

        // Observe-only progress logging for the natural auto-start flow. Reads
        // MP_Strategy state, logs transitions & the VoteTimer countdown, but
        // NEVER mutates state. If after AutoStartTimeoutSeconds we still haven't
        // seen STARTED, log a WARNING so we know the game isn't cooperating —
        // but still don't force. The v0.7.34 fallback code (SetMissionStateNoNotify
        // + reflection) is left in the file for reference but no longer wired.
        static void LogNaturalStartProgress()
        {
            try
            {
                var gm = GameMode.CurrentGameMode;
                if (gm == null) return;
                var mp = gm as MP_Strategy;
                if (mp == null) return;

                var state = mp.MissionState;

                // Publish round-active flag for other subsystems (planner,
                // relocator). Overnight we saw the planner keep firing
                // Node/BC placements for 30+ seconds AFTER MissionState=ENDED
                // and UnitBalance's watchdog logged "Round transition
                // stalled — countdown started (40s)". Almost certainly that
                // stall is what wedged the map rotation. Gate at the source.
                IsRoundActive = (state == GameModeExt.EMissionState.STARTED);

                if (!_haveLastLoggedMissionState || _lastLoggedMissionState != state)
                {
                    var vs = mp.TeamsVersus;
                    float elapsed = Time.time - _sceneLoadedAt;
                    MelonLogger.Msg($"[RTSA/HT] Observed MissionState={state} TeamsVersus={vs} at t+{elapsed:F1}s.");

                    // Re-arm auto-start machinery on ENDED→INIT transition WHEN
                    // the scene didn't actually reload (overnight bug: game
                    // reset to INIT on same TheMaw scene, our one-shot flag
                    // was still true, TryMinimalAutoStart never re-ran →
                    // stuck for 8 hours). If state moved back to INIT after
                    // being STARTED or ENDED, treat it like a fresh scene.
                    bool comingFromRoundEnd =
                        _haveLastLoggedMissionState &&
                        state == GameModeExt.EMissionState.INIT &&
                        (_lastLoggedMissionState == GameModeExt.EMissionState.STARTED ||
                         _lastLoggedMissionState == GameModeExt.EMissionState.ENDED);
                    if (comingFromRoundEnd)
                    {
                        MelonLogger.Msg("[RTSA/HT] Re-arm: MissionState fell back to INIT on same scene — resetting auto-start flags so TryMinimalAutoStart fires again.");
                        _autoStartFired            = false;
                        _forceEndRoundFired        = false;
                        _autoOverrideApplied       = false;
                        _forceAssignFired          = false;
                        _fakeTeamJoinFired         = false;
                        _fakeTeamJoinArmedAt       = 0f;
                        _autoStartVsModeSet        = false;
                        _autoStartVsModeSetAt      = 0f;
                        _sceneLoadedAt             = Time.time;   // reset elapsed timer for ForceEndRound
                        // Also reset LayerReplay's round timer + snapshot
                        // cache so telemetry's /state roundTime doesn't
                        // accumulate across the re-arm (user saw 1800s
                        // displayed when the actual new round had run 15min).
                        try { Perception.MapLayers.LayerReplay.OnNewRound(Perception.MapLayers.LayerReplay.CurrentMap); }
                        catch { }
                    }

                    _lastLoggedMissionState = state;
                    _haveLastLoggedMissionState = true;
                }

                // Log VoteTimer periodically — this is what databomb's code checks
                // (base.VoteTimer <= 0f triggers SetTeamVersusMode). Distinct from
                // GameMode.Timer which is a different countdown.
                if (Time.time >= _nextTimerLogAt)
                {
                    _nextTimerLogAt = Time.time + 5f;
                    float voteT = mp.VoteTimer;
                    float gmT = TryReadGameModeTimer(mp);
                    float elapsed = Time.time - _sceneLoadedAt;
                    string gmStr = float.IsNaN(gmT) ? "n/a" : $"{gmT:F1}s";
                    MelonLogger.Msg($"[RTSA/HT] Waiting — MissionState={state} TeamsVersus={mp.TeamsVersus} VoteTimer={voteT:F1}s GameMode.Timer={gmStr} at t+{elapsed:F1}s.");
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[RTSA/HT] LogNaturalStartProgress threw: {ex.Message}");
            }
        }

        static void ForceEndRound(float elapsed)
        {
            // Persist benchmark row(s) BEFORE calling EndRound — EndRound may tear
            // down team state before the game triggers whatever wind-down we'd
            // otherwise piggyback on. Cheap and idempotent-per-scene.
            try
            {
                string mapName = global::Si_RTS_AI.Perception.MapLayers.LayerReplay.CurrentMap ?? "?";
                string cfgId   = _configId?.Value ?? "-";
                global::Si_RTS_AI.Perception.EcoRateSampler.WriteBenchmarkLines(mapName, cfgId, elapsed);
            }
            catch (Exception ex) { MelonLogger.Warning($"[RTSA/HT] Benchmark write threw: {ex.Message}"); }

            try
            {
                var gm = GameMode.CurrentGameMode;
                if (gm == null)
                {
                    MelonLogger.Warning($"[RTSA/HT] ForceEndRound: GameMode.CurrentGameMode is null — nothing to end.");
                    return;
                }
                if (!gm.GameOngoing)
                {
                    MelonLogger.Msg($"[RTSA/HT] ForceEndRound: game not ongoing (already ended?) — skipping. elapsed={elapsed:F1}s");
                    return;
                }
                MelonLogger.Msg($"[RTSA/HT] ForceEndRound: elapsed={elapsed:F1}s — calling GameMode.CurrentGameMode.EndRound() on {gm.GetType().Name}.");
                gm.EndRound();
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[RTSA/HT] ForceEndRound threw: {ex}");
            }
        }

        // ================================================================
        // ForceAssignAICommanders — walk MP_Strategy.TeamSetups and make sure every
        // active team that lacks a player-commander has an AICommander registered
        // AND enabled in AIManager.
        //
        // Motivation (v0.7.32 findings):
        //   - v0.7.32 already flips MissionState INIT->STARTED, which SHOULD trigger the
        //     game's own EnableCommander loop inside OnMissionStateChanged (STARTED case:
        //     for each active setup, AIManager.EnableCommander(team, setup.Commander == null)).
        //   - But live logs show:
        //       * AIManager.Commanders has 3 entries (added by SpawnSideAITeams which
        //         SetTeamVersusMode invokes) — so AICommander instances DO exist.
        //       * Si_CommManagement still logs "Skipping clearing previous commanders.
        //         Nothing to do" — meaning GameModeExt.GetCommanderForTeam(team) returns
        //         null on every team, so from CommManagement's POV no team has a running
        //         commander.
        //       * Game slips back into a waiting/restart state ~3s after the flip.
        //   - Rather than depend on OnMissionStateChanged's loop firing correctly under
        //     our reflection-invoke path, we make the assignment explicit and idempotent
        //     BEFORE the state flip. That way even if OnMissionStateChanged's per-team
        //     iteration is stunted (e.g. because TeamSetups iteration threw silently),
        //     each empty-commander team already has EnableCommander(true) applied.
        //
        // API path (verified against SilicaCore.dll via Mono.Cecil + a probe.cs compile):
        //   - MP_Strategy.TeamSetups            : List<StrategyTeamSetup>  (public field)
        //   - GameModeExt.GetTeamSetupActive(s) : bool                     (public method,
        //                                                                   filters by vs-mode)
        //   - BaseTeamSetup.Team                : Team                     (public field)
        //   - BaseTeamSetup.Commander           : Player                   (public field)
        //   - BaseTeamSetup.AICommanderSettings : AICommanderSettings      (public field)
        //   - Team.DefaultAICommanderSettings   : AICommanderSettings      (public field,
        //                                                                   fallback when
        //                                                                   setup.AICommanderSettings
        //                                                                   is null)
        //   - AIManager.GetCommander(team,ignoreDisabled) : AICommander    (public static)
        //   - AIManager.IsCommanderEnabled(team)          : bool           (public static)
        //   - AIManager.AddCommander(team, settings)      : AICommander    (public static)
        //   - AIManager.EnableCommander(team, enable)     : void           (public static)
        //
        // Ship status: shipped based on API introspection (all public, no reflection).
        // NOT confirmed on the live server yet — no run of this build has occurred at ship
        // time. Deployment is the caller's responsibility.
        // ================================================================
        static void TryForceAssignAICommanders()
        {
            try
            {
                var gm = GameMode.CurrentGameMode;
                if (gm == null) return;   // retry next tick — scene still loading

                var mp = gm as MP_Strategy;
                if (mp == null)
                {
                    // Not applicable to this mode. Log once and stop trying.
                    _forceAssignFired = true;
                    MelonLogger.Msg($"[RTSA/HT] AssignAICommanders: skipping — CurrentGameMode is {gm.GetType().Name}, not MP_Strategy.");
                    return;
                }

                var setups = mp.TeamSetups;
                if (setups == null || setups.Count == 0)
                {
                    // Retry — SetTeamVersusMode may not have populated TeamSetups yet.
                    // Timeout below will bail us out if this never happens.
                    CheckForceAssignTimeout();
                    return;
                }

                int assigned = 0, alreadyEnabled = 0, hasPlayerCommander = 0, inactive = 0, failed = 0;
                var teamsAssigned = new List<string>();
                var teamsFailed   = new List<string>();
                var teamsRetry    = new List<string>();

                foreach (var setup in setups)
                {
                    if (setup == null) continue;
                    var team = setup.Team;
                    if (team == null) continue;

                    string teamName = SafeTeamShortName(team);

                    // Only teams that are actually in play for the current vs-mode.
                    bool active;
                    try { active = mp.GetTeamSetupActive(setup); }
                    catch (Exception ex)
                    {
                        // Treat as retry-eligible — GetTeamSetupActive can throw if TeamsVersus
                        // isn't set yet on the very first tick.
                        MelonLogger.Warning($"[RTSA/HT] AssignAICommanders: team={teamName} GetTeamSetupActive threw (will retry): {ex.Message}");
                        teamsRetry.Add(teamName);
                        continue;
                    }
                    if (!active) { inactive++; continue; }

                    // Don't override a real player-commander if one somehow exists.
                    if (setup.Commander != null) { hasPlayerCommander++; continue; }

                    try
                    {
                        // 1) Ensure an AICommander instance exists for this team.
                        var existing = AIManager.GetCommander(team, ignoreDisabled: true);
                        if (existing == null)
                        {
                            var settings = setup.AICommanderSettings ?? team.DefaultAICommanderSettings;
                            if (settings == null)
                            {
                                MelonLogger.Warning($"[RTSA/HT] AssignAICommanders: team={teamName} failed: no AICommanderSettings on setup or on team.DefaultAICommanderSettings.");
                                failed++;
                                teamsFailed.Add(teamName);
                                continue;
                            }
                            AIManager.AddCommander(team, settings);
                        }

                        // 2) Enable it. Idempotent — EnableCommander(team,true) is safe to
                        //    call when already enabled (it's what OnMissionStateChanged does
                        //    on the STARTED transition anyway).
                        if (!AIManager.IsCommanderEnabled(team))
                        {
                            AIManager.EnableCommander(team, true);
                            assigned++;
                            teamsAssigned.Add(teamName);
                        }
                        else
                        {
                            alreadyEnabled++;
                            teamsAssigned.Add(teamName + "(already)");
                        }
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        teamsFailed.Add(teamName);
                        MelonLogger.Warning($"[RTSA/HT] AssignAICommanders: team={teamName} failed: {ex.Message}");
                    }
                }

                // Retry semantics:
                //   - If any team is in the retry-eligible bucket (GetTeamSetupActive threw),
                //     leave _forceAssignFired = false and try again next tick until timeout.
                //   - Otherwise, we've made our best pass — declare done regardless of
                //     hard-failures (those aren't going to fix themselves; a log line is more
                //     useful than a spin loop).
                if (teamsRetry.Count > 0)
                {
                    CheckForceAssignTimeout();
                    return;
                }

                _forceAssignFired = true;
                float elapsed = Time.time - _sceneLoadedAt;
                MelonLogger.Msg(
                    $"[RTSA/HT] AssignAICommanders: assigned AI to {assigned} team(s) " +
                    $"({(teamsAssigned.Count > 0 ? string.Join(", ", teamsAssigned) : "-")}). " +
                    $"already-enabled={alreadyEnabled}, player-commander={hasPlayerCommander}, " +
                    $"inactive={inactive}, failed={failed}" +
                    (failed > 0 ? $" ({string.Join(", ", teamsFailed)})" : "") +
                    $" at t+{elapsed:F1}s.");
            }
            catch (Exception ex)
            {
                // Retry next tick — most likely a transient timing issue.
                MelonLogger.Warning($"[RTSA/HT] AssignAICommanders attempt threw (will retry): {ex.Message}");
                CheckForceAssignTimeout();
            }
        }

        static void CheckForceAssignTimeout()
        {
            float elapsed = Time.time - _sceneLoadedAt;
            if (elapsed >= FORCE_ASSIGN_TIMEOUT_S)
            {
                _forceAssignFired = true;
                MelonLogger.Warning(
                    $"[RTSA/HT] AssignAICommanders: giving up after {elapsed:F1}s (timeout {FORCE_ASSIGN_TIMEOUT_S}s). " +
                    $"TeamSetups never became assignable — falling through to AutoStart anyway.");
            }
        }

        static string SafeTeamShortName(Team team)
        {
            try
            {
                if (team == null) return "?";
                if (!string.IsNullOrEmpty(team.TeamShortName)) return team.TeamShortName;
                if (!string.IsNullOrEmpty(team.TeamName))      return team.TeamName;
            }
            catch { }
            return "?";
        }

        // ================================================================
        // AutoStartRound — v0.7.34 PASSIVE-WAIT flow.
        //
        // Ground truth from live modding experience: on a 0-client Silica dedicated
        // server, the correct headless-start sequence is:
        //   1. Call MP_Strategy.SetTeamVersusMode(HUMANS_VS_HUMANS_VS_ALIENS,
        //      preventSpawning:false) ONCE.
        //   2. Wait 15-30s for Silica's own pre-round countdown to wind down.
        //   3. MissionState transitions INIT->STARTED NATURALLY when the timer hits 0.
        //
        // v0.7.33 forced MissionState INIT->STARTED immediately after SetTeamVersusMode.
        // That aborted the game's own bootstrap: SpawnSideAI + TeamSetups population
        // are kicked off by SetTeamVersusMode but complete asynchronously, and the
        // premature notify made CommManagement see "no owning commander per team"
        // and restart the round ~3s later ("Game restart — map changed" cascade),
        // leaving a broken state with no AI thinking.
        //
        // v0.7.34 flow:
        //   1. Once MP_Strategy is available, call SetTeamVersusMode (once per scene).
        //   2. Every tick after that: poll MissionState + GameModeExt.Timer, log
        //      every state transition + a throttled Timer heartbeat so we can SEE
        //      the natural countdown ticking down.
        //   3. As soon as MissionState=STARTED is observed, mark done and log
        //      "natural transition succeeded".
        //   4. If HeadlessTest_AutoStartTimeoutSeconds elapse WITHOUT a natural
        //      STARTED transition, force-flip as a last-resort fallback with a
        //      BIG warning log so it's obvious in the server log which path we took.
        //
        // Reference: data-bomb Si_AutoTeamsSelect (MP_Strategy.Restart postfix ->
        // 2s System.Timers.Timer -> SetTeamVersusMode() with NO manual state flip)
        // and data-bomb Si_CommManagement (Process_RPC_TimerUpdate reads
        // GameModeExt.Timer as the pre-round countdown; resets to 26s when
        // applicants insufficient — confirms Timer is THE natural countdown).
        // ================================================================
        static void TryAutoStartRound()
        {
            try
            {
                var gm = GameMode.CurrentGameMode;
                if (gm == null) return;   // retry next tick — scene still loading

                // Only MP_Strategy has the INIT/STARTED state machine we drive.
                var mp = gm as MP_Strategy;
                if (mp == null)
                {
                    _autoStartFired = true;
                    MelonLogger.Msg($"[RTSA/HT] AutoStart: skipping — CurrentGameMode is {gm.GetType().Name}, not MP_Strategy. No auto-start support for this mode.");
                    return;
                }

                var currentState = mp.MissionState;
                LogStateTransitionIfChanged(currentState, mp);

                // Terminal: MissionState=STARTED means the round is running — natural or otherwise.
                if (currentState == GameModeExt.EMissionState.STARTED)
                {
                    _autoStartFired = true;
                    float t   = Time.time - _sceneLoadedAt;
                    float dt  = _autoStartVsModeSet ? (Time.time - _autoStartVsModeSetAt) : -1f;
                    string when = _autoStartVsModeSet
                        ? $"{dt:F1}s after SetTeamVersusMode"
                        : "before we ever set vs-mode (game bootstrapped it itself)";
                    MelonLogger.Msg(
                        $"[RTSA/HT] AutoStart: MissionState=STARTED reached NATURALLY at t+{t:F1}s ({when}). " +
                        $"No force-flip needed — the game's own pre-round countdown succeeded.");
                    return;
                }

                // Step 1: kick off the game's own bootstrap by pinning the vs-mode ONCE.
                if (!_autoStartVsModeSet)
                {
                    var vsMode = ReadVersusAutoSelectMode();
                    try
                    {
                        mp.SetTeamVersusMode(vsMode, preventSpawning: false);
                        _autoStartVsModeSet   = true;
                        _autoStartVsModeSetAt = Time.time;
                        int timeoutS = ClampTimeoutSeconds(_autoStartTimeoutSeconds?.Value ?? 45);
                        float t = Time.time - _sceneLoadedAt;
                        MelonLogger.Msg(
                            $"[RTSA/HT] AutoStart: SetTeamVersusMode({vsMode}, preventSpawning:false) invoked at t+{t:F1}s. " +
                            $"MissionState={currentState}, GameModeTimer={FormatTimer(TryReadGameModeTimer(mp))}. " +
                            $"Waiting up to {timeoutS}s for natural INIT->STARTED transition.");
                    }
                    catch (Exception ex)
                    {
                        // Retry next tick — SetTeamVersusMode can throw during scene-load edge windows.
                        MelonLogger.Warning($"[RTSA/HT] AutoStart: SetTeamVersusMode({vsMode}) threw (will retry): {ex.Message}");
                    }
                    return;
                }

                // Step 2: passive wait — log a heartbeat every ~2s so the countdown is visible.
                LogGameModeTimerIfDue(mp, currentState);

                // Step 3: last-resort fallback — only if the natural transition never happens.
                int timeout = ClampTimeoutSeconds(_autoStartTimeoutSeconds?.Value ?? 45);
                float sinceModeSet = Time.time - _autoStartVsModeSetAt;
                if (sinceModeSet >= timeout)
                {
                    MelonLogger.Warning(
                        $"[RTSA/HT] AutoStart: TIMEOUT after {sinceModeSet:F1}s (limit {timeout}s). " +
                        $"MissionState still {currentState}, GameModeTimer={FormatTimer(TryReadGameModeTimer(mp))}. " +
                        $"Natural countdown appears STUCK — falling back to force-flip INIT->STARTED as a last resort. " +
                        $"This may still fail (v0.7.33 findings) — investigate the natural path if it does.");
                    ForceFlipMissionStateFallback(mp);
                    _autoStartFired = true;
                }
            }
            catch (Exception ex)
            {
                // Retry next tick — most likely a transient timing issue.
                MelonLogger.Warning($"[RTSA/HT] AutoStart attempt failed (will retry): {ex.Message}");
            }
        }

        // ---- AutoStart helpers ----

        // Log EVERY MissionState transition (including the very first observation) so we can
        // trace the natural bootstrap sequence in server logs. Called every tick — throttled
        // by "only log if changed since last observation".
        static void LogStateTransitionIfChanged(GameModeExt.EMissionState currentState, MP_Strategy mp)
        {
            if (_haveLastLoggedMissionState && currentState == _lastLoggedMissionState) return;

            float t = Time.time - _sceneLoadedAt;
            if (!_haveLastLoggedMissionState)
            {
                MelonLogger.Msg(
                    $"[RTSA/HT] AutoStart: initial MissionState={currentState} at t+{t:F1}s. " +
                    $"GameModeTimer={FormatTimer(TryReadGameModeTimer(mp))}.");
            }
            else
            {
                MelonLogger.Msg(
                    $"[RTSA/HT] AutoStart: MissionState transition {_lastLoggedMissionState}->{currentState} at t+{t:F1}s. " +
                    $"GameModeTimer={FormatTimer(TryReadGameModeTimer(mp))}.");
            }
            _lastLoggedMissionState     = currentState;
            _haveLastLoggedMissionState = true;
        }

        // Every ~2 wall-clock seconds while we're waiting, log the current GameModeTimer value
        // so we can eyeball the countdown ticking down in the server log.
        static void LogGameModeTimerIfDue(MP_Strategy mp, GameModeExt.EMissionState currentState)
        {
            if (Time.time < _nextTimerLogAt) return;
            _nextTimerLogAt = Time.time + 2f;
            float t   = Time.time - _sceneLoadedAt;
            float dt  = Time.time - _autoStartVsModeSetAt;
            MelonLogger.Msg(
                $"[RTSA/HT] AutoStart: waiting… t+{t:F1}s (+{dt:F1}s post-SetVsMode) " +
                $"MissionState={currentState} GameModeTimer={FormatTimer(TryReadGameModeTimer(mp))}.");
        }

        // MP_Strategy inherits GameModeExt.Timer (float, public getter). Wrapped in
        // try/catch and a null-coerce because SetTimer/Timer are exposed via GameModeExt
        // but can throw if the underlying instance isn't ready.
        static float TryReadGameModeTimer(MP_Strategy mp)
        {
            try
            {
                var ext = mp as GameModeExt;
                if (ext != null) return ext.Timer;
            }
            catch { }
            return float.NaN;
        }

        static string FormatTimer(float t) => float.IsNaN(t) ? "?" : t.ToString("F1") + "s";

        // Last-resort fallback — only invoked after HeadlessTest_AutoStartTimeoutSeconds elapse
        // without a natural STARTED transition. Same code path as v0.7.33's aggressive flip.
        // Kept for the rare case where the natural countdown genuinely doesn't fire — better a
        // half-broken round for observability than nothing.
        static void ForceFlipMissionStateFallback(MP_Strategy mp)
        {
            try
            {
                mp.SetMissionStateNoNotify(GameModeExt.EMissionState.STARTED);
                bool notified = InvokeOnMissionStateChanged(mp, GameModeExt.EMissionState.STARTED);
                MelonLogger.Warning(
                    $"[RTSA/HT] AutoStart: fallback force-flip: MissionState pinned STARTED, " +
                    $"OnMissionStateChanged notify={(notified ? "fired" : "SKIPPED-reflection-failed")}.");
            }
            catch (Exception ex)
            {
                var real = (ex as TargetInvocationException)?.InnerException ?? ex;
                MelonLogger.Warning($"[RTSA/HT] AutoStart: fallback force-flip threw: {real.Message}");
            }
        }

        // Read [Silica]/VersusAutoSelectMode from MelonPreferences. Falls back to the
        // 3-team default if the entry isn't registered (e.g. a mod that defines it
        // hasn't loaded yet).
        static GameModeExt.ETeamsVersus ReadVersusAutoSelectMode()
        {
            try
            {
                var silCat = MelonPreferences.GetCategory("Silica");
                if (silCat != null)
                {
                    var entry = silCat.GetEntry<string>("VersusAutoSelectMode");
                    if (entry != null && !string.IsNullOrEmpty(entry.Value))
                    {
                        if (Enum.TryParse<GameModeExt.ETeamsVersus>(entry.Value, ignoreCase: true, out var mode))
                            return mode;
                        MelonLogger.Warning($"[RTSA/HT] AutoStart: [Silica]/VersusAutoSelectMode='{entry.Value}' didn't parse to ETeamsVersus — falling back to HUMANS_VS_HUMANS_VS_ALIENS.");
                    }
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[RTSA/HT] AutoStart: reading [Silica]/VersusAutoSelectMode threw (using default): {ex.Message}");
            }
            return GameModeExt.ETeamsVersus.HUMANS_VS_HUMANS_VS_ALIENS;
        }

        // OnMissionStateChanged is declared protected on MP_Strategy — reach it via
        // reflection. AccessTools handles inherited nonpublic members too, so this
        // is robust even if the override moves up/down the hierarchy.
        static bool InvokeOnMissionStateChanged(MP_Strategy mp, GameModeExt.EMissionState newState)
        {
            try
            {
                var mi = AccessTools.Method(typeof(MP_Strategy), "OnMissionStateChanged",
                    new[] { typeof(GameModeExt.EMissionState) });
                if (mi == null)
                {
                    MelonLogger.Warning("[RTSA/HT] AutoStart: MP_Strategy.OnMissionStateChanged(EMissionState) not found via reflection.");
                    return false;
                }
                mi.Invoke(mp, new object[] { newState });
                return true;
            }
            catch (Exception ex)
            {
                // Unwrap TargetInvocationException so the real error surfaces.
                var real = (ex as TargetInvocationException)?.InnerException ?? ex;
                MelonLogger.Warning($"[RTSA/HT] AutoStart: OnMissionStateChanged invoke threw: {real.Message}");
                return false;
            }
        }

        // ================================================================
        // Harmony patch: pin NoPlayersTime at 0 so stock's 300s empty-server
        // auto-endround never fires. Patch is always installed; the prefix
        // gates on _cachedEnable + _cachedPreventEmptyEndround so it's a
        // near-free no-op when the harness is off.
        //
        // GameMode is in the global namespace; the setter is auto-property
        // protected, but Harmony targets by name regardless of visibility.
        // ================================================================
        [HarmonyPatch(typeof(GameMode), "set_NoPlayersTime")]
        static class Patch_GameMode_NoPlayersTime_Setter
        {
            static void Prefix(ref float value)
            {
                if (_cachedEnable && _cachedPreventEmptyEndround)
                    value = 0f;
            }
        }
    }
}
