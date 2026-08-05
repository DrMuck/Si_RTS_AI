using MelonLoader;
using UnityEngine;

namespace Si_RTS_AI.Planning
{
    /// <summary>
    /// THE ECONOMY GETS A TRAJECTORY TO HIT, NOT A CEILING TO AVOID.
    ///
    /// Everything governing worker count until now was a BRAKE — a per-Bio-Cache
    /// cap, a map-capacity model, a sustainable fraction, headroom held for
    /// expansions. Each was added against a real failure and each is right in
    /// the situation that produced it. Together they had no idea what they were
    /// aiming at, so the economy could sit at 37 workers for five minutes with
    /// tens of thousands unspent and nothing in the code considered that wrong.
    ///
    /// Measured across 14 soak rounds on 2026-08-05:
    ///
    ///     corr(workers at 10min, income at 25min) = 0.80
    ///     corr(Cysts   at 10min, income at 25min) = 0.79
    ///
    /// The runaway round (TheMaw, 1.05M by 25min) had 20 Cysts and 106 workers
    /// at ten minutes. The stalled ones had 3-5 Cysts and 37-58 workers. DrMuck
    /// read the same rounds and set the reference: "getting to 100 shrimp by
    /// 10min seems to be a fair reference, and afterwards focus more on shrimp
    /// production at the outer skirts."
    ///
    /// So there is now a target curve, and being behind it is a fact the rest of
    /// the economy can act on. It drives three levers, because a worker needs
    /// somewhere to work as much as it needs a producer to be born in:
    ///
    ///   1. HOW MANY PRODUCERS the blueprint plans.
    ///   2. WHETHER THE CAPACITY CEILING BINDS. Behind schedule, the sustainable
    ///      model stops being a brake; it is a statement about the long run and
    ///      the long run is what we are losing.
    ///   3. HOW WIDE EXPANSION GOES. Workers with nowhere to harvest are the
    ///      pile-up DrMuck asked to fix in the first place, so falling behind
    ///      has to buy ground too, not only Cysts.
    ///
    /// It is a REFERENCE, not a quota. Nothing here forces a placement or
    /// overrides a physical limit — it raises ambition while we are behind and
    /// gets out of the way once we are not.
    /// </summary>
    internal static class WorkerPlan
    {
        /// <summary>Workers the reference curve wants by ten minutes.</summary>
        internal static int TargetAtTenMin => BlueprintConfig.WorkersByTenMinutes;

        /// <summary>Production cannot start before the first Cyst exists, so the
        /// ramp is measured from roughly when one does rather than from zero.
        /// Earlier than this the curve would demand workers nothing could have
        /// built yet.</summary>
        const float RAMP_START_S = 90f;

        /// <summary>Shrimps one producer delivers per minute at full duty —
        /// derived, so a balance mod that changes build time changes this.</summary>
        static float PerProducerPerMin => 60f / Mathf.Max(1f, EcoSimulator.SHRIMP_BUILD_S);

        /// <summary>Measured fraction of that ceiling actually achieved. Producers
        /// queue against per-Bio-Cache caps and cash, so asking for "deficit
        /// divided by the theoretical rate" always asks for too few. Floored so
        /// a bad patch of measurement cannot demand an absurd roster.</summary>
        const float MIN_DUTY = 0.25f;
        const float MAX_DUTY = 1.0f;

        /// <summary>Producers this may ever ask for. Beyond this the answer is
        /// not more Cysts, it is more ground.</summary>
        const int MAX_PRODUCERS_ASKED = 14;

        internal static int   Have, Target, ProducersNeeded;
        internal static bool  BehindSchedule;
        internal static float Duty = 0.5f;

        // ---- Yield: what a worker is actually worth right now ---------------
        //
        // WORKERS ARE NOT THE OBJECTIVE. Income is, and a worker only converts
        // into income while there is short-cycle ground for it to work. Two
        // things break that link and they pull in opposite directions from a
        // worker count: patches drain, so the same shrimps walk further for
        // each load; and workers added past a patch's useful density crowd it.
        //
        // Measured on IndustrialQuarter 2026-08-05, income per worker per
        // second:
        //
        //     min 10   1.56      min 20   3.48   <- peak
        //     min 15   2.92      min 25   3.12
        //                        min 33   2.24
        //
        // Workers went 129 -> 168 across that decline and income still FELL,
        // from 449/s to 377/s. The economy was telling us its ground was
        // draining ten minutes before the worker count noticed, and nothing
        // was listening.
        //
        // So yield is the control signal that decides WHICH lever to pull:
        // rising or steady, more workers pay and producers are the answer;
        // falling, more workers are being poured into ground that cannot feed
        // them and the answer is more ground. This also guards the trajectory
        // above from its own failure mode — chasing a worker count onto
        // crowded patches is exactly how you lower income while hitting target.
        internal static float Yield, YieldPeak;
        internal static bool  YieldFalling;

