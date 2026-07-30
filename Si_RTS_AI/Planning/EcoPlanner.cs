using MelonLoader;
using Silica;
using SilicaAdminMod;
using System.Collections.Generic;
using UnityEngine;

namespace Si_RTS_AI.Planning
{
    /// <summary>
    /// Rolling-horizon eco planner. Every PLAN_CADENCE_S seconds:
    ///   1. Snapshot live game state (EcoStateBuilder)
    ///   2. Enumerate candidate immediate actions
    ///   3. For each candidate: clone state, apply action, simulate 120s forward
    ///   4. Argmax by cumulative cash at horizon (proxy for "best next move")
    ///
    /// SHADOW MODE: logs decisions to the round file via [PLAN] lines; does NOT
    /// execute them. Once we've verified the planner's calls make sense against
    /// the phased-rule AI's actual choices, a later iteration wires action
    /// execution and disables the phased rules.
    /// </summary>
    public static class EcoPlanner
    {
        // Horizon extended from 120s → 300s so investments with delayed payback
        // (Cysts especially: 20s build + 20s first-shrimp + travel = 60s before
        // any income) get judged over enough of their productive life to justify
        // their upfront cost. Also nudges the planner away from short-termism —
        // matches the observed real-player benchmark (34 shrimps @ 5min ≈ 300s).
        // Beam cost was dominating main thread at late game (1-3.7s per
        // tick blocking game frames, causing visible unit-jumping). Cut
        // horizon 300 → 180, and pair with EcoSimulator.DT 1 → 2 and
        // BEAM_DEPTH 6 → 5 for a combined ~5-10× reduction in per-tick work.
        public const float PLAN_HORIZON_S = 180f;
        // Cadence 5 → 8s: fires 60% less often. Combined with per-tick
        // speedup, expected freeze drops from 3s/5s (60% load) to
        // ~0.3s/8s (~4% load).
        public const float PLAN_CADENCE_S = 8f;
        // Wait for starter units + starting cash to actually be granted
        // before the first plan tick. Round start has a spawn-in period —
        // planning against a still-empty state produces sequences that
        // are irrelevant by the time the game catches up.
        // Wait for the spawn-in before planning. Alien starter units arrive in
        // waves — 1 Crab at t=2s, 6 more at t=18s, 5 Squids at t=22-24s — so a
        // plan made at t=8s is made against a team that does not exist yet.
        //
        // Briefly cut to 8s chasing the late first Bio Cache; that was the
        // wrong culprit (it was a reach-test sign error, see BC_TIGHT_GAP_M)
        // and the user confirmed the delay is correct behaviour: a human
        // commander waits for the same spawn-in.
        const float PLANNER_STARTUP_DELAY_S = 20f;

        // ---- Phase machine ------------------------------------------------
        // Base eco setup and eco expansion are TWO different problems. In
        // Phase 1 (base eco), the planner wants to maximize income from the
        // 3-4 nearby biotics — Cyst-per-BC discipline, tight enumeration
        // radius, ignore distant Node targets that add cost with no immediate
        // payoff. In Phase 2 (expansion), the beam's absolute-income score
        // stops finding gain because the near patches are saturated; income
        // plateaus and cash starts accumulating. We need to push Nodes further
        // out even though their payoff is beyond the 300s horizon (a Node at
        // t=0 lets a BC be placed at t=20 that earns for 280s — often visible
        // in horizon — but a Node at 800m needs another Node chain-hop and
        // the resulting BC only earns for the last ~150s of horizon).
        //
        // Transition trigger: shrimps ≥ PHASE2_MIN_SHRIMPS AND cash ≥ PHASE2_MIN_CASH
        //   AND fire frequency in last 20s < 2 (planner is stuck).
        // Once in Phase 2, stay there — no reversion.
        public enum PlanPhase { Phase1_BaseEco, Phase2_Expand }
        static readonly Dictionary<Team, PlanPhase> _phaseByTeam = new Dictionary<Team, PlanPhase>();
        static PlanPhase _currentPhase = PlanPhase.Phase1_BaseEco;
        // Public accessor for other sub-planners (ShrimpRelocator etc.) that
        // need phase-aware behavior. This is the "shared context" that the
        // future money broker will formalize; for now it's a static read.
        public static PlanPhase CurrentPhase => _currentPhase;
        // Team is set at the top of MaybePlan so EnumerateActions can query
        // team-specific state (fog of war layers, chain-linked structures).
        // Cleaner than threading Team through every internal helper.
        static Team _currentTeam;

        // Lowered again: 20/2000 was still landing Phase 2 at t=189s in
        // recent rounds — 60-90s of Phase-1 gain=0 stall while cash
        // accumulated to 2-3k. Dropping to 15/1500 catches the transition
        // right when base eco is halfway saturated instead of after.
        const int   PHASE2_MIN_SHRIMPS = 15;
        const int   PHASE2_MIN_CASH    = 1500;
        // Time-based fallback tightened accordingly.
        const float PHASE2_TIME_FALLBACK_S = 150f;
        // Was 15 — 15 shrimps at t=180s meant Phase 2 fired with only
        // 345 cash and no eco base yet, wasting the Phase 2 buffs on a
        // planner that couldn't afford anything.
        const int   PHASE2_TIME_FALLBACK_MIN_SHRIMPS = 25;
        // In Phase 2, each Node in the winning sequence gets this score bonus
        // (representing beyond-horizon strategic value). Chosen to be roughly
        // the average income of the BC a Node enables over horizon(300s) −
        // horizon(build+shrimp-fill) = 200-250s of income = ~2000 gross.
        // Phase 2 bonuses — need to overcome the sim's per-action "10s
        // startup cost" penalty which grows with current income rate. At
        // late game with ~1500/sec income, each action in a sequence
        // "loses" 15000 baseline income compared to the Noop sequence. So
        // bonuses must beat that to make expansion score positive. This
        // is a heuristic — the deeper fix is comparing "with-actions"
        // vs "baseline" income at full horizon regardless of sim advance,
        // but that's a bigger sim refactor. For now, big bonuses.
        //
        // User (CrimsonPeak): "expansion stalls again from like 9min ...
        // looks like that". Diagnostic confirmed: beam picked Noop over
        // any expansion because BC bonus 5500 − 10s×1500/s income = −9500.
        const float PHASE2_NODE_BONUS  = 15000f;   // was 6000
        const float PHASE2_CYST_BONUS  = 15000f;   // was 6000 — MAX; scaled by handoff
        const float PHASE2_BC_BONUS    = 15000f;   // was 5500
        // Cluster-aware Cyst thresholds — biotics-weighted handoff score of
        // the target BC scales PHASE2_CYST_BONUS (0 → full).
        //   HANDOFF_CYST_MIN  = below this: isolated BC, no Cyst bonus (bare
        //                       BC preferred; auto-migrated neighbour shrimps
        //                       will service the patch).
        //   HANDOFF_CYST_FULL = at this + above: rich cluster, full Cyst bonus.
        // Handoff formula: Σ patch.remaining × 1/(1+d/200). A "cluster" of
        // 3 patches × 3000 each × 0.7 weight ≈ 6300. An isolated patch × 3000
        // × 1.0 weight ≈ 3000.
        // 4000 gates out isolated BCs (1 patch × 3000 rem × ~1.0 weight = 3000 < 4000).
        // A 2-patch cluster (2 × 3000 × 0.85 = 5100) or single very rich patch
        // (5000 × 0.9 = 4500) will qualify. Above 8000 = definite cluster.
        // Distance from the nearest shrimp source (Cyst or Nest) at which a BC
        // starts to need its own Cyst regardless of how rich its patch is.
        //
        // User 2026-07-29, watching NarakaCity: expanding to the 3rd and 4th
        // biotics with a bare Bio Cache on each is worse than saving the money
        // and putting a Cyst at one of them, because a Cyst there pumps shrimps
        // ON SITE instead of making every one of them walk from the Nest. That
        // matters exactly when the patch is "not directly accessible from the
        // start" — which is the frontier case.
        //
        // Below NEAR, migration from neighbours is realistic. Past FAR it is
        // hopeless: at 9 m/s a 700m walk is 78s one way, and the shrimp has to
        // come back again every trip.
        // ============================================================
        // Frontier chain ROI.
        //
        // The beam scores income inside a 300s horizon. A 4km chain on
        // NarakaCity is ~30 Nodes and 6000 cash and pays nothing at all inside
        // that window, so every deep expansion scores negative and the planner
        // never commits — it topped out at 2363m while the human reached
        // 3166m and took 46 patches we never touched.
        //
        // A chain is therefore evaluated as ONE decision, outside the horizon:
        // total cost to get there against the biotics it opens, discounted for
        // how long until the first deposit lands.
        //
        // Two rules from the user (2026-07-29) on what counts as "opens":
        //   a) Map layout is PRE-KNOWLEDGE. An experienced commander knows
        //      where the biotics are on a map they have played, so planning
        //      against the full ResourceArea list is legitimate — we are not
        //      required to pretend fog.
        //   b) Unexplored biotics are still worth LESS, because the game
        //      gates placement on explored ground. So they are discounted
        //      rather than ignored, which also makes scouting pay: revealing
        //      a patch converts discounted value into full value.
        const float UNEXPLORED_VALUE_DISCOUNT = 0.5f;
        // Income does not start when the chain completes — the BC must build,
        // then shrimps must arrive. Exponential decay on time-to-first-deposit.
        const float ROI_TIME_TAU_S = 300f;

        struct ChainRoi
        {
            public int   Nodes;        // hops needed to reach
            public int   CostCash;     // nodes + BC (+ Cyst when remote)
            public float RawValue;     // biotics opened, explored-discounted
            public float TimeToIncomeS;
            public float Roi;          // discounted value per cash
        }

        /// <summary>
        /// Cost/benefit of chaining out to <paramref name="target"/> and
        /// working everything it brings into range.
        /// </summary>
        static ChainRoi EvaluateChainRoi(EcoState s, Vector3 target, float anchorDistM)
        {
            var r = new ChainRoi();
            float hop = NodeHopDistance(EcoSimulator.NODE_REACH_M);
            // Nodes needed to bring the target inside BC range of the chain.
            float gap = Mathf.Max(0f, anchorDistM - (EcoSimulator.BcPlaceReachM + BC_TIGHT_GAP_M));
            r.Nodes = Mathf.CeilToInt(gap / Mathf.Max(1f, hop));
            // Patches lying along the route become Bio Cache hops instead of
            // Node hops: further reach, and they earn rather than costing. Each
            // one removes roughly a BC-hop's worth of Nodes from the bill.
            int stepping = 0;
            if (r.Nodes > 0 && s.patches != null)
            {
                for (int p = 0; p < s.patches.Count; p++)
                {
                    if (s.patches[p].remaining <= 0) continue;
                    if (IsPatchBcReachable(s.patches[p].pos, s)) continue;
                    // Between us and the target, not off to the side.
                    float dTarget = Vector3.Distance(s.patches[p].pos, target);
                    if (dTarget >= anchorDistM || dTarget < 1f) continue;
                    stepping++;
                }
                int savedPerStep = Mathf.CeilToInt(EcoSimulator.BcPlaceReachM / Mathf.Max(1f, hop));
                r.Nodes = Mathf.Max(0, r.Nodes - stepping * savedPerStep);
            }
            r.CostCash = r.Nodes * EcoSimulator.NODE_COST + EcoSimulator.BC_COST;
            if (anchorDistM > CYST_REMOTE_FAR_M) r.CostCash += EcoSimulator.CYST_COST;

            // Everything a BC at the target would service, plus anything the
            // chain drags into reach on the way.
            var explored = _beamExplored;
            float value = 0f;
            float serviceSq = (EcoSimulator.BcPlaceReachM + BC_TIGHT_GAP_M);
            serviceSq *= serviceSq;
            for (int p = 0; p < s.patches.Count; p++)
            {
                if (s.patches[p].remaining <= 0) continue;
                float dx = s.patches[p].pos.x - target.x, dz = s.patches[p].pos.z - target.z;
                if (dx * dx + dz * dz > serviceSq) continue;
                if (IsPatchBcReachable(s.patches[p].pos, s)) continue;   // already ours
                float w = 1f;
                if (explored != null &&
                    !explored.IsSet(Perception.MapLayers.GridWorld.CellX(s.patches[p].pos.x),
                                    Perception.MapLayers.GridWorld.CellZ(s.patches[p].pos.z)))
                    w = UNEXPLORED_VALUE_DISCOUNT;
                value += s.patches[p].remaining * w;
            }
            r.RawValue = value;

            // Nodes build in sequence as cash allows; then the BC; then the
            // first shrimps have to walk out and back.
            r.TimeToIncomeS = r.Nodes * EcoSimulator.NODE_BUILD_S * 0.5f
                            + EcoSimulator.BC_BUILD_S
                            + anchorDistM / 9f;
            float timeFactor = Mathf.Exp(-r.TimeToIncomeS / ROI_TIME_TAU_S);
            r.Roi = r.CostCash > 0 ? (value * timeFactor) / r.CostCash : 0f;
            return r;
        }

        const float CYST_REMOTE_NEAR_M = 350f;
        const float CYST_REMOTE_FAR_M  = 700f;

        const float HANDOFF_CYST_MIN   = 4000f;
        const float HANDOFF_CYST_FULL  = 8000f;

        // ============================================================
        // Radial-sector reserve for starter Nest shrimps.
        //
        // Sim result 2026-07-07 (Projects/Si_RTS_AI/eco_sim/): partition the
        // plane around the Nest into 8 angular sectors, reserve the closest
        // patch per sector (within 700m). BC candidate enumeration skips
        // reserved patches — they stay for starter Nest shrimps to harvest.
        //
        // Beats fixed skip_K by up to 17 percentage points on dense maps
        // (GreatErg: +9.6% skip_8 vs +26.9% radial). No map regressed.
        // See feedback_si_rts_ai_eco_expansion_shrimps memory for the
        // cross-map result table.
        //
        // Toggle: RadialReserveEnabled — flip false to disable if a live
        // round shows regression vs the sim's prediction.
        // Default OFF for now — soft-reserve pattern is deployed and tested but
        // user wants to focus on TechPlanner improvements first. Flip to true
        // to re-enable; feedback_si_rts_ai_eco_expansion_shrimps memory has
        // the sim result (+20% eco on spread maps, +27% on dense).
        internal static bool RadialReserveEnabled = false;
        const int   RADIAL_SECTORS     = 8;
        const float RADIAL_INNER_R_M   = 700f;
        const float RADIAL_INNER_R_SQ  = RADIAL_INNER_R_M * RADIAL_INNER_R_M;
        // Even in Phase 1, small bonuses tip the beam toward "keep expanding
        // to 4-5 BCs" instead of stalling at 3 once local crowd factor hits
        // 1.0. The bonus is small (500 = BC cost) so it just cancels the
        // cost drag — actual income has to break the tie. User: "sitting on
        // 2-3k for a long time while could have invested better".
        const float PHASE1_BC_BONUS    = 500f;
        const float PHASE1_CYST_BONUS  = 1500f;   // matches Cyst cost — makes Cysting the 4th BC free score-wise

        // ============================================================
        // Phase 1 minimum-biotics objective.
        //
        // NarakaCity 2026-07-28 exposed the hole: the Nest spawns with only
        // TWO patches close by. The planner built both BCs plus their Cysts
        // by t=99s and then emitted seq=[] gain=0 EXEC=held for 128 SECONDS
        // straight, sitting on 5-6k cash, until the shrimp count crossed 15
        // and Phase 2 unlocked. The instant Phase 2 hit at t=228 it fired
        // [N→N→N→N→N] — proving the expansion was available all along and
        // only the Phase 1 gates were hiding it.
        //
        // Two gates were responsible:
        //   1. Phase 1 Node reach is 2×CHAIN_REACH (400m). Naraka's third
        //      patch is beyond that, so the Node enumerator emitted nothing.
        //   2. Even with a candidate, Phase 1 gives Nodes no bonus at all —
        //      a naked Node is pure −200 cost inside the horizon, so Noop
        //      always wins.
        //
        // Fix: until the team has BCs serving PHASE1_MIN_TAPPED_PATCHES
        // distinct patches, treat reaching the next one as a first-class
        // objective — open the Node reach and pay a bonus that makes the
        // [Node → BC] chain outscore doing nothing. Self-limiting: the moment
        // the target is met, both revert to normal Phase 1 discipline.
        // Read from EcoPlannerConfig so it can be retuned per map without a
        // rebuild — same pattern as the Phase 2 Cyst knobs.
        static int PHASE1_MIN_TAPPED_PATCHES => EcoPlannerConfig.Phase1MinTappedPatches;

        // Cysts the opening wants before Nodes may spend freely. Human
        // benchmark reached 3 by t=128s on NarakaCity.
        const int PHASE1_MIN_CYSTS = 3;

