using MelonLoader;
using Si_RTS_AI.Config;
using Si_RTS_AI.Core;
using System;
using UnityEngine;

[assembly: MelonInfo(typeof(Si_RTS_AI.Si_RTS_AI), "Si_RTS_AI", "0.94.0", "DrMuck")]
[assembly: MelonGame("Bohemia Interactive", "Silica")]

namespace Si_RTS_AI
{
    /// <summary>
    /// Si_RTS_AI — replacement / augmentation of Silica's built-in commander AI.
    ///
    /// This class is only the MelonLoader lifecycle and the master switch. The
    /// work lives in:
    ///   Config/      the json configuration (UserData/RTSAI/), switches, state
    ///   Core/        per-team tick, round lifecycle, round log, frame budget
    ///   Chat/        the /rtsai command and its numbered menu
    ///   Perception/  what the game is doing (events, maps, intel, samplers)
    ///   Planning/    the economy (opener, blueprint, beam planner, scouts)
    ///   Mil/         the military layer v3
    ///   Faction/     per-faction construction takeovers and order gates
    ///   TestHarness/ the headless soak driver (testMode)
    ///
    /// See README.md and docs/ARCHITECTURE.md.
    /// </summary>
    public class Si_RTS_AI : MelonMod
    {
        /// <summary>True between a gameplay scene load and the next scene change, while the mod is on.</summary>
        internal static bool SceneReady;
        internal static float _serverFps;   // read by Perception.TelemetryServer.WriteState
        static string _currentScene = "";

        public override void OnInitializeMelon()
        {
            try { System.IO.Directory.CreateDirectory(Paths.LogDir); } catch { }
            MelonLogger.Msg($"[RTSA] Si_RTS_AI v0.94.0 loaded. Config folder: {System.IO.Path.GetFullPath(Paths.Root)}; round logs: {System.IO.Path.GetFullPath(Paths.LogDir)}");

            // WHICH FACTIONS MAY PLAY, spelled the way the game spells it.
            // [Silica]/VersusAutoSelectMode is a vanilla preference whose legal
            // values are ETeamsVersus member names; a value that does not parse
            // falls back silently and the round starts with the wrong teams.
            try
            {
                MelonLogger.Msg("[RTSA] [Silica]/VersusAutoSelectMode accepts: " +
                                string.Join(", ", Enum.GetNames(typeof(GameModeExt.ETeamsVersus))) +
                                "  (currently '" +
                                (MelonPreferences.GetCategory("Silica")?
                                    .GetEntry<string>("VersusAutoSelectMode")?.Value ?? "unset") + "')");
            }
            catch (Exception ex) { MelonLogger.Warning("[RTSA] ETeamsVersus dump failed: " + ex.Message); }

            // Configuration first: everything below reads it.
            ConfigStore.Init();
            RtsaiConfig.Reload(force: true);
            ModSwitches.Refresh("startup");
            Planning.EcoPlannerConfig.Init();
            Planning.BlueprintConfig.Init();
            TestHarnessNs.TestHarness.Init();

            // Patches installed by hand (the attribute ones go in with PatchAll).
            // Each prefix/postfix checks ModSwitches.Enabled itself, so with the
            // mod off they cost a bool read and the game runs its own code.
            try { Perception.PerfProbes.Init(HarmonyInstance); }
            catch (Exception ex) { MelonLogger.Warning("[RTSA/PROBE] init failed: " + ex.Message); }
            try { Faction.SpawnablePrefabGuard.Install(HarmonyInstance); }
            catch (Exception ex) { MelonLogger.Warning("[PREFAB/GUARD] install threw: " + ex.Message); }
            try { Faction.StartingResourcesGuard.Install(HarmonyInstance); }
            catch (Exception ex) { MelonLogger.Warning("[CASH/GUARD] install failed: " + ex.Message); }
        }

        // Runs AFTER all mods have loaded — SilicaAdminMod is guaranteed available here.
        public override void OnLateInitializeMelon()
        {
            Chat.Commands.Register();
            // Register sub-planners with the money broker. Order doesn't
            // matter — broker sorts proposals by score × urgency each tick.
            Planning.MoneyBroker.Register(new Planning.TechPlanner());
        }

