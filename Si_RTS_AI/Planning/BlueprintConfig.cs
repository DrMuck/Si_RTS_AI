using MelonLoader;

namespace Si_RTS_AI.Planning
{
    /// <summary>
    /// Blueprint knobs. Category [Si_RTS_AI_Blueprint] in
    /// UserData/MelonPreferences.cfg.
    ///
    /// Deliberately small. The tree itself has no tuning surface at all — it
    /// pays live structure costs and live structure ranges, so there is nothing
    /// there to fit to one map. What IS a genuine preference is how hard the
    /// economy leans on producers versus migration, which is the strategic dial
    /// DrMuck asked to be able to turn ("the planner's weighting drives income
    /// over the game's progress and should be tunable"). That is the Cyst
    /// group below.
    /// </summary>
    internal static class BlueprintConfig
    {
        const string CAT = "Si_RTS_AI_Blueprint";

        static MelonPreferences_Category _cat;
        static MelonPreferences_Entry<bool>  _enabled;
        static MelonPreferences_Entry<float> _replanS;
        static MelonPreferences_Entry<int>   _maxSites;
        static MelonPreferences_Entry<int>   _maxCysts;
        static MelonPreferences_Entry<bool>  _cystAuto;
        static MelonPreferences_Entry<float> _staffedEnough;
        static MelonPreferences_Entry<float> _relocSpacings;
        static MelonPreferences_Entry<int>   _capHeadroomFrom;
        static MelonPreferences_Entry<bool>  _persist;
        static MelonPreferences_Entry<int>   _workers10;
        static MelonPreferences_Entry<int>   _workerCapPerBc;
        static MelonPreferences_Entry<int>   _producerPerSites;

        internal static void Init()
        {
            try
            {
                _cat = MelonPreferences.CreateCategory(CAT, "Si_RTS_AI Blueprint");

                _enabled = _cat.CreateEntry("BlueprintDrivesPhase2", true,
                    "Phase 2 expansion executes a planned network (scan -> plan -> build -> refresh) instead of each frontier point claiming its nearest patch every cycle. False falls back to natural branching (v0.14.x).");
                _replanS = _cat.CreateEntry("ReplanIntervalS", 30f,
                    "Seconds between replans. The plan is HELD in between — that is the point of it. Replanning reconciles against what has been built, it does not restart.");
                _maxSites = _cat.CreateEntry("MaxSitesPerPlan", 128,
                    "How many Bio Cache sites one plan covers. High on purpose — the point of a blueprint is that it covers the whole discovered map, so its total cost is a number you can look at. Planning all of NarakaCity's 107 patches takes a few ms. Execution is throttled by cash and in-flight sites, not by this.");

                _cystAuto = _cat.CreateEntry("CystStrategyAuto", true,
                    "Let the strategy sweep decide how many producers and how far ahead to build, by simulating each option against the current world over a 300s horizon. False uses MaxCystsPerPlan and a fixed 4 sites ahead instead.");
                _maxCysts = _cat.CreateEntry("MaxCystsPerPlan", 4,
                    "Producers one plan may ask for when CystStrategyAuto is false. LOWER = lean on migration and spend the cash on ground or military. HIGHER = grow shrimps at the frontier.");
                _staffedEnough = _cat.CreateEntry("CystStaffedEnough", 0.8f,
                    "Fraction of a site's capacity migration must already cover (shrimps present AND walking) before a Lesser Cyst there is judged redundant.");
                _relocSpacings = _cat.CreateEntry("CystRelocationSpacings", 3.5f,
                    "How many typical patch spacings a shrimp is expected to walk before a site counts as unreachable on foot and must grow its own. Follows the map (P90 patch spacing) rather than a fixed distance.");
                _capHeadroomFrom = _cat.CreateEntry("NoCystsFromShrimpCount", 180,
                    "Stop planning producers at this shrimp count. Against the unit cap a Lesser Cyst cannot produce anything — the observed failure was a row of them through the middle of the map while the shrimps were all east.");

                _workers10 = _cat.CreateEntry("WorkersByTenMinutes", 100,
                    "Reference worker count by the ten-minute mark — the trajectory the economy aims at, from 14 soak rounds where workers-at-10min correlated 0.80 with income-at-25min. Being BEHIND it raises producer count, suspends the sustainable-capacity ceiling and widens expansion; being on it changes nothing. Lower on maps whose ground genuinely cannot feed that many.");

                // ---- Experiment knobs (2026-08-05 A/B rig) --------------------
                //
                // Both default to the adaptive behaviour, so leaving them alone
                // changes nothing. They exist so an arm of the soak can hold one
                // variable still and sweep another.
                _workerCapPerBc = _cat.CreateEntry("WorkerCapPerBioCache", 18,
                    "Workers one Bio Cache may hold. H1 (deliberate under-saturation) lowers this so the freed unit cap goes to outer sites instead: the crowd curve pays 1.00 for the first six shrimps on a fresh patch and 0.70 for the thirteenth on a full one, so under-saturating pays whenever fresh ground is reachable.");
                _producerPerSites = _cat.CreateEntry("ProducerPerSites", 0,
                    "0 = adaptive (strategy sweep plus the worker trajectory decide). 1..N forces one Lesser Cyst per N planned sites — 2 means every other site gets a producer, 5 means one in five. The sweep arm for 'how many biotics to skip before the next gets a Cyst'. Placement stays worst-supplied-first, so the density changes and the outward bias does not.");

                _persist = _cat.CreateEntry("PersistPlans", true,
                    "Write each plan revision to UserData/RTSA/blueprint/<round>/ and serve the latest at http://localhost:<port>/blueprint for the layers viewer.");

                MelonLogger.Msg($"[RTSA/CONFIG] Blueprint prefs registered. " +
                                $"drivesPhase2={_enabled.Value} replan={_replanS.Value:F0}s " +
                                $"maxSites={_maxSites.Value} maxCysts={_maxCysts.Value} " +
                                $"persist={_persist.Value}");
            }
            catch (System.Exception ex) { MelonLogger.Warning("[RTSA/CONFIG] Blueprint init threw: " + ex.Message); }
        }

        internal static bool  Enabled               => _enabled == null || _enabled.Value;
        internal static float ReplanS               => _replanS != null ? _replanS.Value : 30f;
        internal static int   MaxSites              => _maxSites != null ? _maxSites.Value : 24;
        internal static int   MaxCystsPerPlan       => _maxCysts != null ? _maxCysts.Value : 4;
        internal static bool  CystStrategyAuto      => _cystAuto == null || _cystAuto.Value;
        internal static float StaffedEnough         => _staffedEnough != null ? _staffedEnough.Value : 0.8f;
        internal static float RelocationSpacings    => _relocSpacings != null ? _relocSpacings.Value : 3.5f;
        internal static int   ShrimpCapHeadroomFrom => _capHeadroomFrom != null ? _capHeadroomFrom.Value : 180;
        internal static bool  Persist               => _persist == null || _persist.Value;
        internal static int   WorkersByTenMinutes   => _workers10 != null ? _workers10.Value : 100;
        internal static int   WorkerCapPerBioCache  => _workerCapPerBc != null ? _workerCapPerBc.Value : 18;
        internal static int   ProducerPerSites      => _producerPerSites != null ? _producerPerSites.Value : 0;
    }
}
