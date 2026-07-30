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
        public const float PLAN_HORIZON_S = 300f;
        public const float PLAN_CADENCE_S = 5f;
        // Wait for starter units + starting cash to actually be granted
        // before the first plan tick. Round start has a spawn-in period —
        // planning against a still-empty state produces sequences that
        // are irrelevant by the time the game catches up.
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
        // Team is set at the top of MaybePlan so EnumerateActions can query
        // team-specific state (fog of war layers, chain-linked structures).
        // Cleaner than threading Team through every internal helper.
        static Team _currentTeam;

        // Lowered from 30/3000 after user observed the Phase 2 transition
        // was hitting around t=250s (4:10) — that's already after the base
        // eco has plateaued for ~30s, so half a minute of missed expansion.
        // 20 shrimps + 2000 cash lets us cross the threshold closer to
        // t=180s (3:00) which matches the natural "base eco ramp done" moment.
        const int   PHASE2_MIN_SHRIMPS = 20;
        const int   PHASE2_MIN_CASH    = 2000;
        // Time-based fallback: even if the shrimps/cash thresholds haven't
        // fired, past this round-time we're definitely past base-eco setup
        // and any residual "beam stuck on Noop" behaviour is pure waste.
        const float PHASE2_TIME_FALLBACK_S = 210f;   // was 180 — 30s tighter
        // Was 15 — 15 shrimps at t=180s meant Phase 2 fired with only
        // 345 cash and no eco base yet, wasting the Phase 2 buffs on a
        // planner that couldn't afford anything.
        const int   PHASE2_TIME_FALLBACK_MIN_SHRIMPS = 25;
        // In Phase 2, each Node in the winning sequence gets this score bonus
        // (representing beyond-horizon strategic value). Chosen to be roughly
        // the average income of the BC a Node enables over horizon(300s) −
        // horizon(build+shrimp-fill) = 200-250s of income = ~2000 gross.
        const float PHASE2_NODE_BONUS  = 2000f;
        // Cysts get a bigger bonus than Nodes in Phase 2 because a Cyst on
        // an uncysted BC IMMEDIATELY starts producing shrimps who harvest a
        // patch that was standing idle. A Node just enables future BC
        // placement — sim already discounts Cyst-on-uncysted-BC due to the
        // marginal-utility router preferring to route new shrimps to
        // central-cluster BCs. This bonus overrides that mis-scoring.
        const float PHASE2_CYST_BONUS  = 5000f;
        // BC bonus: BC-Cyst chain's income lands mostly BEYOND the beam's
        // sim horizon (BC finishes at t=20 in sim, Cyst at t=40, first
        // Shrimp at t=60 — near the depth×step=60s beam horizon). So the
        // beam sees near-zero gain from a new BC and picks Noop — that's
        // the min-7 expansion stall. +2000 bonus tips it toward keeping
        // the eco expansion going past the sim horizon.
        const float PHASE2_BC_BONUS    = 2000f;

        // Coverage radius: how far a BC's shrimps can reasonably reach a patch
        // and still contribute meaningful income. Past this we treat the BC as
        // NOT covering the patch (a new BC becomes a valid candidate).
        const float BC_COVERAGE_M = 200f;

        // Chain-reach radius: an alien structure must be within this distance of
        // an existing owned structure (Nest, BC, Cyst, Node) to be legally
        // placeable. Matches BC.MaximumBaseStructureDistance from the constants
        // dump (200m) — same rule the phased BC placer uses.
        const float CHAIN_REACH_M = 200f;

        // Execution gating.
        //   - Only fires when EcoPlannerActive pref is on (default OFF: shadow mode)
        //   - Only when expected_gain > EXEC_MIN_GAIN (skip low-conviction actions)
        //   - REPEAT_SUPPRESS deduplicates same-kind fires within radius
        public static bool ExecutionEnabled;
        const int   EXEC_MIN_GAIN     = 100;
        const float REPEAT_SUPPRESS_M = 60f;
        // Suppress must outlast the build cycle. Structures take ~20s to
        // build; with a 15s suppress window the same target got proposed and
        // fired again just before the first one finished appearing in state.
        // 45s gives the placement 20s build + 20s buffer to appear in
        // team.Structures + 5s slack, so the beam sees it and stops proposing
        // duplicates.
        const float REPEAT_SUPPRESS_S = 45f;
        struct FiredAction { public ActionKind kind; public Vector3 pos; public float at; }
        static readonly Dictionary<Team, List<FiredAction>> _fired = new Dictionary<Team, List<FiredAction>>();

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
        const int   BEAM_DEPTH             = 6;
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

        public enum ActionKind { Noop, PlaceBc, PlaceCyst, PlaceNode }

        public struct Candidate
        {
            public ActionKind kind;
            public Vector3    target;
            public int        cost;
            public float      score;      // predicted cash at horizon
            public int        patchIdx;   // -1 unless PlaceBc
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

            float now = Time.time;
            if (_lastPlanAt.TryGetValue(team, out var last) && now - last < PLAN_CADENCE_S) return;
            _lastPlanAt[team] = now;

            EcoState state;
            try { state = EcoStateBuilder.Build(team); }
            catch (System.Exception ex) { MelonLogger.Warning("[PLAN] Build threw: " + ex.Message); return; }

            _currentTeam  = team;
            _currentPhase = ComputePhase(team, state);

            var best = BeamSearch(state, out var topSequences);

            // Baseline for gain measurement — score of the "do nothing at all" sequence.
            var baseline = state.Clone();
            EcoSimulator.SimulateForward(baseline, PLAN_HORIZON_S);
            float baselineScore = baseline.grossEarned;
            float gain = best.score - baselineScore;

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
                int cashLeft = state.cash;

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
                bool TryFireAction(Candidate c)
                {
                    if (c.kind == ActionKind.Noop) return true;
                    if (c.cost > cashLeft) return true;   // skip this one, try next
                    if (c.kind == ActionKind.PlaceCyst && !HasFinishedBcNear(state, c.target)) return false;
                    if ((c.kind == ActionKind.PlaceBc || c.kind == ActionKind.PlaceNode)
                        && !IsChainReachable(c.target, state)) return false;
                    if (!TryFire(team, c)) return true;   // dedup or game rejected — try next
                    fired++;
                    firedCash += c.cost;
                    cashLeft -= c.cost;
                    return true;
                }

                // 1) Fire the winner in order. Break on prereq/reach hard-
                //    blockers (later actions depend on the blocked one).
                //    Cost blockers are skipped in-place so cheap later
                //    actions still fire.
                foreach (var c in best.sequence)
                {
                    if (!TryFireAction(c)) break;
                }

                // 2) Pick up multi-directional expansion from the runner-ups.
                //    Only consider each runner-up's FIRST non-Noop action —
                //    that's the immediate commit the sequence starts with,
                //    which by construction is what would be fired now if the
                //    beam had picked THIS sequence. Skip the winner (index 0)
                //    since we already fired its actions.
                if (topSequences != null)
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

            // Shrimp relocation: pre-migrate off depleting patches so the
            // vanilla "everyone re-tasks at once" swarm doesn't happen.
            try { ShrimpRelocator.MaybeRun(team); }
            catch (System.Exception ex) { MelonLogger.Warning("[PLAN] ShrimpRelocator.MaybeRun threw: " + ex.Message); }

            var lead = best.sequence.Count > 0 ? best.sequence[0] : new Candidate { kind = ActionKind.Noop };
            Si_RTS_AI.AppendToRound(
                "[PLAN] t=" + Time.time.ToString("F1") +
                " team=" + tn +
                " cash=" + state.cash + "/" + state.cap +
                " bcs=" + state.bcs.Count +
                " cysts=" + state.cysts.Count +
                " shrimps=" + state.totalShrimps +
                " patches_active=" + CountActivePatches(state) +
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

        static bool HasFinishedBcNear(EcoState s, Vector3 target)
        {
            // Cyst target is now placed ~25m off the BC (was AT the BC before
            // the placement-clearance fix), so this radius must accommodate
            // that offset plus a bit of chain-reach slack.
            const float R2 = 60f * 60f;
            for (int i = 0; i < s.bcs.Count; i++)
            {
                if (!s.bcs[i].finished) continue;
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
            for (int i = 0; i < s.nodes.Count; i++) if (s.nodes[i].finished) consider(s.nodes[i].pos);
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
            int nodeKind = perKind * 2;
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
            if (_currentPhase == PlanPhase.Phase2_Expand && n.sequence != null)
            {
                int nodeCount = 0, cystCount = 0, bcCount = 0;
                for (int i = 0; i < n.sequence.Count; i++)
                {
                    if (n.sequence[i].kind == ActionKind.PlaceNode) nodeCount++;
                    if (n.sequence[i].kind == ActionKind.PlaceCyst) cystCount++;
                    if (n.sequence[i].kind == ActionKind.PlaceBc)   bcCount++;
                }
                n.score += nodeCount * PHASE2_NODE_BONUS
                         + cystCount * PHASE2_CYST_BONUS
                         + bcCount   * PHASE2_BC_BONUS;
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

            for (int i = 0; i < log.Count; i++)
            {
                if (log[i].kind != c.kind) continue;
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
            int uncystedBcCount = 0;
            for (int bi = 0; bi < s.bcs.Count; bi++)
            {
                bool cystNear = false;
                for (int ci = 0; ci < s.cysts.Count; ci++)
                {
                    if (s.cysts[ci].pos == s.nestPos) continue;
                    var d = s.cysts[ci].pos - s.bcs[bi].pos;
                    if (d.x * d.x + d.z * d.z < 60f * 60f) { cystNear = true; break; }
                }
                if (!cystNear) uncystedBcCount++;
            }
            // Once we're near the shrimp hard cap, adding more Cysts doesn't
            // add more shrimps — the cap is hit. New outer BCs don't need a
            // local Cyst; the ShrimpRelocator will migrate existing shrimps
            // to them. So relax the uncysted-BC gate at high shrimp count.
            // (Cysts still have military value for later Phase 3, but that's
            // separate from eco expansion.)
            const int MAX_UNCYSTED_QUEUE = 2;
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
            Perception.MapLayers.LayerB explored = null;
            try { if (_currentTeam != null) explored = Perception.MapLayers.FoWLayers.GetExplored(_currentTeam); }
            catch { }
            bool PatchExplored(Vector3 p)
            {
                if (explored == null) return true;   // fail open
                int cx = Perception.MapLayers.GridWorld.CellX(p.x);
                int cz = Perception.MapLayers.GridWorld.CellZ(p.z);
                return explored.IsSet(cx, cz);
            }

            // BC candidates — GATED on the uncysted-BC check above.
            if (s.cash >= EcoSimulator.BC_COST && !anyUncystedBc)
            {
                for (int p = 0; p < s.patches.Count; p++)
                {
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
                    bool covered = false;
                    for (int b = 0; b < s.bcs.Count; b++)
                    {
                        int nearest = EcoSimulator.NearestActivePatchIdxPublic(s, s.bcs[b].pos);
                        if (nearest == p) { covered = true; break; }
                    }
                    if (covered) continue;
                    if (!IsChainReachable(patch.pos, s)) continue;   // must chain-link to Nest/Node graph

                    // Target the patch center — ConstructionPlacement will search
                    // outward for the first legally-clear spot, respecting the
                    // patch's no-build zone. No manual offset needed; the game
                    // places the BC on the boundary of that zone the same way
                    // AlienConstruction's phased placer has been doing.
                    list.Add(new Candidate
                    {
                        kind = ActionKind.PlaceBc, target = patch.pos,
                        cost = EcoSimulator.BC_COST, patchIdx = p,
                    });
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
                    if (!bc.finished) continue;
                    s.shrimpsPerBc.TryGetValue(b, out int cur);
                    if (cur >= 12) continue;
                    // Same radius (60m) as the BC gate uses, and exclude
                    // the synthetic Nest-as-Cyst. Previously the enum used
                    // 40m + didn't exclude Nest, so a BC 45m from a real
                    // Cyst got flagged "Cysted" by the BC gate (60m) but
                    // "not-Cysted" by the enum (40m) → stall: gate closed,
                    // no Cyst candidate. Ditto for a BC near the Nest.
                    bool nearbyCyst = false;
                    for (int c = 0; c < s.cysts.Count; c++)
                    {
                        if (s.cysts[c].pos == s.nestPos) continue;
                        var d = s.cysts[c].pos - bc.pos;
                        if (d.x * d.x + d.z * d.z < 60f * 60f) { nearbyCyst = true; break; }
                    }
                    if (nearbyCyst) continue;

                    float handoff = ComputeHandoffScore(s, bc.pos);
                    eligibleBcs.Add((b, handoff));
                }
                // Sort descending — richest-territory BCs get Cyst candidates
                // first, so the beam's KindPriority truncation doesn't lose
                // the best ones to less-productive BCs at the top of the list.
                eligibleBcs.Sort((a, b) => b.handoff.CompareTo(a.handoff));

                foreach (var (idx, _) in eligibleBcs)
                {
                    list.Add(new Candidate
                    {
                        kind = ActionKind.PlaceCyst, target = s.bcs[idx].pos,
                        cost = EcoSimulator.CYST_COST, patchIdx = -1,
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
            if (s.cash >= EcoSimulator.NODE_COST && !anyUncystedBc)
            {
                float reach   = CHAIN_REACH_M;
                float reachSq = reach * reach;
                // Phase 1 (base eco): 2×reach — tight single-hop enum. Was
                // briefly bumped to 2.5×reach for forward-Node positioning
                // but that added ~50% more Node candidates to Phase 1, which
                // starved Cyst/BC candidates and slowed base-eco setup by
                // ~30% (income 19k vs 24k at 5min in comparable rounds).
                // Reverted — Phase 2's 4×reach handles the expansion.
                // Phase 2 (expansion): 4×reach — one Node hop per plan tick.
                float outerMul = _currentPhase == PlanPhase.Phase2_Expand ? 4f : 2f;
                float outerSq = (outerMul * reach) * (outerMul * reach);
                for (int p = 0; p < s.patches.Count; p++)
                {
                    var patch = s.patches[p];
                    if (patch.remaining <= 0) continue;
                    // Skip already-reachable patches — no need for a Node.
                    if (IsChainReachable(patch.pos, s)) continue;

                    // Find the nearest chain-linked structure to this patch.
                    Vector3 anchor = default; float bestDsq = float.MaxValue;
                    void consider(Vector3 pos)
                    {
                        float dx = patch.pos.x - pos.x, dz = patch.pos.z - pos.z;
                        float dsq = dx * dx + dz * dz;
                        if (dsq < bestDsq) { bestDsq = dsq; anchor = pos; }
                    }
                    if (s.nestPos != Vector3.zero) consider(s.nestPos);
                    for (int i = 0; i < s.bcs.Count; i++)   if (s.bcs[i].finished)   consider(s.bcs[i].pos);
                    for (int i = 0; i < s.cysts.Count; i++) if (s.cysts[i].finished) consider(s.cysts[i].pos);
                    for (int i = 0; i < s.nodes.Count; i++) if (s.nodes[i].finished) consider(s.nodes[i].pos);
                    if (bestDsq > outerSq) continue;   // patch is >2×reach away — need multi-hop chain, skip for now

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
                    Vector3 nodePos = anchor + dir * (reach * 0.75f / len);

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
                    });
                }

                // Biotics-anchored scout Nodes — one per angular sector, but
                // only when there's an actual unreachable patch in that
                // direction to justify the expansion. For each sector, find
                // the CLOSEST unreachable patch whose angular direction from
                // the Nest falls in that sector. Propose the scout Node
                // toward THAT patch, using the most-extended anchor in the
                // same sector as the launching point.
                {
                    Vector3 fromCenter = s.nestPos;
                    const int SECTOR_COUNT = 8;
                    // sectorClosestPatchIdx[k] = index into s.patches of the
                    // closest unreachable patch whose angle falls in sector k.
                    int[] sectorClosestPatchIdx = new int[SECTOR_COUNT];
                    float[] sectorClosestPatchDsq = new float[SECTOR_COUNT];
                    for (int k = 0; k < SECTOR_COUNT; k++) { sectorClosestPatchIdx[k] = -1; sectorClosestPatchDsq[k] = float.MaxValue; }

                    for (int p = 0; p < s.patches.Count; p++)
                    {
                        var patch = s.patches[p];
                        if (patch.remaining <= 0) continue;
                        if (IsChainReachable(patch.pos, s)) continue;
                        float dx = patch.pos.x - fromCenter.x, dz = patch.pos.z - fromCenter.z;
                        float dsq = dx * dx + dz * dz;
                        // atan2 domain is [-π, π]; convert to [0, 2π) then to
                        // sector 0..7 (each sector = 45°, sector 0 centered on east).
                        float ang = Mathf.Atan2(dz, dx);
                        if (ang < 0) ang += 2f * Mathf.PI;
                        int sector = ((int)(ang / (Mathf.PI / 4f)) + SECTOR_COUNT) % SECTOR_COUNT;
                        if (dsq < sectorClosestPatchDsq[sector])
                        {
                            sectorClosestPatchDsq[sector] = dsq;
                            sectorClosestPatchIdx[sector] = p;
                        }
                    }

                    for (int k = 0; k < SECTOR_COUNT; k++)
                    {
                        int pIdx = sectorClosestPatchIdx[k];
                        if (pIdx < 0) continue;
                        Vector3 patchPos = s.patches[pIdx].pos;
                        Vector3 sectorDir = patchPos - fromCenter;
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

                        Vector3 scoutPos = sectorAnchor + sectorDir * (CHAIN_REACH_M * 0.75f);

                        // Skip if a Node already sits within 100m.
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
        static bool IsChainReachable(Vector3 pos, EcoState s)
        {
            float r2 = CHAIN_REACH_M * CHAIN_REACH_M;
            var nest = s.nestPos;
            if (nest != Vector3.zero)
            {
                float dx = pos.x - nest.x, dz = pos.z - nest.z;
                if (dx * dx + dz * dz <= r2) return true;
            }
            for (int i = 0; i < s.bcs.Count; i++)
            {
                if (!s.bcs[i].finished) continue;
                float dx = pos.x - s.bcs[i].pos.x, dz = pos.z - s.bcs[i].pos.z;
                if (dx * dx + dz * dz <= r2) return true;
            }
            for (int i = 0; i < s.cysts.Count; i++)
            {
                if (!s.cysts[i].finished) continue;
                float dx = pos.x - s.cysts[i].pos.x, dz = pos.z - s.cysts[i].pos.z;
                if (dx * dx + dz * dz <= r2) return true;
            }
            for (int i = 0; i < s.nodes.Count; i++)
            {
                if (!s.nodes[i].finished) continue;
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

        internal static void ResetForNewRound()
        {
            _lastPlanAt.Clear();
            _fired.Clear();
            _phaseByTeam.Clear();
            _currentPhase = PlanPhase.Phase1_BaseEco;
        }
    }
}
