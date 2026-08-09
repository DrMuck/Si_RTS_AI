using MelonLoader;
using Silica;
using UnityEngine;

namespace Si_RTS_AI.Perception
{
    /// <summary>
    /// THE FOUR NUMBERS GOOD COMMANDERS ARE ACTUALLY OPTIMISING.
    ///
    /// DrMuck, 2026-08-08, on what separates a good commander from this bot:
    ///
    ///   "Resource utilization: They invest the money as much as they can,
    ///    usually try not sit on money because it is potential sitting there dead"
    ///   "Unit utilization: In best case all units are in an objective and
    ///    actively defending or in offence — unused units is non used potential"
    ///   "Production Utilization: Keep your production buildings busy. Too many
    ///    means overspending... too less means your military output is
    ///    bottlenecked"
    ///   plus map control and situational awareness.
    ///
    /// Those are not four opinions, they are four UTILISATION RATIOS over four
    /// pools, and every one of them is measurable continuously. That matters more
    /// than it sounds, because DrMuck also said the win-rate scoreboard is
    /// useless right now — the bot never wins, so an outcome metric has nothing
    /// to say. Utilisation grades a bot that is losing, which is the bot we have.
    ///
    /// It is also the objective function the placement question kept needing. A
    /// producer is well placed when its output reaches an objective quickly —
    /// which shows up here as army engaged, because units walking are units doing
    /// nothing. "How many producers" is the trade between the production meter
    /// and the cash meter. "Where" is whatever keeps the army meter high. The
    /// argument about placement was an argument about which of these to maximise.
    ///
    /// MEASUREMENT ONLY. Nothing decides on these — they are the instrument, and
    /// this project's own history says instrumentation settles in one round what
    /// argument does not.
    /// </summary>
    internal static class Utilisation
    {
        /// <summary>Minutes of income sitting dead in the bank. Not a fraction:
        /// "we are holding four minutes of earnings" is a sentence a person can
        /// act on, where "cash utilisation 0.82" is not.</summary>
        internal static float CashIdleMinutes { get; private set; }

        /// <summary>Share of army VALUE that is committed to an objective or
        /// filling a garrison that actually needs it. The rest is standing
        /// around, which is what "sitting like a duck" looks like as a number.</summary>
        internal static float ArmyEngaged { get; private set; }

        /// <summary>Share of military producer capacity that is busy. Falling
        /// means we have built more than the economy can feed; pinned at 1.0
        /// means output is bottlenecked and another producer would pay.</summary>
        internal static float ProducersBusy { get; private set; }

        /// <summary>Ground we hold, from ControlMap.</summary>
        internal static float MapHeld { get; private set; }

        /// <summary>Share of the round in which the ECONOMY was blocked for want
        /// of cash. This is the number that says whether military spending is
        /// hurting expansion — the thing DrMuck wants held at zero while the
        /// other meters climb. Above a few percent and the military is taking
        /// money the economy had a use for.</summary>
        internal static float EcoStarvedShare { get; private set; }

        static int _starvedSamples, _samples;
        static int _armyUnits;

        // Producer busy-ness is sampled by MilitaryProduction as it walks the
        // producers anyway — cheaper than looking them up again here, and it
        // cannot disagree with what production actually saw.
        static int _busySamples, _totalSamples;

        internal static void NoteProducers(int busy, int total)
        {
            _busySamples += busy;
            _totalSamples += total;
        }

        const float LOG_S = 30f;
        static float _lastLogAt;

        internal static void ResetForNewRound()
        {
            CashIdleMinutes = 0f;
            ArmyEngaged = ProducersBusy = MapHeld = 0f;
            _busySamples = _totalSamples = 0;
            _starvedSamples = _samples = 0;
            _lastLogAt = 0f;
        }

        internal static void Tick(Team team)
        {
            if (team == null) return;
            try
            {
                Measure(team);
                float now = Time.time;
                if (now - _lastLogAt < LOG_S) return;
                _lastLogAt = now;
                MelonLogger.Msg(
                    $"[UTIL] cash {CashIdleMinutes:F1}min idle | " +
                    $"army {ArmyEngaged * 100f:F0}% engaged | " +
                    $"producers {ProducersBusy * 100f:F0}% busy | " +
                    $"map {MapHeld * 100f:F0}% held | " +
                    $"eco cash-blocked {EcoStarvedShare * 100f:F0}% of the round | " +
                    Planning.BattalionManager.OrderRateReport(_armyUnits));
            }
            catch (System.Exception ex)
            { MelonLogger.Warning("[UTIL] tick threw: " + ex.Message); }
        }

        static void Measure(Team team)
        {
            // ---- cash: how long the bank would take to earn itself again ----
            int cash = 0;
            try { cash = team.TotalResources; } catch { }
            float earned = 0f;
            try { earned = BcIncome.EarnedPerSec(); } catch { }
            CashIdleMinutes = earned > 1f ? cash / earned / 60f : 0f;

            // ---- army: committed, or filling a garrison that needs filling ---
            //
            // A garrison AT its requirement is doing its job. A garrison holding
            // four times its requirement is the surplus parked at home, and
            // counting that as "engaged" would hide exactly the failure this
            // meter exists to show.
            int engaged = 0, total = 0;
            _armyUnits = 0;
            try
            {
                foreach (var b in Planning.BattalionManager.Battalions)
                {
                    total += b.Value;
                    _armyUnits += b.Units.Count;
                    if (b.Kind == Planning.MissionPlanner.Kind.Garrison)
                        engaged += Mathf.Min(b.Value, b.RequiredValue);
                    else if (b.Phase == Planning.BattalionManager.State.Committed)
                        engaged += b.Value;
                }
            }
            catch { }
            ArmyEngaged = total > 0 ? engaged / (float)total : 0f;

            // ---- producers: busy fraction over the round --------------------
            ProducersBusy = _totalSamples > 0 ? _busySamples / (float)_totalSamples : 0f;

            // ---- ground -----------------------------------------------------
            try { MapHeld = ControlMap.HeldFraction; } catch { }

            // ---- is the military standing on the economy's toes? ------------
            _samples++;
            try { if (Planning.EcoPlanner.EcoStarvedOfCash) _starvedSamples++; } catch { }
            EcoStarvedShare = _samples > 0 ? _starvedSamples / (float)_samples : 0f;
        }

        /// <summary>One line for the round summary, so a soak can be read
        /// without opening the live log.</summary>
        internal static string BuildRoundSummaryFragment()
        {
            if (_totalSamples == 0 && ArmyEngaged <= 0f) return "";
            return "--- Utilisation (final) ---\n" +
                   $"  cash idle: {CashIdleMinutes:F1} min of income\n" +
                   $"  army engaged: {ArmyEngaged * 100f:F0}%\n" +
                   $"  producers busy: {ProducersBusy * 100f:F0}% (round average)\n" +
                   $"  map held: {MapHeld * 100f:F0}%\n" +
                   $"  eco blocked for cash: {EcoStarvedShare * 100f:F0}% of samples\n";
        }
    }
}