        // ============================================================
        // Frontier value — "how much map does this placement open up".
        //
        // NarakaCity 2026-07-28 made the case. Every patch on that map holds
        // exactly 22,000, so income is purely a question of how many patches
        // you can REACH, and the patches are a doughnut around the spawn:
        //
        //     <=  500m :   2 patches        <= 2500m :  28
        //     <= 1000m :   8                <= 3166m :  47
        //     <= 2000m :  20                total    : 107  (2.35M biotics)
        //
        // 59% of the map's biotics sit beyond 3km. The human benchmark crossed
        // 2000m of reach at t=658s and its income rate went 173/s -> 550/s in
        // the same minute; it finished with a BC at 3166m. Our v0.7.66 round
        // did not cross 2000m until t=877s and topped out at 2175m, which is
        // the entire 2.6x income gap.
        //
        // The beam could not see this. It scores income inside a 300s horizon
        // and pays a FLAT bonus per BC, so a backfill BC at 900m — cheap to
        // reach, earning immediately — always beat a frontier BC that needs a
        // node chain first. The log shows exactly that oscillation: after
        // reaching 1694m the next four BCs went to 1199m, 940m, 1374m, 1646m.
        //
        // Fix: pay for REACH, not just for the structure. Every candidate is
        // credited with the patches it pulls inside chain reach for the first
        // time, which is a map-independent way of saying "this one opens a new
        // band" — and on a doughnut map it is worth far more than its own
        // immediate income.
        // Valued as a FRACTION OF THE BIOTICS UNLOCKED, not a flat per-patch
        // constant. Per-patch totals are a server/map setting (NarakaCity ran
        // 22,000 per patch; other settings differ), so any fixed number would
        // be wildly over- or under-weighted the moment the setting changes.
        // A fraction auto-scales: it is always worth the same relative to the
        // income those patches will actually produce.
        const float PHASE2_UNLOCK_VALUE_FRACTION = 0.25f;
        const int   PHASE2_UNLOCK_MAX_COUNT = 5;      // don't let one candidate run away

        /// <summary>
        /// Patches that are NOT chain-reachable today but would sit within
        /// CHAIN_REACH_M of <paramref name="at"/> once something is built there.
        /// </summary>
        static int CountUnlockedPatches(EcoState s, Vector3 at)
        {
            if (s == null || s.patches == null) return 0;
            float r2 = CHAIN_REACH_M * CHAIN_REACH_M;
            int n = 0; long biotics = 0;
            for (int p = 0; p < s.patches.Count; p++)
            {
                if (s.patches[p].remaining <= 0) continue;
                float dx = s.patches[p].pos.x - at.x, dz = s.patches[p].pos.z - at.z;
                if (dx * dx + dz * dz > r2) continue;
                if (IsChainReachable(s.patches[p].pos, s)) continue;
                biotics += s.patches[p].remaining;
                if (++n >= PHASE2_UNLOCK_MAX_COUNT) break;
            }
            // Stored as biotics, not a count — see PHASE2_UNLOCK_VALUE_FRACTION.
            return (int)System.Math.Min(biotics, int.MaxValue);
        }
        // Reach multiplier while under-tapped. 6× = 1200m, enough to see the
        // third/fourth patch on spread spawns. The bridge enumerator still
        // emits only ONE Node per tick (the closest out-of-reach patch), so
        // this widens vision without reopening the Node-spam problem.
        const float PHASE1_UNDERTAPPED_OUTER_MUL = 6f;
        // Must beat the sim's per-action "10s of foregone income" drag. Early
        // Phase 1 income is ~50-150/s, so 10s ≈ 1500 — these clear it with
        // margin without being Phase-2-sized.
        const float PHASE1_UNDERTAPPED_NODE_BONUS = 3000f;
        const float PHASE1_UNDERTAPPED_BC_BONUS   = 6000f;

        /// <summary>
        /// How many distinct resource patches are actually served by a BC —
        /// a patch counts once, no matter how many BCs sit near it. This is
        /// the number the Phase 1 objective drives toward.
        /// </summary>
        /// <summary>
        /// Same coverage rule the BC enumerator applies: a patch is served if
        /// it is the nearest active patch of some BC, or sits inside that BC's
        /// co-harvest radius.
        /// </summary>
        static bool IsPatchCovered(EcoState s, int p)
        {
            if (s == null || s.bcs == null || s.patches == null) return false;
            const float CO_HARVEST_RADIUS_SQ = 50f * 50f;
            for (int b = 0; b < s.bcs.Count; b++)
            {
                if (EcoSimulator.NearestActivePatchIdxPublic(s, s.bcs[b].pos) == p) return true;
                float dx = s.bcs[b].pos.x - s.patches[p].pos.x;
                float dz = s.bcs[b].pos.z - s.patches[p].pos.z;
                if (dx * dx + dz * dz < CO_HARVEST_RADIUS_SQ) return true;
            }
            return false;
        }

        static int CountTappedPatches(EcoState s)
        {
            if (s == null || s.bcs == null || s.patches == null) return 0;
            // Deliberately the SAME notion of "covered" the BC enumerator uses
            // below: nearest-active-patch of a BC, or inside its co-harvest
            // radius. Counting nearest-only would read a tight 4-patch cluster
            // served by one BC as a single tapped patch and push the planner
            // into needless expansion on cluster maps like IndustrialQuarter.
            const float CO_HARVEST_RADIUS_SQ = 50f * 50f;
            var seen = new HashSet<int>();
            for (int b = 0; b < s.bcs.Count; b++)
            {
                int p = EcoSimulator.NearestActivePatchIdxPublic(s, s.bcs[b].pos);
                if (p >= 0) seen.Add(p);
                for (int q = 0; q < s.patches.Count; q++)
                {
                    if (s.patches[q].remaining <= 0) continue;
                    float dx = s.bcs[b].pos.x - s.patches[q].pos.x;
                    float dz = s.bcs[b].pos.z - s.patches[q].pos.z;
                    if (dx * dx + dz * dz < CO_HARVEST_RADIUS_SQ) seen.Add(q);
                }
            }
            return seen.Count;
        }

        // Coverage radius: how far a BC's shrimps can reasonably reach a patch
        // and still contribute meaningful income. Past this we treat the BC as
        // NOT covering the patch (a new BC becomes a valid candidate).
        const float BC_COVERAGE_M = 200f;

        // Chain-reach radius — read from CD.MaximumBaseStructureDistance at
        // round start (see EcoSimulator.SetCostsFromCds). Vanilla ~200m,
        // modded Si_UnitBalance can override (user 2026-07-07: "modded 150m
        // build range of nodes"). ChainReach shim keeps call sites clean.
        static float CHAIN_REACH_M => EcoSimulator.CHAIN_REACH_M;

        // How far short of the chain limit a Node is aimed.
        //
        // Absolute metres, not a fraction of reach: the game's placement search
        // slides OUTWARD from our target looking for clear ground, and that
        // slide is a distance — it does not shrink because a mod configured a
        // shorter chain. The anchor has to HOLD; a Node landing outside the
        // chain costs full price, anchors nothing, and stalls every hop queued
        // behind it.
        //
        // Set from measurement, not guesswork, and always relative to the
        // MODDED reach so it follows whatever Si_UnitBalance configures:
        //
        //     hop = NODE_REACH_M - PLACEMENT_MARGIN_M
        //
        // At the current 150m Node reach that is 135m, matching the human
        // benchmark's median exactly. Evidence it is safe: v0.7.77 ran 120m
        // hops with 0 of 25 Nodes past the limit (the search lands close to
        // target, and the slides that do occur move laterally or backwards
        // rather than outward), and the human ran 157 Nodes at a 135m median
        // with a 146m maximum and no orphans either.
        //
        // The [PLACE] telemetry reports any hop that orphans, so a regression
        // here is loud rather than silent.
        static float PLACEMENT_MARGIN_M = 15f;
        // Never aim closer in than this fraction of reach, so a generous margin
        // on a short-reach map cannot collapse the hop to nothing.
        const float NODE_HOP_MIN_FRACTION = 0.45f;

        // How close we want a Bio Cache to sit to its patch.
        //
        // The placement rule turns out to be arithmetic. A BC must be within
        // BC_REACH_M of an anchor, so if the nearest anchor is further from the
        // patch than that, positions next to the patch are illegal and the
        // search walks the BC BACK toward the anchor until it fits:
        //
        //     BC-to-patch distance  ~=  anchorDist - BC_REACH_M
        //
        // Measured on Badlands 2026-07-29 and it predicts to the metre:
        //     anchor 272m -> BC landed  86m from the patch  (predicted 72m)
        //     anchor 325m -> BC landed 125m from the patch  (predicted 125m)
        //
        // 80m of extra travel each way is ~18s on a ~50s harvest cycle — about
        // 26% of that patch's throughput, for its whole life. A 200-cash Node
        // that moves the anchor close enough pays for itself many times over.
        //
        // So we do NOT skip the opening Node. We require an anchor near enough
        // that the BC can hug the patch, and let the Node enumerator bridge
        // toward any patch that fails the test. User 2026-07-29: "we need
        // opening node if it required to get the biotics closer. E.g. in
        // Badlands 1-2 nodes would have been enough."
        //
        // This is the human benchmark's opening exactly: ONE Node at 139m, then
        // both Bio Caches at 189m, each sitting 25m from its patch.
        // Measured against the BC's PLACEMENT reach, which is its chain reach
        // plus its own 37m physical radius (MaximumDistanceUseRadius is set) —
        // user 2026-07-29: "1-2 nodes would have been enough (+ build radius of
        // the biocache)". Effective 237m, so we bridge only past ~197m.
        // How far short of its patch a Bio Cache may end up and still count as
        // "tight". SIGN MATTERS and I had it backwards.
        //
        // If the anchor is D from the patch and the BC lands s short of the
        // patch (pushed toward the anchor), then anchor-to-BC is D - s. So the
        // condition for a legal, tight placement is:
        //
        //     D <= BcPlaceReachM + BC_TIGHT_GAP_M
        //
        // I had written `- BC_TIGHT_GAP_M`, which demanded the anchor be within
        // 169m of a patch and so bridged toward patches at 214m that a BC could
        // reach directly. Cost, measured on NarakaCity: BC #1 at 82s against the
        // human's 52s — a Node placed and then ~20s waiting for it to finish,
        // for nothing. Same 30s penalty on BC #2, and the Cysts queued behind
        // both of them landed ~50s late.
        //
        // The placements themselves confirm the game will hug when it can: ours
        // land 8-13m from their patches, tighter than the human's 13-29m.
        const float BC_TIGHT_GAP_M = 40f;

        /// <summary>
        /// Is there an anchor close enough that a Bio Cache for this patch can
        /// be placed tight against it, rather than dragged back toward the
        /// chain? Deliberately STRICTER than raw chain reach.
        /// </summary>
        /// <summary>
        /// PLANNING view: may a Bio Cache for this patch eventually be placed
        /// tight against it? Counts Nodes that are still building, because a
        /// sequence that lays a Node intends to wait for it.
        ///
        /// v0.7.88 made this require FINISHED anchors and stalled the planner
        /// completely — 0 structures built, seq=[] from t=26s. Inside the beam
        /// a just-placed Node is never finished yet, so [Node -> BC] could not
        /// form; a lone [Node] scores -200 against Noop's 0, so Noop won every
        /// tick, forever. Planning and firing need different tests.
        /// </summary>
        static bool IsPatchBcReachable(Vector3 patchPos, EcoState s) =>
            IsChainReachable(patchPos, s, EcoSimulator.BcPlaceReachM + BC_TIGHT_GAP_M,
                             unfinishedNodesAnchor: true);

        /// <summary>
        /// FIRING view: is the anchor actually BUILT right now? A Bio Cache
        /// placed off an unfinished Node falls back to a more distant finished
        /// anchor and lands far from its patch, so we hold it for the ~20s the
        /// Node needs and then place it hugging the biotics.
        /// </summary>
        static bool CanPlaceBcTightNow(Vector3 patchPos, EcoState s) =>
            IsChainReachable(patchPos, s, EcoSimulator.BcPlaceReachM + BC_TIGHT_GAP_M,
                             unfinishedNodesAnchor: false);

        /// <summary>Hop distance for one Node along the chain, margin applied.</summary>
        static float NodeHopDistance(float reach) =>
            Mathf.Max(reach - PLACEMENT_MARGIN_M, reach * NODE_HOP_MIN_FRACTION);

        // ---- Placement-slide measurement -----------------------------------
        // We ask the game to build at a point; ConstructionPlacement searches
        // outward and builds somewhere else. Nobody has ever measured by how
        // much. Every structure that appears is matched against the target we
        // fired for it, and the offset logged. Feeds PLACEMENT_MARGIN_M.
        internal static int   SlideSamples;
        internal static float SlideSumM, SlideMaxM;

        /// <summary>Called from the structure-spawn observer.</summary>
        internal static void NoteStructureSpawned(Team team, string name, Vector3 actual)
        {
            if (team == null || !_fired.TryGetValue(team, out var log)) return;
            ActionKind want = name == "Node" ? ActionKind.PlaceNode
                            : name == "Bio Cache" ? ActionKind.PlaceBc
                            : name == "Lesser Spawning Cyst" ? ActionKind.PlaceCyst
                            : ActionKind.Noop;
            if (want == ActionKind.Noop) return;

            int best = -1; float bestSq = 500f * 500f;   // generous — we want to catch big slides
            for (int i = 0; i < log.Count; i++)
            {
                if (log[i].kind != want) continue;
                if (Time.time - log[i].at > 60f) continue;   // stale
                float dx = log[i].pos.x - actual.x, dz = log[i].pos.z - actual.z;
                float d = dx * dx + dz * dz;
                if (d < bestSq) { bestSq = d; best = i; }
            }
            if (best < 0) return;
            float slide = Mathf.Sqrt(bestSq);
            SlideSamples++; SlideSumM += slide;
            if (slide > SlideMaxM) SlideMaxM = slide;
            if (slide > PLACEMENT_MARGIN_M)
                MelonLogger.Msg($"[PLACE] {name} slid {slide:F0}m past target " +
                                $"(margin={PLACEMENT_MARGIN_M:F0}m) — target=({log[best].pos.x:F0},{log[best].pos.z:F0}) " +
                                $"actual=({actual.x:F0},{actual.z:F0})");
        }

        internal static string BuildPlacementSummaryFragment()
        {
            if (SlideSamples == 0) return "";
            return "--- Placement slide (target -> actual) ---\n" +
                   $"  samples={SlideSamples} mean={SlideSumM / SlideSamples:F0}m max={SlideMaxM:F0}m " +
                   $"(margin in use: {PLACEMENT_MARGIN_M:F0}m)\n";
        }

        // Execution gating.
        //   - Only fires when EcoPlannerActive pref is on (default OFF: shadow mode)
        //   - Only when expected_gain > EXEC_MIN_GAIN (skip low-conviction actions)
        //   - REPEAT_SUPPRESS deduplicates same-kind fires within radius
        public static bool ExecutionEnabled;
        // Placement requests the Opener may issue per tick — see the note in
        // the fire loop. The game's search is one-at-a-time per team.
        // Placement searches resolve in 1-123ms and never failed across a
        // full round, so there is no reason to trickle requests.
        // Opening requests per tick. Placement resolves in 1-123ms with no
        // failures, so a whole site's BC+Cyst pair goes in one tick and a
        // four-site opening is fully requested inside two.
        const int   OPENER_STEPS_PER_TICK = 6;
        const int   EXEC_MIN_GAIN     = 100;
        const float REPEAT_SUPPRESS_M = 60f;
        // Suppress must outlast the build cycle. Structures take ~20s to
        // build; with a 15s suppress window the same target got proposed and
        // fired again just before the first one finished appearing in state.
        // 45s gives the placement 20s build + 20s buffer to appear in
        // team.Structures + 5s slack, so the beam sees it and stops proposing
        // duplicates.
        const float REPEAT_SUPPRESS_CYST_S = 6f;
        const float REPEAT_SUPPRESS_S = 45f;
        struct FiredAction { public ActionKind kind; public Vector3 pos; public float at; }
        static readonly Dictionary<Team, List<FiredAction>> _fired = new Dictionary<Team, List<FiredAction>>();
        // Why the fire loop declined each candidate this tick — read by PLAN/DIAG.
        static string _skipReasons = "";
        static void Skip(string why)
        {
            if (_skipReasons.Length < 120) _skipReasons += why + " ";
        }

