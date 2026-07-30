using MelonLoader;

namespace Si_RTS_AI.Planning
{
    /// <summary>
    /// Runtime-tunable EcoPlanner parameters. Read by EcoPlanner each plan
    /// tick — edit UserData/MelonPreferences.cfg while the server runs and
    /// the changes take effect on the next plan tick (MelonPreferences
    /// auto-reloads).
    ///
    /// Category name in the .cfg: [Si_RTS_AI_EcoPlanner].
    /// </summary>
    internal static class EcoPlannerConfig
    {
        const string CAT = "Si_RTS_AI_EcoPlanner";

        static MelonPreferences_Category _cat;
        static MelonPreferences_Entry<float> _p2CoverRadius;
        static MelonPreferences_Entry<int>   _p2MinCluster;
        static MelonPreferences_Entry<bool>  _p2TargetFarthest;
        static MelonPreferences_Entry<int>   _p2MaxUncystedQueue;
        static MelonPreferences_Entry<int>   _p1MinTappedPatches;

        internal static void Init()
        {
            try
            {
                _cat = MelonPreferences.CreateCategory(CAT, "Si_RTS_AI Eco Planner");

                // Phase 2 Cyst placement — three knobs for user 2026-07-09
                // "possible to setup a json where I can choose between this
                // approach and the previous approach?"
                //
                // Approach A ("cluster", new — default):
                //   CoverageRadius=250, MinCluster=2, TargetFarthest=true
                //   → fewer Cysts, placed at cluster's outer patch, shrimps
                //     serve outward first. Frees cash for military.
                //
                // Approach B ("per_bc", previous):
                //   CoverageRadius=150, MinCluster=0, TargetFarthest=false
                //   → one Cyst per BC (unless another is within 150m), placed
                //     at BC position. More aggressive Phase 2 expansion but
                //     higher Cyst spend.
                // Defaults RESTORED to cluster approach 2026-07-09 after
                // confirming perf ripples are game-side (LAG log shows
                // periodic=0ms for all stalls) — mod isn't the cause.
                // Flip in the .cfg to try the previous approach:
                //   CoverageRadius=150, MinCluster=0, TargetFarthest=false
                _p2CoverRadius     = _cat.CreateEntry("Phase2CystCoverageRadiusM", 250f,
                    "Phase 2 only. Existing Cyst within this many m of a BC → BC covered → no new Cyst candidate. 250 = cluster approach; 150 = previous approach.");
                _p2MinCluster      = _cat.CreateEntry("Phase2CystMinClusterPatches", 2,
                    "Phase 2 only. BC needs at least this many patches within CoverageRadius to emit a Cyst candidate. 2 = cluster approach; 0 = disabled (previous approach).");
                _p2TargetFarthest  = _cat.CreateEntry("Phase2CystTargetFarthestInCluster", true,
                    "Phase 2 only. When true, Cyst target = farthest-from-Nest patch in the cluster (shrimps serve outward first). When false, Cyst target = BC position (previous approach).");
                _p2MaxUncystedQueue = _cat.CreateEntry("Phase2MaxUncystedBcQueue", 4,
                    "Phase 2 only. How many in-flight uncysted BCs the beam tolerates before blocking new-BC enumeration. Bump to 6 for more aggressive expansion.");

                _p1MinTappedPatches = _cat.CreateEntry("Phase1MinTappedPatches", 2,
                    "Phase 1 objective. Until BCs serve this many DISTINCT biotics patches, the planner widens Node reach to 6x chain and pays an explicit [Node -> BC] bonus so it expands toward the next patch instead of stalling. It ALSO gates multi-directional expansion, which is why 4 was too high: the opener already takes four sites, so breadth stayed switched off through the whole opening. Set 3 for a leaner opening, 0 to disable.");

                MelonLogger.Msg($"[RTSA/CONFIG] EcoPlanner prefs registered. " +
                                $"P2Cover={_p2CoverRadius.Value:F0}m " +
                                $"P2MinCluster={_p2MinCluster.Value} " +
                                $"P2TargetFar={_p2TargetFarthest.Value} " +
                                $"P2MaxUncysted={_p2MaxUncystedQueue.Value} " +
                                $"P1MinTapped={_p1MinTappedPatches.Value}");
            }
            catch (System.Exception ex) { MelonLogger.Warning("[RTSA/CONFIG] Init threw: " + ex.Message); }
        }

        public static float Phase2CystCoverageRadiusM        => _p2CoverRadius     != null ? _p2CoverRadius.Value     : 250f;
        public static int   Phase2CystMinClusterPatches      => _p2MinCluster      != null ? _p2MinCluster.Value      : 2;
        public static bool  Phase2CystTargetFarthestInCluster=> _p2TargetFarthest  != null ? _p2TargetFarthest.Value  : true;
        public static int   Phase2MaxUncystedBcQueue         => _p2MaxUncystedQueue != null ? _p2MaxUncystedQueue.Value: 4;
        public static int   Phase1MinTappedPatches          => _p1MinTappedPatches != null ? _p1MinTappedPatches.Value: 2;
    }
}
