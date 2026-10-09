using MelonLoader;
using Silica;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Si_RTS_AI.Core
{
    /// <summary>
    /// THE 1 HZ PER-TEAM DISPATCHER — perception, then planners, for every team.
    ///
    /// Driven from MelonMod.OnUpdate rather than from inside the game's
    /// AICommander.Think, which the game only ticks for AI-commanded teams: the
    /// moment a human took commander control, Think stopped for that team and
    /// every observability call inside it went silent. Iterating
    /// MP_Strategy.TeamSetups here covers player-commanded rounds too, which are
    /// precisely the rounds worth observing.
    ///
    /// Order inside a team matters and is commented at each step. Everything
    /// that issues orders or spends cash sits behind FactionControl.IsEnabled;
    /// the perception above it runs for any team the mod is set to perceive for.
    /// </summary>
    internal static class TeamTick
    {
        static float _nextTickAt;
        static int   _tickCounter;
        const float INTERVAL_S = 1f;

        // Cost-tracking: if one team's tick exceeds this many milliseconds on
        // the main thread, log it. Server FPS 240 = 4.17ms per frame, so 50ms
        // freezes the game for ~12 frames — very visible to a human player.
        const long SLOW_TICK_LOG_THRESHOLD_MS = 50;
        static readonly System.Diagnostics.Stopwatch _sw = new System.Diagnostics.Stopwatch();
        static long TimedMs(Action a)
        {
            _sw.Restart(); try { a(); } finally { _sw.Stop(); }
            return _sw.ElapsedMilliseconds;
        }

        /// <summary>Teams that have owned at least one structure, and those whose stand-down we logged.</summary>
        static readonly HashSet<int> _everBuilt = new HashSet<int>();
        static readonly HashSet<int> _standDownLogged = new HashSet<int>();

        internal static void ResetForNewRound()
        {
            _everBuilt.Clear(); _standDownLogged.Clear();
        }

        /// <summary>Live structures a team owns; 0 for a team that has been wiped out.</summary>
        static int StructureCount(Team team)
        {
            try { return team?.Structures?.Count ?? 0; } catch { return 0; }
        }

        internal static void Tick()
        {
            if (!Si_RTS_AI.SceneReady) return;
            if (Time.time < _nextTickAt) return;
            _nextTickAt = Time.time + INTERVAL_S;
            _tickCounter++;

            try
            {
                var gm = GameMode.CurrentGameMode as MP_Strategy;
                if (gm == null) return;

                // BEFORE the per-team work, because standing the planner down for
                // a seat we are about to take back would waste the tick.
                Faction.AlienCommanderLock.Tick(gm);
                Faction.VanillaOrderGate.Report();
                var setups = gm.TeamSetups;
                if (setups == null) return;

                for (int i = 0; i < setups.Count; i++)
                {
                    var setup = setups[i];
                    var team = setup?.Team;
                    if (team == null) continue;
                    TickTeam(team);
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[RTSA] TeamTick threw: {ex.Message}");
            }
        }

        static void TickTeam(Team team)
        {
            string tn = team.name ?? "";
            bool isAlien = tn.Contains("Alien");
            // ONE STATE PER TEAM: the military layer's statics are swapped to
            // this team's before anything below reads or writes them.
            Mil.MilContext.Use(team);
            long tLayer = 0, tBcMetrics = 0, tShrimpState = 0, tEcoRate = 0, tPlan = 0, tMil = 0;
            bool snapshots = Config.RtsaiConfig.Bool("layerSnapshots", true);
            if (isAlien)
            {
                if (snapshots) tLayer = TimedMs(() => Perception.MapLayers.LayerReplay.MaybeSnapshot(_tickCounter, team));
                tBcMetrics = TimedMs(() => Perception.BcMetrics.TickAlien(team));
            }
            else if (tn.Contains("Sol") || tn.Contains("Cent"))
            {
                if (snapshots) tLayer = TimedMs(() => Perception.MapLayers.LayerReplay.MaybeSnapshotHuman(_tickCounter, team));
                tBcMetrics = TimedMs(() => Perception.BcMetrics.TickHuman(team));
            }
            tShrimpState = TimedMs(() => Perception.ShrimpStateSampler.Tick(team));
            try { Perception.CommanderLog.SampleCash(team); } catch { }

            // THE PERCEIVER IS THE TEAM THAT HOLDS THE MILITARY LAYER. Intel,
            // fields and the threat map are per-team state (MilContext); every
            // enabled team perceives for itself, and the alien also perceives
            // when nobody is enabled (shadow/eco soaks).
            bool alienOn = Faction.FactionControl.AlienEnabled;
            bool humanOn = Faction.FactionControl.SolEnabled || Faction.FactionControl.CentauriEnabled;

            // A TEAM WITH NOTHING LEFT TO BUILD FROM IS DONE THINKING. RiftBasin,
            // 2026-09-08 23:00: the alien lost its last structure at t=881s and
            // the layer kept planning for eighteen more minutes. Once a team
            // that HAD structures has none, stand its planners down.
            int tid = team.GetInstanceID();
            if (StructureCount(team) > 0) _everBuilt.Add(tid);
            bool eliminated = _everBuilt.Contains(tid) && StructureCount(team) == 0;
            if (eliminated && _standDownLogged.Add(tid))
                MelonLogger.Msg($"[RTSA] {team.name} has no structures left — planners stood down for the rest of the round");

            bool enabled = Faction.FactionControl.IsEnabled(team);
            bool perceiver = !eliminated && (isAlien ? (alienOn || !humanOn)
                                                     : (Faction.Construction.IsHuman(team) && enabled));
            if (perceiver)
            {
                if (isAlien) try { Mil.Shadow.Tick(team); }
                catch (Exception ex) { MelonLogger.Warning("[MIL/SHADOW] threw: " + ex.Message); }
                if (!isAlien) try { Perception.BcIncome.Sample(team, "Refinery"); } catch { }
                // Perception for the military layer runs whatever the switches
                // say: tracks and walkability are data, and the rounds that
                // need them most are the ones with the decisions turned off.
                tMil += TimedMs(() => { try { Mil.Fields.BuildTick(); }
                                        catch (Exception ex) { MelonLogger.Warning("[MIL/FIELDS] threw: " + ex.Message); } });
                tMil += TimedMs(() => { try { Mil.Intel.Tick(team); }
                                        catch (Exception ex) { MelonLogger.Warning("[INTEL] threw: " + ex.Message); } });
                Mil.MilLog.Flush();
                // Static defence is the thing that keeps an eco-only round
                // alive, and those are exactly the rounds the military gate is
                // off for. It builds nothing unless mil.spires.execute is set.
                if (enabled) try { Mil.SpirePlanner.Tick(team); }
                catch (Exception ex) { MelonLogger.Warning("[MIL/SPIRE] threw: " + ex.Message); }
            }
            Perception.BuildTimeline.Tick(team);
            if (isAlien)
            {
                Perception.QueenStatus.Evaluate(team);
                if (enabled) try { Mil.QueenKeeper.Tick(team); }
                catch (Exception ex) { MelonLogger.Warning("[QUEEN] keeper threw: " + ex.Message); }
            }
            long tThreat = perceiver ? TimedMs(() => { Perception.ThreatMap.Observe(team);
                                                        Perception.ThreatMap.Tick(team); }) : 0;
            // OURS MEANS THIS TEAM'S: ControlMap is swapped per team by
            // MilContext, so every perceiver rebuilds its own.
            long tControl = perceiver ? TimedMs(() => Perception.ControlMap.Rebuild(team)) : 0;
            Planning.NodeManager.Tick(team);
            tEcoRate = TimedMs(() => Perception.EcoRateSampler.Tick(team));
            RecentModWork.AddLayer(tLayer);
            RecentModWork.AddBcMetrics(tBcMetrics);
            RecentModWork.AddShrimpState(tShrimpState);
            RecentModWork.AddEcoRate(tEcoRate);

            // MoneyBroker BEFORE EcoPlanner — gives TechPlanner first dibs on
            // cash when it is ready, else Eco spends on the next BC/Cyst and
            // starves tech placement.
            if (enabled && !eliminated)
            {
                Planning.MoneyBroker.Tick(team);
                tPlan = TimedMs(() => Planning.EcoPlanner.MaybePlan(team));
                if (isAlien)
                {
                    // SCOUTING IS ASKED FIRST: it used to run after the military
                    // layer, so battalions absorbed the whole free pool and
                    // ScoutPlanner then conscripted Crabs back out of it. Map
                    // discovery also has to run ahead of everything that reads
                    // the explored layer, because Bio Cache candidates are fog-gated.
                    Planning.ScoutPlanner.Tick(team);
                    // MILITARY V3, IN THE ORDER IT DECIDES: what is worth doing,
                    // which units do it, what to build next.
                    MilitaryTick(team, ref tMil);
                    // The instrument, last, so it grades the tick that just happened.
                    Perception.Utilisation.Tick(team);
                    // Cyst steps react within ~1s of their BC finishing.
                    Planning.OpenerPlanner.TickFast(team);
                }
                else if (Faction.Construction.IsHuman(team))
                {
                    // SOL AND CENTAURI UNDER OUR COMMAND share the military layer;
                    // their economy is HumanConstruction for now.
                    Planning.ScoutPlanner.Tick(team);
                    MilitaryTick(team, ref tMil);
                    try { Mil.SpirePlanner.Tick(team); }
                    catch (Exception ex) { MelonLogger.Warning("[MIL/SPIRE] threw: " + ex.Message); }
                    try { Human.HarvesterManager.Tick(team); }
                    catch (Exception ex) { MelonLogger.Warning("[HARV] threw: " + ex.Message); }
                    Perception.Utilisation.Tick(team);
                }
            }
            RecentModWork.AddPlanKick(tPlan);

            long total = tLayer + tBcMetrics + tShrimpState + tEcoRate + tPlan + tThreat + tControl + tMil;
            if (total >= SLOW_TICK_LOG_THRESHOLD_MS)
            {
                MelonLogger.Msg("[RTSA/PERF] slow tick team=" + tn +
                                " total=" + total + "ms  layer=" + tLayer +
                                " bcmetrics=" + tBcMetrics + " shrimpstate=" + tShrimpState +
                                " ecorate=" + tEcoRate + " plan=" + tPlan +
                                " threat=" + tThreat + " control=" + tControl + " mil=" + tMil);
            }
        }

        static void MilitaryTick(Team team, ref long tMil)
        {
            tMil += TimedMs(() => { try { Mil.Objectives.Tick(team); }
                                    catch (Exception ex) { MelonLogger.Warning("[OBJ] threw: " + ex.Message); } });
            tMil += TimedMs(() => { try { Mil.Forces.Tick(team); }
                                    catch (Exception ex) { MelonLogger.Warning("[FORCE] threw: " + ex.Message); } });
            tMil += TimedMs(() => { try { Mil.ProductionV3.Tick(team); }
                                    catch (Exception ex) { MelonLogger.Warning("[MIL/PROD] threw: " + ex.Message); } });
        }
    }

    /// <summary>
    /// OUR SHARE OF THE FRAME, ONCE A MINUTE, EVERY MINUTE — plus the lag-spike
    /// detector. DrMuck, 2026-09-05: "the serverfps drops, the longer the game
    /// takes." The slow-tick line only speaks when one tick passes 50 ms, so a
    /// mod that costs 30 ms every second is silent while the game itself sinks.
    /// The budget line is unconditional: how many milliseconds of main-thread
    /// time the whole mod took in the last minute against wall clock, next to
    /// the frame rate.
    /// </summary>
    internal static class FrameBudget
    {
        /// <summary>Frame time above this counts as a lag spike (~20 FPS or worse).</summary>
        const float LAG_SPIKE_MS = 50f;

        static double _oursMs, _wallMs, _worstMs;
        static int _frames, _gc0, _gc2;

        /// <summary>Called once per frame with the unscaled frame time and the
        /// timestamp the mod's own work started at this frame.</summary>
        internal static void NoteFrame(float dt, long startTs)
        {
            // Scene loads are seconds long by nature; only a gameplay scene's spikes mean anything.
            if (dt * 1000f >= LAG_SPIKE_MS && Si_RTS_AI.SceneReady)
            {
                var recent = RecentModWork.SnapshotAndReset(dt);
                MelonLogger.Msg($"[RTSA/LAG] spike dt={dt * 1000f:F0}ms  {recent}");
            }

            double ours = (System.Diagnostics.Stopwatch.GetTimestamp() - startTs) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            _oursMs += ours; _wallMs += dt * 1000.0; _frames++;
            if (ours > _worstMs) _worstMs = ours;
            if (_wallMs < 60000.0) return;
            float fps = _frames / (float)(_wallMs / 1000.0);
            int gc0 = GC.CollectionCount(0), gc2 = GC.CollectionCount(2);
            long heapMb = GC.GetTotalMemory(false) / 1000000L;
            int structures = 0, sites = 0, units = 0;
            try { structures = Structure.Structures.Count; } catch { }
            try { sites = ConstructionSite.ConstructionSites.Count; } catch { }
            try { units = Unit.Units.Count; } catch { }
            string line = $"[RTSA/PERF] budget: ours {_oursMs:F0} ms of {_wallMs:F0} ms wall " +
                          $"({100.0 * _oursMs / _wallMs:F1}%), worst frame {_worstMs:F0} ms, " +
                          $"fps {fps:F0}, frame {_wallMs / _frames:F1} ms | " +
                          Perception.PerfProbes.TakeMinute() +
                          $" | fixedDt {Time.fixedDeltaTime * 1000f:F0} ms, gc0 +{gc0 - _gc0} gc2 +{gc2 - _gc2}, " +
                          $"heap {heapMb} MB | units {units} structures {structures} sites {sites}";
            _gc0 = gc0; _gc2 = gc2;
            MelonLogger.Msg(line);
            RoundLog.Append(line);
            _oursMs = 0; _wallMs = 0; _worstMs = 0; _frames = 0;
        }
    }
}
