using MelonLoader;
using Silica;
using Silica.AI;
using System.Collections.Generic;
using UnityEngine;

namespace Si_RTS_AI.Planning
{
    /// <summary>
    /// Shrimp micro / relocation planner — full marginal-utility rebalance.
    ///
    /// Human benchmark on NorthPolarCap produced ~365 income/sec with 211 units.
    /// AI baseline produced ~133 income/sec with 209 units — 2.75× gap despite
    /// nearly-identical unit counts. That gap is per-shrimp productivity:
    /// vanilla routing wastes shrimps on scattered patches, over-crowds hot
    /// ones, and lets some sit idle after their patch depletes.
    ///
    /// This planner runs every tick and, for EVERY shrimp:
    ///   1. Computes its current-assignment score (income/sec at its nearest
    ///      active patch given current load and travel cycle).
    ///   2. Computes the best alternative patch it could work — accounting for
    ///      crowd factor at destination and walk time to get there.
    ///   3. If the alternative beats current by REBALANCE_THRESHOLD, issues a
    ///      Move order (via PlannerOverride so our own anti-scatter filter
    ///      doesn't block us).
    ///
    /// Depletion migration falls out naturally: a patch approaching zero
    /// remaining becomes a bad "current" score, and any other-patch beats it.
    ///
    /// Load bookkeeping is incremental — as we assign shrimps, we bump the
    /// tentative load counter for the destination so subsequent shrimps see the
    /// updated crowd and don't all pile onto the same underloaded patch.
    /// </summary>
    internal static class ShrimpRelocator
    {
        // Fast cadence — we need to catch depletions + idle shrimps quickly.
        // Cost is ~200 shrimps × ~60 patches × constant work = <100k ops per
        // tick, well within a frame budget at 2 Hz.
        const float TICK_CADENCE_S = 2f;

        // Movement constants matching EcoSimulator.
        const float SHRIMP_SPEED   = 9f;
        const float HARVEST_RATE   = 9.5f;
        const float DEPOSIT_RATE   = 50f;
        const int   CARRY          = 400;

        // Phase-aware relocator strategy. User insight (2026-07-06): pile-ups
        // are OK early game (near patches are the only productive targets)
        // and hurt late game (mass simultaneous depletion causes income
        // cliffs). Each phase has its own aggression profile:
        //
        //   Phase 1 (base eco):     near-vanilla — piles allowed to 40, only
        //                           relocate on 50% improvement
        //   Phase 2 (expansion):    moderate    — piles allowed to 25, 30%
        //                           improvement threshold, depletion timers
        //                           trigger pre-migration
        //   Phase 3 (sustainability):aggressive — piles capped at 18, any
        //                           15% improvement, full depletion planning
        //                           (Phase 3 doesn't exist yet in EcoPlanner;
        //                            for now late-game Phase 2 gets these)
        struct RelocProfile
        {
            public int   HotThreshold;
            public float RebalanceThreshold;
            public float ReorderCooldownS;
            public int   MaxMigrationsPerPatchPerTick;   // rate-limit mass moves
        }
        static RelocProfile ProfileForPhase(EcoPlanner.PlanPhase phase)
        {
            if (phase == EcoPlanner.PlanPhase.Phase1_BaseEco)
                return new RelocProfile { HotThreshold = 40, RebalanceThreshold = 1.50f, ReorderCooldownS = 8f, MaxMigrationsPerPatchPerTick = 1 };
            // Phase 2 — moderate aggression by default.
            return new RelocProfile { HotThreshold = 25, RebalanceThreshold = 1.25f, ReorderCooldownS = 4f, MaxMigrationsPerPatchPerTick = 2 };
        }

        // Patches below this remaining are considered "on the way out" —
        // current-assignment score gets a heavy penalty so shrimps migrate off
        // proactively rather than mining the last few units. Bumped from
        // 2000 to 3000 (7.5 shrimp trips) so migration starts earlier and
        // is spread over more time, smoothing the "step-case" income drops
        // user observed after 10min. Late-game a chunk of patches near
        // depletion caused mass simultaneous re-tasking → income cliff.
        const int   NEAR_DEPLETION_THRESHOLD = 3000;

        // A shrimp with < this remaining at its current patch is treated as
        // idle-ish — always eligible for reassignment even if it just moved.
        const int   PATCH_EMPTY_HARD          = 200;

        static readonly Dictionary<Unit, float> _lastReorderAt = new Dictionary<Unit, float>();
        static float _lastTickAt;
        static float _lastDiagAt;

        internal static int MigratedThisRound;

        public static void ResetForNewRound()
        {
            _lastReorderAt.Clear();
            _lastTickAt = 0f;
            MigratedThisRound = 0;
        }

