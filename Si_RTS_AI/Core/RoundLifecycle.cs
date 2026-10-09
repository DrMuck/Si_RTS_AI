using MelonLoader;
using System;

namespace Si_RTS_AI.Core
{
    /// <summary>
    /// WHAT HAPPENS AT THE SEAM BETWEEN ROUNDS.
    ///
    /// EndRound writes every subsystem's summary into the round log that is
    /// about to close, then BeginRound resets every subsystem for the scene
    /// that just loaded. Both lists are long on purpose: each module owns its
    /// own reset, and this is the one place that says in which order they run.
    /// </summary>
    internal static class RoundLifecycle
    {
        /// <summary>Flush the finished round's summaries into its log. Safe to call with no round open.</summary>
        internal static void EndRound()
        {
            if (string.IsNullOrEmpty(RoundLog.Path)) return;

            Append(Perception.EventLog.BuildRoundSummary());
            // Taken while the round's lines are still in Latest.log.
            RoundLog.CopyMelonLog();

            Append(Suppression.Phase31_Production.BuildRoundSummaryFragment());
            Append(Faction.AlienConstruction.BuildRoundSummaryFragment());
            Append(Faction.AlienShrimpProducer.BuildRoundSummaryFragment());
            Append(Planning.ShrimpGroupPlanner.BuildRoundSummaryFragment());
            Append(Planning.ScoutPlanner.BuildRoundSummaryFragment());
            Append(Planning.EcoPlanner.BuildPlacementSummaryFragment());
            Append(Faction.AlienShrimpAntiAttack.BuildRoundSummaryFragment());
            Append(Human.HarvesterManager.Summary());
            Append(Faction.SuppressCombat.BuildRoundSummaryFragment());
            Append(Mil.ProductionV3.BuildRoundSummaryFragment());
            Append(Faction.AlienCommanderLock.BuildRoundSummaryFragment());
            Append(Perception.Utilisation.BuildRoundSummaryFragment());
            Append(Faction.HumanConstruction.BuildRoundSummaryFragment());
            Append(Faction.HumanTechResearcher.BuildRoundSummaryFragment());
            Append(Faction.HumanHarvesterController.BuildRoundSummaryFragment());
            Append(Perception.EcoRateSampler.BuildRoundSummaryFragment());
            RoundLog.Flush(force: true);
        }

        static void Append(string fragment)
        {
            if (!string.IsNullOrEmpty(fragment)) RoundLog.Append(fragment);
        }

        /// <summary>Reset every subsystem for a freshly loaded scene and open its log.</summary>
        internal static void BeginRound(string sceneName)
        {
            Perception.EventLog.ClearForNewRound();
            Suppression.Phase31_Production.ResetForNewRound();
            Faction.AlienConstruction.ResetForNewRound();
            Faction.AlienShrimpProducer.ResetForNewRound();
            Faction.AlienShrimpAntiAttack.ResetForNewRound();
            Faction.StorageBuffer.ResetForNewRound();
            Planning.ShrimpGroupPlanner.ResetForNewRound();
            Planning.ScoutPlanner.ResetForNewRound();
            Planning.MapProfile.ResetForNewRound();
            Planning.OpenerPlanner.ResetForNewRound();
            Faction.AlienConstruction.ClearOrderedForNewRound();
            Perception.BuildTimeline.ResetForNewRound();
            Planning.GrowthModel.ResetForNewRound();
            Planning.NaturalBranching.ResetForNewRound();
            Planning.Blueprint.ResetForNewRound();
            Planning.ExpansionStrategy.ResetForNewRound();
            Planning.SupplyForecast.ResetForNewRound();
            Planning.WorkerPlan.ResetForNewRound();
            Perception.QueenStatus.ResetForNewRound();
            Mil.QueenKeeper.ResetForNewRound();
            Perception.Reach.ResetForNewRound();
            Planning.NodeManager.ResetForNewRound();
            Perception.ThreatMap.ResetForNewRound();
            Perception.ControlMap.ResetForNewRound();
            TeamTick.ResetForNewRound();
            Perception.BuildTimeline.ReportHookState();
            Faction.SuppressCombat.ResetForNewRound();
            Faction.HumanConstruction.ResetForNewRound();
            Faction.HumanBuild.ResetForNewRound();
            Human.HarvesterManager.ResetForNewRound();
            Perception.CommanderLog.ResetForNewRound();
            Faction.HumanTechResearcher.ResetForNewRound();
            Faction.HumanHarvesterController.ResetForNewRound();
            Perception.EcoRateSampler.ResetForNewRound();
            Perception.FpsSampler.ResetForNewRound();
            Perception.TimeScaleControl.ResetForNewRound();
            Perception.MapLayers.GridWorld.ConfigureFromMap(sceneName);
            Perception.MapLayers.AlienEcoLayers.OnRoundReset();
            Perception.MapLayers.HumanEcoLayers.OnRoundReset();
            Perception.MapLayers.FoWLayers.OnRoundReset();
            Perception.BcMetrics.ResetForNewRound();
            Perception.BcIncome.ResetForNewRound();
            Perception.UnitCaps.ResetForNewRound();
            Perception.CombatLog.ResetForNewRound();
            Planning.MilitaryConfig.Reload();
            Planning.UnitPrior.Reload();
            Mil.Shadow.Configure();
            Mil.Shadow.ResetForNewRound();
            Mil.SpirePlanner.Configure();
            Mil.SpirePlanner.ResetForNewRound();
            Faction.AlienCommanderLock.Reload();
            Faction.AlienCommanderLock.ResetForNewRound();
            Planning.ArmyPlan.ResetForNewRound();
            Perception.Utilisation.ResetForNewRound();
            Mil.MilLog.ResetForNewRound();
            Mil.UnitStats.Reload();
            Mil.MilConfig.Reload();
            Mil.Intel.ResetForNewRound();
            Mil.Fields.ResetForNewRound();
            Perception.Ground.ResetForNewRound();
            Mil.Objectives.ResetForNewRound();
            Mil.Forces.ResetForNewRound();
            Mil.ProductionV3.ResetForNewRound();
            Perception.UnitValues.ResetForNewRound();
            Perception.ShrimpStateSampler.ResetForNewRound();
            Perception.GameConstantsDumper.ResetForNewRound();
            Planning.EcoPlanner.ResetForNewRound();
            Planning.EcoSimulator.ResetForNewRound();
            Planning.MoneyBroker.ResetForNewRound();
            Planning.TechPlanner.ResetForNewRound();
            Faction.SuppressHumanAI.ResetForNewRound();
            Faction.VanillaOrderGate.ResetForNewRound();
            // last: the reset + configured state above is the template every further team starts from
            Mil.MilContext.ResetForNewRound();
            Perception.MapLayers.LayerReplay.OnNewRound(sceneName);

            RoundLog.Begin(sceneName);
            // Silica clears every GameEvents delegate on scene transition, so
            // subs made during a previous scene are gone by now. Re-hook every scene.
            Perception.EventLog.Hook();
        }
    }
}
