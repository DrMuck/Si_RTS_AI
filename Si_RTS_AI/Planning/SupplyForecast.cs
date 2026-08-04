using System.Collections.Generic;
using UnityEngine;

namespace Si_RTS_AI.Planning
{
    /// <summary>
    /// WHEN THE NEXT GROUP COMES FREE, AND WHERE IT CAN BE BY THEN.
    ///
    /// A Bio Cache without a Lesser Cyst is not an unstaffed Bio Cache — it is
    /// one staffed by whoever finishes a patch next. DrMuck, 2026-08-05: the
    /// planning on top of the blueprint has to PREDICT which biotics depletes,
    /// when its shrimps come free, and how long they take to walk somewhere
    /// else.
    ///
    /// Everything needed is already measured. A patch drains at the rate the
    /// Bio Caches working it are harvesting, so its remaining amount divided by
    /// that rate is a depletion time; the shrimps standing there are the group
    /// that comes free; the distance to a candidate site over shrimp speed is
    /// the walk. Three numbers we already have, multiplied out.
    ///
    /// ON THE ERROR BARS. DrMuck is right that these predictions carry a
    /// standard deviation and, worse, offset errors when a player interferes —
    /// a patch attacked, a group killed, income halved. The answer here is not
    /// a confidence interval nobody can calibrate: it is to only trust the
    /// forecast as far ahead as the next replan, and let the replan correct it.
    /// A prediction 30 seconds out is worth acting on; one four minutes out
    /// will be made again six times before it matters, so it is not used.
    /// </summary>
    internal static class SupplyForecast
    {
        /// <summary>A group that will come free, where it will be standing, and
        /// how many seconds from now.</summary>
        internal struct Release { public Vector3 at; public int shrimps; public float etaS; }

        internal static readonly List<Release> Releases = new List<Release>(16);

        /// <summary>How far ahead a prediction is worth acting on. Tied to the
        /// replan interval rather than picked: what we will re-derive shortly is
        /// not something to commit cash against now.</summary>
        internal static float TrustHorizonS => Mathf.Max(60f, BlueprintConfig.ReplanS * 4f);

        internal static void ResetForNewRound() => Releases.Clear();

        /// <summary>
        /// Rebuild from the live snapshot. Patches are drained by every Bio
        /// Cache that works them, so the rate is summed per patch before the
        /// division — attributing a shared patch's whole life to one Bio Cache
        /// would predict a release two or three times too late.
        /// </summary>
        internal static void Rebuild(EcoState s)
        {
            Releases.Clear();

            var ratePerPatch   = new Dictionary<int, float>();
            var bcPatch        = new Dictionary<int, int>();
            var bcRate         = new Dictionary<int, float>();

            for (int i = 0; i < s.bcs.Count; i++)
            {
                if (!s.bcs[i].finished) continue;
                s.shrimpsPerBc.TryGetValue(i, out int n);
                if (n <= 0) continue;
                int p = EcoSimulator.NearestActivePatchIdxPublic(s, s.bcs[i].pos);
                if (p < 0)
                {
                    // Nothing left anywhere near it — this group is free NOW.
                    Releases.Add(new Release { at = s.bcs[i].pos, shrimps = n, etaS = 0f });
                    continue;
                }
                float d = Mathf.Sqrt(SqXZ(s.bcs[i].pos, s.patches[p].pos));
                float cycle = 2f * d / EcoSimulator.SHRIMP_SPEED
                            + (float)EcoSimulator.CARRY_CAPACITY / EcoSimulator.HARVEST_RATE
                            + (float)EcoSimulator.CARRY_CAPACITY / EcoSimulator.DEPOSIT_RATE;
                float ips = n * (float)EcoSimulator.CARRY_CAPACITY / cycle;
                bcPatch[i] = p;
                bcRate[i]  = ips;
                ratePerPatch.TryGetValue(p, out float acc);
                ratePerPatch[p] = acc + ips;
            }

            foreach (var kv in bcPatch)
            {
                int bc = kv.Key, p = kv.Value;
                s.shrimpsPerBc.TryGetValue(bc, out int n);
                float total = ratePerPatch[p];
                if (total <= 0.01f) continue;
                float eta = s.patches[p].remaining / total;
                Releases.Add(new Release { at = s.bcs[bc].pos, shrimps = n, etaS = eta });
            }
        }

        /// <summary>
        /// Shrimps that can be standing at <paramref name="site"/> within
        /// <paramref name="deadlineS"/> — free by then AND able to walk there by
        /// then. Beyond the trust horizon nothing counts.
        /// </summary>
        internal static int ArrivingBy(Vector3 site, float deadlineS)
        {
            float limit = Mathf.Min(deadlineS, TrustHorizonS);
            int n = 0;
            for (int i = 0; i < Releases.Count; i++)
            {
                float walk = Mathf.Sqrt(SqXZ(Releases[i].at, site)) / EcoSimulator.SHRIMP_SPEED;
                if (Releases[i].etaS + walk <= limit) n += Releases[i].shrimps;
            }
            return n;
        }

        /// <summary>Soonest a group of any size could be here — for the log,
        /// so a decision to skip a producer can be read back against what
        /// actually happened.</summary>
        internal static float SoonestArrivalS(Vector3 site)
        {
            float best = float.MaxValue;
            for (int i = 0; i < Releases.Count; i++)
            {
                float walk = Mathf.Sqrt(SqXZ(Releases[i].at, site)) / EcoSimulator.SHRIMP_SPEED;
                float t = Releases[i].etaS + walk;
                if (t < best) best = t;
            }
            return best;
        }

        static float SqXZ(Vector3 a, Vector3 b)
        {
            float dx = a.x - b.x, dz = a.z - b.z;
            return dx * dx + dz * dz;
        }
    }
}