        public static void MaybeRun(Team team)
        {
            if (team == null) return;
            string tn = team.name ?? "";
            if (!tn.Contains("Alien")) return;

            // Round-active gate — same fix as EcoPlanner. Overnight the
            // relocator kept issuing shrimp move orders after the round
            // ended, contributing to the stalled scene transition.
            if (!global::Si_RTS_AI.TestHarnessNs.TestHarness.IsRoundActive) return;

            float now = Time.time;
            if (now - _lastTickAt < TICK_CADENCE_S) return;
            _lastTickAt = now;

            try { Run(team); }
            catch (System.Exception ex) { MelonLogger.Warning("[SHRIMP-RELOC] threw: " + ex.Message); }
        }

        static void Run(Team team)
        {
            // ---- Snapshot patches ----
            var patches = new List<Patch>();
            try
            {
                var all = ResourceArea.AllResourceAreas;
                if (all != null)
                {
                    var uType = team.UsableResource;
                    for (int i = 0; i < all.Count; i++)
                    {
                        var ra = all[i];
                        if (ra == null || ra.IsEmpty || ra.ResourceType != uType) continue;
                        patches.Add(new Patch {
                            pos = ra.SignalCenter,
                            remaining = ra.ResourceAmountCurrent,
                        });
                    }
                }
            }
            catch { return; }
            if (patches.Count == 0) return;

            // ---- Find shrimps ----
            var shrimps = new List<Unit>();
            try
            {
                var units = team.Units;
                if (units == null) return;
                for (int i = 0; i < units.Count; i++)
                {
                    var u = units[i];
                    if (u == null || u.ObjectInfo == null || u.IsDestroyed) continue;
                    if (u.ObjectInfo.DisplayName != "Shrimp") continue;
                    shrimps.Add(u);
                }
            }
            catch { return; }
            if (shrimps.Count == 0) return;

            // Pick phase-aware behavior profile once per tick.
            var profile = ProfileForPhase(EcoPlanner.CurrentPhase);

            // ---- Initial load bookkeeping: assign each shrimp to its nearest
            //      active patch (approximate current work). ----
            var load = new int[patches.Count];
            var currentIdx = new int[shrimps.Count];
            for (int si = 0; si < shrimps.Count; si++)
            {
                int nearest = NearestActivePatchIdx(patches, shrimps[si].transform.position);
                currentIdx[si] = nearest;
                if (nearest >= 0) load[nearest]++;
            }

            // Depletion-timer awareness. For each patch, estimate seconds
            // until empty: remaining / (load × 8/sec sustained harvest at
            // crowd=1.0, floor at 1s to avoid div-by-zero on empty patches).
            // Patches with life < CRITICAL_LIFE_S get pre-migration priority
            // regardless of the phase's normal thresholds.
            const float CRITICAL_LIFE_S = 30f;
            const float PER_SHRIMP_HARVEST_PER_SEC = 8f;
            var critical = new bool[patches.Count];
            for (int pi = 0; pi < patches.Count; pi++)
            {
                if (patches[pi].remaining <= PATCH_EMPTY_HARD) continue;
                float rate = load[pi] * PER_SHRIMP_HARVEST_PER_SEC;
                if (rate < 1f) continue;   // no shrimps at all — not critical
                float life = patches[pi].remaining / rate;
                if (life < CRITICAL_LIFE_S) critical[pi] = true;
            }

            // Per-tick migration rate limit to avoid mass-migration income cliffs.
            var migratedFrom = new int[patches.Count];

            int moved = 0;
            for (int si = 0; si < shrimps.Count; si++)
            {
                var u = shrimps[si];
                int curP = currentIdx[si];
                Vector3 sPos = u.transform.position;

                float curScore = curP < 0 ? 0f : ScoreForShrimpAtPatch(sPos, patches[curP], load[curP]);
                // Near-depletion discount — was 0.4× flat; now stronger when
                // remaining life is really short.
                if (curP >= 0 && patches[curP].remaining < NEAR_DEPLETION_THRESHOLD)
                    curScore *= 0.4f;
                if (curP >= 0 && critical[curP])
                    curScore *= 0.2f;   // very-short-lifespan patch — bail hard

                int bestP = -1; float bestScore = curScore * profile.RebalanceThreshold;
                for (int pi = 0; pi < patches.Count; pi++)
                {
                    if (pi == curP) continue;
                    if (patches[pi].remaining <= PATCH_EMPTY_HARD) continue;
                    if (load[pi] >= profile.HotThreshold) continue;
                    float score = ScoreForShrimpAtPatch(sPos, patches[pi], load[pi]);
                    if (score > bestScore) { bestScore = score; bestP = pi; }
                }

                if (bestP < 0) continue;

                // Cooldown gate — bypass if idle-urgent (empty patch, overloaded,
                // or lifespan-critical). Phase profile controls cooldown length.
                bool overCrowded  = curP >= 0 && load[curP] > profile.HotThreshold;
                bool lifeCritical = curP >= 0 && critical[curP];
                bool isIdleUrgent = curP < 0 || patches[curP].remaining <= PATCH_EMPTY_HARD
                                    || overCrowded || lifeCritical;
                if (!isIdleUrgent
                    && _lastReorderAt.TryGetValue(u, out float last)
                    && Time.time - last < profile.ReorderCooldownS) continue;

                // Per-tick per-patch rate limit — prevents mass simultaneous
                // migrations that would create a temporary income cliff.
                if (curP >= 0 && !isIdleUrgent && migratedFrom[curP] >= profile.MaxMigrationsPerPatchPerTick) continue;

                IssueMove(u, patches[bestP].pos);
                _lastReorderAt[u] = Time.time;

                if (curP >= 0) { load[curP]--; migratedFrom[curP]++; }
                load[bestP]++;
                moved++;
                MigratedThisRound++;
            }

            // Diagnostic: dump load distribution every 20s so we can see
            // whether shrimps are settling optimally without moves being
            // needed, or piling on the wrong patches.
            if (Time.time - _lastDiagAt > 20f)
            {
                _lastDiagAt = Time.time;
                var loadSummary = new System.Text.StringBuilder();
                int active = 0, over18 = 0;
                for (int i = 0; i < load.Length; i++)
                {
                    if (load[i] > 0) active++;
                    if (load[i] > 18) over18++;
                }
                MelonLogger.Msg($"[SHRIMP-RELOC] tick shrimps={shrimps.Count} patches_used={active} over18={over18} moved={moved} total_this_round={MigratedThisRound}");
            }
            if (moved > 0)
                MelonLogger.Msg($"[SHRIMP-RELOC] rebalanced {moved} shrimps across {patches.Count} patches (total_this_round={MigratedThisRound})");
        }

