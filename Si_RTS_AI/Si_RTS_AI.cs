using MelonLoader;
using HarmonyLib;
using UnityEngine;
using Silica;
using Silica.AI;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

[assembly: MelonInfo(typeof(Si_RTS_AI.Si_RTS_AI), "Si_RTS_AI", "0.13.10-short-bridges-buildable", "DrMuck")]
[assembly: MelonGame("Bohemia Interactive", "Silica")]

namespace Si_RTS_AI
{
    /// <summary>
    /// Si_RTS_AI — replacement / augmentation of Silica's built-in commander AI.
    /// (Renamed from Si_RTS_AI. See ARCHITECTURE.md for the current design.)
    ///
    /// Phase 1 (current): OBSERVABILITY ONLY. Logs both AI commander decisions
    /// AND player RTS actions to help characterize what needs replacing. Two outputs:
    ///
    ///   1) Rolling per-tick summary (MelonLogger) — throttled, one per commander per
    ///      N Think() ticks:
    ///        [RTSA/OBS] [Sol] tick=25 reqs=8 type={Construct:3,Unit:5} task={Guard:4,...} ...
    ///
    ///   2) Per-round dump: UserData/RTSA/round-<yyyyMMdd_HHmmss>-<map>.log
    ///      One line per event, prefixed with a category tag:
    ///        [AI] [Sol] tick=25 reqs=8 ...
    ///        [ORDER] player=Dram team=Sol unit=Rifleman kind=attack tgt=Cent_Barracks@(-140,0,220)
    ///        [ORDER] ai team=Cent    unit=Sniper     kind=move   dst=(50,0,0)
    ///        [SPAWN_S] team=Sol  structure=Refinery
    ///        [SPAWN_U] team=Cent unit=Rifleman
    ///        [PILOT] player=Dram team=Sol unit=Heavy (was: Rifleman)
    ///        [TEAM] player=Dram Sol -> Alien
    ///
    /// See DESIGN.md for the full 7-phase plan.
    /// </summary>
    public class Si_RTS_AI : MelonMod
    {
        // ---- Config ----
        // Log a summary line every N Think() calls per team. Stock ThinkInterval is
        // ~3s, so at LOG_EVERY_N_TICKS = 5 that's one summary every ~15s per team.
        const int LOG_EVERY_N_TICKS = 5;

        // ---- State per team (AI observations) ----
        static readonly Dictionary<Team, TeamObservation> _obs = new Dictionary<Team, TeamObservation>();
        // ---- Per-round totals (player actions + spawns) ----
        static readonly Dictionary<Team, PlayerActionTally> _actions = new Dictionary<Team, PlayerActionTally>();
        static readonly Dictionary<Team, SpawnTally> _spawns = new Dictionary<Team, SpawnTally>();

        static string _sessionLogDir = "";
        static string _roundLogPath = "";
        static bool _sceneReady;
        static bool _eventsHooked;

        class TeamObservation
        {
            public string TeamName = "?";
            public int TickCount;
            public int TotalRequests;
            public Dictionary<EAIRequestType, int> ReqTypeCount = new Dictionary<EAIRequestType, int>();
            public Dictionary<EAITaskType, int>    TaskCount    = new Dictionary<EAITaskType, int>();
            public long PrioritySum;
            public int PriorityCount;
            public int MaxPriority = int.MinValue;
            public int MinPriority = int.MaxValue;
        }

        class PlayerActionTally
        {
            // Split by attribution: "player" = ControlledBy != null at order time,
            //                       "ai"     = ControlledBy null (AI / commander order)
            public int OrdersPlayer_Attack, OrdersPlayer_Move, OrdersPlayer_Stop;
            public int OrdersAI_Attack,     OrdersAI_Move,     OrdersAI_Stop;
            public int PlayerPilotChanges;   // times a player switched into a new unit
            public int TeamJoins;            // times a player joined this team
        }

        class SpawnTally
        {
            public int StructuresSpawned;
            public int UnitsSpawned;
            public Dictionary<string, int> StructureByName = new Dictionary<string, int>();
            public Dictionary<string, int> UnitByName      = new Dictionary<string, int>();
        }

        public override void OnInitializeMelon()
        {
            _sessionLogDir = Path.Combine("UserData", "RTSA");
            try { Directory.CreateDirectory(_sessionLogDir); } catch { }

            MelonLogger.Msg("[RTSA] Si_RTS_AI v0.4.0-phase32-aliennode loaded. Phase 3.1 (Sol unit composition) + Phase 3.2 (Alien Node injection toward biotics). Both opt-in via /rtsai override <team> on.");
            MelonLogger.Msg($"[RTSA] Per-round dump dir: {Path.GetFullPath(_sessionLogDir)}");
            MelonLogger.Msg($"[RTSA] AI summary cadence: every {LOG_EVERY_N_TICKS} Think() ticks per team.");

            // Headless test harness — MelonPreferences-gated soak-test driver.
            TestHarnessNs.TestHarness.Init();
            Planning.EcoPlannerConfig.Init();
        }

