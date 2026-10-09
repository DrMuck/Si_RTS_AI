using MelonLoader;
using Si_RTS_AI.Config;

namespace Si_RTS_AI.Planning
{
    /// <summary>
    /// Blueprint knobs, from the active config. The round-to-round knobs are
    /// top-level keys (blueprintDrivesPhase2, replanIntervalS, maxCystsPerPlan,
    /// cystStrategyAuto, workersByTenMinutes, workerCapPerBioCache,
    /// producerPerSites — the names every existing config already uses); the
    /// ones nobody should change mid-soak sit under "blueprint".
    ///
    /// Deliberately small. The tree itself has no tuning surface at all — it
    /// pays live structure costs and live structure ranges, so there is nothing
    /// there to fit to one map. What IS a genuine preference is how hard the
    /// economy leans on producers versus migration, which is the strategic dial
    /// DrMuck asked to be able to turn.
    /// </summary>
    internal static class BlueprintConfig
    {
        internal static void Init()
        {
            MelonLogger.Msg($"[RTSA/CONFIG] Blueprint: drivesPhase2={Enabled} replan={ReplanS:F0}s " +
                            $"maxSites={MaxSites} maxCysts={MaxCystsPerPlan} auto={CystStrategyAuto} " +
                            $"workers10={WorkersByTenMinutes} cap/BC={WorkerCapPerBioCache} producerPerSites={ProducerPerSites} persist={Persist}");
        }

        /// <summary>Phase 2 expansion executes a planned network instead of each frontier point claiming its nearest patch. False = natural branching (v0.14.x).</summary>
        internal static bool  Enabled               => RtsaiConfig.Bool("blueprintDrivesPhase2", true);
        /// <summary>Seconds between replans. The plan is HELD in between.</summary>
        internal static float ReplanS               => RtsaiConfig.Float("replanIntervalS", 30f);
        /// <summary>Bio Cache sites one plan covers. High on purpose: a blueprint covers the whole discovered map.</summary>
        internal static int   MaxSites              => RtsaiConfig.Int("blueprint.maxSitesPerPlan", 128);
        /// <summary>Producers one plan may ask for when CystStrategyAuto is false.</summary>
        internal static int   MaxCystsPerPlan       => RtsaiConfig.Int("maxCystsPerPlan", 4);
        /// <summary>Let the strategy sweep decide producer count and look-ahead by simulating each option over 300s.</summary>
        internal static bool  CystStrategyAuto      => RtsaiConfig.Bool("cystStrategyAuto", true);
        /// <summary>Fraction of a site's capacity migration must already cover before a Lesser Cyst there is redundant.</summary>
        internal static float StaffedEnough         => RtsaiConfig.Float("blueprint.cystStaffedEnough", 0.8f);
        /// <summary>Typical patch spacings a shrimp is expected to walk before a site must grow its own.</summary>
        internal static float RelocationSpacings    => RtsaiConfig.Float("blueprint.cystRelocationSpacings", 3.5f);
        /// <summary>Stop planning producers at this shrimp count (unit cap headroom).</summary>
        internal static int   ShrimpCapHeadroomFrom => RtsaiConfig.Int("blueprint.noCystsFromShrimpCount", 180);
        /// <summary>Write each plan revision to UserData/RTSA/blueprint/&lt;round&gt;/ for the layers viewer.</summary>
        internal static bool  Persist               => RtsaiConfig.Bool("blueprint.persistPlans", true);
        /// <summary>Reference worker count by the ten-minute mark — the trajectory the economy aims at.</summary>
        internal static int   WorkersByTenMinutes   => RtsaiConfig.Int("workersByTenMinutes", 100);

        // The A/B rig owns these two while an arm is active — see RtsaiConfig.IntUnlessArm.
        /// <summary>Workers one Bio Cache may hold.</summary>
        internal static int   WorkerCapPerBioCache  => RtsaiConfig.IntUnlessArm("workerCapPerBioCache", 18);
        /// <summary>0 = adaptive. 1..N forces one Lesser Cyst per N planned sites.</summary>
        internal static int   ProducerPerSites      => RtsaiConfig.IntUnlessArm("producerPerSites", 0);
    }
}
