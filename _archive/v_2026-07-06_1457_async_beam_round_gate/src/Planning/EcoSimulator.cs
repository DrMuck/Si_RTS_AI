using UnityEngine;

namespace Si_RTS_AI.Planning
{
    /// <summary>
    /// Deterministic forward stepping of an EcoState using the constants
    /// measured directly from the shrimp-state sampler in the 1/10-shrimp
    /// scenario tests. Aggregate throughput model — a BC with N shrimps and
    /// distance d to the nearest active patch produces:
    ///
    ///   cycle(d)      = 2·d/9 + 400/9.5 + 400/50   seconds
    ///   crowd(N)      = 1.0 (N≤6) / 0.85 (N≤12) / 0.70 (N≤18) / 0 (else)
    ///   income_per_s  = min(N, 18) · 400 · crowd(N) / cycle(d)
    ///
    /// Ignores: enemy interference, terrain routing, exact per-shrimp position.
    /// Good enough for planning at 120s horizon; drift corrected by the fact
    /// that Build() re-reads real state every planning tick.
    /// </summary>
    public static class EcoSimulator
    {
        // ---- Measured constants ----
        // DT was 1s → 300 sim ticks per SimulateForward(300s). Combined with
        // the beam's 6×6×4 = ~150 SearchNode calls per plan tick, that was
        // ~45k sim steps per plan tick blocking the main thread for 1-3.7s.
        // DT=2 halves the sim ticks; combined with horizon 300→180 the
        // per-tick cost drops ~3.5×. Accuracy loss: negligible for planning
        // (shrimp production cadence is 20s → 2s DT still catches it).
        public const float DT              = 2f;
        public const float HARVEST_RATE    = 9.5f;    // measured single-shrimp
        public const float DEPOSIT_RATE    = 50f;     // measured from cash slope
        public const float SHRIMP_SPEED    = 9f;      // MoveSpeed from dump
        public const int   CARRY_CAPACITY  = 400;     // ResourceHolder.MaxAmount
        public const int   MAX_PER_PATCH   = 18;      // observed access-contention ceiling

        // ---- Game constants (from dump) ----
        public const int   BC_COST         = 500;
        public const int   CYST_COST       = 1500;
        public const int   NODE_COST       = 100;
        public const float BC_BUILD_S      = 20f;
        public const float CYST_BUILD_S    = 20f;
        public const float NODE_BUILD_S    = 20f;
        public const float SHRIMP_BUILD_S  = 20f;

        public static void SimulateForward(EcoState s, float horizonS)
        {
            // Zero the gross-earned accumulator so the caller's score reflects
            // ONLY what this simulation window produced — not any leftover from
            // a previous Clone.
            s.grossEarned = 0;
            int steps = Mathf.CeilToInt(horizonS / DT);
            for (int i = 0; i < steps; i++) Step(s);
        }

