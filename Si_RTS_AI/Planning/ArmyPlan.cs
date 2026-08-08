using MelonLoader;
using System.Collections.Generic;
using UnityEngine;

namespace Si_RTS_AI.Planning
{
    /// <summary>
    /// WHERE THE ARMY STANDS AGAINST WHAT WINNING USUALLY LOOKS LIKE.
    ///
    /// READ THIS FIRST: nothing decides anything on these numbers. DrMuck, on
    /// being shown the measured curve — "we dont need to rotate around hard
    /// coded numbers, this is only an indication." He is right, and the reasons
    /// are worth writing down because the temptation will come back:
    ///
    ///   A curve indexed on the CLOCK is a target to chase, and a round that
    ///   opens slowly is then permanently "behind" through no fault of the
    ///   decision being made now.
    ///   It is pooled across every map, and the alien economy on NarakaCity is
    ///   half what it is on NorthPolarCap — so the same number is easy on one
    ///   map and impossible on another.
    ///   And it is a number fitted to an outcome. Winners hold more army at
    ///   minute thirty partly BECAUSE they are winning.
    ///
    /// So the curve is a gauge on the dashboard, not a hand on the wheel. It
    /// answers "is this round going roughly like the ones that get won", which
    /// is worth seeing in a log and is not worth steering by.
    ///
    /// Producer count is decided by MilitaryProduction from something local and
    /// situational: whether the producers we already have can absorb the money
    /// we already have. See WantedProducerCount.
    ///
    /// Source: median army value per minute in rounds the ALIENS WON across the
    /// main-server archive. Losing alien rounds sit flat near 8,000 for forty
    /// minutes, which is why the comparison is informative at all.
    /// </summary>
    internal static class ArmyPlan
    {
        /// <summary>minute -> target army value, from rtsai_units.json.</summary>
        static readonly SortedDictionary<int, int> _curve = new SortedDictionary<int, int>();

        internal static int   Have;
        internal static int   Target;      // indication only — see the class note
        internal static float Ratio = 1f;  // Have / Target

        static float _lastLogAt;

        internal static void ResetForNewRound()
        {
            Have = Target = 0;
            Ratio = 1f;
            _lastLogAt = 0f;
        }

        /// <summary>Load the curve alongside the unit prior. Empty curve means
        /// no opinion, and everything below degrades to "not behind".</summary>
        internal static void SetCurve(IDictionary<string, int> points)
        {
            _curve.Clear();
            if (points == null) return;
            foreach (var kv in points)
                if (int.TryParse(kv.Key, out int m)) _curve[m] = kv.Value;
        }

        internal static bool HasCurve => _curve.Count >= 2;

        /// <summary>Target at this point in the round, linearly interpolated
        /// between measured minutes and held flat past the last one — the curve
        /// plateaus around minute 36 in the data and extrapolating a plateau
        /// upward would invent a demand nobody ever met.</summary>
        internal static int TargetAt(float roundS)
        {
            if (_curve.Count == 0) return 0;
            float minute = roundS / 60f;
            int prevM = -1, prevV = 0;
            foreach (var kv in _curve)
            {
                if (kv.Key >= minute)
                {
                    if (prevM < 0) return kv.Value;
                    float t = (minute - prevM) / Mathf.Max(1, kv.Key - prevM);
                    return Mathf.RoundToInt(Mathf.Lerp(prevV, kv.Value, t));
                }
                prevM = kv.Key; prevV = kv.Value;
            }
            return prevV;
        }

        /// <summary>Called once per military tick with the live army value.</summary>
        internal static void Update(float roundS, int armyValue)
        {
            Have = armyValue;
            Target = TargetAt(roundS);
            Ratio = Target > 0 ? Have / (float)Target : 1f;

            if (Time.time - _lastLogAt > 30f)
            {
                _lastLogAt = Time.time;
                MelonLogger.Msg($"[ARMY] t={roundS:F0}s have={Have} " +
                                (Target > 0
                                    ? $"— winning alien rounds hold about {Target} by now ({Ratio:F2}x). "
                                    : "— no reference for this minute. ") +
                                "Indication only; nothing is decided on it.");
            }
        }
    }
}