        // ---- Beam search ----
        // Sequences of up to BEAM_DEPTH actions, keeping the top BEAM_WIDTH after
        // each expansion by score = grossEarned_at_horizon − Σ costs. With 15
        // typical candidates per level and MAX_CANDIDATES cap, per-plan cost is
        // ~40 sims × 300 steps × 60 patch-ops ≈ 720k ops. Fine at 5s cadence.
        //
        // INTER_ACTION_STEP_S is how much sim time passes between consecutive
        // action fires within a sequence. Matches PLAN_CADENCE_S because that's
        // how the planner would execute the sequence in real life (one action per
        // plan tick). Small enough that a 5-deep sequence covers 25s = about the
        // full opening.
        // Deeper beam is needed after the opening: expansion to a new patch
        // often needs Node(t=0) → wait for build(t=20) → BC(t=25) → wait for
        // build(t=45) → Cyst(t=50). That's 5 non-noops separated by 20s
        // waits — at INTER_ACTION_STEP_S=5s a depth of 8-10 is required to
        // see the full chain's payoff. Cost per plan tick scales linearly
        // with depth × width × avg-cands (~10) so 8×5×10 = 400 nodes/tick.
        // Depth 8 × step 10s = 80s of planning per beam node. Enough for a
        // Node → BC → Cyst expansion chain (Node builds 20s, BC builds 20s,
        // Cyst builds 20s = 60s) to prove positive gain within the horizon,
        // instead of getting pruned at depth 4 as a "spent 100 for nothing yet"
        // Node placement.
        // Beam depth 6 → 5 to further trim per-tick compute at late-game
        // structure counts. Combined with DT=2 + horizon=180 + cadence=8,
        // per-tick cost target < 500ms (was 1.4-3.7s).
        const int   BEAM_DEPTH             = 5;
        // Was 10; late-game with 200+ structures + 200+ shrimps each SearchNode
        // clones the full state and simulates 300s forward. Compute cost was
        // ~5s per plan tick, causing a visible freeze every 5s (user saw the
        // layer viewer counting 1s ticks then pausing 5s). Reduced to 6.
        const int   BEAM_WIDTH             = 6;
        const int   MAX_CANDIDATES_PER_LEVEL = 10;
        // 10s per beam-position (was 5s). Any expansion action spans a 20s
        // build cycle, so at 5s per position we had to spend 4 sequence slots
        // just to wait through a build — and by then the beam ran out of
        // depth. At 10s per position, 2 slots per build cycle: enough for a
        // Node → BC → Cyst chain to complete inside a depth-8 sequence.
        const float INTER_ACTION_STEP_S    = 10f;

        static readonly Dictionary<Team, float> _lastPlanAt = new Dictionary<Team, float>();

        // Async plan pipeline. Beam search is pure computation on immutable
        // snapshots — no game-object access — so we run it on a background
        // task. Main thread does the fast game-side work (state snapshot,
        // fire actions when result ready).
        //
        // per-team pending task. If completed, next MaybePlan on that team
        // consumes the result and fires. If not completed, we wait.
        class PlanRequest
        {
            public EcoState                       State;
            public PlanPhase                      Phase;
            public Perception.MapLayers.LayerB    Explored;   // snapshotted FoW
            public Team                           Team;
        }
        class PlanResult
        {
            public SearchNode           Winner;
            public List<SearchNode>     TopK;
            public float                BaselineScore;
            public float                Gain;
        }
        static readonly Dictionary<Team, System.Threading.Tasks.Task<PlanResult>> _pendingPlan
            = new Dictionary<Team, System.Threading.Tasks.Task<PlanResult>>();

        public enum ActionKind { Noop, PlaceBc, PlaceCyst, PlaceNode }

        public struct Candidate
        {
            public ActionKind kind;
            public Vector3    target;
            public int        cost;
            public float      score;      // predicted cash at horizon
            public int        patchIdx;   // -1 unless PlaceBc
            // For PlaceCyst: biotics-weighted handoff score of the target BC.
            // Used to scale PHASE2_CYST_BONUS so a Cyst on a "cluster" BC
            // (multiple rich patches nearby) gets full bonus, while a Cyst
            // on an isolated BC (one small patch) gets little — the beam
            // then chooses BC-only expansion there. User rule 2026-07-06:
            // "Lesser Cyst is HIGH VALUE on biotic clusters; not always
            // one Cyst per BC".
            public float      handoff;
            // Biotics this placement would pull inside chain reach for the
            // first time. NarakaCity is why this exists — see
            // PHASE2_UNLOCK_VALUE_FRACTION.
            public int        unlocks;
        }

        // Beam search node: a partial sequence with its resulting state.
        struct SearchNode
        {
            public EcoState        state;
            public List<Candidate> sequence;
            public int             totalCost;
            public float           score;   // grossEarned at horizon − Σ costs
        }

        public static void MaybePlan(Team team)
        {
            if (team == null) return;
            string tn = team.name ?? "";
            if (!tn.Contains("Alien")) return;   // alien-only planner

            // Round-active gate. Overnight we saw the planner keep firing
            // Node/BC placements for 30+ seconds after MissionState=ENDED
            // → UnitBalance's watchdog flagged "Round transition stalled"
            // and the whole map rotation wedged.
            if (!TestHarnessNs.TestHarness.IsRoundActive) return;

            // Human-commander opt-out. When a real player takes over as
            // commander of an alien team, IsCommanderEnabled goes false
            // (game disables the AI commander). Without this gate the
            // planner keeps firing placement orders on top of the human's
            // build actions — user observed "the AI commander overwrites
            // me" when they joined to play a benchmark round.
            try { if (!Silica.AI.AIManager.IsCommanderEnabled(team)) return; }
            catch { /* if the API throws, fail open — behave as before */ }

            // Startup delay: wait ~20s after the round begins before
            // planning. Starter units (shrimps, initial cash) spawn in
            // during the first ~15s and a plan tick that fires at t=5s
            // sees zero shrimps → picks a sequence around the pre-spawn
            // state, which then goes stale as reality catches up. Kicking
            // in at t=20s means our first plan operates on the actual
            // starting state (starter shrimps present, cash granted).
            if (Time.time < PLANNER_STARTUP_DELAY_S) return;

            // 1) Consume any pending async plan result for this team first.
            //    Firing must be main-thread because ConstructionPlacement +
            //    Structure.Construct are game-engine calls.
            if (_pendingPlan.TryGetValue(team, out var pending) && pending != null && pending.IsCompleted)
            {
                _pendingPlan[team] = null;
                if (pending.IsFaulted)
                {
                    MelonLogger.Warning("[PLAN] async beam threw: " + pending.Exception?.GetBaseException().Message);
                    return;
                }
                var result = pending.Result;
                ApplyPlanResult(team, result);
                return;
            }
            // If a plan is still running, don't start another. Waiting one more
            // main-thread tick to consume the result is fine.
            if (pending != null && !pending.IsCompleted) return;

            // 2) Cadence gate — how often do we KICK OFF a new plan.
            float now = Time.time;
            if (_lastPlanAt.TryGetValue(team, out var last) && now - last < PLAN_CADENCE_S) return;
            _lastPlanAt[team] = now;

            // 3) Snapshot phase — all game-object reads happen here, on the
            //    main thread, quickly. Result feeds the background beam.
            EcoState state;
            try { state = EcoStateBuilder.Build(team); }
            catch (System.Exception ex) { MelonLogger.Warning("[PLAN] Build threw: " + ex.Message); return; }

            _currentTeam  = team;
            var phase = ComputePhase(team, state);
            _currentPhase = phase;
            // ALSO set the ThreadStatic _beamPhase on the main thread — this
            // is the same thread where ApplyPlanResult later runs the escape
            // hatch's EnumerateActions. Without this, main-thread reads of
            // _beamPhase see the ThreadStatic default (Phase1_BaseEco) and
            // apply Phase 1 outerMul=2×reach to the Node enumeration —
            // which killed candidate discovery late-game when everything
            // within 400m of any anchor was already reachable.
            _beamPhase = phase;
            _beamUnderTapped = CountTappedPatches(state) < PHASE1_MIN_TAPPED_PATCHES;

            // Snapshot FoW layer on the main thread — GetExplored iterates
            // team.Structures/Units which is NOT thread-safe.
            Perception.MapLayers.LayerB explored = null;
            try { explored = Perception.MapLayers.FoWLayers.GetExplored(team); }
            catch { }

            // 4) Kick off the beam on a worker thread. State/phase/FoW are
            //    passed as captured locals — no shared mutable game state
            //    from here down.
            var req = new PlanRequest { State = state, Phase = phase, Explored = explored, Team = team };
            _pendingPlan[team] = System.Threading.Tasks.Task.Run(() => RunBeamAsync(req));
        }

        // Pure-computation beam search that runs off the main thread. Does NOT
        // touch team.Structures / team.Units / ResourceArea — only the
        // snapshotted EcoState it was handed.
        static PlanResult RunBeamAsync(PlanRequest req)
        {
            // NOTE: BeamSearch + EnumerateActions read _currentPhase / _currentTeam
            // statics. To keep this thread-safe with concurrent beams for
            // multiple teams (uncommon but possible), we thread-static the
            // phase/team/FoW here and BeamSearch will read them.
            _beamPhase    = req.Phase;
            _beamTeam     = req.Team;
            _beamExplored = req.Explored;
            _beamUnderTapped = CountTappedPatches(req.State) < PHASE1_MIN_TAPPED_PATCHES;

            var best = BeamSearch(req.State, out var topK);

            // Baseline — score of "do nothing" over the horizon, for the log.
            var baseline = req.State.Clone();
            EcoSimulator.SimulateForward(baseline, PLAN_HORIZON_S);
            float baselineScore = baseline.grossEarned;
            float gain = best.score - baselineScore;

            return new PlanResult { Winner = best, TopK = topK, BaselineScore = baselineScore, Gain = gain };
        }

        // Thread-static context for the currently-running beam. Set by
        // RunBeamAsync, read by BeamSearch/EnumerateActions/ScoreNode.
        // ThreadStatic ensures two team beams running concurrently don't
        // clobber each other.
        [System.ThreadStatic] static PlanPhase                      _beamPhase;
        // Snapshotted from the ROOT state of this plan tick, not from the
        // evolving beam state — the bonus must reflect "we are short on
        // biotics right now", otherwise it would evaporate the moment a
        // sequence's own first BC pushes the count to target.
        [System.ThreadStatic] static bool                           _beamUnderTapped;
        [System.ThreadStatic] static Team                           _beamTeam;
        [System.ThreadStatic] static Perception.MapLayers.LayerB    _beamExplored;

