using MelonLoader;

namespace Si_RTS_AI.Planning
{
    /// <summary>
    /// EVERY MILITARY KNOB, IN ONE PLACE, READ FROM rtsai.json.
    ///
    /// The military layer arrives with more unmeasured numbers than the economy
    /// ever had at one time, because the data that would settle them — exchange
    /// ratios per composition — cannot exist until something fights, and nothing
    /// fights during a soak. That is not a reason to invent precision in source.
    /// It is a reason to put every one of them where they can be changed between
    /// rounds without a rebuild, so a played game can move them and the next
    /// round tests the move.
    ///
    /// Read at every map load along with the rest of rtsai.json. The layer is OFF
    /// unless the file says otherwise: a mod that starts commanding an army
    /// because a DLL was replaced is not a mod anyone can soak with.
    ///
    /// PLACEHOLDERS ARE LABELLED. CashPerThreat and StrengthMargin price a
    /// mission in cash against a threat reading, and neither has been measured.
    /// They are the two numbers a played round should move first.
    /// </summary>
    internal static class MilitaryConfig
    {
        // ---- Switches --------------------------------------------------------

        /// <summary>Master switch. Off = the whole layer computes nothing.</summary>
        internal static bool Enabled;

        /// <summary>Issue orders. Off = the portfolio is planned and logged and
        /// no unit is touched — the shadow round every behavioural rule in this
        /// project should have had and several did not.</summary>
        internal static bool Execute;

        /// <summary>Queue combat units and place higher-tier producers.</summary>
        internal static bool Produce;

        /// <summary>Allow push missions. Defence runs without it, so the two can
        /// be brought up separately — which is the whole point of having it.</summary>
        internal static bool Offence;

        // ---- Pricing (placeholders — see the class comment) -------------------

        /// <summary>How much more than the ENEMY'S OWN CASH a force should be
        /// worth before it is sent at it. Above 1 because trading evenly on
        /// defence is a loss — we paid for that ground twice.
        ///
        /// This is now the only pricing number in the layer. There used to be a
        /// second, cashPerThreat, converting the threat field into cash; the
        /// threat field is an accumulator and the conversion was wrong by four
        /// orders of magnitude, so both sides of the comparison are now prices the
        /// game itself set and nothing has to be converted at all.
        ///
        /// Still a placeholder in the sense that 1.5 is a guess — but a guess
        /// about how much of an edge a defender needs, which is a question one
        /// played round can answer, rather than about the scale of a unit nobody
        /// can see.</summary>
        internal static float StrengthMargin;

        // ---- Allocation ------------------------------------------------------

        /// <summary>Least of the army that stays home, as a FRACTION of army
        /// value rather than a unit count. Self-scaling on purpose: four Crabs
        /// early and a real force later, without a constant that is wrong at one
        /// end of the round or the other.</summary>
        internal static float HomeShare;

        /// <summary>MOST of the army the garrison may hold, however much enemy
        /// turns up at home. The garrison is filled before anything else, so
        /// without a ceiling it is not a floor — it is a claim on everything, and
        /// on 2026-08-07 it took every unit for a whole round while nine Bio
        /// Caches were destroyed by forces no battalion was ever given units to
        /// meet.</summary>
        internal static float HomeCapShare;

        /// <summary>Defend missions funded at once. A cap, not a score — beyond
        /// a few simultaneous responses the army is being divided into pieces
        /// that lose separately.</summary>
        internal static int MaxDefendMissions;

        // ---- The money claim -------------------------------------------------

        /// <summary>Cash held for the economy while the economy can still convert
        /// it. Applies only while WorkerPlan is behind its trajectory AND yield
        /// is not falling — that is the measured condition under which another
        /// shrimp still earns. Otherwise the military may spend down to zero,
        /// because rounds sit on 100-200k unspent from minute twelve and an idle
        /// bank buys nothing.</summary>
        internal static int EcoReserve;

        /// <summary>Share of Lesser Spawning Cysts dedicated to combat units.
        /// Forced to zero while the economy is behind schedule and still
        /// converting — the same signal that governs the cash reserve, so the
        /// two cannot disagree.</summary>
        internal static float LesserCystShare;

        // ---- Push trigger ----------------------------------------------------

        /// <summary>Army value growth, in cash per second, below which holding
        /// stops paying. One signal for all three ceilings — unit cap,
        /// production throughput, economy — because we do not need to know which
        /// one binds, only that the army has stopped growing.</summary>
        internal static float PushGrowthFloor;

        /// <summary>Our army value over the enemy estimate before committing.</summary>
        internal static float PushMargin;

        /// <summary>Fraction of its committed peak value a push may fall to
        /// before it is called off. Not a timer, and not first contact.</summary>
        internal static float PushRetreatFraction;

        static bool _announced;

        /// <summary>Re-read at every map load, from RtsaiConfig which has already
        /// reloaded by then.</summary>
        internal static void Reload()
        {
            Enabled  = RtsaiConfig.Bool ("military.enabled",  false);
            Execute  = RtsaiConfig.Bool ("military.execute",  true);
            Produce  = RtsaiConfig.Bool ("military.produce",  true);
            Offence  = RtsaiConfig.Bool ("military.offence",  true);

            StrengthMargin  = RtsaiConfig.Float("military.strengthMargin", 1.5f);

            HomeShare         = RtsaiConfig.Float("military.homeShare",         0.25f);
            HomeCapShare      = RtsaiConfig.Float("military.homeCapShare",      0.5f);
            MaxDefendMissions = RtsaiConfig.Int  ("military.maxDefendMissions", 3);

            EcoReserve      = RtsaiConfig.Int  ("military.ecoReserve",      15000);
            LesserCystShare = RtsaiConfig.Float("military.lesserCystShare", 0.25f);

            PushGrowthFloor     = RtsaiConfig.Float("military.pushGrowthFloor",     20f);
            PushMargin          = RtsaiConfig.Float("military.pushMargin",          1.5f);
            PushRetreatFraction = RtsaiConfig.Float("military.pushRetreatFraction", 0.4f);

            // Say what the round is about to run under. A military layer that
            // silently switched itself on would be the worst kind of surprise
            // in a game somebody is playing.
            if (Enabled || _announced)
            {
                _announced = true;
                MelonLogger.Msg($"[MIL/CONFIG] enabled={Enabled} execute={Execute} produce={Produce} " +
                                $"offence={Offence} | margin={StrengthMargin:F2} " +
                                $"home={HomeShare:F2}..{HomeCapShare:F2} of army " +
                                $"ecoReserve={EcoReserve} lesserCystShare={LesserCystShare:F2} " +
                                $"push(growth<{PushGrowthFloor:F0}/s, margin {PushMargin:F2}, " +
                                $"retreat {PushRetreatFraction:F2})");
            }
        }
    }
}