        public override void OnUpdate()
        {
            // FPS EMA — sampled every main-thread frame. TelemetryServer
            // reads _serverFps from any thread; it's a plain float write so
            // torn reads produce visible garbage very rarely, and the chart
            // averages anyway.
            float dt = UnityEngine.Time.deltaTime;
            if (dt > 0.0001f)
            {
                float instFps = 1f / dt;
                _serverFps = _serverFps * 0.9f + instFps * 0.1f;
                Perception.FpsSampler.OnFrame(dt, _serverFps);
            }

            // Lag-spike detector — user 2026-07-08: "server fps has sometimes
            // lag spikes ... maybe too many orders at the same time?" Any
            // frame taking >LAG_SPIKE_MS gets logged with recent-mod-activity
            // breakdown so we can see if our mod was on the critical path.
            if (dt * 1000f >= LAG_SPIKE_MS)
            {
                var recent = RecentModWork.SnapshotAndReset(dt);
                MelonLogger.Msg($"[RTSA/LAG] spike dt={dt*1000f:F0}ms  {recent}");
            }

            // Drain 1 deferred layer write per frame — spreads the ~15-file
            // snapshot burst across many frames so it never appears as a spike.
            Perception.MapLayers.LayerReplay.DrainPending();

            TestHarnessNs.TestHarness.Tick();

            // Time the periodic dispatcher itself — attribute cost to mod
            // even outside per-team measurement.
            long tPeriodic = 0;
            long ts = System.Diagnostics.Stopwatch.GetTimestamp();
            PeriodicTelemetryTick();
            tPeriodic = (System.Diagnostics.Stopwatch.GetTimestamp() - ts) * 1000L / System.Diagnostics.Stopwatch.Frequency;
            RecentModWork.AddPeriodic(tPeriodic);
        }

        // Frame time above this counts as a lag spike.
        const float LAG_SPIKE_MS = 50f;   // ~20 FPS or worse

        internal static float _serverFps;   // read by Perception.TelemetryServer.WriteState

        /// <summary>
        /// Rolling accumulator of "work done by the mod since the last frame".
        /// Every hot path (broker tick, shrimp producer, relocator, planner
        /// fires, shrimp order issuance) reports its ms cost + item count via
        /// the Add* methods below. When a lag spike hits, SnapshotAndReset()
        /// dumps and clears the accumulator — attributing the spike to the
        /// mod paths that ran in the window before it.
        /// </summary>
        internal static class RecentModWork
        {
            public static long PeriodicMs, BrokerMs, ShrimpProducerMs, ShrimpRelocatorMs, AlienConstructMs, PlannerFireMs;
            public static long LayerMs, BcMetricsMs, ShrimpStateMs, EcoRateMs, PlanKickMs;
            public static int  ShrimpMoves, ShrimpQueues, PlannerFires;
            public static void AddPeriodic(long ms)      { PeriodicMs         += ms; }
            public static void AddLayer(long ms)         { LayerMs            += ms; }
            public static void AddBcMetrics(long ms)     { BcMetricsMs        += ms; }
            public static void AddShrimpState(long ms)   { ShrimpStateMs      += ms; }
            public static void AddEcoRate(long ms)       { EcoRateMs          += ms; }
            public static void AddPlanKick(long ms)      { PlanKickMs         += ms; }
            public static void AddBroker(long ms)        { BrokerMs           += ms; }
            public static void AddShrimpProducer(long ms, int queues) { ShrimpProducerMs += ms; ShrimpQueues += queues; }
            public static void AddShrimpRelocator(long ms, int moves) { ShrimpRelocatorMs += ms; ShrimpMoves += moves; }
            public static void AddAlienConstruct(long ms) { AlienConstructMs += ms; }
            public static void AddPlannerFires(long ms, int fires) { PlannerFireMs += ms; PlannerFires += fires; }
            public static string SnapshotAndReset(float dt)
            {
                var s = $"periodic={PeriodicMs}ms (layer={LayerMs} bc={BcMetricsMs} shrimpState={ShrimpStateMs} ecoRate={EcoRateMs} plan={PlanKickMs}) " +
                        $"broker={BrokerMs}ms " +
                        $"shrimpProd={ShrimpProducerMs}ms(q={ShrimpQueues}) " +
                        $"shrimpReloc={ShrimpRelocatorMs}ms(m={ShrimpMoves})";
                PeriodicMs = BrokerMs = ShrimpProducerMs = ShrimpRelocatorMs = AlienConstructMs = PlannerFireMs = 0;
                LayerMs = BcMetricsMs = ShrimpStateMs = EcoRateMs = PlanKickMs = 0;
                ShrimpMoves = ShrimpQueues = PlannerFires = 0;
                return s;
            }
        }