        // Called back on the main thread to apply the completed plan.
        static void ApplyPlanResult(Team team, PlanResult result)
        {
            string tn = team.name ?? "";
            // Rebuild the live state so the fire-time chain-reach / prereq
            // checks see the CURRENT world (the beam ran on a snapshot that
            // is now at least cadence-seconds old).
            EcoState state;
            try { state = EcoStateBuilder.Build(team); }
            catch (System.Exception ex) { MelonLogger.Warning("[PLAN] Build threw at apply: " + ex.Message); return; }

            _currentTeam  = team;
            try { MapProfile.MaybeBuild(team, Perception.MapLayers.GridWorld.CurrentMapName); } catch { }
            try { OpenerPlanner.MaybePlan(state); } catch { }
            _currentPhase = ComputePhase(team, state);
            // Sync ThreadStatic _beamPhase on the main thread (see comment in
            // MaybePlan). The escape hatch below calls EnumerateActions and
            // it reads _beamPhase to choose outerMul / bonuses.
            _beamPhase = _currentPhase;
            _beamUnderTapped = CountTappedPatches(state) < PHASE1_MIN_TAPPED_PATCHES;
            // Rebuild FoW here for the main-thread escape hatch. Safe to
            // call on main thread since GetExplored reads live team.Structures.
            try { _beamExplored = Perception.MapLayers.FoWLayers.GetExplored(team); }
            catch { _beamExplored = null; }
            var best = result.Winner;
            var topSequences = result.TopK;
            float baselineScore = result.BaselineScore;
            float gain = result.Gain;

            // Fire from the WINNING sequence first (in order), then from each
            // runner-up in the top-K frontier. Runner-ups typically diverge on
            // the first Node direction (east vs west vs south), so this yields
            // multi-directional expansion: instead of committing all-east this
            // tick and all-west next tick, we fire east-Node + west-Node in the
            // same tick when both look profitable.
            //
            // Positional dedup in TryFire handles the case where two sequences
            // both propose the same target — we only fire once.
            int fired = 0;
            int firedCash = 0;
            if (ExecutionEnabled && gain >= EXEC_MIN_GAIN)
            {
                // Subtract any cash TechPlanner (or future sub-planners) has
                // reserved for their own pending fires. Prevents eco from
                // spending money that tech is holding for the next Cortex —
                // user calibration: "the money for the biocache to the west
                // could have saved up to build the cortex earlier".
                int reserved = 0;
                try { reserved = MoneyBroker.GetReservedCash(team); } catch { }
                int cashLeft = System.Math.Max(0, state.cash - reserved);

                // Under the minimum-biotics target the opening wants ONE
                // focused chain, not breadth. Two behaviours are suspended
                // until the target is met — see the Naraka post-mortem on
                // PHASE1_MIN_TAPPED_PATCHES.
                bool underTappedNow = CountTappedPatches(state) < PHASE1_MIN_TAPPED_PATCHES;

                // Cyst-first reserve. The human benchmark on NarakaCity had
                // THREE Cysts up by t=128s; our v0.7.63 round did not get its
                // third until t=457s and finished the round on 64 shrimps
                // against the human's 135 (55/s income vs 258/s). Cysts are
                // where shrimps come from, so a late third Cyst caps the whole
                // economy no matter how much else gets built.
                //
                // The mechanism that starved it: Nodes cost 100 and fire on
                // whatever cash is lying around, so the 1500 for a Cyst never
                // accumulated. Until PHASE1_MIN_CYSTS real Cysts exist, a Node
                // may only spend cash that a Cyst would not want.
                // Only reserve for a Cyst the plan actually contains — see the
                // deadlock note on frontierCystWanted below.
                bool seqHasCyst = false;
                for (int i = 0; i < best.sequence.Count; i++)
                    if (best.sequence[i].kind == ActionKind.PlaceCyst) { seqHasCyst = true; break; }

                int realCysts = 0;
                for (int ci = 0; ci < state.cysts.Count; ci++)
                    if (state.cysts[ci].pos != state.nestPos) realCysts++;
                bool cystHungry = _currentPhase != PlanPhase.Phase2_Expand
                               && realCysts < PHASE1_MIN_CYSTS
                               && seqHasCyst;

                // Frontier Cyst reserve. If we already own a BC that is far
                // from every shrimp source, the next 1500 is worth more as a
                // Cyst there than as another bare BC further out — the BC we
                // have cannot work its patch without one. Hold the cash.
                // Holding cash for a Cyst that never gets enumerated is a
                // deadlock: the floor blocks every BC and Node, nothing is
                // built, so the condition that raised the floor never clears.
                // Citadel on v0.7.92 held 74% of its ticks and finished 548s
                // with 5 BCs. Hence the seqHasCyst guard.
                bool frontierCystWanted = false;
                for (int b = 0; b < state.bcs.Count && !frontierCystWanted && seqHasCyst; b++)
                {
                    if (!state.bcs[b].finished) continue;
                    float nearest = float.MaxValue;
                    for (int c = 0; c < state.cysts.Count; c++)
                    {
                        var d = state.cysts[c].pos - state.bcs[b].pos;
                        float dd = d.x * d.x + d.z * d.z;
                        if (dd < nearest) nearest = dd;
                    }
                    if (nearest < float.MaxValue
                        && Mathf.Sqrt(nearest) > CYST_REMOTE_FAR_M) frontierCystWanted = true;
                }

                // Return values:
                //   true  = "keep going through the sequence" (fired, skipped
                //           Noop, cost-skipped, or dedup collision)
                //   false = "stop — this action is a hard blocker (chain-reach
                //           or missing BC prereq means later actions in the
                //           sequence depend on this one being complete)"
                //
                // Cost is now SKIP not ABORT. A [B(500) N(100)] sequence with
                // cash=200 previously broke on B; now it skips B and still
                // fires N. Since Cyst-follows-BC uses HasFinishedBcNear against
                // LIVE state (not sequence state), a skipped BC will still
                // block its dependent Cyst — safety preserved.
                // A Bio Cache is a chain anchor as well as an income source —
                // and a BETTER one than a Node: 237m reach against ~159m.
                //
                // So once a BC fires this tick, stop firing Nodes behind it.
                // Nodes that come BEFORE the BC in the sequence are the ones
                // that make the BC placeable and still go in; Nodes that would
                // extend PAST it are anchored on the old chain, which buys a
                // short hop from a worse position. Waiting one plan tick lets
                // them hop 237m off the new BC instead.
                //
                // User 2026-07-29 on the Naraka opening: "the 2 nodes towards
                // the biocaches are built at the same time with the biocache
                // ... more helpful to wait until the biocache is built and use
                // this as anchor point ... so biocache has a dual use here."
                //
                // Costs nothing in tempo: since v0.7.75 an in-progress
                // structure already anchors, so the deferred Node fires on the
                // very next tick without waiting out the BC's build.
                bool firedBcThisTick = false;
                // Set while the Opener's committed plan is firing. The two
                // pacing rules below exist to stop the BEAM committing cash to
                // a shape it has not seen land yet; the Opener's chain was
                // validated as a whole, so applying them there just throttles
                // an already-vetted plan.
                //
                // Measured cost of not exempting it: "step 5 Node refused:
                // afterBc, step 6 Node refused: afterBc, step 7 Bc refused:
                // bcAnchor, step 8 Cyst refused: cystNoBc" — the block cascades
                // from nodes to the BC that needs them to the Cyst that needs
                // the BC, and one-node-per-tick then spread four nodes over 32s.
                // That was the whole of the remaining Cyst delay.
                bool openerFiring = false;

                // Nodes go in ONE AT A TIME early on. The opening fired two in
                // the same tick (t=40 on NarakaCity), which commits 400 cash to
                // a shape the very next Node placement might have improved on —
                // and the tempo cost of serialising them is almost nothing,
                // because a plan tick is 8s against a Node's 20s build, so the
                // second one still starts before the first finishes.
                //
                // User 2026-07-29: "might limit building not too many nodes
                // simultaneously at the start (rather one by one), that saves a
                // bit resources and the time loss is minimal."
                //
                // Phase 2 allows a little more, since by then chains are long
                // and the frontier is far enough that serialising every hop
                // does start to cost real time.
                // Shrimp production outranks another Node. A Lesser Cyst that
                // cannot afford to queue a Shrimp is an idle 1500-cash
                // structure, and shrimps are the only thing that actually
                // earns — user rule 2026-07-29: "Lesser Cyst should always
                // have enough funds to queue shrimps, more important than
                // spending it on more nodes if there is a resource conflict."
                //
                // Reserve one Shrimp per real Cyst, capped so a large late
                // base does not lock expansion out entirely.
                int shrimpReserve = Mathf.Min(realCysts, 4) * EcoSimulator.SHRIMP_COST;

                _skipReasons = "";
                int nodeFiresThisTick = 0;
                int maxNodeFires = (_currentPhase == PlanPhase.Phase2_Expand && !underTappedNow) ? 2 : 1;

                bool TryFireAction(Candidate c)
                {
                    if (c.kind == ActionKind.Noop) return true;
                    if (!openerFiring)
                    {
                        if (c.kind == ActionKind.PlaceNode && firedBcThisTick) { Skip("afterBc"); return true; }
                        if (c.kind == ActionKind.PlaceNode && nodeFiresThisTick >= maxNodeFires) { Skip("nodeRate"); return true; }
                    }
                    if (c.cost > cashLeft) return true;   // skip this one, try next
                    // Never let a Node eat the cash the next Bio Cache needs.
                    // This is what pinned Naraka at 0 cash for five minutes:
                    // every 100 that trickled in went straight into another
                    // Node, so the 500 for the BC never accumulated.
                    if (c.kind == ActionKind.PlaceNode || c.kind == ActionKind.PlaceBc)
                    {
                        int floor = 0;
                        if (cystHungry || frontierCystWanted) floor = EcoSimulator.CYST_COST;
                        else if (underTappedNow && c.kind == ActionKind.PlaceNode) floor = EcoSimulator.BC_COST;
                        // Nodes never eat the shrimp-queue money.
                        if (c.kind == ActionKind.PlaceNode) floor = Mathf.Max(floor, shrimpReserve);
                        if (floor > 0 && cashLeft - c.cost < floor) { Skip("reserve" + floor); return true; }
                    }
                    // These two were ABORTS ("later actions depend on this one"),
                    // and they were killing whole ticks: one unreachable action
                    // at the head of the sequence and nothing else got a look.
                    // 214 held ticks across six rounds showed exactly this —
                    // seqLen=5, gain>100k, candidates available, nothing fired,
                    // and no skip recorded because neither path was traced.
                    //
                    // Every action re-checks its own prerequisites anyway, so
                    // skipping the blocked one and continuing is safe: a Cyst
                    // whose BC is missing still gets refused on its own test.
                    // A Cyst may follow its Bio Cache IN THE SAME TICK.
                    //
                    // `state` is the snapshot from the top of this plan tick, so
                    // a BC fired moments ago is not in it — and the game's
                    // placement search is async, so it will not appear in
                    // team.Structures for up to 8s either. Checking only the
                    // snapshot meant the Cyst was refused, the opening queue
                    // broke, and it waited several ticks. User: "the delay
                    // between first biocache finished and lesser cyst building
                    // is too large, it needs to be in a few second window."
                    //
                    // So also accept a BC we REQUESTED nearby just now.
                    // A Cyst needs a FINISHED Bio Cache — the game enforces it.
                    //
                    // v0.8.6 relaxed this to accept a merely REQUESTED BC, on the
                    // theory that the snapshot was just stale. It was not: the
                    // game silently refuses the Construct call, so the request
                    // logged as "built", the opening marked the step done, and no
                    // structure ever appeared. The Cyst only turned up ~25s later
                    // when the beam re-proposed it after the BC had completed.
                    // That accounted for the entire remaining Cyst delay.
                    // Both tests must pass: the snapshot must show a BC, AND
                    // enough time must have elapsed for it to actually be built.
                    // EcoState.finished comes from IsFunctional, which flips true
                    // ~1s after construction starts — so the snapshot alone lets
                    // a Cyst fire 30s early, the game silently refuses it, and
                    // the 45s dedup window then blocks the retry.
                    if (c.kind == ActionKind.PlaceCyst
                        && !(HasFinishedBcNear(state, c.target)
                             && Perception.BuildTimeline.BcCompleteNear(
                                    c.target, 90f, Perception.MapLayers.LayerReplay.CurrentRoundTime)))
                    { Skip("cystNoBc"); return true; }
                    // Hold a Bio Cache until its anchor is actually built —
                    // see CanPlaceBcTightNow. Returns true (skip, keep going)
                    // rather than false, so the rest of the sequence still runs.
                    if (c.kind == ActionKind.PlaceBc && !CanPlaceBcTightNow(c.target, state)) { Skip("bcAnchor"); return true; }
                    // Reach is PER STRUCTURE KIND. One shared CHAIN_REACH_M
                    // check here was wrong both ways: too strict for a Bio
                    // Cache (200m against its real 209m + the standoff it may
                    // land at) and too loose for a Node (200m against 150m).
                    //
                    // The strict half was doing real damage. Measured on the
                    // NarakaCity opening: BC candidates for the 214m and 216m
                    // patches were enumerated at 249m and then refused here at
                    // 200m, every tick — "skips: noReach x4" with 8800 cash
                    // idle. The first Bio Cache did not land until a Node
                    // happened to bring something inside 200m, 24s later, and
                    // the NORTHERN patch never became reachable at all. Hence
                    // both "first biocache too late" and "only the southern
                    // biocache is built" — one cause.
                    if (c.kind == ActionKind.PlaceBc
                        && !IsChainReachable(c.target, state,
                                EcoSimulator.BcPlaceReachM + BC_TIGHT_GAP_M))
                    { Skip("noReach"); return true; }
                    if (c.kind == ActionKind.PlaceNode
                        && !IsChainReachable(c.target, state,
                                EcoSimulator.NODE_REACH_M, unfinishedNodesAnchor: true))
                    { Skip("noReach"); return true; }
                    if (!TryFire(team, c)) { Skip("dedup"); return true; }
                    fired++;
                    firedCash += c.cost;
                    cashLeft -= c.cost;
                    if (c.kind == ActionKind.PlaceBc)   firedBcThisTick = true;
                    if (c.kind == ActionKind.PlaceNode) nodeFiresThisTick++;
                    return true;
                }

                // 1) Fire the winner in order. Break on prereq/reach hard-
                //    blockers (later actions depend on the blocked one).
                //    Cost blockers are skipped in-place so cheap later
                //    actions still fire.
                // OPENING FIRST. While the Opener's committed queue is live it
                // drives construction and the beam is bypassed entirely — the
                // whole point is that the opening is decided once, up front,
                // rather than re-derived every 8s from a 180s horizon that
                // cannot see past the first Cyst.
                //
                // Steps still pass the normal gates (reach, cash, dedup), so an
                // illegal step is skipped rather than forced. If the queue makes
                // no progress for QUEUE_STALL_LIMIT_S it abandons itself and the
                // beam takes over — no single step can hold the round.
                bool openerDrove = false;
                bool openerWaiting = false;
                if (OpenerPlanner.QueueActive)
                {
                    openerDrove = true;
                    // AT MOST ONE SITE'S WORTH PER TICK.
                    //
                    // The game's placement search handles one request at a time
                    // per team. A single request resolves in ~2s (BC requested
                    // t=48s, construction started t=50s), but firing four in one
                    // tick made the last wait 54s — which is why the first Cyst
                    // appeared long after its Bio Cache had finished building.
                    //
                    // Two per tick keeps a BC+Cyst pair together and lets each
                    // search resolve promptly. At an 8s cadence a four-site
                    // opening is fully requested within ~32s, and nothing queues
                    // behind another site's placements.
                    // Fire every outstanding step that is legal right now, in
                    // plan order but NOT strictly — a site whose Bio Cache is
                    // waiting on its Cyst must not hold up another site.
                    int stepsThisTick = 0;
                    openerFiring = true;
                    foreach (int si in System.Linq.Enumerable.ToList(OpenerPlanner.PendingSteps()))
                    {
                        if (stepsThisTick >= OPENER_STEPS_PER_TICK) break;
                        var st = OpenerPlanner.StepAt(si);

                        // Cyst steps belong to OpenerPlanner.TickFast, which
                        // retries at 1Hz until the structure exists. Firing them
                        // here as well would just re-add dedup entries.
                        if (st.Kind == OpenerPlanner.StepKind.Cyst) continue;

                        var c = new Candidate
                        {
                            kind = st.Kind == OpenerPlanner.StepKind.Node ? ActionKind.PlaceNode
                                 : ActionKind.PlaceBc,
                            target = st.Target,
                            cost = st.Cost,
                            patchIdx = -1,
                        };
                        int before = fired;
                        string skipsBefore = _skipReasons;
                        TryFireAction(c);
                        if (fired == before)
                        {
                            // Name the refusal. Four wrong fixes went into the
                            // Cyst-delay problem because this was invisible.
                            string why = _skipReasons.Length > skipsBefore.Length
                                ? _skipReasons.Substring(skipsBefore.Length).Trim() : "?";
                            MelonLogger.Msg($"[OPENER] step {si + 1}/{OpenerPlanner.StepCount} " +
                                            $"{st.Kind} at ({st.Target.x:F0},{st.Target.z:F0}) refused: {why}");
                            continue;
                        }
                        OpenerPlanner.MarkDone(si);
                        stepsThisTick++;
                    }
                    openerFiring = false;
                    if (fired == 0 && !openerWaiting) OpenerPlanner.NoteNoProgress(Time.time);
                }

                if (!openerDrove)
                {
                    foreach (var c in best.sequence)
                    {
                        if (!TryFireAction(c)) break;
                    }
                }

                // Every Bio Cache blocked for want of a finished anchor, and
                // nothing else fired? Then the thing to do is BUILD the anchor.
                // Observed live: "skips: bcAnchor x6" with 3600 cash idle and a
                // Node candidate sitting unused, because the winning sequence
                // was all BCs and the fire loop never reached a Node.
                if (!openerDrove && fired == 0 && _skipReasons.Contains("bcAnchor"))
                {
                    var fallback = EnumerateActions(state);
                    for (int i = 0; i < fallback.Count; i++)
                    {
                        if (fallback[i].kind != ActionKind.PlaceNode) continue;
                        if (TryFireAction(fallback[i]) && fired > 0) break;
                    }
                }

                // 2) Pick up multi-directional expansion from the runner-ups.
                //    Only consider each runner-up's FIRST non-Noop action —
                //    that's the immediate commit the sequence starts with,
                //    which by construction is what would be fired now if the
                //    beam had picked THIS sequence. Skip the winner (index 0)
                //    since we already fired its actions.
                if (topSequences != null && !underTappedNow && !openerDrove)
                {
                    for (int i = 1; i < topSequences.Count; i++)
                    {
                        var seq = topSequences[i].sequence;
                        for (int j = 0; j < seq.Count; j++)
                        {
                            var c = seq[j];
                            if (c.kind == ActionKind.Noop) continue;
                            TryFireAction(c);   // dedup handles same-target collisions
                            break;              // only the head of the runner-up
                        }
                    }
                }

                // 3) Beam-wait escape hatch. Whenever nothing fired — typically
                //    the beam's winner starts with Noops because it wants to
                //    WAIT for an in-progress structure to finish and unlock a
                //    better placement — force-fire any candidate that's
                //    immediately actionable. A suboptimal placement now
                //    almost always beats sitting idle while cash accumulates.
                //    (Previous version gated on `cash >= cap - 500`; that meant
                //    cash grew from 200 to 12k over 3 minutes without a single
                //    fire because the threshold was never reached mid-cash.)
                if (fired == 0)
                {
                    if (topSequences != null)
                    {
                        for (int i = 0; i < topSequences.Count && fired == 0; i++)
                        {
                            foreach (var c in topSequences[i].sequence)
                            {
                                if (c.kind == ActionKind.Noop) continue;
                                if (TryFireAction(c) && fired > 0) break;
                            }
                        }
                    }

                    // Deeper fallback — if EVERY top-K sequence's first
                    // non-Noop was blocked (all share a "wait for X" target),
                    // enumerate fresh candidates from the LIVE state and fire
                    // the highest-priority actionable one. BC > Cyst > Node.
                    if (fired == 0)
                    {
                        var cands = EnumerateActions(state);
                        int candBcs = 0, candCysts = 0, candNodes = 0;
                        foreach (var c in cands)
                        {
                            if (c.kind == ActionKind.PlaceBc)   candBcs++;
                            if (c.kind == ActionKind.PlaceCyst) candCysts++;
                            if (c.kind == ActionKind.PlaceNode) candNodes++;
                        }
                        cands.Sort((a, b) => KindPriority(a.kind).CompareTo(KindPriority(b.kind)));
                        foreach (var c in cands)
                        {
                            if (c.kind == ActionKind.Noop) continue;
                            if (TryFireAction(c) && fired > 0) break;
                        }
                        MelonLogger.Msg("[PLAN/ESCAPE] team=" + tn +
                                        " cash=" + state.cash + "/" + state.cap +
                                        " cands B=" + candBcs + " C=" + candCysts + " N=" + candNodes +
                                        " → fired=" + fired);
                    }
                }
            }

            // Keep Shrimp production topped up at every Cyst we own. When the
            // planner has ExecutionEnabled, AlienConstruction.HandleTick early-
            // returns to keep vanilla AI out of our cash pool — but that also
            // skipped the shrimp-producer piggyback that used to run there.
            // Cysts sat idle for 150s in the last test round with 3 BCs but 0
            // spawn queue depth. Re-attach here where we DO own the tick.
            try { Faction.AlienShrimpProducer.Tick(team); }
            catch (System.Exception ex) { MelonLogger.Warning("[PLAN] ShrimpProducer.Tick threw: " + ex.Message); }

            // Shrimp group allocation: keep every BC-anchored harvest group
            // near its capacity so no single patch piles up, and bleed groups
            // off their biotics BEFORE depletion so the vanilla "everyone
            // re-tasks at once" swarm never has a reason to fire.
            try { ShrimpGroupPlanner.MaybeRun(team); }
            catch (System.Exception ex) { MelonLogger.Warning("[PLAN] ShrimpGroupPlanner.MaybeRun threw: " + ex.Message); }

            // When the planner has money and plans nothing, say what the
            // enumerator actually offered — guessing at this from the outside
            // cost most of a session.
            // Fires whenever the planner HAS money and commits nothing —
            // whether the sequence was empty or every action got blocked at
            // fire time. v0.7.92 instrumented only the empty case and printed
            // nothing across 50 held Citadel ticks, which is why this exists.
            if (fired == 0 && state.cash >= EcoSimulator.BC_COST)
            {
                try
                {
                    var diag = EnumerateActions(state);
                    int nb = 0, nc = 0, nn = 0;
                    for (int i = 0; i < diag.Count; i++)
                    {
                        if (diag[i].kind == ActionKind.PlaceBc) nb++;
                        else if (diag[i].kind == ActionKind.PlaceCyst) nc++;
                        else if (diag[i].kind == ActionKind.PlaceNode) nn++;
                    }
                    MelonLogger.Msg($"[PLAN/DIAG] nothing fired, cash={state.cash} " +
                                    $"seqLen={best.sequence.Count} gain={gain:F0} " +
                                    $"candidates: BC={nb} Cyst={nc} Node={nn} | " +
                                    $"bcs={state.bcs.Count} cysts={state.cysts.Count} " +
                                    $"tapped={CountTappedPatches(state)}/{PHASE1_MIN_TAPPED_PATCHES} " +
                                    $"skips: {_skipReasons}");
                }
                catch (System.Exception ex) { MelonLogger.Warning("[PLAN/DIAG] threw: " + ex.Message); }
            }

            var lead = best.sequence.Count > 0 ? best.sequence[0] : new Candidate { kind = ActionKind.Noop };
            Si_RTS_AI.AppendToRound(
                "[PLAN] t=" + Time.time.ToString("F1") +
                " team=" + tn +
                " cash=" + state.cash + "/" + state.cap +
                " bcs=" + state.bcs.Count +
                " cysts=" + state.cysts.Count +
                " shrimps=" + state.totalShrimps +
                " patches_active=" + CountActivePatches(state) +
                " tapped=" + CountTappedPatches(state) + "/" + PHASE1_MIN_TAPPED_PATCHES +
                " phase=" + (_currentPhase == PlanPhase.Phase2_Expand ? "EXP" : "BASE") +
                " seq=" + FormatSequence(best.sequence) +
                " score=" + best.score.ToString("F0") +
                " baseline=" + baselineScore.ToString("F0") +
                " gain=" + gain.ToString("F0") +
                (ExecutionEnabled ? (" EXEC=" + (fired > 0 ? "fired×" + fired + "(" + firedCash + ")" : "held")) : ""));
        }

