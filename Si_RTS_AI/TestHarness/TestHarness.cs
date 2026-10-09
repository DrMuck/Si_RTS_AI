using HarmonyLib;
using Si_RTS_AI.Config;
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
    /// All behaviour is gated on testMode in the active config (Config.ModSwitches);
    /// the harness keys live under "harness" there. Historical key names:
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
        // How long ForceAssignAICommanders will retry before giving up (per scene).
        // Keep this small — if TeamSetups aren't populated within 10s, something upstream
        // is wrong and we're better off logging the failure than looping forever.
        const float FORCE_ASSIGN_TIMEOUT_S = 10f;

        // Every setting comes from Config.ModSwitches (the active json config).
        static int _roundsOnThisMap;
        static string _currentMapName = "";
        static int _armIndex;

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
            RefreshCache();
            MelonLogger.Msg($"[RTSA/HT] TestHarness ready. testMode={_cachedEnable} AutoOverride={ModSwitches.AutoOverrideProduction} " +
                            $"PreventEmpty={_cachedPreventEmptyEndround} EndAfter={ModSwitches.EndRoundAfterMinutes}min AutoStart={ModSwitches.AutoStartRound} " +
                            $"AutoStartTimeout={ClampTimeoutSeconds(ModSwitches.AutoStartTimeoutSeconds)}s ForceAssignAI={ModSwitches.ForceAssignAICommanders} " +
                            $"FakeTeamJoin={ModSwitches.FakeTeamJoin}");
        }

        static void RefreshCache()
        {
            // ONE SWITCH SEPARATES A SOAK FROM A GAME YOU CAN PLAY.
            //
            // testMode gates the whole harness: auto-join, the forced round end,
            // map cycling, combat suppression, the broke-enemy hack. Leaving it
            // on while a human plays means the round dies mid-match and the
            // aliens never shoot back. The subsystem switches themselves are
            // pushed by ModSwitches.Refresh; this only caches the two the
            // Harmony prefix reads.
            _cachedEnable               = ModSwitches.Enabled && ModSwitches.TestMode;
            _cachedPreventEmptyEndround = ModSwitches.PreventEmptyEndround;
        }

        // ================================================================
        // Scene load — arm timers and (later) apply the auto-override.
        // ================================================================
        /// <summary>
        /// Apply the next arm of the A/B cycle, if one is configured.
        ///
        /// Preferences are only read at scene load and MelonLoader rewrites the
        /// file from memory on shutdown, so without this an experiment can only
        /// change configuration by restarting the server — which means somebody
        /// has to be awake for it. Arms rotate per round instead.
        /// </summary>
        /// <summary>The cycle used when none is configured. See ApplyNextArm.</summary>
        // The cycle in use when none is configured. Currently the ratio3 vs
        // ratio4 head-to-head DrMuck asked for on 2026-08-06 — the earlier
        // four-arm sweep put the optimum somewhere in this gap, so the pair is
        // worth more rounds than another spread of the whole range.
        const string DEFAULT_ARMS = "ratio3,ratio4";

        static void ApplyNextArm()
        {
            // rtsai.json first, so the cycle for a soak can be changed between
            // rounds with the server up — the preference form of this knob is
            // what cost 25 rounds on 2026-08-05.
            string spec = ModSwitches.ConfigCycle ?? "";

            // EMPTY IS NOT "NO EXPERIMENT" — IT IS THE DEFAULT CYCLE.
            //
            // It used to mean "do nothing", and doing nothing looks exactly like
            // doing something: 25 rounds ran on 2026-08-05 with an unset cycle,
            // every one tagged with a stale configId, and the run was only
            // discovered to be worthless when the arms were missing from the
            // analysis a day later. A rig whose failure mode is silence is worse
            // than no rig. Say "off" to mean off.
            if (string.IsNullOrWhiteSpace(spec)) spec = DEFAULT_ARMS;
            if (string.Equals(spec.Trim(), "off", StringComparison.OrdinalIgnoreCase))
            {
                Config.RtsaiConfig.ArmActive = false;
                Config.RtsaiConfig.ArmBridgeMode = null;
                MelonLogger.Msg("[RTSA/HT] A/B arms disabled (ConfigCycle=off) — " +
                                "rounds run on whatever the active config says.");
                return;
            }

            var arms = new List<string>();
            foreach (var part in spec.Split(','))
            {
                var t = part.Trim();
                if (t.Length > 0) arms.Add(t);
            }
            if (arms.Count == 0) return;

            string arm = arms[_armIndex % arms.Count];
            _armIndex++;

            // Every arm holds the worker cap at 10 (H1) except the explicit
            // control, so the sweep moves one variable: producers per site.
            int cap = 10, perSites = 0;
            // Bridge arms hold producer density at the incumbent ratio3 and move
            // only how a loop is priced, so the two experiments never share a
            // round. See RtsaiConfig.BridgeMode.
            Config.RtsaiConfig.BridgeMode? bridge = null;
            switch (arm.ToLowerInvariant())
            {
                case "adaptive": perSites = 0; break;
                case "ratio2":   perSites = 2; break;
                case "ratio3":   perSites = 3; break;
                case "ratio4":   perSites = 4; break;
                case "ratio5":   perSites = 5; break;
                // Same settings as ratio3, different tag. The anti-pile-up
                // manager (v0.29.0) changes the bot underneath, so rounds run
                // after it must not pool with last night's ratio3 in
                // benchmarks.jsonl — the data has to label itself rather than
                // rely on someone remembering a cutoff timestamp.
                case "ratio3ap": perSites = 3; break;
                case "cap18":    cap = 18; perSites = 0; break;
                case "bridgeloop":
                    perSites = 3; bridge = Config.RtsaiConfig.BridgeMode.Loop; break;
                case "bridgeshortcut":
                    perSites = 3; bridge = Config.RtsaiConfig.BridgeMode.Shortcut; break;
                case "bridgeoff":
                    perSites = 3; bridge = Config.RtsaiConfig.BridgeMode.Off; break;
                default:
                    MelonLogger.Warning($"[RTSA/HT] Unknown arm '{arm}' — leaving the config as it is.");
                    return;
            }

            try
            {
                Config.RtsaiConfig.ArmWorkerCapPerBioCache = cap;
                Config.RtsaiConfig.ArmProducerPerSites     = perSites;
                ModSwitches.ConfigId = arm;
                Config.RtsaiConfig.ArmBridgeMode = bridge;
                Config.RtsaiConfig.ArmActive = true;
                MelonLogger.Msg($"[RTSA/HT] A/B arm {_armIndex}: '{arm}' " +
                                $"(WorkerCapPerBioCache={cap} ProducerPerSites={perSites}" +
                                (bridge.HasValue ? $" bridgeMode={bridge.Value.ToString().ToLowerInvariant()}" : "") +
                                $") — benchmarks tagged configId='{arm}'");
            }
            catch (Exception ex)
            { MelonLogger.Warning("[RTSA/HT] arm apply threw: " + ex.Message); }
        }

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
            // The map that is loaded, which is the only reliable answer to
            // "the same map again" when the round ends.
            if (!string.Equals(_currentMapName, sceneName, StringComparison.OrdinalIgnoreCase))
            {
                _currentMapName  = sceneName;
                _roundsOnThisMap = 0;
            }
            Perception.ShrimpStateSampler.Enabled = ModSwitches.Enabled && ModSwitches.ShrimpStateSampler;
            ApplyNextArm();

            if (!_cachedEnable) return;
            int mins    = ClampMinutes(ModSwitches.EndRoundAfterMinutes);
            int autoTo  = ClampTimeoutSeconds(ModSwitches.AutoStartTimeoutSeconds);
            bool supHumanAI = global::Si_RTS_AI.Faction.SuppressHumanAI.Enabled;
            string cfgId   = (ModSwitches.ConfigId ?? "-");
            MelonLogger.Msg($"[RTSA/HT] Armed for scene '{sceneName}'. Force-end in {mins} min. AutoOverride={ModSwitches.AutoOverrideProduction}. PreventEmptyEndround={_cachedPreventEmptyEndround}. AutoStartRound={ModSwitches.AutoStartRound} (timeout {autoTo}s). ForceAssignAICommanders={ModSwitches.ForceAssignAICommanders}. FakeTeamJoin={ModSwitches.FakeTeamJoin}. SuppressHumanAI={supHumanAI} ConfigId='{cfgId}'");

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

        /// <summary>
        /// Load the next map in the rotation. Silent no-op unless
        /// HeadlessTest_AutoRotateMap is on.
        ///
        /// Uses GameLevelLoader.StartLoadLevelCoroutine(path, GameModeInfo,
        /// bool) via reflection, with the GameModeInfo taken from the mode
        /// currently running so the rotation cannot change game mode by
        /// accident. Reflection because the signature is not verified here — if
        /// it does not resolve, the round simply replays the same map, which is
        /// the behaviour we already have.
        /// </summary>
        static void TryRotateMap()
        {
            if (!ModSwitches.AutoRotateMap) return;
            try
            {
                var rot = ParseMapRotation();
                if (rot.Count < 2) return;

                string cur = Perception.MapLayers.GridWorld.CurrentMapName ?? "";
                int idx = rot.FindIndex(m => string.Equals(m, cur, StringComparison.OrdinalIgnoreCase));
                string next = rot[(idx < 0 ? 0 : idx + 1) % rot.Count];
                if (string.Equals(next, cur, StringComparison.OrdinalIgnoreCase)) return;

                var gm = GameMode.CurrentGameMode;
                if (gm == null) { MelonLogger.Warning("[RTSA/HT] rotate: no current GameMode."); return; }

                var infoField = gm.GetType().GetField("GameModeInfo");
                object info = infoField?.GetValue(gm);
                if (info == null) { MelonLogger.Warning("[RTSA/HT] rotate: no GameModeInfo on the current mode."); return; }

                var loaderType = typeof(GameLevelLoader);
                object loader = loaderType.GetProperty("Instance")?.GetValue(null, null);
                var start = loaderType.GetMethod("StartLoadLevelCoroutine");
                if (loader == null || start == null)
                { MelonLogger.Warning("[RTSA/HT] rotate: StartLoadLevelCoroutine not resolvable — staying on this map."); return; }

                MelonLogger.Msg($"[RTSA/HT] rotate: '{cur}' -> '{next}' ({idx + 1}/{rot.Count} in rotation).");
                start.Invoke(loader, new object[] { next, info, false });
            }
            catch (Exception ex)
            {
                MelonLogger.Warning("[RTSA/HT] rotate threw, staying on this map: " + ex.Message);
            }
        }

        static List<string> ParseMapRotation()
        {
            var raw = ModSwitches.MapRotation ?? "";
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
            if (!_autoStartFired && ModSwitches.AutoStartRound)
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
            if (!_fakeTeamJoinFired && ModSwitches.FakeTeamJoin && _autoStartFired)
                TryFakeTeamJoin();

            // (1) auto-override: try each tick until at least one commander exists, then
            // flip Phase31.OverrideByTeam=true for every AI-commanded team, once.
            if (!_autoOverrideApplied && ModSwitches.AutoOverrideProduction)
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
                int mins = ClampMinutes(ModSwitches.EndRoundAfterMinutes);
                float thresholdSeconds = mins * 60f;
                float elapsed = Time.time - _sceneLoadedAt;
                if (elapsed >= thresholdSeconds)
                {
                    _forceEndRoundFired = true;
                    string tag = (ModSwitches.ConfigId ?? "");
                    if (string.IsNullOrWhiteSpace(tag) || tag.IndexOf("planner_active", StringComparison.OrdinalIgnoreCase) >= 0)
                        MelonLogger.Warning($"[RTSA/HT] round ends tagged configId='{tag}' — " +
                                            "that is NOT an experiment arm, so this round groups with nothing. " +
                                            "Check configCycle in the config.");
                    ForceEndRound(elapsed);
                    QueueNextMap();
                }
            }
        }

        /// <summary>
        /// Reload the map we are ON, and only move elsewhere after
        /// RoundsPerMap rounds.
        ///
        /// The first version took the map from position 0 of the rotation list,
        /// which sent a NarakaCity run to NorthPolarCap on its second round — the
        /// console command worked perfectly and went to the wrong place. The map
        /// currently loaded is the only thing that can answer "the same map
        /// again", so that is what it asks for now.
        ///
        /// Staying put is the point: an A/B run needs its arms on identical
        /// ground, and most maps randomise the alien spawn between rounds.
        /// </summary>
        static void QueueNextMap()
        {
            int perMap = ModSwitches.RoundsPerMap;
            if (perMap <= 0) return;                   // map commands disabled

            string next = _currentMapName;
            _roundsOnThisMap++;

            if (_roundsOnThisMap >= perMap)
            {
                _roundsOnThisMap = 0;
                var rotation = ParseMapRotation();
                if (rotation.Count > 0)
                {
                    // The map after the current one, so the rotation follows
                    // where we are rather than where a counter thinks we are.
                    int cur = rotation.FindIndex(m =>
                        string.Equals(m, _currentMapName, StringComparison.OrdinalIgnoreCase));
                    next = rotation[(cur + 1) % rotation.Count];
                }
                MelonLogger.Msg($"[RTSA/HT] {perMap} rounds played on '{_currentMapName}' — moving to '{next}'");
            }

            if (string.IsNullOrEmpty(next))
            {
                MelonLogger.Warning("[RTSA/HT] no current map name — cannot queue the next round.");
                return;
            }

            MelonLogger.Msg($"[RTSA/HT] queueing next round: map {next} mp_strategy " +
                            $"(round {_roundsOnThisMap + 1}/{perMap} on '{next}')");
            try { Perception.EcoRateSampler.TryRunConsoleCommand($"map {next} mp_strategy"); }
            catch (Exception ex) { MelonLogger.Warning("[RTSA/HT] map command threw: " + ex.Message); }
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

                        // ROTATE, RATHER THAN REPLAYING THE SAME MAP.
                        //
                        // A force-end restarts the round on the SAME scene, so a
                        // soak session stays on one map indefinitely — observed
                        // 2026-08-02, WhisperingPlains running back-to-back
                        // rounds from 20:25 onward while the rotation list named
                        // three maps. That defeats the reason the list exists,
                        // which is not overfitting the AI to one map, and we
                        // have been tuning against NarakaCity all day.
                        //
                        // Done HERE, at INIT, rather than inside ForceEndRound:
                        // loading a scene while the old one is being torn down
                        // is what produced an access violation earlier today.
                        TryRotateMap();
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
                string cfgId   = (ModSwitches.ConfigId ?? "-");
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
                        int timeoutS = ClampTimeoutSeconds(ModSwitches.AutoStartTimeoutSeconds);
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
                int timeout = ClampTimeoutSeconds(ModSwitches.AutoStartTimeoutSeconds);
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
            // THE ENTRY BELONGS TO ANOTHER MOD. [Silica]/VersusAutoSelectMode is
            // created by Si_AutoTeamsSelect; with that mod parked the category has
            // no such entry, the lookup above finds nothing, and every "2-way"
            // round of 2026-09-06 silently ran three teams. The value is still in
            // the file, so read the file.
            try
            {
                string cfg = System.IO.Path.Combine("UserData", "MelonPreferences.cfg");
                if (System.IO.File.Exists(cfg))
                {
                    var m = System.Text.RegularExpressions.Regex.Match(System.IO.File.ReadAllText(cfg),
                        @"(?m)^\s*VersusAutoSelectMode\s*=\s*""([A-Za-z0-9_]+)""");
                    if (m.Success && Enum.TryParse<GameModeExt.ETeamsVersus>(m.Groups[1].Value, ignoreCase: true, out var fromFile))
                    {
                        MelonLogger.Msg($"[RTSA/HT] AutoStart: VersusAutoSelectMode={fromFile} read from MelonPreferences.cfg (no mod owns the entry)");
                        return fromFile;
                    }
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[RTSA/HT] AutoStart: reading MelonPreferences.cfg threw (using default): {ex.Message}");
            }
            MelonLogger.Warning("[RTSA/HT] AutoStart: VersusAutoSelectMode unset everywhere; defaulting to HUMANS_VS_HUMANS_VS_ALIENS");
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
