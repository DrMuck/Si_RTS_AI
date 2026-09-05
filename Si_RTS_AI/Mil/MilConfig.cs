using Si_RTS_AI.Planning;

namespace Si_RTS_AI.Mil
{
    /// <summary>
    /// THE HANDFUL OF NUMBERS THE V3 LAYER STILL NEEDS, ALL FROM rtsai.json.
    ///
    /// MIL_V3_PLAN section 5 lists them. Three are fitted (the commit bands,
    /// which Doctrine reads from mil_doctrine.json); the rest are labelled
    /// placeholders with a physical meaning, so that a played round can move
    /// them and the next round tests the move. Nothing here is a score weight.
    ///
    /// The four switches — enabled / execute / produce / offence — stay on
    /// MilitaryConfig under the `military.*` keys so existing configs keep
    /// working. Everything v3-specific lives under `mil.*`.
    /// </summary>
    internal static class MilConfig
    {
        internal static bool Enabled  => MilitaryConfig.Enabled;
        internal static bool Execute  => MilitaryConfig.Execute;
        internal static bool Produce  => MilitaryConfig.Produce;
        internal static bool Offence  => MilitaryConfig.Offence;

        /// <summary>Cash worth of force kept at the Nest whatever the forecast
        /// says. About three Behemoths. PLACEHOLDER.</summary>
        internal static int   HomeFloorCash        = 12000;
        /// <summary>How far ahead an arrival is believed, seconds.</summary>
        internal static float ForecastHorizonS     = 180f;
        /// <summary>Raids may never hold more than this share of army value.</summary>
        internal static float RaidShare            = 0.25f;
        /// <summary>Longest a force waits at its rally for stragglers.</summary>
        internal static float StagingPatienceS     = 45f;
        /// <summary>Producers the forward base may hold.</summary>
        internal static int   FobProducers         = 3;
        /// <summary>Multiplier on a defence structure's cost when counted as
        /// force. PLACEHOLDER until combat.jsonl v2 carries the static flag.</summary>
        internal static float DefenceWeight        = 1.0f;
        /// <summary>Per-site threat memory half-life, seconds. A raided site keeps
        /// a screen for a while after the raid.</summary>
        internal static float ScreenMemoryHalfLifeS = 300f;
        /// <summary>Recon overflights: cheapest fast unit, one per stale base.</summary>
        internal static bool  ReconEnabled         = true;
        /// <summary>Allow the layer to plan (and with Execute, build) spires.
        /// Mirrors mil.spires.execute for the v3 site source.</summary>
        internal static float StandoffM            = 450f;
        /// <summary>Cash the military always leaves the economy, once the worker
        /// reserve no longer applies: three of the dearest eco action.</summary>
        internal static int   EcoFloorCash         = 4500;

        internal static void Reload()
        {
            HomeFloorCash          = RtsaiConfig.Int  ("mil.homeFloorCash",        RtsaiConfig.Int("military.homeFloorCash", 12000));
            ForecastHorizonS       = RtsaiConfig.Float("mil.forecastHorizonS",     180f);
            RaidShare              = RtsaiConfig.Float("mil.raidShare",            0.25f);
            StagingPatienceS       = RtsaiConfig.Float("mil.stagingPatienceS",     45f);
            FobProducers           = RtsaiConfig.Int  ("mil.fobProducers",         3);
            DefenceWeight          = RtsaiConfig.Float("mil.defenceWeight",        1.0f);
            ScreenMemoryHalfLifeS  = RtsaiConfig.Float("mil.screenMemoryHalfLifeS", 300f);
            ReconEnabled           = RtsaiConfig.Bool ("mil.recon",                true);
            StandoffM              = RtsaiConfig.Float("mil.standoffM",            450f);
            EcoFloorCash           = RtsaiConfig.Int  ("mil.ecoFloorCash",         4500);
            MilLog.Msg($"[MIL/V3] config: homeFloor={HomeFloorCash} horizon={ForecastHorizonS:F0}s " +
                       $"raidShare={RaidShare:F2} staging={StagingPatienceS:F0}s fob={FobProducers} " +
                       $"defW={DefenceWeight:F2} recon={ReconEnabled} standoff={StandoffM:F0}m | " +
                       $"bands refuse<{Doctrine.RefuseBelow:F2} commit>={Doctrine.CommitAt:F2} wasteful>{Doctrine.WastefulAbove:F2} k={Doctrine.K:F2}");
        }
    }
}