        // Estimated income per second if this shrimp works this patch.
        // Combines:
        //   cycle time = 2·distance/speed + carry/harvest + carry/deposit
        //   crowd factor at destination (based on current shrimp load)
        //   distance-to-arrive penalty (shrimp needs to walk before earning)
        static float ScoreForShrimpAtPatch(Vector3 shrimpPos, Patch p, int load)
        {
            float dx = shrimpPos.x - p.pos.x, dz = shrimpPos.z - p.pos.z;
            float d = Mathf.Sqrt(dx * dx + dz * dz);
            // Cycle time uses same d for round-trip, matches sim's constant.
            float cycle = 2f * d / SHRIMP_SPEED + (float)CARRY / HARVEST_RATE + (float)CARRY / DEPOSIT_RATE;
            float crowd = CrowdFactor(load + 1);
            float ips = CARRY * crowd / cycle;
            // Amortize walk-there time across a 60s "planning window" — a
            // shrimp walking 600m (67s) provides basically no near-term
            // benefit, so the score drops. Closer patches win.
            float walkTime = d / SHRIMP_SPEED;
            // 30s planning window — shrimps that would spend 30s walking to a
            // new patch produce ZERO near-term income. This suppresses "shrimp
            // walks 500m to a distant patch because crowd factor is slightly
            // better" — user observation was that shrimps went too far. Local
            // patches always win unless current is truly depleted.
            const float window = 30f;
            float utility = window > walkTime ? (window - walkTime) / window : 0f;
            return ips * utility;
        }

        static float CrowdFactor(int n)
        {
            if (n <= 6)  return 1.0f;
            if (n <= 12) return 0.85f;
            if (n <= 18) return 0.70f;
            return 0f;
        }

        static void IssueMove(Unit u, Vector3 pos)
        {
            try
            {
                Faction.AlienShrimpAntiAttack.PlannerOverride = true;
                u.OnMoveOrder(pos, AgentMoveSpeed.Fast);
            }
            catch (System.Exception ex) { MelonLogger.Warning("[SHRIMP-RELOC] OnMoveOrder threw: " + ex.Message); }
            finally
            {
                Faction.AlienShrimpAntiAttack.PlannerOverride = false;
            }
        }

        static int NearestActivePatchIdx(List<Patch> patches, Vector3 pos)
        {
            int best = -1; float bd = float.MaxValue;
            for (int i = 0; i < patches.Count; i++)
            {
                if (patches[i].remaining <= PATCH_EMPTY_HARD) continue;
                float dx = patches[i].pos.x - pos.x, dz = patches[i].pos.z - pos.z;
                float d = dx * dx + dz * dz;
                if (d < bd) { bd = d; best = i; }
            }
            return best;
        }

        struct Patch { public Vector3 pos; public int remaining; }
    }
}