        static string FormatSequence(List<Candidate> seq)
        {
            if (seq == null || seq.Count == 0) return "[]";
            var sb = new System.Text.StringBuilder("[");
            for (int i = 0; i < seq.Count; i++)
            {
                if (i > 0) sb.Append("→");
                sb.Append(seq[i].kind == ActionKind.Noop  ? "-"
                       : seq[i].kind == ActionKind.PlaceBc  ? "B"
                       : seq[i].kind == ActionKind.PlaceCyst ? "C"
                       : "N");
            }
            sb.Append("]");
            return sb.ToString();
        }

        // Depletion handoff scoring: how much productive territory surrounds
        // this BC. A Cyst placed here produces shrimps who will walk to
        // whichever active patch they can reach — so a BC with 3 nearby
        // active patches has 3× the handoff window before its Cyst's
        // shrimps run out of work.
        //
        // Distance-weighted: patches within 200m (same-cell) count fully,
        // patches at 400m count half, etc. Beyond 600m contribute little
        /// <summary>
        /// Compute the radial-sector reserved patch indices for the current
        /// eco state. Returns null when disabled or no patches. When enabled,
        /// returns the set of patch indices that should be excluded from BC
        /// candidate enumeration — starter Nest shrimps get them instead.
        ///
        /// Model: partition 360° around Nest into RADIAL_SECTORS × 45° slices,
        /// take the closest patch per sector (within RADIAL_INNER_R_M). Sectors
        /// with no patches contribute nothing, so on lopsided maps (patches all
        /// in one direction) only a few patches are reserved.
        /// </summary>
        static HashSet<int> ComputeRadialReservedPatches(EcoState s)
        {
            if (!RadialReserveEnabled) return null;
            if (s.patches == null || s.patches.Count == 0) return null;

            // (bestDistSq, bestIdx) per sector — initialise to sentinel.
            var bestDist = new float[RADIAL_SECTORS];
            var bestIdx  = new int[RADIAL_SECTORS];
            for (int i = 0; i < RADIAL_SECTORS; i++) { bestDist[i] = float.MaxValue; bestIdx[i] = -1; }

            Vector3 nest = s.nestPos;
            for (int pi = 0; pi < s.patches.Count; pi++)
            {
                var patch = s.patches[pi];
                if (patch.remaining <= 0) continue;
                float dx = patch.pos.x - nest.x;
                float dz = patch.pos.z - nest.z;
                float dSq = dx * dx + dz * dz;
                if (dSq > RADIAL_INNER_R_SQ) continue;

                // Atan2 returns -π..π. Shift to 0..2π then bin into sectors.
                float angle = Mathf.Atan2(dz, dx);
                int sector = (int)((angle + Mathf.PI) / (2f * Mathf.PI) * RADIAL_SECTORS) % RADIAL_SECTORS;
                if (sector < 0) sector = 0;
                if (sector >= RADIAL_SECTORS) sector = RADIAL_SECTORS - 1;

                if (dSq < bestDist[sector])
                {
                    bestDist[sector] = dSq;
                    bestIdx[sector] = pi;
                }
            }

            var reserved = new HashSet<int>();
            for (int i = 0; i < RADIAL_SECTORS; i++)
                if (bestIdx[i] >= 0) reserved.Add(bestIdx[i]);

            // Log the reserved set once per round for post-hoc analysis. Runs
            // on the beam worker thread but MelonLogger is thread-safe.
            if (!_radialReserveLogged && reserved.Count > 0)
            {
                _radialReserveLogged = true;
                var sb = new System.Text.StringBuilder();
                sb.Append("[ECO/RADIAL] reserved ").Append(reserved.Count)
                  .Append(" patches (nest=").Append(nest.x.ToString("F0"))
                  .Append(',').Append(nest.z.ToString("F0")).Append("):");
                foreach (int idx in reserved)
                {
                    var pp = s.patches[idx].pos;
                    float dx = pp.x - nest.x, dz = pp.z - nest.z;
                    float d = Mathf.Sqrt(dx * dx + dz * dz);
                    sb.Append(" #").Append(idx)
                      .Append("(").Append(pp.x.ToString("F0")).Append(',').Append(pp.z.ToString("F0"))
                      .Append(") d=").Append(d.ToString("F0")).Append("m");
                }
                MelonLoader.MelonLogger.Msg(sb.ToString());
            }

            return reserved.Count > 0 ? reserved : null;
        }

        // because shrimps would prefer to walk somewhere closer.
        static float ComputeHandoffScore(EcoState s, Vector3 bcPos)
        {
            float total = 0f;
            for (int p = 0; p < s.patches.Count; p++)
            {
                if (s.patches[p].remaining <= 0) continue;
                float dx = s.patches[p].pos.x - bcPos.x;
                float dz = s.patches[p].pos.z - bcPos.z;
                float dSq = dx * dx + dz * dz;
                if (dSq > 600f * 600f) continue;
                float d = Mathf.Sqrt(dSq);
                float weight = 1f / (1f + d / 200f);   // 1.0 at 0m, 0.5 at 200m, 0.33 at 400m
                total += s.patches[p].remaining * weight;
            }
            return total;
        }

        static PlanPhase ComputePhase(Team team, EcoState state)
        {
            _phaseByTeam.TryGetValue(team, out var current);
            // Once in Phase 2, stay there. Base-eco characteristics don't
            // meaningfully return once you're at 4+ BCs.
            if (current == PlanPhase.Phase2_Expand) return current;

            // Time-based fallback: past t=180s (3:00) with 15+ shrimps we're
            // definitely past base-eco setup regardless of cash. Trigger
            // Phase 2 unconditionally so residual beam-stuck behaviour
            // doesn't cost more expansion time.
            bool timeFallbackHit =
                Time.time >= PHASE2_TIME_FALLBACK_S &&
                state.totalShrimps >= PHASE2_TIME_FALLBACK_MIN_SHRIMPS;

            if (!timeFallbackHit)
            {
                if (state.totalShrimps < PHASE2_MIN_SHRIMPS) return PlanPhase.Phase1_BaseEco;
                if (state.cash < PHASE2_MIN_CASH) return PlanPhase.Phase1_BaseEco;

                // Fire frequency in last 20s. Fewer than 2 fires means the
                // beam is stuck — Noop-heavy sequences winning because
                // absolute income gain from near-patch actions has flattened.
                int recentFires = 0;
                if (_fired.TryGetValue(team, out var log))
                {
                    float now = Time.time;
                    for (int i = 0; i < log.Count; i++)
                        if (now - log[i].at < 20f) recentFires++;
                }
                if (recentFires >= 2) return PlanPhase.Phase1_BaseEco;
            }

            _phaseByTeam[team] = PlanPhase.Phase2_Expand;
            MelonLogger.Msg("[PLAN/PHASE] team=" + (team.name ?? "?") +
                            " → PHASE2_EXPAND at t=" + Time.time.ToString("F0") +
                            "s (shrimps=" + state.totalShrimps + " cash=" + state.cash +
                            (timeFallbackHit ? " via=time-fallback" : " via=threshold") + ")");
            return PlanPhase.Phase2_Expand;
        }

        /// <summary>
        /// Is there a Bio Cache at this spot — BUILT OR BUILDING?
        ///
        /// Requiring FINISHED serialised the pair and left the BC idle. A Cyst
        /// takes 35s to build plus 15s for its first Shrimp; a BC takes 30s. So
        /// waiting for the BC before even starting the Cyst put the first local
        /// shrimp ~80s after the BC was placed — the BC sat there earning
        /// nothing for ~50s while shrimps walked in from the previous site.
        /// That is the north-to-south migration: it is not a relocation
        /// problem, it is a site with no local production.
        ///
        /// Started together, the Cyst's first Shrimp lands ~50s in and the BC
        /// is ready at 30s — so production begins almost as the BC opens.
        /// User 2026-07-29: "the cyst at the third biotics could have been
        /// placed earlier, even before the biocache itself, because the biotics
        /// needs shrimps first."
        /// </summary>
        /// <summary>
        /// Did we ask the game for a Bio Cache near here recently? Covers the
        /// window between firing the request and the structure showing up in a
        /// state snapshot, which is up to two plan ticks plus the async
        /// placement search.
        /// </summary>
        static bool BcRequestedNear(Team team, Vector3 target)
        {
            if (!_fired.TryGetValue(team, out var log)) return false;
            const float R2 = 90f * 90f;      // a little wider than the Cyst offset
            for (int i = 0; i < log.Count; i++)
            {
                if (log[i].kind != ActionKind.PlaceBc) continue;
                float dx = log[i].pos.x - target.x, dz = log[i].pos.z - target.z;
                if (dx * dx + dz * dz < R2) return true;
            }
            return false;
        }

        /// <summary>Is a FINISHED Cyst standing near this spot?</summary>
        static bool HasFinishedCystNear(EcoState s, Vector3 target)
        {
            const float R2 = 120f * 120f;    // covers the Cyst's sideways offset
            for (int i = 0; i < s.cysts.Count; i++)
            {
                if (!s.cysts[i].finished) continue;
                if (s.cysts[i].pos == s.nestPos) continue;   // the Nest is not a site Cyst
                float dx = s.cysts[i].pos.x - target.x, dz = s.cysts[i].pos.z - target.z;
                if (dx * dx + dz * dz < R2) return true;
            }
            return false;
        }

        static bool HasFinishedBcNear(EcoState s, Vector3 target)
        {
            // Cyst target sits ~25m off the BC, so this radius covers that
            // offset plus chain-reach slack.
            const float R2 = 60f * 60f;
            for (int i = 0; i < s.bcs.Count; i++)
            {
                float dx = s.bcs[i].pos.x - target.x, dz = s.bcs[i].pos.z - target.z;
                if (dx * dx + dz * dz < R2) return true;
            }
            return false;
        }

        // Move `patchPos` off the resource patch itself so a BC dropped here
        // won't collide with the game's "no build area" around biotics. Shift
        // `d` meters along the vector toward the nearest chain-linked structure
        // (so the BC ends up between the patch and the anchor — still within
        // 200m of both if the anchor is close enough). Fall back to a fixed
        // south-east nudge if no anchor is found.
        static Vector3 OffsetTargetTowardAnchor(Vector3 patchPos, EcoState s, float d)
        {
            Vector3 anchor = default; float bestDsq = float.MaxValue;
            void consider(Vector3 pos)
            {
                float dx = patchPos.x - pos.x, dz = patchPos.z - pos.z;
                float dsq = dx * dx + dz * dz;
                if (dsq < bestDsq) { bestDsq = dsq; anchor = pos; }
            }
            if (s.nestPos != Vector3.zero) consider(s.nestPos);
            for (int i = 0; i < s.bcs.Count;   i++) if (s.bcs[i].finished)   consider(s.bcs[i].pos);
            for (int i = 0; i < s.cysts.Count; i++) if (s.cysts[i].finished) consider(s.cysts[i].pos);
            for (int i = 0; i < s.nodes.Count; i++) consider(s.nodes[i].pos);
            if (bestDsq == float.MaxValue) return patchPos + new Vector3(d * 0.707f, 0, d * 0.707f);
            float len = Mathf.Sqrt(bestDsq);
            if (len < 1f) return patchPos + new Vector3(d, 0, 0);
            Vector3 dir = (anchor - patchPos) / len;   // unit vector patch → anchor
            return patchPos + dir * d;
        }

        // Move the Cyst target off the BC's exact position. Push perpendicular
        // to the BC → nearest-patch direction so the Cyst sits alongside the BC
        // rather than blocking the harvest path. If no active patch is
        // available, fall back to a due-east nudge.
        static Vector3 OffsetCystFromBc(Vector3 bcPos, EcoState s, float d)
        {
            int pIdx = EcoSimulator.NearestActivePatchIdxPublic(s, bcPos);
            if (pIdx < 0) return bcPos + new Vector3(d, 0, 0);
            Vector3 patchPos = s.patches[pIdx].pos;
            float dx = patchPos.x - bcPos.x, dz = patchPos.z - bcPos.z;
            float len = Mathf.Sqrt(dx * dx + dz * dz);
            if (len < 1f) return bcPos + new Vector3(d, 0, 0);
            // Perpendicular unit vector (rotate 90° in XZ plane).
            Vector3 perp = new Vector3(-dz / len, 0, dx / len);
            return bcPos + perp * d;
        }

        // How many top-scoring sequences from the beam's final frontier we
        // consider for firing. Set to BEAM_WIDTH so we get natural
        // diversification: the frontier's top sequences will diverge on their
        // first action (e.g. Node east vs Node west), and merging their
        // first-actions gives us parallel multi-directional expansion.
        const int TOP_K_TO_FIRE = 10;

        // Full beam search: expand the frontier BEAM_DEPTH times, keeping top
        // BEAM_WIDTH sequences at each level by their horizon-score. Returns
        // the winner (top scorer) via `best`, plus the top-K survivors of the
        // final frontier via `topK` for multi-directional fire.
        static SearchNode BeamSearch(EcoState initial, out List<SearchNode> topK)
        {
            var frontier = new List<SearchNode>
            {
                new SearchNode
                {
                    state     = initial.Clone(),
                    sequence  = new List<Candidate>(),
                    totalCost = 0,
                    score     = 0f,
                }
            };
            SearchNode best = frontier[0];
            ScoreNode(ref best);

            for (int depth = 0; depth < BEAM_DEPTH; depth++)
            {
                var next = new List<SearchNode>(frontier.Count * MAX_CANDIDATES_PER_LEVEL);

                foreach (var node in frontier)
                {
                    var cands = EnumerateActions(node.state);
                    // Per-kind cap so no single kind starves the others. Old
                    // "top-N sort by KindPriority" trim would trigger this
                    // bug: with 10+ BC candidates in Phase 2, all 10 slots
                    // filled with BCs and Cyst/Node candidates got zero
                    // representation → beam never picked Cysts → far BCs
                    // stayed uncysted despite being built. Keep top-6 of each
                    // kind (BC, Cyst, Node) + the single Noop = 19 max.
                    // Per-kind cap reduced from 6 to 4 as part of the late-
                    // game beam-cost cut. 4 BC + 4 Cyst + 4 Node + 1 Noop =
                    // 13 candidates per node × 6×6 beam = ~470 SearchNodes
                    // per tick, vs 1520 before. Freeze pause drops from ~5s
                    // to sub-second.
                    cands = TrimPerKind(cands, 4);

                    foreach (var c in cands)
                    {
                        var fork = node.state.Clone();
                        Apply(fork, c);
                        // Advance sim time so the next depth level's enumerator sees
                        // any constructions started here as under-way (finishes if
                        // build time has elapsed, still-building otherwise).
                        EcoSimulator.SimulateForward(fork, INTER_ACTION_STEP_S);

                        var newSeq = new List<Candidate>(node.sequence.Count + 1);
                        newSeq.AddRange(node.sequence);
                        newSeq.Add(c);

                        var child = new SearchNode
                        {
                            state     = fork,
                            sequence  = newSeq,
                            totalCost = node.totalCost + c.cost,
                        };
                        ScoreNode(ref child);
                        next.Add(child);

                        if (child.score > best.score) best = child;
                    }
                }

                if (next.Count == 0) break;
                next.Sort((a, b) => b.score.CompareTo(a.score));
                if (next.Count > BEAM_WIDTH) next.RemoveRange(BEAM_WIDTH, next.Count - BEAM_WIDTH);
                frontier = next;
            }

            // Also expose the top-K sequences from the final frontier for
            // multi-directional expansion — the winning single sequence only
            // commits to one direction per plan tick, but the frontier's #2 and
            // #3 sequences will typically extend the OTHER direction (Node east
            // vs west vs south). Merging their first actions gives us a
            // parallel N-way expansion volley that lands 30-60s of BC-build
            // time earlier than the sequential-per-tick alternative.
            topK = new List<SearchNode>(Mathf.Min(TOP_K_TO_FIRE, frontier.Count));
            for (int i = 0; i < frontier.Count && i < TOP_K_TO_FIRE; i++)
                topK.Add(frontier[i]);

            return best;
        }

        // Keep at most `perKind` candidates of each of PlaceBc / PlaceCyst,
        // plus 2×perKind for PlaceNode (because Node enum now produces two
        // classes: regular-Nodes-to-reach-a-patch AND scout-Nodes for the
        // 8 directional sectors — both classes need survival slots), plus
        // one Noop.
        static List<Candidate> TrimPerKind(List<Candidate> src, int perKind)
        {
            // Nodes get 3× the per-kind slots (was 2×) so both regular
            // expansion Nodes AND the 8 scout Nodes have room to compete
            // in the beam. Extra survival slots let Node-heavy sequences
            // form in Phase 2.
            int nodeKind = perKind * 3;
            var outList = new List<Candidate>(perKind * 2 + nodeKind + 1);
            int bc = 0, cy = 0, nd = 0;
            bool haveNoop = false;
            for (int i = 0; i < src.Count; i++)
            {
                var c = src[i];
                if (c.kind == ActionKind.Noop) { if (!haveNoop) { outList.Add(c); haveNoop = true; } continue; }
                if (c.kind == ActionKind.PlaceBc   && bc < perKind) { outList.Add(c); bc++; continue; }
                if (c.kind == ActionKind.PlaceCyst && cy < perKind) { outList.Add(c); cy++; continue; }
                if (c.kind == ActionKind.PlaceNode && nd < nodeKind) { outList.Add(c); nd++; continue; }
            }
            return outList;
        }

