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

        // ---- Game constants — populated at runtime from ConstructionData ----
        // User 2026-07-07: "Node costs are 200! because it is modded. Very
        // important to fetch the correct price from the game." Costs are
        // Si_UnitBalance-overridable, so hardcoding vanilla values makes the
        // planner's math diverge from actual cash flow. SetCostsFromCds fires
        // from AlienConstruction once the CDs are resolved.
        public static int   BC_COST         = 500;
        public static int   CYST_COST       = 1500;
        public static int   NODE_COST       = 200;   // base 100 x Si_UnitBalance cost_mult 2
        public static int   SHRIMP_COST     = 160;

        /// <summary>Shrimp CD arrives separately (AlienShrimpProducer resolves it).</summary>
        public static void SetShrimpCd(ConstructionData shrimpCd)
        {
            if (shrimpCd == null) return;
            try { SHRIMP_COST    = shrimpCd.ResourceCost; } catch { }
            try { SHRIMP_BUILD_S = shrimpCd.TotalConstructionTime; } catch { }
        }
        // Chain reach — CD.MaximumBaseStructureDistance, and it is NOT one
        // number. Measured live from game_constants.dump on 2026-07-28 with
        // Si_UnitBalance active:
        //
        //     Bio Cache  cost  500  reach 200m  build 30s
        //     Cyst       cost 1500  reach 150m  build 35s
        //     Node       cost  200  reach 150m  build 20s
        //
        // So a Node hop only extends the chain 150m, but a Bio Cache may be
        // placed 200m out from the end of it. Collapsing both into a single
        // CHAIN_REACH_M either under-uses BC range or over-reaches on Nodes.
        public static float BC_REACH_M      = 200f;
        // The placed structure's own radius counts toward the limit —
        // ConstructionData carries MaximumDistanceUseRadius = True, and the
        // Bio Cache is a big structure (PhysicalRadius 37m against a Node's
        // 9.4m). So the effective CENTRE-TO-CENTRE distance at which a BC can
        // still be placed is its chain reach PLUS its own radius. Ignoring it
        // under-estimates BC range by nearly 20% and makes the planner bridge
        // toward patches it could already build on.
        public static float BC_RADIUS_M     = 37f;
        /// <summary>Centre-to-centre range for placing a Bio Cache off an anchor.</summary>
        public static float BcPlaceReachM => BC_REACH_M + BC_RADIUS_M;
        public static float NODE_REACH_M    = 150f;
        // Retained as the general/BC figure for callers that just mean
        // "how far can we place from the chain".
        public static float CHAIN_REACH_M   = 200f;

        // Build times — CD.TotalConstructionTime. See ReadUsableTime.
        //
        // This block used to say the opposite: that a structure is usable after
        // BuildUpTime and clean-up is only the site tearing itself down. It read
        // plausibly and it was wrong. The game's own construction_complete
        // events land at Total, not BuildUp:
        //
        //              BuildUpTime  CleanUpTime  Total    MEASURED
        //   Bio Cache      20s          10s        30s     30.11s
        //   Cyst           20s          15s        35s     34.99s
        //   Node           12s           8s        20s     20.02s
        //
        // So every timing the planner produced was optimistic by 50-75%, and
        // the error compounded: the Cyst gate keys off a FINISHED Bio Cache, so
        // a Bio Cache modelled 10s early pulled every Cyst 10s early too, and
        // the shrimp ramp with it.
        //
        // These four are LAST-RESORT fallbacks only, used when the read off the
        // live ConstructionData fails. They carry the modded totals measured on
        // 2026-08-10 rather than vanilla numbers, because a wrong fallback that
        // looks reasonable is how the previous error survived so long.
        public static float BC_BUILD_S      = 30f;
        public static float CYST_BUILD_S    = 35f;
        public static float NODE_BUILD_S    = 20f;
        public static float SHRIMP_BUILD_S  = 15f;

        /// <summary>One numeric field or property off a ConstructionData.</summary>
        static float ReadField(ConstructionData cd, string name, out bool ok)
        {
            ok = false;
            try
            {
                var t = cd.GetType();
                var f = t.GetField(name);
                if (f != null) { ok = true; return System.Convert.ToSingle(f.GetValue(cd)); }
                var p = t.GetProperty(name);
                if (p != null) { ok = true; return System.Convert.ToSingle(p.GetValue(cd)); }
            }
            catch { }
            return 0f;
        }

        /// <summary>
        /// Seconds from placement to the structure being USABLE — anchoring a
        /// chain, unlocking a Cyst, taking a deposit.
        ///
        /// This is TotalConstructionTime, not BuildUpTime. An older comment here
        /// dismissed TotalConstructionTime as "the build-up plus the site
        /// teardown rather than time-to-usable" and read BuildUpTime instead.
        /// That was wrong, and it made every timing the planner produced
        /// optimistic by half. Measured against the game's own
        /// construction_start / construction_complete events on NarakaCity
        /// 2026-08-10:
        ///
        ///     Bio Cache   BuildUp 20 + CleanUp 10 = Total 30   measured 30.11
        ///     Cyst        BuildUp 20 + CleanUp 15 = Total 35   measured 34.99
        ///     Node        BuildUp 12 + CleanUp  8 = Total 20   measured 20.02
        ///
        /// Exact to a tenth of a second, and the executor's own "no finished BC
        /// anywhere yet" flipped 0.8s after the Bio Cache's completion event —
        /// so this is also when the game starts accepting it as an anchor.
        ///
        /// Read as a property first because that is what the game exposes, then
        /// reconstructed from the parts, then the caller's default. All three
        /// come off the live ConstructionData, so Si_UnitBalance's build-time
        /// multipliers are picked up without a constant here tracking them —
        /// these ARE the modded values (DrMuck: "no hard coded value
        /// reference").
        /// </summary>
        static float ReadUsableTime(ConstructionData cd, float fallback)
        {
            float total = ReadField(cd, "TotalConstructionTime", out bool haveTotal);
            if (haveTotal && total > 0f) return total;

            float build = ReadField(cd, "BuildUpTime",      out bool haveBuild);
            float clean = ReadField(cd, "CleanUpTime",      out _);
            float wait  = ReadField(cd, "FinishedWaitTime", out _);
            if (haveBuild && build > 0f) return build + clean + wait;

            return fallback;
        }

        static bool _costsResolved;
        /// <summary>True once the one-shot read off the game's CDs has landed.
        /// Callers use this to skip the resolution path entirely rather than
        /// re-entering it every tick — these values do not change mid-round.</summary>
        public static bool CostsResolved => _costsResolved;
        public static void SetCostsFromCds(ConstructionData bcCd, ConstructionData cystCd, ConstructionData nodeCd)
        {
            if (_costsResolved) return;
            bool any = false;
            try { if (bcCd   != null) { BC_COST   = bcCd.ResourceCost;   any = true; } } catch { }
            try { if (cystCd != null) { CYST_COST = cystCd.ResourceCost; any = true; } } catch { }
            try { if (nodeCd != null) { NODE_COST = nodeCd.ResourceCost; any = true; } } catch { }
            // Per-structure reach — Nodes extend the chain, BCs get placed off
            // the end of it, and the two limits are different.
            try
            {
                if (nodeCd != null) { NODE_REACH_M = nodeCd.MaximumBaseStructureDistance; any = true; }
                if (bcCd   != null) { BC_REACH_M   = bcCd.MaximumBaseStructureDistance;   any = true; }
                CHAIN_REACH_M = BC_REACH_M;
            }
            catch { }
            // Physical radius of the Bio Cache — counts toward its placement
            // range because MaximumDistanceUseRadius is set. Reflected rather
            // than typed, since it lives on ObjectInfo and the field vs
            // property form differs between builds.
            try
            {
                var oi = bcCd?.ObjectInfo;
                if (oi != null)
                {
                    var t = oi.GetType();
                    var pi = t.GetProperty("PhysicalRadius");
                    object v = pi != null ? pi.GetValue(oi) : t.GetField("PhysicalRadius")?.GetValue(oi);
                    if (v != null) BC_RADIUS_M = System.Convert.ToSingle(v);
                }
            }
            catch { }

            // Build times straight off the CDs.
            try
            {
                if (bcCd   != null) BC_BUILD_S   = ReadUsableTime(bcCd,   BC_BUILD_S);
                if (cystCd != null) CYST_BUILD_S = ReadUsableTime(cystCd, CYST_BUILD_S);
                if (nodeCd != null) NODE_BUILD_S = ReadUsableTime(nodeCd, NODE_BUILD_S);
            }
            catch { }
            if (any && bcCd != null && cystCd != null && nodeCd != null)
            {
                _costsResolved = true;
                MelonLoader.MelonLogger.Msg(
                    $"[ECO/COSTS] resolved from game: " +
                    $"BC={BC_COST}/{BC_REACH_M:F0}m(+r{BC_RADIUS_M:F0}={BcPlaceReachM:F0}m)/usable{BC_BUILD_S:F0}s " +
                    $"Cyst={CYST_COST}/-/usable{CYST_BUILD_S:F0}s " +
                    $"Node={NODE_COST}/{NODE_REACH_M:F0}m/usable{NODE_BUILD_S:F0}s " +
                    $"shrimp={SHRIMP_COST}/{SHRIMP_BUILD_S:F0}s");
            }
        }

        internal static void ResetForNewRound() { _costsResolved = false; }

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
                    // SHRIMPS COST MONEY. They were free here, which quietly
                    // invalidated every number the simulator produced: five
                    // Cysts spawning one Shrimp each per 15s over a 240s window
                    // is ~60 Shrimps, or ~9,600 cash — MORE than the entire
                    // opening bank. With them free the sim reported 385/s
                    // income and 48,000 cash in hand at 240s against a reality
                    // of roughly 50-80/s and near-zero spare cash, so the
                    // Opener judged a 5-site 5-Cyst opening trivially
                    // affordable on every map it saw.
                    //
                    // A Cyst that cannot pay simply does not spawn, and does
                    // not advance its timer — production resumes when there is
                    // money again, which is what the real producer does.
                    if (s.cash < SHRIMP_COST) break;
                    s.cash -= SHRIMP_COST;
                    s.totalShrimps++;
                    int bcIdx = ArgMaxMarginalBcIdx(s);
                    if (bcIdx < 0) bcIdx = ClosestFinishedBcIdx(s, c.pos);   // fallback
                    if (bcIdx >= 0)
                    {
                        // A NEW SHRIMP EARNS NOTHING UNTIL ITS FIRST LOAD LANDS.
                        //
                        // It was counted into shrimpsPerBc on the spawn tick and
                        // credited full steady-state income from that instant,
                        // which ignored two real delays: the walk from the Cyst
                        // to the patch — hundreds of metres on the 3rd or 4th
                        // site — and the fill-and-return before anything is
                        // deposited. Both are largest exactly where it matters,
                        // in a 240s window where most shrimps are young, and
                        // they are the leading suspects for the model reporting
                        // 42,418 earned at 240s against a real ~12,000.
                        //
                        // Per-shrimp STEADY-STATE rate was already about right
                        // (5.5/s modelled against 6.0/s measured), so the error
                        // is in shrimp-seconds of production, not in the harvest
                        // maths — which is what this corrects.
                        s.pendingShrimps.Add(new EcoState.PendingShrimp
                        {
                            bcIdx = bcIdx,
                            activeAt = s.t + FirstDepositDelay(s, c.pos, bcIdx),
                        });
                    }
                    c.nextSpawnAt += SHRIMP_BUILD_S;
                }
                s.cysts[i] = c;
            }

            // (2b) Promote shrimps whose first load has now landed.
            for (int i = s.pendingShrimps.Count - 1; i >= 0; i--)
            {
                if (s.t < s.pendingShrimps[i].activeAt) continue;
                int bi = s.pendingShrimps[i].bcIdx;
                s.shrimpsPerBc.TryGetValue(bi, out int cur);
                s.shrimpsPerBc[bi] = cur + 1;
                s.pendingShrimps.RemoveAt(i);
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

                // CLUSTER MODE — IndustrialQuarter-shaped maps only.
                //
                // Normally a BC's income is modelled against its NEAREST patch
                // alone. That is fine when patches stand apart, but on a map
                // that packs them into 4-groups ~30m across, one BC works the
                // whole group and the single-patch model under-values it by
                // ~4x. The entire "1-2 Cysts feed one cluster, open on 2-4
                // clusters" strategy is invisible without this.
                //
                // Effect is deliberately narrow: shrimps are spread over the
                // patches in range, so the group drains as one pool and the
                // cycle distance becomes the group's mean rather than the
                // nearest patch's. On non-cluster maps the flag is off and
                // this block never runs.
                int   clusterCount = 1;
                int   clusterIdx0  = pIdx;
                float meanD        = d;
                if (MapProfile.ClusterMode)
                {
                    float rSq = MapProfile.CLUSTER_RADIUS_M * MapProfile.CLUSTER_RADIUS_M;
                    float sumD = 0f; int n = 0;
                    for (int pi = 0; pi < s.patches.Count; pi++)
                    {
                        if (s.patches[pi].remaining <= 0) continue;
                        float ddx = s.patches[pi].pos.x - patch.pos.x;
                        float ddz = s.patches[pi].pos.z - patch.pos.z;
                        if (ddx * ddx + ddz * ddz > rSq) continue;
                        sumD += HorizontalDistance(bc.pos, s.patches[pi].pos);
                        n++;
                    }
                    if (n > 1) { clusterCount = n; meanD = sumD / n; }
                }
                d = meanD;
                float cycleTime = 2f * d / SHRIMP_SPEED
                                + (float)CARRY_CAPACITY / HARVEST_RATE
                                + (float)CARRY_CAPACITY / DEPOSIT_RATE;
                // No hard cap on useful count — the diminishing CrowdFactor
                // already models the queueing penalty at high N.
                float crowd = CrowdFactor(shrimps);
                float ips = shrimps * (float)CARRY_CAPACITY / cycleTime * crowd;
                int gain = Mathf.RoundToInt(ips * DT);

                if (clusterCount > 1)
                {
                    // Drain the cluster as one pool, nearest patch first, so
                    // depletion timing stays honest.
                    int left = gain, drained = 0;
                    float rSq = MapProfile.CLUSTER_RADIUS_M * MapProfile.CLUSTER_RADIUS_M;
                    for (int pi = 0; pi < s.patches.Count && left > 0; pi++)
                    {
                        var cp = s.patches[pi];
                        if (cp.remaining <= 0) continue;
                        float ddx = cp.pos.x - s.patches[clusterIdx0].pos.x;
                        float ddz = cp.pos.z - s.patches[clusterIdx0].pos.z;
                        if (ddx * ddx + ddz * ddz > rSq) continue;
                        int take = left < cp.remaining ? left : cp.remaining;
                        cp.remaining -= take;
                        s.patches[pi] = cp;
                        left -= take; drained += take;
                    }
                    s.grossEarned += drained;
                    s.cash        += drained;
                    continue;
                }

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

            // (4) Depleted sites give up their shrimps.
            RelocateFromDepletedPatches(s);
        }

        /// <summary>
        /// SHRIMPS WHOSE PATCH IS GONE WALK TO BETTER WORK.
        ///
        /// The game does this by itself — auto-relocation on depletion is a
        /// vanilla feature — and it is the mechanism the whole "skip the Lesser
        /// Cyst, let the group move on" strategy rests on. The simulator did not
        /// model it: a Bio Cache whose patch ran dry simply kept its shrimps and
        /// started harvesting the next-nearest patch at an ever longer cycle. So
        /// migration never appeared as a way to staff anything, and a site with
        /// no producer looked permanently dead. Every comparison between
        /// producing and migrating was therefore rigged toward producing, which
        /// is exactly the bias that fills the map with Lesser Cysts and hits the
        /// shrimp cap by mid-game.
        ///
        /// Modelled as a stream, not a teleport: a few shrimps at a time, each
        /// arriving after the walk, and only when somewhere else genuinely pays
        /// more per shrimp than staying does.
        /// </summary>
        static void RelocateFromDepletedPatches(EcoState s)
        {
            // The destination is the same for everybody who moves this step, and
            // finding it costs a scan of every Bio Cache against every patch —
            // so find it once, and only if somebody is actually stranded.
            int to = -1;
            bool anyStranded = false;
            for (int i = 0; i < s.bcs.Count && !anyStranded; i++)
            {
                if (!s.bcs[i].finished) continue;
                s.shrimpsPerBc.TryGetValue(i, out int n);
                if (n <= 0) continue;
                int pi = NearestActivePatchIdx(s, s.bcs[i].pos);
                if (pi < 0 || HorizontalDistance(s.bcs[i].pos, s.patches[pi].pos) > RELOCATE_TRIGGER_M)
                    anyStranded = true;
            }
            if (!anyStranded) return;
            to = ArgMaxMarginalBcIdx(s);
            if (to < 0) return;

            for (int i = 0; i < s.bcs.Count; i++)
            {
                if (!s.bcs[i].finished || i == to) continue;
                s.shrimpsPerBc.TryGetValue(i, out int have);
                if (have <= 0) continue;

                int pIdx = NearestActivePatchIdx(s, s.bcs[i].pos);
                float dHere = pIdx < 0 ? float.MaxValue
                            : HorizontalDistance(s.bcs[i].pos, s.patches[pIdx].pos);
                // Still working its own patch? Then nobody is going anywhere.
                if (dHere <= RELOCATE_TRIGGER_M) continue;

                float cycleHere = 2f * dHere / SHRIMP_SPEED
                                + (float)CARRY_CAPACITY / HARVEST_RATE
                                + (float)CARRY_CAPACITY / DEPOSIT_RATE;
                float staying = pIdx < 0 ? 0f
                              : IncomePerSec(have, dHere, cycleHere) - IncomePerSec(have - 1, dHere, cycleHere);

                int tp = NearestActivePatchIdx(s, s.bcs[to].pos);
                if (tp < 0) continue;
                float dThere = HorizontalDistance(s.bcs[to].pos, s.patches[tp].pos);
                float cycleThere = 2f * dThere / SHRIMP_SPEED
                                 + (float)CARRY_CAPACITY / HARVEST_RATE
                                 + (float)CARRY_CAPACITY / DEPOSIT_RATE;
                s.shrimpsPerBc.TryGetValue(to, out int there);
                for (int p = 0; p < s.pendingShrimps.Count; p++)
                    if (s.pendingShrimps[p].bcIdx == to) there++;
                float going = IncomePerSec(there + 1, dThere, cycleThere)
                            - IncomePerSec(there,     dThere, cycleThere);
                if (going <= staying) continue;

                int move = have < RELOCATE_PER_STEP ? have : RELOCATE_PER_STEP;
                s.shrimpsPerBc[i] = have - move;
                float walkS = HorizontalDistance(s.bcs[i].pos, s.bcs[to].pos) / SHRIMP_SPEED;
                for (int m = 0; m < move; m++)
                    s.pendingShrimps.Add(new EcoState.PendingShrimp
                    { bcIdx = to, activeAt = s.t + walkS });
            }
        }

        /// <summary>A Bio Cache whose nearest live patch is beyond this is not
        /// working a patch of its own any more — its shrimps are walking either
        /// way, so where they walk to becomes a real choice. Set at the
        /// co-harvest radius the planner uses everywhere else.</summary>
        const float RELOCATE_TRIGGER_M = 50f;

        /// <summary>Shrimps that leave one Bio Cache per sim step. A stream
        /// rather than a stampede — the real thing takes a minute or more to
        /// drain a depleted site.</summary>
        const int RELOCATE_PER_STEP = 2;

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
                // Count shrimps already WALKING here too, or a burst of spawns
                // all picks the same Bio Cache — none of them are in
                // shrimpsPerBc yet, so each sees the same empty marginal slot.
                s.shrimpsPerBc.TryGetValue(i, out int N);
                for (int p = 0; p < s.pendingShrimps.Count; p++)
                    if (s.pendingShrimps[p].bcIdx == i) N++;
                float now  = IncomePerSec(N,     d, cycle);
                float next = IncomePerSec(N + 1, d, cycle);
                float marginal = next - now;
                if (marginal > bestMarginal) { bestMarginal = marginal; best = i; }
            }
            return best;
        }

        /// <summary>
        /// Seconds from spawning at a Cyst to that shrimp's FIRST deposit:
        /// walk Cyst to patch, fill, walk patch to Bio Cache, unload.
        ///
        /// One way each leg, not the round trip — the steady-state cycle already
        /// carries the return once the shrimp is producing.
        /// </summary>
        static float FirstDepositDelay(EcoState s, Vector3 cystPos, int bcIdx)
        {
            if (bcIdx < 0 || bcIdx >= s.bcs.Count) return 0f;
            Vector3 bcPos = s.bcs[bcIdx].pos;

            int pi = NearestActivePatchIdx(s, bcPos);
            Vector3 patchPos = pi >= 0 ? s.patches[pi].pos : bcPos;

            float toPatch = HorizontalDistance(cystPos, patchPos) / SHRIMP_SPEED;
            float toBc    = HorizontalDistance(patchPos, bcPos)   / SHRIMP_SPEED;
            return toPatch + (float)CARRY_CAPACITY / HARVEST_RATE
                 + toBc    + (float)CARRY_CAPACITY / DEPOSIT_RATE;
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