        public override void OnUpdate()
        {
            if (!ModSwitches.Enabled) return;

            RoundLog.Flush();
            // FPS EMA — UNSCALED, because fps and stalls are real-time questions:
            // Time.deltaTime is multiplied by timeScale.
            float dt = Time.unscaledDeltaTime;
            Perception.PerfProbes.OnFrame();
            if (dt > 0.0001f)
            {
                float instFps = 1f / dt;
                _serverFps = _serverFps * 0.9f + instFps * 0.1f;
                Perception.FpsSampler.OnFrame(dt, _serverFps);
            }

            // Start the mod-wide round clock the frame the round actually begins.
            // Must run before anything that stamps a roundT this frame.
            Perception.MapLayers.LayerReplay.TickRoundClock();
            // Drain 1 deferred layer write per frame — spreads the snapshot burst.
            Perception.MapLayers.LayerReplay.DrainPending();

            Perception.TimeScaleControl.Tick();
            TestHarnessNs.TestHarness.Tick();

            long ts = System.Diagnostics.Stopwatch.GetTimestamp();
            TeamTick.Tick();
            // Observation only — no orders, no placements.
            Perception.CombatLog.Tick();
            RecentModWork.AddPeriodic((System.Diagnostics.Stopwatch.GetTimestamp() - ts) * 1000L / System.Diagnostics.Stopwatch.Frequency);
            FrameBudget.NoteFrame(dt, ts);
        }

        public override void OnSceneWasLoaded(int buildIndex, string sceneName)
        {
            // Flush the previous round's summaries into its own log BEFORE we clear state.
            RoundLifecycle.EndRound();
            RoundLog.Close();
            Perception.EventLog.MarkUnhooked();   // Silica cleared the delegates with the scene
            SceneReady = false;
            _currentScene = sceneName ?? "";

            // Re-read the active config first, so everything reset below starts
            // the round on whatever the file says right now. This is the one
            // configuration path that does not need the server stopped.
            RtsaiConfig.Reload();
            ModSwitches.Refresh("map load");
            if (!ModSwitches.Enabled) return;

            RoundLifecycle.BeginRound(sceneName);
            SceneReady = true;

            // Headless test harness — must run AFTER Phase31 reset so its AutoOverride
            // (which populates Phase31.OverrideByTeam) sticks for this round.
            TestHarnessNs.TestHarness.OnSceneLoaded(sceneName);
        }

        /// <summary>
        /// THE MASTER SWITCH, APPLIED. Called by ModSwitches.Refresh whenever the
        /// resolved value changes (startup counts as a change). Off: let go of
        /// everything the mod holds, close the log, drop the event hooks, undo
        /// the time scale and fps cap. On mid-round: reset and start observing
        /// the round that is already running.
        /// </summary>
        internal static void ApplyEnabled(bool on, string reason)
        {
            if (on)
            {
                MelonLogger.Msg($"[RTSA] mod ON ({reason})");
                if (!SceneReady && !string.IsNullOrEmpty(_currentScene) && IsGameplayScene(_currentScene))
                {
                    RoundLifecycle.BeginRound(_currentScene);
                    SceneReady = true;
                    TestHarnessNs.TestHarness.OnSceneLoaded(_currentScene);
                }
                return;
            }

            MelonLogger.Msg($"[RTSA] mod OFF ({reason}) — vanilla commands every team; no logs, no patches act, no telemetry");
            try { Mil.Forces.ReleaseAll("mod switched off"); } catch { }
            try { Planning.ScoutPlanner.ResetForNewRound(); } catch { }
            RoundLifecycle.EndRound();
            RoundLog.Close();
            Perception.EventLog.Unhook();
            Perception.TimeScaleControl.Restore();
            Perception.TelemetryServer.Stop();
            SceneReady = false;
        }

        static bool IsGameplayScene(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            string ls = s.ToLowerInvariant();
            return !(ls.Contains("mainmenu") || ls.Contains("loading") || ls.Contains("intro") ||
                     ls.Contains("splash") || ls.Contains("startup") || ls.Contains("boot") || ls.Contains("dontdestroy"));
        }

        /// <summary>Kept for the ~80 call sites across the mod; writes to the round log.</summary>
        internal static void AppendToRound(string line) => RoundLog.Append(line);

        public override void OnApplicationQuit() { RoundLog.Close(); }
    }
}