        public static void Step(EcoState s)
        {
            s.t += DT;

            // (1) Finalize any construction that just finished.
            for (int i = 0; i < s.bcs.Count; i++)
            {
                var b = s.bcs[i];
                if (!b.finished && s.t >= b.readyAt) { b.finished = true; s.bcs[i] = b; }
            }
            for (int i = 0; i < s.cysts.Count; i++)
            {
                var c = s.cysts[i];
                if (!c.finished && s.t >= c.readyAt) { c.finished = true; s.cysts[i] = c; }
            }
            for (int i = 0; i < s.nodes.Count; i++)
            {
                var n = s.nodes[i];
                if (!n.finished && s.t >= n.readyAt) { n.finished = true; s.nodes[i] = n; }
            }

            // (1b) Sweep unassigned shrimps into the first finished BC we find.
            //
            // The state builder assigns each shrimp to its nearest existing BC.
            // But at round start there are typically 3 starter shrimps and 0 BCs,
            // so all 3 stay "unassigned" (present in totalShrimps but absent from
            // shrimpsPerBc). Without this sweep, a candidate PlaceBc would never
            // recruit them — the BC would sit finished with 0 workers and score 0
            // over the entire horizon.
            {
                int assigned = 0;
                foreach (var kv in s.shrimpsPerBc) assigned += kv.Value;
                int unassigned = s.totalShrimps - assigned;
                if (unassigned > 0)
                {
                    for (int i = 0; i < s.bcs.Count; i++)
                    {
                        if (!s.bcs[i].finished) continue;
                        s.shrimpsPerBc.TryGetValue(i, out int cur);
                        s.shrimpsPerBc[i] = cur + unassigned;
                        break;
                    }
                }
            }

            // (2) Cysts spawn shrimps at their scheduled cadence UNTIL the
            //     shrimp cap. Reality hard-caps shrimp production at 200
            //     (AlienShrimpProducer.SHRIMP_HARD_CAP), so the sim must
            //     also cap or beam will over-value new BC/Cyst placement.
            //     Once at cap:
            //       - Cysts stop spawning (matches producer's gate)
            //       - New BCs steal shrimps from existing BCs (redistribution)
            //       - Beam correctly sees "no growth, just spreading" → less
            //         eager on Cyst placement, more eager on Nodes / new
            //         patches (which still add income via redistribution
            //         onto FRESH patches vs depleting ones).
            const int SIM_SHRIMP_HARD_CAP = 200;
            for (int i = 0; i < s.cysts.Count; i++)
            {
                var c = s.cysts[i];
                if (!c.finished) continue;
                while (s.t >= c.nextSpawnAt)
                {
                    if (s.totalShrimps >= SIM_SHRIMP_HARD_CAP)
                    {
                        // Don't advance nextSpawnAt — cap holds until real-
                        // world shrimps drop (via death, unit-cap changes,
                        // etc). For sim horizon this pins us at the cap.
                        break;
                    }
                    s.totalShrimps++;
                    int bcIdx = ArgMaxMarginalBcIdx(s);
                    if (bcIdx < 0) bcIdx = ClosestFinishedBcIdx(s, c.pos);   // fallback
                    if (bcIdx >= 0)
                    {
                        s.shrimpsPerBc.TryGetValue(bcIdx, out int cur);
                        s.shrimpsPerBc[bcIdx] = cur + 1;
                    }
                    c.nextSpawnAt += SHRIMP_BUILD_S;
                }
                s.cysts[i] = c;
            }

            // (3) Per-BC harvest -> cash + patch depletion.
            for (int i = 0; i < s.bcs.Count; i++)
            {
                var bc = s.bcs[i]; if (!bc.finished) continue;
                s.shrimpsPerBc.TryGetValue(i, out int shrimps);
                if (shrimps <= 0) continue;

                int pIdx = NearestActivePatchIdx(s, bc.pos);
                if (pIdx < 0) continue;
                var patch = s.patches[pIdx];

                float d = HorizontalDistance(bc.pos, patch.pos);
                float cycleTime = 2f * d / SHRIMP_SPEED
                                + (float)CARRY_CAPACITY / HARVEST_RATE
                                + (float)CARRY_CAPACITY / DEPOSIT_RATE;
                // No hard cap on useful count — the diminishing CrowdFactor
                // already models the queueing penalty at high N.
                float crowd = CrowdFactor(shrimps);
                float ips = shrimps * (float)CARRY_CAPACITY / cycleTime * crowd;
                int gain = Mathf.RoundToInt(ips * DT);
                if (gain > patch.remaining) gain = patch.remaining;

                patch.remaining -= gain;
                s.grossEarned  += gain;                              // uncapped — the score metric
                // Cash grows freely in the simulation. The old clamp
                // (`s.cash = min(cash+gain, cap)`) misbehaved whenever cap
                // was smaller than current cash — e.g. round start with
                // 9000 starter cash and cap=0 (no BCs yet) clamped every
                // income tick straight down to 0, so every candidate BC
                // scored 0. grossEarned is what we score against anyway.
                s.cash += gain;
                s.patches[pIdx] = patch;
            }
        }

