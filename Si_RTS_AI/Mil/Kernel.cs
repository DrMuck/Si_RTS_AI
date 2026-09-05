using System;
using System.Collections.Generic;
using UnityEngine;

namespace Si_RTS_AI.Mil
{
    /// <summary>
    /// THE FORWARD MODEL. One set of maths, the same as mil_sim/fit_kernel.py,
    /// reading the same file (mil_doctrine.json, through Doctrine).
    ///
    ///   effective(force) = SUM count * cost * w[unit]
    ///   pWin(A, B)       = sigmoid( K * ln(effA / effB) )
    ///   lossRatio(r)     = r ^ -Beta            (the fitted exchange exponent)
    ///   razeTime         = SUM hp(structures) / SUM dps(force)
    ///
    /// Pure functions over compositions. No Unity calls, no game state — this
    /// is the boundary MIL_V3_PLAN section 8 draws for moving the search off the
    /// main thread later.
    ///
    /// THE KERNEL IS ONLY BELIEVED WHERE IT WAS VALIDATED. Between RefuseBelow
    /// and WastefulAbove the archive says the band is decisive; at parity it
    /// says a coin flip that nothing predicts, and the planner declines. Every
    /// prediction made for a real engagement is logged beside the outcome by
    /// CombatLog so the calibration can be read off a round.
    /// </summary>
    internal static class Kernel
    {
        /// <summary>A composition: unit name -> count. The unit of everything here.</summary>
        internal sealed class Force
        {
            public readonly Dictionary<string, int> Units =
                new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            public void Add(string name, int n = 1)
            {
                if (string.IsNullOrEmpty(name) || n <= 0) return;
                Units[name] = Units.TryGetValue(name, out int c) ? c + n : n;
            }

            public int Count { get { int n = 0; foreach (var kv in Units) n += kv.Value; return n; } }
            public bool IsEmpty => Units.Count == 0;

            public float Effective()
            {
                float e = 0f;
                foreach (var kv in Units)
                    e += kv.Value * CostOf(kv.Key) * Doctrine.ValueOf(kv.Key);
                return e;
            }

            public int Cash()
            {
                int c = 0;
                foreach (var kv in Units) c += kv.Value * CostOf(kv.Key);
                return c;
            }

            public float Dps()
            {
                float d = 0f;
                foreach (var kv in Units) d += kv.Value * UnitStats.DpsOf(kv.Key);
                return d;
            }

            /// <summary>Slowest member — the pace of the group.</summary>
            public float SlowestSpeed()
            {
                float s = float.MaxValue;
                foreach (var kv in Units) s = Mathf.Min(s, UnitStats.SpeedOf(kv.Key));
                return s == float.MaxValue ? 9f : s;
            }

            public override string ToString()
            {
                var sb = new System.Text.StringBuilder();
                foreach (var kv in Units)
                {
                    if (sb.Length > 0) sb.Append(' ');
                    sb.Append(kv.Value).Append('x').Append(kv.Key);
                }
                return sb.Length == 0 ? "nothing" : sb.ToString();
            }
        }

        static int CostOf(string name)
        {
            int c = 0;
            try { c = Perception.UnitValues.CostOf(name); } catch { }
            if (c <= 0 && UnitStats.TryGet(name, out var s)) c = s.Cost;
            return Mathf.Max(0, c);
        }

        /// <summary>Effective value of one unit — its price times the fitted weight.</summary>
        internal static float EffectiveOf(string name) => CostOf(name) * Doctrine.ValueOf(name);

        static float Sigmoid(float x) => 1f / (1f + Mathf.Exp(-x));

        /// <summary>Probability that a force of effA holds the ground against effB.</summary>
        internal static float PWin(float effA, float effB)
        {
            if (effA <= 0f) return 0f;
            if (effB <= 0f) return 1f;
            return Sigmoid(Doctrine.K * Mathf.Log(effA / effB));
        }

        /// <summary>Ours over theirs, in effective cash. Infinity when they have nothing.</summary>
        internal static float Ratio(float effA, float effB) =>
            effB <= 0f ? float.PositiveInfinity : effA / Mathf.Max(1f, effB);

        internal static Doctrine.Band BandOf(float effA, float effB) =>
            Doctrine.Classify(Ratio(effA, effB));

        /// <summary>
        /// What a decisive win costs the winner, in the winner's effective cash.
        /// The loser loses ~70 % of its force (that is the decisive-fight
        /// definition the archive was cut on); the winner's losses scale with the
        /// fitted exponent. Clamped to what the winner actually has.
        /// </summary>
        internal static float ExpectedLossOfWinner(float effWinner, float effLoser)
        {
            if (effLoser <= 0f || effWinner <= 0f) return 0f;
            float r = Ratio(effWinner, effLoser);
            float loserLoss = 0.7f * effLoser;
            float loss = loserLoss * Mathf.Pow(Mathf.Max(r, 0.01f), -Doctrine.Beta);
            return Mathf.Min(loss, effWinner);
        }

        /// <summary>Smallest effective force that reaches the commit band against
        /// this much defence. One unit's worth at least, so an empty target still
        /// needs someone to walk there.</summary>
        internal static float PriceToBeat(float effDefence) =>
            Mathf.Max(effDefence * Doctrine.CommitAt, 80f);

        /// <summary>Seconds to remove this much structure health with this force.</summary>
        internal static float RazeTimeS(Force f, float totalHp)
        {
            float dps = f.Dps();
            if (dps <= 0.1f) return float.PositiveInfinity;
            return totalHp / dps;
        }

        /// <summary>Seconds to remove this much health with this many effective
        /// cash of ours, assuming the dps-per-effective-cash of the current army.
        /// Used when the force does not exist yet (pricing a producer plan).</summary>
        internal static float RazeTimeS(float effForce, float dpsPerEff, float totalHp)
        {
            float dps = effForce * dpsPerEff;
            return dps <= 0.1f ? float.PositiveInfinity : totalHp / dps;
        }

        /// <summary>Short reading for a log line.</summary>
        internal static string Describe(float effA, float effB)
        {
            float r = Ratio(effA, effB);
            string rt = float.IsInfinity(r) ? "inf" : r.ToString("F2");
            return $"{effA:F0} vs {effB:F0} eff (x{rt}, pWin {PWin(effA, effB):F2}, {Doctrine.Classify(r)})";
        }
    }
}