        // ---- Periodic telemetry dispatcher ----
        // Fires ~1x/sec regardless of whether any team has an AI commander.
        //
        // Rationale: LayerReplay.MaybeSnapshot, BcMetrics.TickX, and every other
        // observability call used to live INSIDE AIConstructionHandler.Think() —
        // which the game only ticks for AI-commanded teams. The moment a human
        // player took commander control, the game stopped ticking Think() for
        // that team → our Harmony prefix went silent → viewer froze on the last
        // snapshot. Player-commanded rounds were completely invisible to the
        // telemetry pipeline, which is precisely the rounds we most want to
        // observe (the human demonstrating "good eco" as a reference).
        //
        // Fix: drive telemetry from MelonMod.OnUpdate instead. Iterate every
        // team via MP_Strategy.TeamSetups (present regardless of who commands
        // that team) and dispatch by team name. LayerReplay/BcMetrics have
        // their own throttles (5s / 30s) so calling them at 1Hz is cheap and
        // doesn't duplicate frames when the AI path also runs.
        static float _nextTelemetryTickAt;
        static int   _telemetryTickCounter;
        const float TELEMETRY_INTERVAL_S = 1f;

        // Cost-tracking: if any subsystem call exceeds this many milliseconds
        // on the main thread, log it. Server FPS 240 = 4.17ms per frame, so
        // 50ms freezes the game for ~12 frames — very visible to the viewer
        // and to any human player.
        const long SLOW_TICK_LOG_THRESHOLD_MS = 50;
        static readonly System.Diagnostics.Stopwatch _sw = new System.Diagnostics.Stopwatch();
        static long TimedMs(System.Action a)
        {
            _sw.Restart(); try { a(); } finally { _sw.Stop(); }
            return _sw.ElapsedMilliseconds;
        }