        // Ordering heuristic for pruning candidates before deep scoring.
        // Lower number = higher priority. BCs unlock new patch throughput and
        // are cheap (500); Cysts multiply throughput of existing BCs but are
        // expensive (1500); Nodes are enabling moves only. Noop last.
        static int KindPriority(ActionKind k)
        {
            if (k == ActionKind.PlaceBc)   return 0;
            if (k == ActionKind.PlaceCyst) return 1;
            if (k == ActionKind.PlaceNode) return 2;
            return 3;   // Noop
        }

        static void ScoreNode(ref SearchNode n)
        {
            var sim = n.state.Clone();
            float remaining = PLAN_HORIZON_S - sim.t;
            if (remaining > 0) EcoSimulator.SimulateForward(sim, remaining);
            n.score = sim.grossEarned - n.totalCost;

            // Phase 2 bonuses:
            //   Node +2000 — reach-extension pays off beyond horizon.
            //   Cyst +2000 — a Cyst on a far BC that has no local Cyst yet
            //                lets that BC's local patch be worked by its OWN
            //                shrimp production instead of relying on slow
            //                migration from central Cysts. The sim currently
            //                underprices this because its marginal-utility
            //                shrimp router sends new shrimps to whichever BC
            //                has highest N+1 income — often a central BC, not
            //                the Cyst's own BC. In reality shrimps go local.
            //                Explicit bonus makes the beam value uncysted-BC
            //                Cysts correctly.
            if (n.sequence != null)
            {
                int nodeCount = 0, bcCount = 0;
                float cystBonusSum = 0f;   // handoff-scaled Cyst bonus contribution
                int   cystCountFlat = 0;    // used for Phase 1 (unweighted)
                for (int i = 0; i < n.sequence.Count; i++)
                {
                    var kind = n.sequence[i].kind;
                    if (kind == ActionKind.PlaceNode) nodeCount++;
                    else if (kind == ActionKind.PlaceBc) bcCount++;
                    else if (kind == ActionKind.PlaceCyst)
                    {
                        cystCountFlat++;
                        // Cluster-aware scaling: full bonus only for high-handoff
                        // Cysts (rich patch cluster served by ONE Cyst).
                        // Isolated BCs get near-zero bonus so the beam prefers
                        // bare-BC expansion there (shrimps auto-migrate from
                        // neighbours per user's game-mechanic note).
                        // Reference handoff = a "solid cluster" of 3 patches
                        // × ~3000 remaining × 0.7 weight = ~6300. Below 2000
                        // it's an isolated single-patch BC, no bonus.
                        float h = n.sequence[i].handoff;
                        float scale = Mathf.Clamp01((h - HANDOFF_CYST_MIN) / (HANDOFF_CYST_FULL - HANDOFF_CYST_MIN));
                        cystBonusSum += scale * PHASE2_CYST_BONUS;
                    }
                }
                if (_beamPhase == PlanPhase.Phase2_Expand)
                {
                    int unlockSum = 0;
                    for (int i = 0; i < n.sequence.Count; i++) unlockSum += n.sequence[i].unlocks;
                    n.score += nodeCount * PHASE2_NODE_BONUS
                             + cystBonusSum
                             + bcCount   * PHASE2_BC_BONUS
                             + unlockSum * PHASE2_UNLOCK_VALUE_FRACTION;
                }
                else if (_beamUnderTapped)
                {
                    // Under the minimum-biotics target. A naked Node earns
                    // nothing inside the horizon, so without an explicit
                    // bonus the beam picks Noop and base eco stalls at
                    // whatever the spawn happened to hand us (2 patches on
                    // NarakaCity). Pay for the [Node → BC] chain directly.
                    // The Node bonus is paid ONLY when the sequence also
                    // commits to a BC. Unconditional, it made a bare [Node]
                    // outscore doing nothing every single tick, so the planner
                    // spent every spare 100 on another Node and never saved the
                    // 500 for the Bio Cache that actually earns. A Node is
                    // worth paying for as the first step of [Node -> BC];
                    // on its own it is just a hole in the wallet.
                    // Full Node bonus when the sequence commits to a BC as
                    // well; a PARTIAL one when it does not.
                    //
                    // Requiring a BC outright deadlocked the opening: when the
                    // next patch is two hops away the beam must find the whole
                    // [N->N->B] in one plan, and if it does not, a lone [N]
                    // scores -200 against Noop's 0 and NOTHING fires. Observed
                    // on NarakaCity: 2 BCs at t=130s and then seq=[] with 4000
                    // cash idle for the rest of the round.
                    //
                    // The partial bonus is below a Node's own cost-plus-drag,
                    // so it cannot justify aimless Nodes — but while we are
                    // under the minimum-biotics target it does let the chain
                    // advance one hop per tick until the BC comes into range.
                    float nodeBonus = bcCount > 0
                        ? PHASE1_UNDERTAPPED_NODE_BONUS
                        : PHASE1_UNDERTAPPED_NODE_BONUS * 0.5f;
                    n.score += nodeCount * nodeBonus
                             + bcCount   * PHASE1_UNDERTAPPED_BC_BONUS
                             + cystCountFlat * PHASE1_CYST_BONUS;
                }
                else
                {
                    // Phase 1: unscaled — early game we always want Cysts to
                    // build shrimps ramping.
                    n.score += bcCount * PHASE1_BC_BONUS
                             + cystCountFlat * PHASE1_CYST_BONUS;
                }
            }
        }

        // Fire the winning action via the game's own commander-build path —
        // ConstructionPlacement.QueueFirstValidPlacementAroundPoint → Structure.
        // Construct. That's the exact code path a human commander triggers when
        // they click to place a structure. It handles:
        //   * no-build zones (resource patches, structure clearance)
        //   * chain-reach validation against the anchor structure
        //   * cost deduction from Team.TotalResources (no manual drain needed)
        //   * grid snap
        // Previous approach used HelperMethods.SpawnAtLocation (raw admin
        // spawn) plus manual cost drain. That bypassed no-build zones — the
        // planner stacked BCs and Cysts directly on top of biotics patches,
        // something a human commander could not do — and required a shadow
        // cost accounting we now delete.
        //
        // Positional dedup still applies: the game's placement search is async
        // (up to 8s), so repeated plan ticks would otherwise queue multiple
        // searches for the same target while the first is still resolving.
        //
        // Returns true iff we successfully asked the game to build.
        static bool TryFire(Team team, Candidate c)
        {
            if (!_fired.TryGetValue(team, out var log)) { log = new List<FiredAction>(); _fired[team] = log; }

            for (int i = log.Count - 1; i >= 0; i--)
                if (Time.time - log[i].at > REPEAT_SUPPRESS_S) log.RemoveAt(i);

            // Cysts get a much shorter suppression window than everything else.
            //
            // The 45s window exists so an 8s async placement search is not
            // re-requested while pending. But the game can also refuse a
            // Construct SILENTLY (a Cyst whose Bio Cache is not finished), and
            // then 45s of lockout is applied to a structure that was never
            // placed. That produced the fixed "requested t=48s, built t=106s"
            // gap. Placement searches actually resolve in 1-123ms, so a short
            // window is ample.
            float suppressS = c.kind == ActionKind.PlaceCyst
                ? REPEAT_SUPPRESS_CYST_S : REPEAT_SUPPRESS_S;
            for (int i = 0; i < log.Count; i++)
            {
                if (log[i].kind != c.kind) continue;
                if (Time.time - log[i].at > suppressS) continue;
                float dx = log[i].pos.x - c.target.x, dz = log[i].pos.z - c.target.z;
                if (dx * dx + dz * dz < REPEAT_SUPPRESS_M * REPEAT_SUPPRESS_M) return false;
            }

            try
            {
                bool ok = Faction.AlienConstruction.TryBuildStructureForPlanner(team, c.kind, c.target);
                if (ok) log.Add(new FiredAction { kind = c.kind, pos = c.target, at = Time.time });
                MelonLogger.Msg("[PLAN/EXEC] team=" + team.name +
                                " requested " + c.kind +
                                " at (" + c.target.x.ToString("F0") + "," + c.target.z.ToString("F0") +
                                ") queued=" + ok);
                return ok;
            }
            catch (System.Exception ex)
            {
                MelonLogger.Warning("[PLAN/EXEC] TryBuildStructureForPlanner threw: " + ex.Message);
                return false;
            }
        }

        static int CountActivePatches(EcoState s)
        {
            int n = 0;
            for (int i = 0; i < s.patches.Count; i++) if (s.patches[i].remaining > 0) n++;
            return n;
        }