        /// <summary>How far below its best yield may drift before the ground is
        /// judged to be draining. Wide enough not to trip on the noise of a
        /// depot finishing or a group relocating.</summary>
        const float YIELD_DECAY_TRIGGER = 0.20f;

        /// <summary>Before this the sample is meaningless — a handful of
        /// starter shrimps on the richest patch on the map produce a yield the
        /// rest of the round can never match, and every later reading would
        /// look like decay against it.</summary>
        const float YIELD_WARMUP_S = 300f;
        const int   YIELD_MIN_WORKERS = 25;

        static float _lastCum, _lastCumAt;
        static float _lastLogAt;

        internal static void ResetForNewRound()
        {
            Have = Target = ProducersNeeded = 0;
            BehindSchedule = false;
            Duty = 0.5f;
            Yield = YieldPeak = 0f;
            YieldFalling = false;
            _lastCum = _lastCumAt = 0f;
            _lastLogAt = 0f;
        }

        /// <summary>Measured duty cycle, fed by the shrimp producer.</summary>
        internal static void NoteDuty(float achievedPerMin, float ceilingPerMin)
        {
            if (ceilingPerMin <= 0.01f) return;
            float d = Mathf.Clamp(achievedPerMin / ceilingPerMin, MIN_DUTY, MAX_DUTY);
            Duty = Duty * 0.7f + d * 0.3f;      // slow, so one bad window cannot swing it
        }

        /// <summary>
        /// Where the curve should be at this point in the round. Linear from the
        /// first producer to the ten-minute reference, and CONTINUING past it at
        /// the same slope — the economy does not stop wanting workers at ten
        /// minutes, it just stops being the only thing that matters.
        /// </summary>
        internal static int TargetAt(float roundS)
        {
            if (roundS <= RAMP_START_S) return 0;
            float slope = TargetAtTenMin / (600f - RAMP_START_S);
            return Mathf.RoundToInt((roundS - RAMP_START_S) * slope);
        }

        /// <summary>
        /// Called once per plan cycle with the live counts.
        /// </summary>
        internal static void Update(float roundS, int workers, int producers, int cumulativeIncome)
        {
            Have = workers;
            Target = TargetAt(roundS);
            BehindSchedule = Have < Target;

            // Income over the plan interval, per worker. Derived from the
            // cumulative total rather than the round average, which is far too
            // slow to show a decline.
            float now = Time.time;
            if (_lastCumAt > 0f && now - _lastCumAt >= 1f && workers > 0)
            {
                float rate = (cumulativeIncome - _lastCum) / (now - _lastCumAt);
                float sample = rate / workers;
                if (sample >= 0f)
                    Yield = Yield <= 0f ? sample : Yield * 0.8f + sample * 0.2f;
            }
            if (now - _lastCumAt >= 1f) { _lastCum = cumulativeIncome; _lastCumAt = now; }

            if (roundS >= YIELD_WARMUP_S && Have >= YIELD_MIN_WORKERS)
            {
                if (Yield > YieldPeak) YieldPeak = Yield;
                YieldFalling = YieldPeak > 0f && Yield < YieldPeak * (1f - YIELD_DECAY_TRIGGER);
            }

            int deficit = Mathf.Max(0, Target - Have);
            // MORE WORKERS ONLY WHEN A WORKER STILL EARNS.
            //
            // Behind the curve AND yield falling means the shortfall is ground,
            // not producers — adding shrimps there buys crowding. Expansion
            // picks the deficit up instead; see the sites-ahead floor.
            if (deficit <= 0 || YieldFalling)
            {
                ProducersNeeded = 0;
            }
            else
            {
                // How long we have to close it: the rest of the ramp to ten
                // minutes, or a rolling window once past it. Either way the
                // question is the same — how many producers does closing this
                // gap in that time take, at the rate producers actually
                // achieve rather than the rate they theoretically could.
                float windowMin = roundS < 600f
                    ? Mathf.Max(1f, (600f - roundS) / 60f)
                    : 5f;
                float perProducer = PerProducerPerMin * Duty * windowMin;
                ProducersNeeded = Mathf.Clamp(
                    Mathf.CeilToInt(deficit / Mathf.Max(0.1f, perProducer)),
                    0, MAX_PRODUCERS_ASKED);
            }

            if (Time.time - _lastLogAt > 30f)
            {
                _lastLogAt = Time.time;
                MelonLogger.Msg($"[WORKERS] t={roundS:F0}s have={Have} target={Target} " +
                                $"({(BehindSchedule ? "behind" : "on track")}) duty={Duty:F2} " +
                                $"producers={producers} wants={ProducersNeeded} " +
                                $"yield={Yield:F2}/peak {YieldPeak:F2}" +
                                (YieldFalling ? " FALLING - take ground, not workers" : "") +
                                $" (reference {TargetAtTenMin} by 10min)");
            }
        }
    }
}