        static float CrowdFactor(int n)
        {
            // Efficiency DIMINISHES but never hits zero — user observation:
            // 42 shrimps piling on one spot IS the vanilla-AI's optimal
            // choice, so per-capita productivity must stay positive at high N.
            // Old model (0 above 18) was under-predicting income at hot spots
            // and steering the relocator to fight vanilla for no benefit.
            if (n <= 6)  return 1.00f;
            if (n <= 12) return 0.85f;
            if (n <= 18) return 0.70f;
            if (n <= 24) return 0.55f;
            if (n <= 32) return 0.45f;
            if (n <= 42) return 0.38f;
            return 0.32f;   // asymptote for very high N — shrimps are queuing
                            //   for the harvest slot but still contribute
        }

        static float HorizontalDistance(Vector3 a, Vector3 b)
        {
            float dx = a.x - b.x, dz = a.z - b.z;
            return Mathf.Sqrt(dx * dx + dz * dz);
        }

        static int NearestActivePatchIdx(EcoState s, Vector3 pos)
        {
            int best = -1; float bd = float.MaxValue;
            for (int i = 0; i < s.patches.Count; i++)
            {
                if (s.patches[i].remaining <= 0) continue;
                float d = HorizontalDistance(s.patches[i].pos, pos);
                if (d < bd) { bd = d; best = i; }
            }
            return best;
        }

        static int ClosestFinishedBcIdx(EcoState s, Vector3 pos)
        {
            int best = -1; float bd = float.MaxValue;
            for (int i = 0; i < s.bcs.Count; i++)
            {
                if (!s.bcs[i].finished) continue;
                float d = HorizontalDistance(s.bcs[i].pos, pos);
                if (d < bd) { bd = d; best = i; }
            }
            return best;
        }

        // Marginal utility of adding one more shrimp to each finished BC.
        // For each BC: find nearest active patch, compute income at (N+1) minus
        // income at (N) using the same closed-form model as Step's harvest phase.
        // The BC with highest marginal value gets the new shrimp — this is what
        // makes a Cyst's spawned shrimps rebalance across the network instead of
        // piling up at whatever BC is closest to the Cyst.
        internal static int ArgMaxMarginalBcIdx(EcoState s)
        {
            int best = -1; float bestMarginal = -1f;
            for (int i = 0; i < s.bcs.Count; i++)
            {
                if (!s.bcs[i].finished) continue;
                int pIdx = NearestActivePatchIdx(s, s.bcs[i].pos);
                if (pIdx < 0) continue;
                var patch = s.patches[pIdx];
                float d = HorizontalDistance(s.bcs[i].pos, patch.pos);
                float cycle = 2f * d / SHRIMP_SPEED
                            + (float)CARRY_CAPACITY / HARVEST_RATE
                            + (float)CARRY_CAPACITY / DEPOSIT_RATE;
                s.shrimpsPerBc.TryGetValue(i, out int N);
                float now  = IncomePerSec(N,     d, cycle);
                float next = IncomePerSec(N + 1, d, cycle);
                float marginal = next - now;
                if (marginal > bestMarginal) { bestMarginal = marginal; best = i; }
            }
            return best;
        }

        static float IncomePerSec(int N, float d, float cycle)
        {
            if (N <= 0) return 0f;
            return N * (float)CARRY_CAPACITY / cycle * CrowdFactor(N);
        }

        // Wrappers for the beam-search planner. The private helpers above are
        // instance-agnostic; these expose them so EcoPlanner can query "closest
        // finished BC" and "nearest active patch" during candidate enumeration.
        internal static int NearestActivePatchIdxPublic(EcoState s, Vector3 pos) => NearestActivePatchIdx(s, pos);
        internal static int ClosestFinishedBcIdxPublic(EcoState s, Vector3 pos)  => ClosestFinishedBcIdx(s, pos);
    }
}