        // Iteration 1 enumerator — small, conservative candidate set:
        //   * Noop (baseline)
        //   * PlaceBc at every active patch not already covered by a BC ≤200m
        //   * PlaceCyst at every finished BC with <12 shrimps assigned
        // Cost gating: skip candidates the team can't afford right now.
        // (Node placement is planned but deferred to iteration 2 — needs chain-reach
        //  reachability check to be useful.)
        static List<Candidate> EnumerateActions(EcoState s)
        {
            var list = new List<Candidate>();
            list.Add(new Candidate { kind = ActionKind.Noop, cost = 0, patchIdx = -1 });

            // Compute whether there's an uncysted finished BC. If yes, we
            // SUPPRESS new BC enumeration so the beam has to consider
            // Cyst-on-uncysted-BC placements before spending on more BCs.
            //
            // Rationale (2026-07-05): human benchmark showed 32 BCs producing
            // 361k income. AI baseline showed 25 BCs producing 78k — same
            // shrimp count. Diagnostic: AI's shrimps concentrated on 4-5
            // patches out of 25 BCs' worth of coverage. Reason: only 4 BCs had
            // Cysts. The other 21 BCs stood idle without local shrimp
            // production. Refusing to enumerate more BCs while uncysted BCs
            // exist forces the beam to close that gap first.
            // Count BCs (finished OR in-progress) that lack a nearby Cyst.
            // Block new-BC enumeration only when the queue of uncysted BCs
            // reaches MAX_UNCYSTED_QUEUE — this lets 1-2 BCs be in-flight
            // while their Cysts are being placed, doubling BC placement rate
            // vs the strict "block if any uncysted" rule. Human benchmark
            // built ~1 BC per 30s; strict gate slowed us to ~1 per 55s.
            // Same coverage radius as the Cyst enumerator uses — 60m Phase 1,
            // 150m Phase 2 (one Cyst can serve multiple nearby BCs). Keeps
            // this gate consistent with the Cyst enumeration; otherwise
            // Phase 2 would report a BC as "uncysted" while the enumerator
            // wouldn't add a Cyst candidate for it → beam stall.
            float uncystedCoverRadius = _beamPhase == PlanPhase.Phase2_Expand
                ? EcoPlannerConfig.Phase2CystCoverageRadiusM : 60f;
            float uncystedCoverSq     = uncystedCoverRadius * uncystedCoverRadius;
            int uncystedBcCount = 0;
            for (int bi = 0; bi < s.bcs.Count; bi++)
            {
                bool cystNear = false;
                for (int ci = 0; ci < s.cysts.Count; ci++)
                {
                    if (s.cysts[ci].pos == s.nestPos) continue;
                    var d = s.cysts[ci].pos - s.bcs[bi].pos;
                    if (d.x * d.x + d.z * d.z < uncystedCoverSq) { cystNear = true; break; }
                }
                if (!cystNear) uncystedBcCount++;
            }
            // Once we're near the shrimp hard cap, adding more Cysts doesn't
            // add more shrimps — the cap is hit. New outer BCs don't need a
            // local Cyst; the ShrimpRelocator will migrate existing shrimps
            // to them. So relax the uncysted-BC gate at high shrimp count.
            // (Cysts still have military value for later Phase 3, but that's
            // separate from eco expansion.)
            // Phase-aware queue depth. Phase 1 stays disciplined (queue 2
            // uncysted BCs max) so eco doesn't get ahead of itself. Phase 2+
            // opens up (queue 4) so aggressive multi-BC expansion runs at
            // full tilt — user: "Phase 2/3 could be more aggressive".
            int MAX_UNCYSTED_QUEUE = _beamPhase == PlanPhase.Phase2_Expand
                ? EcoPlannerConfig.Phase2MaxUncystedBcQueue : 2;
            const int SHRIMP_NEAR_CAP    = 180;   // 200 hard cap - 20 buffer
            bool nearShrimpCap  = s.totalShrimps >= SHRIMP_NEAR_CAP;
            bool anyUncystedBc  = !nearShrimpCap && uncystedBcCount >= MAX_UNCYSTED_QUEUE;

            // Fog-of-war filter for BC candidates. Fetch the team's explored
            // layer once — it's a cheap grid sample. Skip patches whose cell
            // hasn't been explored yet; the game rejects BC placement on
            // undiscovered terrain, so proposing those wastes an 8s
            // ConstructionPlacement search on something guaranteed to fail.
            // Nodes remain unfiltered — they act as scouts, revealing new
            // territory on completion, which makes previously-unseen patches
            // eligible next tick.
            // Use the snapshotted FoW layer built on the main thread. This
            // runs on the async beam worker — calling GetExplored here would
            // iterate team.Structures which is NOT thread-safe.
            Perception.MapLayers.LayerB explored = _beamExplored;
            bool PatchExplored(Vector3 p)
            {
                if (explored == null) return true;   // fail open
                int cx = Perception.MapLayers.GridWorld.CellX(p.x);
                int cz = Perception.MapLayers.GridWorld.CellZ(p.z);
                return explored.IsSet(cx, cz);
            }

            // BC candidates — GATED on the uncysted-BC check above.
            // Radial reserve: patches earmarked for starter Nest shrimps
            // (closest patch per angular sector within 700m of Nest). We use
            // a SOFT reserve — non-reserved candidates are preferred, but if
            // no non-reserved candidate qualifies (bootstrap moment: only the
            // close reserved patches are chain-reachable), the reserved
            // candidates fall back in so eco can bootstrap. Once Nodes extend
            // reach to non-reserved patches, the reserve reasserts and pushes
            // expansion outward.
            HashSet<int> radialReserved = ComputeRadialReservedPatches(s);
            var reservedFallback = radialReserved != null ? new List<Candidate>() : null;
            int preAddCount = list.Count;
            if (s.cash >= EcoSimulator.BC_COST && !anyUncystedBc)
            {
                for (int p = 0; p < s.patches.Count; p++)
                {
                    bool isReserved = radialReserved != null && radialReserved.Contains(p);
                    var patch = s.patches[p];
                    if (patch.remaining <= 0) continue;
                    if (!PatchExplored(patch.pos)) continue;   // FoW filter
                    // A patch is "covered" only if it is the CLOSEST active patch
                    // to some existing BC — that BC's shrimps will harvest it.
                    // The previous "any BC within 200m" check was too aggressive:
                    // on NorthPolarCap, the (1289, 273) patch sat 179m from BC0
                    // at (1406, 409) but BC0 already had a patch at 0m (its own
                    // position), so BC0 harvested THAT one and the (1289, 273)
                    // patch went untapped for the entire round. Nearest-patch
                    // uniqueness is the correct notion of "already served".
                    // Coverage has TWO conditions — patch is uncovered only if BOTH fail:
                    //   1) it's the nearest active patch to some existing BC (that BC's
                    //      shrimps will primarily harvest it)
                    //   2) it's within CO_HARVEST_RADIUS_M of some existing BC (that BC's
                    //      shrimps effectively rotate among tightly-clustered nearby patches)
                    //
                    // Condition 2 was added for IndustrialQuarter-style maps: biotics
                    // clustered in 4-patch groups within 30m of each other. Without it,
                    // one cluster gets 4 BCs built on it (each patch #2-#4 flagged as
                    // "uncovered" because they're not the SINGLE nearest to the first BC).
                    // With it, one BC covers the whole cluster.
                    //
                    // 80m radius = tight enough that a BC on NorthPolarCap (patches
                    // 200m+ apart) still gets its own BC per patch, but IQ's tight
                    // clusters are recognized as co-covered by one BC.
                    bool covered = false;
                    for (int b = 0; b < s.bcs.Count; b++)
                    {
                        int nearest = EcoSimulator.NearestActivePatchIdxPublic(s, s.bcs[b].pos);
                        if (nearest == p) { covered = true; break; }
                    }
                    if (!covered)
                    {
                        // Tightened from 80m to 50m: 80m was killing legit BC
                        // candidates near patch clusters on medium-density maps
                        // like GreatErg (128 patches spread across the map).
                        // 50m still catches IQ's ~30m tight 4-biotic groups
                        // but leaves 60-100m spaced patches alone (they get
                        // their own BC).
                        const float CO_HARVEST_RADIUS_SQ = 50f * 50f;
                        for (int b = 0; b < s.bcs.Count; b++)
                        {
                            float dx = s.bcs[b].pos.x - patch.pos.x;
                            float dz = s.bcs[b].pos.z - patch.pos.z;
                            if (dx * dx + dz * dz < CO_HARVEST_RADIUS_SQ) { covered = true; break; }
                        }
                    }
                    if (covered) continue;
                    if (!IsPatchBcReachable(patch.pos, s)) continue;   // must chain-link to Nest/Node graph

                    // Target the patch center — ConstructionPlacement will search
                    // outward for the first legally-clear spot, respecting the
                    // patch's no-build zone. No manual offset needed; the game
                    // places the BC on the boundary of that zone the same way
                    // AlienConstruction's phased placer has been doing.
                    // Aim ADJACENT to the patch, nudged toward the anchor.
                    //
                    // Targeting the bare patch centre leaves the direction of
                    // the game's outward search unconstrained, so it settles
                    // wherever it first finds legal ground — which repeatedly
                    // put the BC far out even when the anchor would have
                    // permitted a tight placement (NarakaCity BC #3 landed
                    // 163m from the biotics while a Node 6s earlier sat 59m
                    // from one). Offsetting toward the anchor by roughly the
                    // BC's own radius biases the search into the side where
                    // legality actually lives, so it lands hugging the patch.
                    //
                    // User rule 2026-07-29: "Biocache as close as possible
                    // towards a biotics, in particular at Phase 1."
                    Vector3 bcTarget = OffsetTargetTowardAnchor(
                        patch.pos, s, EcoSimulator.BC_RADIUS_M);
                    var cand = new Candidate
                    {
                        kind = ActionKind.PlaceBc, target = bcTarget,
                        cost = EcoSimulator.BC_COST, patchIdx = p,
                        unlocks = CountUnlockedPatches(s, patch.pos),
                    };
                    if (isReserved) reservedFallback.Add(cand);
                    else            list.Add(cand);
                }
                // Bootstrap fallback: if the soft reserve blocked ALL BC
                // candidates this tick, unblock the reserved patches so the
                // planner can at least place SOMETHING. This handles the
                // t=0 case where only close (reserved) patches are within
                // chain reach — without the fallback, eco stalls forever.
                if (list.Count == preAddCount && reservedFallback != null && reservedFallback.Count > 0)
                {
                    list.AddRange(reservedFallback);
                }
            }

            // Cyst candidates with DEPLETION HANDOFF ranking.
            //
            // Old behavior: iterate BCs in order, add Cyst candidate for any BC
            // that has <12 shrimps and no nearby Cyst. Beam picks whichever
            // scores highest.
            //
            // Problem: without ranking, the beam gets identical Cyst candidates
            // at every uncysted BC. It picks one arbitrarily (usually the
            // first — the earliest-placed BC). But the OPTIMAL Cyst placement
            // is at a BC positioned near RICH TERRITORY (many productive
            // nearby patches). When the local patch eventually depletes, the
            // Cyst's shrimps naturally walk to a neighboring productive patch
            // rather than scattering or migrating from far away.
            //
            // Handoff score = sum of nearby patch remaining, distance-weighted.
            // BCs surrounded by rich territory rank first, so their Cysts get
            // picked before Cysts at BCs whose only patch is nearly depleted.
            if (s.cash >= EcoSimulator.CYST_COST)
            {
                var eligibleBcs = new List<(int idx, float handoff)>();
                for (int b = 0; b < s.bcs.Count; b++)
                {
                    var bc = s.bcs[b];
                    // Under-construction BCs count — the Cyst should be going
                    // up alongside, not queued behind. See HasFinishedBcNear.
                    s.shrimpsPerBc.TryGetValue(b, out int cur);
                    if (cur >= 12) continue;
                    // Coverage radius — a BC within this distance of an
                    // existing Cyst gets its shrimp supply from that Cyst
                    // via migration, no new Cyst needed. Phase 2 configurable
                    // via [Si_RTS_AI_EcoPlanner] Phase2CystCoverageRadiusM
                    // (default 250 = cluster approach; 150 = previous approach).
                    // Phase 1 stays 60m for tight starter setup.
                    float cystCoverageRadius = _beamPhase == PlanPhase.Phase2_Expand
                        ? EcoPlannerConfig.Phase2CystCoverageRadiusM : 60f;
                    float cystCoverageSq     = cystCoverageRadius * cystCoverageRadius;
                    bool nearbyCyst = false;
                    for (int c = 0; c < s.cysts.Count; c++)
                    {
                        if (s.cysts[c].pos == s.nestPos) continue;
                        var d = s.cysts[c].pos - bc.pos;
                        if (d.x * d.x + d.z * d.z < cystCoverageSq) { nearbyCyst = true; break; }
                    }
                    if (nearbyCyst) continue;
                    // The opening owns its own Cyst placements — don't let the
                    // beam propose a second one at the same Bio Cache while the
                    // opener still has that step outstanding.
                    if (OpenerPlanner.HasPendingCystNear(bc.pos, 120f)) continue;

                    // How far is this BC from the nearest shrimp SOURCE? A BC
                    // out at the 3rd or 4th biotic has no local production and
                    // no neighbours close enough to migrate over — every shrimp
                    // it ever uses has to walk the whole way from the Nest.
                    float distToCystM = float.MaxValue;
                    for (int c = 0; c < s.cysts.Count; c++)
                    {
                        var d = s.cysts[c].pos - bc.pos;       // Nest counts: it spawns too
                        float dd = d.x * d.x + d.z * d.z;
                        if (dd < distToCystM) distToCystM = dd;
                    }
                    distToCystM = distToCystM < float.MaxValue ? Mathf.Sqrt(distToCystM) : 0f;
                    float remoteScale = Mathf.Clamp01(
                        (distToCystM - CYST_REMOTE_NEAR_M) / (CYST_REMOTE_FAR_M - CYST_REMOTE_NEAR_M));

                    float handoff = ComputeHandoffScore(s, bc.pos);
                    // Phase 2 hard-skip: don't even emit a Cyst candidate at
                    // an isolated BC. Scale-based bonus alone left a tiny
                    // positive score so the beam still paired 1:1. User rule
                    // 2026-07-06: "not always one lesser per bio ... if this
                    // bio is good reachable by already working shrimps".
                    // Phase 1 still emits everywhere — starter setup needs
                    // Cysts at all BCs even if handoff is modest.
                    // The isolation skip is about RICHNESS — "a lone patch will
                    // be serviced by shrimps migrating in from next door". That
                    // reasoning needs neighbours within walking distance. At the
                    // frontier there are none, and the skip left expansion BCs
                    // sitting there with no shrimp supply at all. Remoteness
                    // overrides it.
                    if (_beamPhase == PlanPhase.Phase2_Expand
                        && handoff < HANDOFF_CYST_MIN
                        && remoteScale <= 0f) continue;
                    // Rank by whichever case is stronger: rich cluster, or a
                    // frontier BC that cannot be supplied any other way.
                    eligibleBcs.Add((b, Mathf.Max(handoff, remoteScale * HANDOFF_CYST_FULL)));
                }
                // Sort descending — richest-territory BCs get Cyst candidates
                // first, so the beam's KindPriority truncation doesn't lose
                // the best ones to less-productive BCs at the top of the list.
                eligibleBcs.Sort((a, b) => b.handoff.CompareTo(a.handoff));

                foreach (var (idx, handoff) in eligibleBcs)
                {
                    Vector3 cystTarget = s.bcs[idx].pos;
                    // Phase 2 cluster-aware targeting — user 2026-07-09:
                    // "close the lesser cyst close to one of the multiple
                    // biotics ... Preferably the furthest compared to
                    // starter nest in that cluster." Shrimps spawn at the
                    // Cyst → serve outward first → migrate inward as the
                    // outer patch depletes (natural handoff pattern).
                    //
                    // Cluster gate = ≥2 patches within 250m of the BC. Was
                    // originally ≥3 for dense maps, ≥2 for sparse — user
                    // 2026-07-09 pushed back that patch count doesn't
                    // capture map density (e.g. IndustrialQuarter has tight
                    // clusters). Simpler: use ≥2 everywhere; the 250m
                    // Cyst-coverage gate above already rate-limits Cysts
                    // on dense maps because one Cyst covers many nearby BCs.
                    if (_beamPhase == PlanPhase.Phase2_Expand
                        && EcoPlannerConfig.Phase2CystMinClusterPatches > 0)
                    {
                        float CLUSTER_RADIUS_M  = EcoPlannerConfig.Phase2CystCoverageRadiusM;
                        float CLUSTER_RADIUS_SQ = CLUSTER_RADIUS_M * CLUSTER_RADIUS_M;
                        int   MIN_CLUSTER_PATCHES = EcoPlannerConfig.Phase2CystMinClusterPatches;

                        var bcPos = s.bcs[idx].pos;
                        int clusterCount = 0;
                        int farthestPatchIdx = -1;
                        float farthestFromNestDsq = -1f;
                        for (int p = 0; p < s.patches.Count; p++)
                        {
                            if (s.patches[p].remaining <= 0) continue;
                            float pdx = s.patches[p].pos.x - bcPos.x;
                            float pdz = s.patches[p].pos.z - bcPos.z;
                            if (pdx * pdx + pdz * pdz > CLUSTER_RADIUS_SQ) continue;
                            clusterCount++;
                            // Track farthest-from-Nest patch inside the cluster.
                            float nx = s.patches[p].pos.x - s.nestPos.x;
                            float nz = s.patches[p].pos.z - s.nestPos.z;
                            float ndSq = nx * nx + nz * nz;
                            if (ndSq > farthestFromNestDsq)
                            {
                                farthestFromNestDsq = ndSq;
                                farthestPatchIdx = p;
                            }
                        }
                        if (clusterCount < MIN_CLUSTER_PATCHES)
                        {
                            // Cluster below threshold — skip Cyst on this BC.
                            // A single-patch BC gets its shrimps via migration.
                            continue;
                        }
                        if (farthestPatchIdx >= 0 && EcoPlannerConfig.Phase2CystTargetFarthestInCluster)
                            cystTarget = s.patches[farthestPatchIdx].pos;
                    }

                    list.Add(new Candidate
                    {
                        kind = ActionKind.PlaceCyst, target = cystTarget,
                        cost = EcoSimulator.CYST_COST, patchIdx = -1,
                        handoff = handoff,
                    });
                }
            }

            // Node candidates — enable expansion to patches that are currently
            // out of chain reach. For each patch >CHAIN_REACH_M but ≤2×CHAIN_REACH_M
            // from any owned structure, place a Node at the midpoint of the vector
            // from that structure toward the patch. That Node then makes the patch
            // chain-reachable, so a future PlaceBc becomes viable.
            //
            // Score-wise, the simulator doesn't yet auto-place a BC on the
            // newly-reachable patch, so a naked Node scores poorly relative to a
            // Cyst that produces immediate income. Which is the behavior the user
            // asked for: "don't expand strong early, tap the closer biotics first."
            // The planner will pick Cyst until close-patch options saturate, then
            // Node when the marginal benefit of expansion catches up.
            // Phase 1 Node gate — bridging enabled 2026-07-09: if this tick's
            // BC enumeration produced ZERO candidates (no reachable uncovered
            // patch), allow a single-shot Node RIGHT AWAY to bridge to the
            // next patch. Otherwise: nodes require 3 Cysts committed OR Cortex.
            int realCystCount = 0;
            for (int ci = 0; ci < s.cysts.Count; ci++)
                if (s.cysts[ci].pos != s.nestPos) realCystCount++;
            int bcCandidatesThisTick = 0;
            for (int li = preAddCount; li < list.Count; li++)
                if (list[li].kind == ActionKind.PlaceBc) bcCandidatesThisTick++;
            bool needsBridge = bcCandidatesThisTick == 0;
            // Phase 1 minimum-biotics objective — see PHASE1_MIN_TAPPED_PATCHES.
            // Computed from the LIVE beam state (not the root snapshot) so a
            // sequence that has already added a BC stops widening its reach.
            bool underTapped = CountTappedPatches(s) < PHASE1_MIN_TAPPED_PATCHES;
            // The 3-Cyst gate exists to stop the planner Node-rushing before
            // base eco is producing. It must not also strand a spawn that
            // simply has too few patches within reach.
            bool nodesAllowedYet = needsBridge || underTapped
                                || realCystCount >= 3 || s.hasResearchStructure;

            if (s.cash >= EcoSimulator.NODE_COST && !anyUncystedBc && nodesAllowedYet)
            {
                // Node hops are limited by the NODE's own reach (150m here),
                // not the BC's 200m. Using the larger number aimed every hop
                // beyond what the game would accept.
                float reach   = EcoSimulator.NODE_REACH_M;
                float reachSq = reach * reach;
                // Phase 1 (base eco): 2×reach — tight single-hop enum. Was
                // briefly bumped to 2.5×reach for forward-Node positioning
                // but that added ~50% more Node candidates to Phase 1, which
                // starved Cyst/BC candidates and slowed base-eco setup by
                // ~30% (income 19k vs 24k at 5min in comparable rounds).
                // Reverted — Phase 2's 4×reach handles the expansion.
                // Phase 2 (expansion): 4×reach — one Node hop per plan tick.
                // Phase 2 (expansion): base 4×reach, but ramp UP with cash
                // surplus so late game (cash near cap) we push out further.
                // NPC benchmark showed 10-15min expansion stalling on this
                // map (spread biotics 700-3500m from Nest) — user wants
                // "more progressive from 10min". Ramp cap: cash/cap = 0.4
                // adds +2, 0.7 adds another +2 → up to 8× reach at cash-cap.
                float outerMul;
                if (_beamPhase == PlanPhase.Phase2_Expand)
                {
                    outerMul = 4f;
                    float cashRatio = s.cap > 0 ? (float)s.cash / s.cap : 0f;
                    if (cashRatio > 0.4f) outerMul += 2f;
                    if (cashRatio > 0.7f) outerMul += 2f;
                }
                else
                {
                    outerMul = 2f;
                }
                // Under the minimum-biotics target, widen the search so the
                // third/fourth patch is visible at all. Phase 1's 2×reach
                // (400m) is what left NarakaCity stuck on two patches — the
                // next one simply never entered the enumeration. Applies in
                // both phases; Phase 2 already sits at 4-8× so this only
                // raises the floor.
                if (underTapped) outerMul = Mathf.Max(outerMul, PHASE1_UNDERTAPPED_OUTER_MUL);
                float outerSq = (outerMul * reach) * (outerMul * reach);

                // Bridging mode (Phase 1, needsBridge): propose ONLY ONE Node
                // targeting the CLOSEST out-of-reach patch. User 2026-07-09:
                // "only node to the closest biotics, directly in a straight
                // line if possible. On erg there was a node spam." Single
                // candidate = no simultaneous firing of many Nodes.
                int singleClosestPatchIdx = -1;
                float singleClosestDsq    = float.MaxValue;
                float singleBestRank      = float.MaxValue;
                Vector3 singleClosestAnchor = default;

                // Sector occupancy around the Nest — how many BCs each 45-degree
                // wedge already holds. Bridging picks by anchor distance, and
                // pure nearest-first SNOWBALLS: every BC becomes an anchor, so
                // the next patch in that same direction is now the closest one,
                // and expansion walks off in a single line.
                //
                // NarakaCity 2026-07-28, measured: the patch at (2307,713) is
                // 601m from the Nest and plainly visible from spawn — the third
                // closest on the map. The AI took patches at 682m, 848m and
                // 1087m first, ALL of them north, and did not reach the near
                // southern one until t=459s. Each northern hop was 415-472m
                // from its freshly built anchor while the southern one sat at
                // 535m from anything, so nearest-first kept choosing north.
                //
                // Weighting anchor distance by how crowded the target's sector
                // already is fixes it: north's fourth hop at 415m scores
                // 415*(1+3*0.6)=1162 against the southern first-in-sector at
                // 535*1.0=535, so the empty wedge wins.
                int[] sectorBcCount = new int[8];
                for (int bi = 0; bi < s.bcs.Count; bi++)
                {
                    float sdx = s.bcs[bi].pos.x - s.nestPos.x, sdz = s.bcs[bi].pos.z - s.nestPos.z;
                    if (sdx * sdx + sdz * sdz < 1f) continue;
                    float ang = Mathf.Atan2(sdz, sdx);
                    if (ang < 0f) ang += 2f * Mathf.PI;
                    sectorBcCount[((int)(ang / (Mathf.PI / 4f))) & 7]++;
                }
                const float SECTOR_CROWD_PENALTY = 0.6f;
                // Under-tapped counts as bridging too. NarakaCity 2026-07-28
                // showed why: with BC candidates present, needsBridge was false,
                // so the FULL enumerator ran at the widened 6x reach and emitted
                // a Node candidate toward every out-of-reach patch within 1200m.
                // The beam then fired a handful per tick and the team built 28
                // Nodes against 5 Bio Caches — a starburst in every direction
                // instead of a straight chain, with cash pinned at 0 for five
                // minutes. One candidate, aimed at the closest patch, is the
                // whole point of the minimum-biotics objective.
                bool bridgeSingleShot = (needsBridge || underTapped) && _beamPhase != PlanPhase.Phase2_Expand;

                for (int p = 0; p < s.patches.Count; p++)
                {
                    var patch = s.patches[p];
                    if (patch.remaining <= 0) continue;
                    // Skip patches a Bio Cache can already reach on its own —
                    // no Node needed. Uses the BC's larger placement range.
                    if (IsPatchBcReachable(patch.pos, s)) continue;

                    // While bridging, aim only at patches that would actually
                    // earn a BC once reached. Heading for a patch an existing
                    // BC already harvests spends 100/Node for nothing.
                    if (bridgeSingleShot && IsPatchCovered(s, p)) continue;

                    // Find the nearest chain-linked structure to this patch.
                    Vector3 anchor = default; float bestDsq = float.MaxValue;
                    void consider(Vector3 pos)
                    {
                        float dx = patch.pos.x - pos.x, dz = patch.pos.z - pos.z;
                        float dsq = dx * dx + dz * dz;
                        if (dsq < bestDsq) { bestDsq = dsq; anchor = pos; }
                    }
                    // Node-to-node chaining may stand on an unfinished Node.
                    if (s.nestPos != Vector3.zero) consider(s.nestPos);
                    for (int i = 0; i < s.bcs.Count; i++)   if (s.bcs[i].finished)   consider(s.bcs[i].pos);
                    for (int i = 0; i < s.cysts.Count; i++) if (s.cysts[i].finished) consider(s.cysts[i].pos);
                    for (int i = 0; i < s.nodes.Count; i++) consider(s.nodes[i].pos);
                    if (bestDsq > outerSq) continue;   // patch is >2×reach away — need multi-hop chain, skip for now

                    if (bridgeSingleShot)
                    {
                        // In bridging mode, just track the closest — emit outside the loop.
                        float pdx = patch.pos.x - s.nestPos.x, pdz = patch.pos.z - s.nestPos.z;
                        float pang = Mathf.Atan2(pdz, pdx);
                        if (pang < 0f) pang += 2f * Mathf.PI;
                        int psec = ((int)(pang / (Mathf.PI / 4f))) & 7;
                        // Rank by RETURN, not by proximity. Distance still
                        // matters — it drives both the node count and the
                        // time discount inside the ROI — but a cluster worth
                        // ten patches now outbids a lone patch that happens
                        // to be nearer, which is the judgement a human makes.
                        float anchorDist = Mathf.Sqrt(bestDsq);
                        if (anchorDist > MapProfile.MaxChainDepthM) continue;
                        var roi = EvaluateChainRoi(s, patch.pos, anchorDist);
                        if (roi.Roi <= 0f) continue;
                        // Sector crowding still breaks ties, so a good return
                        // in an untouched wedge beats an equal one where we
                        // are already invested.
                        float rank = -roi.Roi / (1f + SECTOR_CROWD_PENALTY * sectorBcCount[psec]);
                        if (rank < singleBestRank)
                        {
                            singleBestRank = rank;
                            singleClosestDsq = bestDsq;
                            singleClosestPatchIdx = p;
                            singleClosestAnchor = anchor;
                        }
                        continue;
                    }

                    // Node lands 75% of reach along the anchor→patch line —
                    // pulled BACK from the boundary because the game's own
                    // ConstructionPlacement search then slides OUTWARD looking
                    // for a clear spot. First round showed a Node targeted at
                    // ~180m (90% of 200m) getting built at 280m from anchor,
                    // outside chain reach entirely. 75% (150m target) leaves
                    // 50m of outward-slide budget before the placement escapes
                    // the anchor's reach.
                    Vector3 dir = patch.pos - anchor;
                    float len = Mathf.Sqrt(bestDsq);
                    if (len < 1f) continue;
                    Vector3 nodePos = anchor + dir * (NodeHopDistance(reach) / len);

                    // Skip if there's already a Node (finished OR in-progress)
                    // within 80m of the proposed position. The multi-directional
                    // fire loop mines top-K sequences, so runner-ups can all
                    // propose Nodes toward the SAME sector's patches; without
                    // this we ended up with 8 half-built Nodes at ~(1183,123).
                    bool nodeAlreadyHere = false;
                    for (int ni = 0; ni < s.nodes.Count; ni++)
                    {
                        float ndx = s.nodes[ni].pos.x - nodePos.x, ndz = s.nodes[ni].pos.z - nodePos.z;
                        if (ndx * ndx + ndz * ndz < 80f * 80f) { nodeAlreadyHere = true; break; }
                    }
                    if (nodeAlreadyHere) continue;

                    list.Add(new Candidate
                    {
                        kind = ActionKind.PlaceNode, target = nodePos,
                        cost = EcoSimulator.NODE_COST, patchIdx = -1,
                        unlocks = CountUnlockedPatches(s, nodePos),
                    });
                }

                // Bridging mode: emit the single closest Node candidate.
                // Straight-line vector from anchor toward the target patch,
                // clamped to 75% of reach. Same dedup as the per-patch path.
                // Only pay for a Node when nothing else can extend the chain.
                //
                // A Bio Cache anchors at 237m (200m chain + its own 37m radius)
                // against a Node's ~159m, so a BC hop covers 75% more ground
                // AND earns, where a Node is 200 cash of pure overhead. If any
                // BC candidate survived enumeration this tick, that BC will
                // extend the network further than a Node would — let it, and
                // re-evaluate next tick from the new anchor.
                //
                // User 2026-07-29: "it might be even worth to consider to use
                // only biocache ... but of course needs to consider its actual
                // build range" — which is what BcPlaceReachM now carries.
                bool bcCanExtendInstead = bcCandidatesThisTick > 0;
                if (bridgeSingleShot && singleClosestPatchIdx >= 0 && !bcCanExtendInstead)
                {
                    var patch = s.patches[singleClosestPatchIdx];
                    Vector3 dir = patch.pos - singleClosestAnchor;
                    float len = Mathf.Sqrt(singleClosestDsq);
                    if (len >= 1f)
                    {
                        Vector3 nodePos = singleClosestAnchor + dir * (NodeHopDistance(reach) / len);
                        bool nodeAlreadyHere = false;
                        for (int ni = 0; ni < s.nodes.Count; ni++)
                        {
                            float ndx = s.nodes[ni].pos.x - nodePos.x, ndz = s.nodes[ni].pos.z - nodePos.z;
                            if (ndx * ndx + ndz * ndz < 80f * 80f) { nodeAlreadyHere = true; break; }
                        }
                        if (!nodeAlreadyHere)
                        {
                            list.Add(new Candidate
                            {
                                kind = ActionKind.PlaceNode, target = nodePos,
                                cost = EcoSimulator.NODE_COST, patchIdx = -1,
                                unlocks = CountUnlockedPatches(s, nodePos),
                            });
                        }
                    }
                }

                // Biotics-anchored scout Nodes with DENSITY WEIGHTING.
                //
                // Old behavior: for each of 8 angular sectors, propose a
                // scout Node toward the CLOSEST unreachable patch in that
                // sector. This meant a lonely far patch in one sector got
                // equal priority to a rich cluster of 10 patches in another.
                //
                // New behavior: for each sector, compute:
                //   - centroid of unreached patches, resource-weighted
                //   - total unreached resources (density score)
                // Aim scout Node toward the CENTROID (not just closest patch),
                // and enumerate sectors sorted by density descending — so
                // beam's per-kind trim keeps the richest-target sectors first.
                //
                // This directly addresses NPC's 10-15min stall where distant
                // biotics clusters at ~1500-2500m from Nest got ignored
                // because sim only saw the isolated closest patch per sector.
                //
                // Suppressed in bridging mode — single-Node output only,
                // user 2026-07-09: "On erg there was a node spam."
                if (!bridgeSingleShot)
                {
                    Vector3 fromCenter = s.nestPos;
                    const int SECTOR_COUNT = 8;
                    // Per-sector aggregates for density weighting.
                    float[] sectorTotalRemaining = new float[SECTOR_COUNT];
                    Vector3[] sectorWeightedCentroid = new Vector3[SECTOR_COUNT];
                    int[] sectorPatchCount = new int[SECTOR_COUNT];

                    for (int p = 0; p < s.patches.Count; p++)
                    {
                        var patch = s.patches[p];
                        if (patch.remaining <= 0) continue;
                        if (IsPatchBcReachable(patch.pos, s)) continue;
                        float dx = patch.pos.x - fromCenter.x, dz = patch.pos.z - fromCenter.z;
                        float ang = Mathf.Atan2(dz, dx);
                        if (ang < 0) ang += 2f * Mathf.PI;
                        int sector = ((int)(ang / (Mathf.PI / 4f)) + SECTOR_COUNT) % SECTOR_COUNT;
                        float w = patch.remaining;
                        sectorTotalRemaining[sector] += w;
                        sectorWeightedCentroid[sector].x += patch.pos.x * w;
                        sectorWeightedCentroid[sector].z += patch.pos.z * w;
                        sectorPatchCount[sector]++;
                    }

                    // Sort sectors by density (highest first) so the beam
                    // enumerates rich-cluster sectors before sparse ones.
                    int[] sectorOrder = new int[SECTOR_COUNT];
                    for (int k = 0; k < SECTOR_COUNT; k++) sectorOrder[k] = k;
                    System.Array.Sort(sectorOrder, (a, b) => sectorTotalRemaining[b].CompareTo(sectorTotalRemaining[a]));

                    for (int oi = 0; oi < SECTOR_COUNT; oi++)
                    {
                        int k = sectorOrder[oi];
                        if (sectorPatchCount[k] == 0) continue;
                        // Resource-weighted centroid of unreached patches
                        // in this sector.
                        Vector3 centroid = new Vector3(
                            sectorWeightedCentroid[k].x / sectorTotalRemaining[k], 0,
                            sectorWeightedCentroid[k].z / sectorTotalRemaining[k]);
                        Vector3 sectorDir = centroid - fromCenter;
                        float sectorLen = Mathf.Sqrt(sectorDir.x * sectorDir.x + sectorDir.z * sectorDir.z);
                        if (sectorLen < 1f) continue;
                        sectorDir /= sectorLen;

                        // Anchor = whichever owned structure is most-extended
                        // in this sector's direction (so the Node chains from
                        // our frontier, not always from the Nest).
                        Vector3 sectorAnchor = fromCenter;
                        float bestDot = 0f;
                        void probe(Vector3 pos)
                        {
                            Vector3 v = pos - fromCenter;
                            float dot = v.x * sectorDir.x + v.z * sectorDir.z;
                            if (dot > bestDot) { bestDot = dot; sectorAnchor = pos; }
                        }
                        for (int i = 0; i < s.bcs.Count; i++)   if (s.bcs[i].finished)   probe(s.bcs[i].pos);
                        for (int i = 0; i < s.cysts.Count; i++) if (s.cysts[i].finished) probe(s.cysts[i].pos);
                        for (int i = 0; i < s.nodes.Count; i++) if (s.nodes[i].finished) probe(s.nodes[i].pos);

                        Vector3 scoutPos = sectorAnchor + sectorDir * NodeHopDistance(EcoSimulator.NODE_REACH_M);

                        // 100m dedup — user confirmed this wasn't the cause
                        // of the expansion stall; the 50m attempt "messed
                        // things up". Keeping the classic value.
                        bool nodeThere = false;
                        for (int ni = 0; ni < s.nodes.Count; ni++)
                        {
                            float ndx = s.nodes[ni].pos.x - scoutPos.x, ndz = s.nodes[ni].pos.z - scoutPos.z;
                            if (ndx * ndx + ndz * ndz < 100f * 100f) { nodeThere = true; break; }
                        }
                        if (nodeThere) continue;

                        list.Add(new Candidate
                        {
                            kind = ActionKind.PlaceNode, target = scoutPos,
                            cost = EcoSimulator.NODE_COST, patchIdx = -1,
                            unlocks = CountUnlockedPatches(s, scoutPos),
                        });
                    }
                }
            }

            return list;
        }

