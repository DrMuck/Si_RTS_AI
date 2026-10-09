using MelonLoader;
using Si_RTS_AI.Config;

namespace Si_RTS_AI.Planning
{
    /// <summary>
    /// EcoPlanner knobs, from the active config under the "eco" section (plus
    /// the top-level ecoAssistWithHumanCommander switch). Every accessor reads
    /// the config directly, so an edit takes effect at the next map load — the
    /// file is re-read there — and a config selected from chat applies at once.
    ///
    /// Phase 2 Cyst placement — three knobs for DrMuck 2026-07-09 "possible to
    /// setup a json where I can choose between this approach and the previous
    /// approach?":
    ///   Approach A ("cluster", default): CoverageRadius=250, MinCluster=2,
    ///     TargetFarthest=true — fewer Cysts, placed at the cluster's outer
    ///     patch, shrimps serve outward first. Frees cash for military.
    ///   Approach B ("per_bc"): CoverageRadius=150, MinCluster=0,
    ///     TargetFarthest=false — one Cyst per BC (unless another is within
    ///     150m), placed at the BC. More aggressive, higher Cyst spend.
    /// </summary>
    internal static class EcoPlannerConfig
    {
        internal static void Init()
        {
            MelonLogger.Msg($"[RTSA/CONFIG] EcoPlanner: P2Cover={Phase2CystCoverageRadiusM:F0}m " +
                            $"P2MinCluster={Phase2CystMinClusterPatches} P2TargetFar={Phase2CystTargetFarthestInCluster} " +
                            $"P2MaxUncysted={Phase2MaxUncystedBcQueue} P1MinTapped={Phase1MinTappedPatches} " +
                            $"ecoAssistWithHumanCommander={EcoAssistWithHumanCommander}");
        }

        /// <summary>Phase 2 only. Existing Cyst within this many m of a BC → BC covered → no new Cyst candidate.</summary>
        public static float Phase2CystCoverageRadiusM         => RtsaiConfig.Float("eco.phase2CystCoverageRadiusM", 250f);
        /// <summary>Phase 2 only. BC needs at least this many patches within CoverageRadius to emit a Cyst candidate. 0 = disabled.</summary>
        public static int   Phase2CystMinClusterPatches       => RtsaiConfig.Int("eco.phase2CystMinClusterPatches", 2);
        /// <summary>Phase 2 only. True: Cyst target = farthest-from-Nest patch in the cluster. False: the BC position.</summary>
        public static bool  Phase2CystTargetFarthestInCluster => RtsaiConfig.Bool("eco.phase2CystTargetFarthestInCluster", true);
        /// <summary>Phase 2 only. In-flight uncysted BCs the beam tolerates before blocking new-BC enumeration.</summary>
        public static int   Phase2MaxUncystedBcQueue          => RtsaiConfig.Int("eco.phase2MaxUncystedBcQueue", 4);
        /// <summary>Phase 1 objective: until BCs serve this many DISTINCT biotics patches the planner widens
        /// Node reach and pays a [Node -> BC] bonus. Also gates multi-directional expansion. 0 disables.</summary>
        public static int   Phase1MinTappedPatches            => RtsaiConfig.Int("eco.phase1MinTappedPatches", 2);
        /// <summary>CO-OP ECO ASSIST. Normally the eco planner stands down the moment a real player takes
        /// the alien commander seat. True keeps the economy running for a human commander — Bio Caches,
        /// Cysts, Nodes, shrimp assignment — while they handle military.</summary>
        public static bool  EcoAssistWithHumanCommander       => RtsaiConfig.Bool("ecoAssistWithHumanCommander", false);
    }
}