        static void PeriodicTelemetryTick()
        {
            if (!_sceneReady) return;
            if (Time.time < _nextTelemetryTickAt) return;
            _nextTelemetryTickAt = Time.time + TELEMETRY_INTERVAL_S;
            _telemetryTickCounter++;

            try
            {
                var gm = GameMode.CurrentGameMode as MP_Strategy;
                if (gm == null) return;
                var setups = gm.TeamSetups;
                if (setups == null) return;

                for (int i = 0; i < setups.Count; i++)
                {
                    var setup = setups[i];
                    var team = setup?.Team;
                    if (team == null) continue;

                    string tn = team.name ?? "";
                    long tLayer = 0, tBcMetrics = 0, tShrimpState = 0, tEcoRate = 0, tPlan = 0;
                    if (tn.Contains("Alien"))
                    {
                        tLayer     = TimedMs(() => Perception.MapLayers.LayerReplay.MaybeSnapshot(_telemetryTickCounter, team));
                        tBcMetrics = TimedMs(() => Perception.BcMetrics.TickAlien(team));
                    }
                    else if (tn.Contains("Sol") || tn.Contains("Cent"))
                    {
                        tLayer     = TimedMs(() => Perception.MapLayers.LayerReplay.MaybeSnapshotHuman(_telemetryTickCounter, team));
                        tBcMetrics = TimedMs(() => Perception.BcMetrics.TickHuman(team));
                    }
                    tShrimpState = TimedMs(() => Perception.ShrimpStateSampler.Tick(team));
                    Perception.BuildTimeline.Tick(team);
                    if ((team.name ?? "").Contains("Alien")) Perception.QueenStatus.Evaluate(team);
                    Perception.ThreatMap.Observe(team);
                    Perception.ThreatMap.Tick(team);
                    Planning.NodeManager.Tick(team);
                    tEcoRate     = TimedMs(() => Perception.EcoRateSampler.Tick(team));
                    RecentModWork.AddLayer(tLayer);
                    RecentModWork.AddBcMetrics(tBcMetrics);
                    RecentModWork.AddShrimpState(tShrimpState);
                    RecentModWork.AddEcoRate(tEcoRate);
                    // MoneyBroker tick BEFORE EcoPlanner — gives TechPlanner
                    // first dibs on cash when it's ready. Otherwise Eco would
                    // spend money on the next BC/Cyst and starve tech placement,
                    // pushing the first Cortex a tick later per BC (compounds
                    // fast — user saw Cortex arriving ~5min mark when target
                    // per their commander replay is ~3min).
                    // Skip Alien-side planners when RTSAI_Alien=false —
                    // stock game AI runs for Alien instead. Telemetry
                    // samplers above keep running (observability only).
                    if (Faction.FactionControl.IsEnabled(team))
                    {
                        Planning.MoneyBroker.Tick(team);
                        tPlan    = TimedMs(() => Planning.EcoPlanner.MaybePlan(team));
                        // Basic military manager tick — Alien only. Guarded internally
                        // by MilitaryManager.Enabled (opt-in preference).
                        if ((team.name ?? "").Contains("Alien"))
                        {
                            Faction.MilitaryManager.Tick(team);
                            // Map discovery — starter Crabs sweep the star.
                            // Runs ahead of everything that reads the explored
                            // layer, because BC candidates are FoW-gated.
                            Planning.ScoutPlanner.Tick(team);
                            // Cyst steps react within ~1s of their BC finishing.
                            Planning.OpenerPlanner.TickFast(team);
                        }
                    }
                    RecentModWork.AddPlanKick(tPlan);

                    long total = tLayer + tBcMetrics + tShrimpState + tEcoRate + tPlan;
                    if (total >= SLOW_TICK_LOG_THRESHOLD_MS)
                    {
                        MelonLogger.Msg("[RTSA/PERF] slow tick team=" + tn +
                                        " total=" + total + "ms  layer=" + tLayer +
                                        " bcmetrics=" + tBcMetrics + " shrimpstate=" + tShrimpState +
                                        " ecorate=" + tEcoRate + " plan=" + tPlan);
                    }
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[RTSA] PeriodicTelemetryTick threw: {ex.Message}");
            }
        }

        // Runs AFTER all mods have loaded — SilicaAdminMod is guaranteed available here.
        // Same pattern KGT uses for its /koh and /buy command registration.
        public override void OnLateInitializeMelon()
        {
            Commands.Register();
            // Register sub-planners with the money broker. Order doesn't
            // matter — broker sorts proposals by score × urgency each tick.
            Planning.MoneyBroker.Register(new Planning.TechPlanner());
        }

        public override void OnSceneWasLoaded(int buildIndex, string sceneName)
        {
            // Flush the previous round's summary into its own log BEFORE we clear state.
            // We only bother when the previous log path exists — first scene load has none.
            if (!string.IsNullOrEmpty(_roundLogPath))
            {
                string summary = Phase2.BuildRoundSummary();
                if (!string.IsNullOrEmpty(summary))
                    AppendToRound(summary);

                string p31 = Suppression.Phase31_Production.BuildRoundSummaryFragment();
                if (!string.IsNullOrEmpty(p31))
                    AppendToRound(p31);

                string p32 = Faction.AlienConstruction.BuildRoundSummaryFragment();
                if (!string.IsNullOrEmpty(p32))
                    AppendToRound(p32);

                string p32s = Faction.AlienShrimpProducer.BuildRoundSummaryFragment();
                if (!string.IsNullOrEmpty(p32s))
                    AppendToRound(p32s);

                string sgp = Planning.ShrimpGroupPlanner.BuildRoundSummaryFragment();
                if (!string.IsNullOrEmpty(sgp))
                    AppendToRound(sgp);

                string scp = Planning.ScoutPlanner.BuildRoundSummaryFragment();
                if (!string.IsNullOrEmpty(scp))
                    AppendToRound(scp);

                string pls = Planning.EcoPlanner.BuildPlacementSummaryFragment();
                if (!string.IsNullOrEmpty(pls))
                    AppendToRound(pls);

                string sna = Faction.AlienShrimpAntiAttack.BuildRoundSummaryFragment();
                if (!string.IsNullOrEmpty(sna))
                    AppendToRound(sna);

                string sc = Faction.SuppressCombat.BuildRoundSummaryFragment();
                if (!string.IsNullOrEmpty(sc))
                    AppendToRound(sc);

                string hh = Faction.HumanConstruction.BuildRoundSummaryFragment();
                if (!string.IsNullOrEmpty(hh))
                    AppendToRound(hh);

                string htr = Faction.HumanTechResearcher.BuildRoundSummaryFragment();
                if (!string.IsNullOrEmpty(htr))
                    AppendToRound(htr);

                string hhc = Faction.HumanHarvesterController.BuildRoundSummaryFragment();
                if (!string.IsNullOrEmpty(hhc))
                    AppendToRound(hhc);

                string eco = Perception.EcoRateSampler.BuildRoundSummaryFragment();
                if (!string.IsNullOrEmpty(eco))
                    AppendToRound(eco);
            }

            _obs.Clear(); _actions.Clear(); _spawns.Clear();
            Phase2.ClearForNewRound();
            Suppression.Phase31_Production.ResetForNewRound();
            Faction.AlienConstruction.ResetForNewRound();
            Faction.AlienShrimpProducer.ResetForNewRound();
            Faction.AlienShrimpAntiAttack.ResetForNewRound();
            Planning.ShrimpGroupPlanner.ResetForNewRound();
            Planning.ScoutPlanner.ResetForNewRound();
            Planning.MapProfile.ResetForNewRound();
            Planning.OpenerPlanner.ResetForNewRound();
            Faction.AlienConstruction.ClearOrderedForNewRound();
            Perception.BuildTimeline.ResetForNewRound();
            Planning.GrowthModel.ResetForNewRound();
            Perception.QueenStatus.ResetForNewRound();
            Planning.NodeManager.ResetForNewRound();
            Perception.ThreatMap.ResetForNewRound();
            Perception.BuildTimeline.ReportHookState();
            Faction.SuppressCombat.ResetForNewRound();
            Faction.HumanConstruction.ResetForNewRound();
            Faction.HumanTechResearcher.ResetForNewRound();
            Faction.HumanHarvesterController.ResetForNewRound();
            Perception.EcoRateSampler.ResetForNewRound();
            Perception.FpsSampler.ResetForNewRound();
            Perception.MapLayers.GridWorld.ConfigureFromMap(sceneName);
            Perception.MapLayers.AlienEcoLayers.OnRoundReset();
            Perception.MapLayers.HumanEcoLayers.OnRoundReset();
            Perception.MapLayers.FoWLayers.OnRoundReset();
            Perception.BcMetrics.ResetForNewRound();
            Perception.ShrimpStateSampler.ResetForNewRound();
            Perception.GameConstantsDumper.ResetForNewRound();
            Planning.EcoPlanner.ResetForNewRound();
            Planning.EcoSimulator.ResetForNewRound();
            Planning.MoneyBroker.ResetForNewRound();
            Planning.TechPlanner.ResetForNewRound();
            Faction.SuppressHumanAI.ResetForNewRound();
            Faction.MilitaryManager.ResetForNewRound();
            Perception.MapLayers.LayerReplay.OnNewRound(sceneName);
            _sceneReady = true;

            _roundLogPath = Path.Combine(_sessionLogDir, $"round-{DateTime.Now:yyyyMMdd_HHmmss}-{Safe(sceneName)}.log");
            AppendToRound($"# Si_RTS_AI observability log — scene={sceneName} startedAt={DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            AppendToRound($"# categories: [AI] commander Think summary, [ORDER] unit order (attack/move/stop), [SPAWN_S] structure, [SPAWN_U] unit, [PILOT] player controlled-unit change, [TEAM] player team change");
            AppendToRound($"# Phase 2 additions: AIConstructionHandler + AIUnitHandler observability rolled into the round-summary block at the end of this file.");

            // Silica clears every GameEvents delegate on scene transition, so subs made
            // during a previous scene are gone by now. Re-hook every scene, not just once.
            _eventsHooked = false;
            HookGameEvents();
            MelonLogger.Msg($"[RTSA] New round → logging to {_roundLogPath}");

            // Headless test harness — must run AFTER Phase31 reset so its AutoOverride
            // (which populates Phase31.OverrideByTeam) sticks for this round.
            TestHarnessNs.TestHarness.OnSceneLoaded(sceneName);
        }

        // ==================== AI COMMANDER OBSERVABILITY ====================

        [HarmonyPatch(typeof(AICommander), nameof(AICommander.Think))]
        static class Patch_AICommander_Think
        {
            static void Postfix(AICommander __instance)
            {
                try { OnCommanderThought(__instance); } catch (Exception ex)
                {
                    MelonLogger.Warning($"[RTSA] AI-Postfix threw: {ex.Message}");
                }
            }
        }

        static void OnCommanderThought(AICommander cmd)
        {
            if (cmd == null || cmd.Team == null || !_sceneReady) return;

            if (!_obs.TryGetValue(cmd.Team, out var ob))
            {
                ob = new TeamObservation { TeamName = ResolveTeamName(cmd.Team) };
                _obs[cmd.Team] = ob;
            }

            ob.TickCount++;
            var reqs = cmd.Requests;
            if (reqs == null) return;

            var tickReqType = new Dictionary<EAIRequestType, int>();
            var tickTask    = new Dictionary<EAITaskType, int>();
            int tickPrioSum = 0, tickPrioCount = 0, tickPrioMax = int.MinValue, tickPrioMin = int.MaxValue;
            int n = reqs.Count;
            for (int i = 0; i < n; i++)
            {
                var r = reqs[i];
                if (r == null) continue;

                Bump(tickReqType, r.Type);
                Bump(ob.ReqTypeCount, r.Type);
                if (r.Type == EAIRequestType.Unit)
                {
                    Bump(tickTask, r.UnitTask);
                    Bump(ob.TaskCount, r.UnitTask);
                }

                tickPrioSum += r.Priority; tickPrioCount++;
                if (r.Priority > tickPrioMax) tickPrioMax = r.Priority;
                if (r.Priority < tickPrioMin) tickPrioMin = r.Priority;

                ob.PrioritySum += r.Priority; ob.PriorityCount++;
                if (r.Priority > ob.MaxPriority) ob.MaxPriority = r.Priority;
                if (r.Priority < ob.MinPriority) ob.MinPriority = r.Priority;
            }
            ob.TotalRequests += n;

            string line =
                $"[AI] [{ob.TeamName}] tick={ob.TickCount} reqs={n} " +
                $"type={{{FormatDict(tickReqType)}}} " +
                $"task={{{FormatDict(tickTask)}}} " +
                $"prio(min/avg/max)={(tickPrioCount > 0 ? tickPrioMin.ToString() : "-")}/" +
                $"{(tickPrioCount > 0 ? (tickPrioSum / (float)tickPrioCount).ToString("F1") : "-")}/" +
                $"{(tickPrioCount > 0 ? tickPrioMax.ToString() : "-")} " +
                $"groups={(cmd.Groups != null ? cmd.Groups.Count : 0)}";
            AppendToRound(line);

            if (ob.TickCount % LOG_EVERY_N_TICKS == 0)
                MelonLogger.Msg($"[RTSA/OBS] {line}");
        }

        // ==================== PLAYER ACTION OBSERVABILITY ====================

        static void HookGameEvents()
        {
            if (_eventsHooked) return;
            try
            {
                GameEvents.OnUnitReceivedAttackOrder -= OnUnitReceivedAttackOrder;
                GameEvents.OnUnitReceivedAttackOrder += OnUnitReceivedAttackOrder;
                GameEvents.OnUnitReceivedMoveOrder   -= OnUnitReceivedMoveOrder;
                GameEvents.OnUnitReceivedMoveOrder   += OnUnitReceivedMoveOrder;
                GameEvents.OnUnitReceivedStopOrder   -= OnUnitReceivedStopOrder;
                GameEvents.OnUnitReceivedStopOrder   += OnUnitReceivedStopOrder;

                GameEvents.OnPlayerSelectUnit        -= OnPlayerSelectUnit;
                GameEvents.OnPlayerSelectUnit        += OnPlayerSelectUnit;
                GameEvents.OnPlayerChangedTeam       -= OnPlayerChangedTeam;
                GameEvents.OnPlayerChangedTeam       += OnPlayerChangedTeam;
                GameEvents.OnPlayerChangedUnit       -= OnPlayerChangedUnit;
                GameEvents.OnPlayerChangedUnit       += OnPlayerChangedUnit;

                GameEvents.OnStructureSpawned        -= OnStructureSpawned;
                GameEvents.OnStructureSpawned        += OnStructureSpawned;
                GameEvents.OnUnitSpawned             -= OnUnitSpawned;
                GameEvents.OnUnitSpawned             += OnUnitSpawned;
                GameEvents.OnStructureDestroyed      -= OnStructureDestroyed;
                GameEvents.OnStructureDestroyed      += OnStructureDestroyed;
                GameEvents.OnUnitDestroyed           -= OnUnitDestroyed;
                GameEvents.OnUnitDestroyed           += OnUnitDestroyed;

                // Note: /rtsai chat command uses SilicaAdminMod's PlayerMethods.RegisterPlayerCommand
                // (see OnLateInitializeMelon below) — no GameEvents.OnChatMessage sub needed here.

                _eventsHooked = true;
                MelonLogger.Msg("[RTSA] GameEvents hooked (orders + selections + spawns).");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[RTSA] HookGameEvents failed: {ex.Message}");
            }
        }

        // GameEvents callbacks are cleared on scene transition — re-hook each new scene.
        // Attribution rules — a unit's order can come from three sources:
        //   1. PILOTED  — unit.ControlledBy != null → someone in first-person mode is
        //                 personally driving the unit, and the order came from them.
        //   2. COMMANDER-PLAYER — team has no AI commander enabled (a human is the
        //                 team's RTS commander) → the order was issued top-down by
        //                 the human commander clicking select + right-click.
        //   3. COMMANDER-AI — team has an AI commander enabled → the order was
        //                 emitted by the AI's Think() → group routing.
        // Note: we can't easily tell WHICH human commander issued a given order
        // (there's no "issuer" field on the event), only that it came from the
        // player-commander of that team.
        static string AttributeSource(Unit unit)
        {
            try
            {
                var actor = unit?.ControlledBy;
                if (actor != null) return $"piloted={SafeName(actor)}";

                var team = unit?.Team;
                if (team != null && !AIManager.IsCommanderEnabled(team))
                    return "commander=player";
            }
            catch { }
            return "commander=ai";
        }

        static void OnUnitReceivedAttackOrder(Unit unit, Target target)
        {
            if (unit == null) return;
            var team = unit.Team;
            GetActionTally(team);   // ensure legacy dict exists (kept for compat)
            string src = AttributeSource(unit);
            var p2 = Phase2.Get(team);
            if      (src.StartsWith("piloted"))    p2.OrdersPiloted_Attack++;
            else if (src == "commander=player")    p2.OrdersCommanderPlayer_Attack++;
            else                                    p2.OrdersCommanderAi_Attack++;

            string tgtDesc = "?";
            try { tgtDesc = target?.ToString() ?? "?"; } catch { }
            AppendToRound(
                $"[ORDER] {src} team={ResolveTeamName(team)} " +
                $"unit={UnitDisplay(unit)} kind=attack tgt={tgtDesc}");
        }

        static void OnUnitReceivedMoveOrder(Unit unit, Vector3 destination)
        {
            if (unit == null) return;
            var team = unit.Team;
            GetActionTally(team);
            string src = AttributeSource(unit);
            var p2 = Phase2.Get(team);
            if      (src.StartsWith("piloted"))    p2.OrdersPiloted_Move++;
            else if (src == "commander=player")    p2.OrdersCommanderPlayer_Move++;
            else                                    p2.OrdersCommanderAi_Move++;

            AppendToRound(
                $"[ORDER] {src} team={ResolveTeamName(team)} " +
                $"unit={UnitDisplay(unit)} kind=move " +
                $"dst=({destination.x:F0},{destination.y:F0},{destination.z:F0})");
        }

        static void OnUnitReceivedStopOrder(Unit unit)
        {
            if (unit == null) return;
            var team = unit.Team;
            GetActionTally(team);
            string src = AttributeSource(unit);
            var p2 = Phase2.Get(team);
            if      (src.StartsWith("piloted"))    p2.OrdersPiloted_Stop++;
            else if (src == "commander=player")    p2.OrdersCommanderPlayer_Stop++;
            else                                    p2.OrdersCommanderAi_Stop++;

            AppendToRound(
                $"[ORDER] {src} team={ResolveTeamName(team)} " +
                $"unit={UnitDisplay(unit)} kind=stop");
        }

        static void OnPlayerSelectUnit(Player player, Unit oldUnit, Unit newUnit)
        {
            if (player == null) return;
            AppendToRound(
                $"[SELECT] player={SafeName(player)} team={ResolveTeamName(player.Team)} " +
                $"unit={(newUnit != null ? UnitDisplay(newUnit) : "-")} " +
                $"(was: {(oldUnit != null ? UnitDisplay(oldUnit) : "-")})");
        }

        static void OnPlayerChangedUnit(Player player, Unit oldUnit, Unit newUnit)
        {
            if (player == null) return;
            GetActionTally(player.Team).PlayerPilotChanges++;
            AppendToRound(
                $"[PILOT] player={SafeName(player)} team={ResolveTeamName(player.Team)} " +
                $"unit={(newUnit != null ? UnitDisplay(newUnit) : "-")} " +
                $"(was: {(oldUnit != null ? UnitDisplay(oldUnit) : "-")})");
        }

        static void OnPlayerChangedTeam(Player player, Team oldTeam, Team newTeam)
        {
            if (player == null) return;
            if (newTeam != null) GetActionTally(newTeam).TeamJoins++;
            AppendToRound(
                $"[TEAM] player={SafeName(player)} " +
                $"{ResolveTeamName(oldTeam)} -> {ResolveTeamName(newTeam)}");
        }

        static void OnStructureSpawned(Structure structure)
        {
            if (structure == null || structure.ObjectInfo == null) return;
            var team = structure.Team;
            var tally = GetSpawnTally(team);
            tally.StructuresSpawned++;
            string name = structure.ObjectInfo.DisplayName ?? "?";
            Bump(tally.StructureByName, name);

            var p2 = Phase2.Get(team);
            p2.StructuresSpawned++;
            Bump(p2.StructureBuiltByName, name);

            // Layer invalidation — only trigger on layer-relevant structure types so we
            // don't rebuild after every random Alien creature spawn.
            if (string.Equals(name, "Bio Cache",           StringComparison.OrdinalIgnoreCase)) Perception.MapLayers.AlienEcoLayers.OnBcChanged();
            if (string.Equals(name, "Lesser Spawning Cyst", StringComparison.OrdinalIgnoreCase)) Perception.MapLayers.AlienEcoLayers.OnCystChanged();
            if (string.Equals(name, "Refinery",             StringComparison.OrdinalIgnoreCase)) Perception.MapLayers.HumanEcoLayers.OnRefineryChanged();
            if (string.Equals(name, "Headquarters",         StringComparison.OrdinalIgnoreCase)) Perception.MapLayers.HumanEcoLayers.OnHqChanged();

            Vector3 sp = Vector3.zero;
            try { sp = structure.transform.position; } catch { }
            AppendToRound($"[SPAWN_S] team={ResolveTeamName(team)} structure={name} at=({sp.x:F0},{sp.z:F0})");
            // Measure how far the game's placement search moved this from where
            // the planner asked for it — sets the chain-anchor margin.
            try { Planning.EcoPlanner.NoteStructureSpawned(team, name, sp); } catch { }
        }

        static void OnUnitSpawned(Unit unit)
        {
            if (unit == null || unit.ObjectInfo == null) return;
            // Filter out common noise: dropped drops / debris / soldiers spawning from
            // barracks fire this too, which is fine — that's real production data.
            var team = unit.Team;
            var tally = GetSpawnTally(team);
            tally.UnitsSpawned++;
            string name = unit.ObjectInfo.DisplayName ?? "?";
            Bump(tally.UnitByName, name);

            var p2 = Phase2.Get(team);
            p2.UnitsSpawned++;
            Bump(p2.UnitBuiltByName, name);

            AppendToRound($"[SPAWN_U] team={ResolveTeamName(team)} unit={name}");
        }

        // Loss tracking. Keeps [SPAWN_x] logs quiet (deaths are already in Silica's HL
        // log) but rolls counts into the round summary.
        static void OnStructureDestroyed(Structure structure, GameObject instigator)
        {
            if (structure == null || structure.ObjectInfo == null) return;
            var p2 = Phase2.Get(structure.Team);
            p2.StructuresLost++;
            string name = structure.ObjectInfo.DisplayName ?? "?";
            Bump(p2.StructureLostByName, name);

            if (string.Equals(name, "Bio Cache",           StringComparison.OrdinalIgnoreCase)) Perception.MapLayers.AlienEcoLayers.OnBcChanged();
            if (string.Equals(name, "Lesser Spawning Cyst", StringComparison.OrdinalIgnoreCase)) Perception.MapLayers.AlienEcoLayers.OnCystChanged();
            if (string.Equals(name, "Refinery",             StringComparison.OrdinalIgnoreCase)) Perception.MapLayers.HumanEcoLayers.OnRefineryChanged();
            if (string.Equals(name, "Headquarters",         StringComparison.OrdinalIgnoreCase)) Perception.MapLayers.HumanEcoLayers.OnHqChanged();
        }

        static void OnUnitDestroyed(Unit unit, GameObject instigator)
        {
            if (unit == null || unit.ObjectInfo == null) return;
            var p2 = Phase2.Get(unit.Team);
            p2.UnitsLost++;
            Bump(p2.UnitLostByName, unit.ObjectInfo.DisplayName ?? "?");
        }

        // ==================== HELPERS ====================

        static void Bump<TKey>(Dictionary<TKey, int> d, TKey k) where TKey : notnull
        {
            d[k] = d.TryGetValue(k, out int v) ? v + 1 : 1;
        }

        static string FormatDict<TKey>(Dictionary<TKey, int> d) where TKey : notnull
        {
            if (d.Count == 0) return "";
            var sb = new StringBuilder(); bool first = true;
            foreach (var kv in d)
            {
                if (!first) sb.Append(',');
                sb.Append(kv.Key).Append(':').Append(kv.Value);
                first = false;
            }
            return sb.ToString();
        }

        static string ResolveTeamName(Team team)
        {
            try { if (team != null && !string.IsNullOrEmpty(team.name)) return team.name; } catch { }
            return "Team?";
        }

        static string UnitDisplay(Unit u)
        {
            try { return u?.ObjectInfo?.DisplayName ?? "?"; } catch { return "?"; }
        }

        static string SafeName(Player p)
        {
            try { return p?.PlayerName ?? "?"; } catch { return "?"; }
        }

        static string Safe(string s)
        {
            if (string.IsNullOrEmpty(s)) return "unknown";
            var sb = new StringBuilder(s.Length);
            foreach (var c in s)
                sb.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_');
            return sb.ToString();
        }

        static PlayerActionTally GetActionTally(Team team)
        {
            if (team == null) return _unteamedActions;
            if (!_actions.TryGetValue(team, out var t))
            {
                t = new PlayerActionTally();
                _actions[team] = t;
            }
            return t;
        }

        static SpawnTally GetSpawnTally(Team team)
        {
            if (team == null) return _unteamedSpawns;
            if (!_spawns.TryGetValue(team, out var t))
            {
                t = new SpawnTally();
                _spawns[team] = t;
            }
            return t;
        }

        // BUCKET, NOT A KEY, FOR TEAM-LESS EVENTS.
        //
        // This was a "sentinel key" that was itself null — `static readonly Team
        // _placeholderTeam = null!` — so `if (team == null) team =
        // _placeholderTeam` assigned null to null and the very next line did
        // Dictionary.TryGetValue(null), which throws ArgumentNullException. The
        // `null!` is what hid it: the compiler was told not to warn about the
        // one thing that mattered.
        //
        // It fired on every structure or unit that spawns before its team is
        // assigned — the handlers run from Structure.Awake() — 27 times in the
        // 2026-08-01 server log.
        //
        // Counting still happens, into a plain bucket, so the events are not
        // dropped and no dictionary ever sees a null key.
        static readonly SpawnTally        _unteamedSpawns  = new SpawnTally();
        static readonly PlayerActionTally _unteamedActions = new PlayerActionTally();

        internal static void AppendToRound(string line)
        {
            if (string.IsNullOrEmpty(_roundLogPath)) return;
            try { File.AppendAllText(_roundLogPath, line + Environment.NewLine); }
            catch (Exception ex) { MelonLogger.Warning($"[RTSA] round-log write failed: {ex.Message}"); }
        }
    }
}