        // Chain reach: `pos` is legal iff within CHAIN_REACH_M of any owned
        // finished structure (Nest, BC, Cyst, Node). Matches the game's
        // "MaximumBaseStructureDistance" rule the phased AI placer already uses.
        // Small O(N) scan; N is total owned-structure count which is bounded.
        /// <summary>
        /// Is <paramref name="pos"/> within chain reach of the base network?
        ///
        /// Anchors include structures still UNDER CONSTRUCTION. That is the
        /// single change that lets the planner build a chain at a useful rate.
        /// Requiring `finished` meant each Node hop had to wait out the
        /// previous Node's full 20s build before the next one could even be
        /// enumerated — and worse, it applied inside the beam too, so a
        /// sequence like [Node -> Node -> Node -> BC] could never form: every
        /// hop past the first looked unreachable in the simulated state. The
        /// planner was structurally limited to one hop per plan tick.
        ///
        /// Measured cost of that limit on NarakaCity: 0.09 Nodes/s against the
        /// human benchmark's 0.164/s, and reach 2000m at t=877s against the
        /// human's t=658s — which on that map is most of the income gap.
        ///
        /// The rule is NODE-ONLY, corrected by the user 2026-07-29: "only
        /// nodes can anchor on themselves while in ongoing construction phase"
        /// — a Bio Cache or Cyst has to FINISH before it anchors anything.
        /// v0.7.75 let every in-progress structure anchor, which had the
        /// planner chaining off Bio Caches that could not yet carry the chain.
        ///
        /// Keeping it for Nodes is what matters and is what the human round
        /// demonstrates: 157 Nodes in 960s is one per 6s, in chains of 4-8
        /// spaced ~130m. At a 20s build time that is only possible if a Node
        /// under construction already anchors the next one.
        /// </summary>
        static bool IsChainReachable(Vector3 pos, EcoState s, float reachM = -1f,
                                    bool unfinishedNodesAnchor = false)
        {
            float reach = reachM > 0f ? reachM : CHAIN_REACH_M;
            float r2 = reach * reach;
            var nest = s.nestPos;
            if (nest != Vector3.zero)
            {
                float dx = pos.x - nest.x, dz = pos.z - nest.z;
                if (dx * dx + dz * dz <= r2) return true;
            }
            for (int i = 0; i < s.bcs.Count; i++)
            {
                if (!s.bcs[i].finished) continue;          // must be BUILT to anchor
                float dx = pos.x - s.bcs[i].pos.x, dz = pos.z - s.bcs[i].pos.z;
                if (dx * dx + dz * dz <= r2) return true;
            }
            for (int i = 0; i < s.cysts.Count; i++)
            {
                if (!s.cysts[i].finished) continue;        // must be BUILT to anchor
                float dx = pos.x - s.cysts[i].pos.x, dz = pos.z - s.cysts[i].pos.z;
                if (dx * dx + dz * dz <= r2) return true;
            }
            for (int i = 0; i < s.nodes.Count; i++)
            {
                // A Node under construction anchors ANOTHER NODE, and nothing
                // else. That is the narrow reading of the user's rule — "only
                // nodes can anchor on themselves while in ongoing construction
                // phase" — and it is what the symptom shows: a Bio Cache placed
                // off an unfinished Node has to fall back on some older, more
                // distant anchor and lands far from its patch. Waiting the ~20s
                // for the Node to finish lets the same BC hug the biotics.
                if (!s.nodes[i].finished && !unfinishedNodesAnchor) continue;
                float dx = pos.x - s.nodes[i].pos.x, dz = pos.z - s.nodes[i].pos.z;
                if (dx * dx + dz * dz <= r2) return true;
            }
            return false;
        }

        static void Apply(EcoState s, Candidate c)
        {
            switch (c.kind)
            {
                case ActionKind.PlaceBc:
                    s.cash -= c.cost;
                    s.bcs.Add(new EcoState.Bc
                    {
                        pos = c.target, finished = false,
                        readyAt = s.t + EcoSimulator.BC_BUILD_S,
                        storage = 0, storageCap = 4000,
                    });
                    break;
                case ActionKind.PlaceCyst:
                    s.cash -= c.cost;
                    s.cysts.Add(new EcoState.Cyst
                    {
                        pos = c.target, finished = false,
                        readyAt = s.t + EcoSimulator.CYST_BUILD_S,
                        // First shrimp lands one shrimp-build-cycle after the cyst itself finishes.
                        nextSpawnAt = s.t + EcoSimulator.CYST_BUILD_S + EcoSimulator.SHRIMP_BUILD_S,
                    });
                    break;
                case ActionKind.PlaceNode:
                    s.cash -= c.cost;
                    s.nodes.Add(new EcoState.Node
                    {
                        pos = c.target, finished = false,
                        readyAt = s.t + EcoSimulator.NODE_BUILD_S,
                    });
                    break;
                case ActionKind.Noop:
                    break;
            }
        }

        // Diagnostic one-time-per-round log for the radial reserve set.
        static bool _radialReserveLogged;

        internal static void ResetForNewRound()
        {
            _lastPlanAt.Clear();
            _fired.Clear();
            SlideSamples = 0; SlideSumM = 0f; SlideMaxM = 0f;
            _phaseByTeam.Clear();
            _currentPhase = PlanPhase.Phase1_BaseEco;
            _radialReserveLogged = false;
        }
    }
}
