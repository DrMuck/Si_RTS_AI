using HarmonyLib;
using MelonLoader;
using Silica;
using Silica.AI;
using SilicaAdminMod;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Si_RTS_AI.Faction
{
    /// <summary>
    /// Phase 3.2 slice 1 — OWN Alien construction. Stock's AIConstructionHandler.Think()
    /// is prefix-returned-false for managed Alien teams; we handle everything ourselves.
    ///
    /// This slice implements ONLY rules 1 and 2:
    ///   1. Place a BioCache at each of the (up to 4) nearest uncovered visible biotics
    ///      patches, tight placement search so BC lands right at the patch.
    ///   2. Place a Lesser Spawning Cyst near each BC that doesn't already have one.
    ///
    /// No stock construction fires. Nothing else built (no Cortex, no defensive turrets,
    /// no tech tier-ups yet). Add those in later slices when this works.
    /// </summary>
    internal static class AlienConstruction
    {
        // Rule-1 knobs
        // Starter BCs — the first STARTER_BC_COUNT go up unconditionally so the AI has
        // some eco to spend from. Beyond that, additional BCs only go up when we're
        // eco-saturated AND we can afford to spend the credits (Rule 1b — BC delay).
        const int   STARTER_BC_COUNT      = 3;
        const int   ABSOLUTE_MAX_BCS      = 10;    // safety upper limit
        // BC counts as "assigned" to a biotics if placed within this radius. Sized
        // to accommodate the biotics deposit's own no-build zone (~60m physical
        // footprint) plus the placement search radius below, so a BC that lands at
        // the edge of the search still counts as this patch's BC.
        const float BC_ASSIGNED_RADIUS    = 140f;
        // In-reach heuristic — if the biotics is at most this far from any owned
        // structure CENTER, try BC placement directly. If that placement lands too far
        // from the biotics (see BC_PLACEMENT_QUALITY_RADIUS), we reject the placement
        // and force a node instead. So the exact value of this threshold matters less
        // than the placement-quality post-check — but keeping it moderate avoids
        // spamming node placements at every biotics.
        const float BC_IN_REACH_RADIUS    = 250f;

        // Post-placement quality gate. The game's placement API is happy to land the
        // BC up to PLACEMENT_SEARCH_RADIUS from the biotics center — but if the
        // returned spot is way off (BC lands 100m on the Nest-side of the biotics
        // because that's where team coverage reaches), the biotics is NOT actually
        // tap-able from that BC. Reject those and mark the biotics for node treatment.
        const float BC_PLACEMENT_QUALITY_RADIUS = 90f;

        // Rule 1b — BC delay knobs. Skip a candidate expansion BC when either:
        //   - existing infrastructure is not yet at capacity (why add more if not full?)
        //   - reserves would drop below the safety buffer (save cash for other slices:
        //     Cortex, defense, military; those come in future slices).
        const int   BC_COST_ESTIMATE      = 500;
        const int   RESERVE_SAFETY_BUFFER = 1500;   // keep this many credits free
        const int   ECO_SATURATION_PCT    = 90;     // shrimps >= 90% of capacity → saturated

        // Rule-2 knobs — cluster-based, map-adaptive.
        // Cluster BCs by proximity, then place Cysts by cluster size instead of
        // per-BC or per-fixed-radius. Two BCs belong to the same cluster if any BC
        // already in the cluster is within CLUSTER_JOIN_RADIUS of them (transitive
        // — 3 BCs each 500m apart form one cluster even though the endpoints are
        // 1000m apart). One knob replaces all three previous fixed radii.
        const float CLUSTER_JOIN_RADIUS   = 600f;
        // How many Cysts a cluster of size N needs. Roughly 1 Cyst per 3 BCs, min 1.
        //   1-3 BCs → 1 Cyst
        //   4-6 BCs → 2 Cysts
        //   7-9 BCs → 3 Cysts
        // Formula: 1 + (size - 1) / 3
        // BC-to-Cyst practical distance — if a BC has a Cyst within this radius it's
        // already served, don't pick it as the placement spot when adding more Cysts
        // to the cluster.
        const float BC_HAS_LOCAL_CYST_RADIUS = 100f;

        // Starter phase — the first STARTER_CYST_COUNT Cysts go up one-per-BC,
        // ignoring cluster geometry, to prioritise fast starter shrimp ramp. Only
        // AFTER hitting this floor do we switch to the map-adaptive cluster rule.
        // Matches STARTER_BC_COUNT so each starter BC gets its own Cyst.
        const int STARTER_CYST_COUNT = STARTER_BC_COUNT;

        // Rule-3 knobs — node chain toward distant biotics
        const int   MAX_NODES = 30;                // hard team-level cap on nodes we place
        // Search radius for node placement — we want the placement search to slide the
        // node to the nearest reachable spot toward the biotics target, so this is
        // deliberately wide (unlike BC/Cyst which need tight placement).
        const float NODE_SEARCH_RADIUS = 500f;

        // Placement — search radius around the target biotics for a valid placement.
        // Widened from 60m to 120m in v0.7.3 because biotics deposits have a physical
        // no-build footprint (~40-60m). A 60m search often finds NO valid spots — the
        // whole search circle overlaps the deposit itself. 120m lets us find a spot in
        // the annulus around the deposit while still keeping the BC close (BC_ASSIGNED_RADIUS
        // is sized to accept the widest possible placement within this radius).
        const float PLACEMENT_SEARCH_RADIUS = 120f;

        // Per-type placement quota per tick — separate so BC placement doesn't
        // starve Cyst placement in the starter phase (both need to happen fast).
        const int MAX_BC_PER_TICK   = 3;
        const int MAX_CYST_PER_TICK = 3;
        const int MAX_NODE_PER_TICK = 2;

        // How long to consider a placement "pending" before we assume it failed silently.
        // Prevents duplicate fires for the same target if the callback never lands.
        const int PENDING_TIMEOUT_TICKS = 40;

        // ---- State ----
        static int _tickCounter;
        static ConstructionData? _bcCd;
        static ConstructionData? _cystCd;
        static ConstructionData? _nodeCd;

        // Pending placements keyed by target position (use squared-distance dedup on position).
        // Value = tick number when fired.
        static readonly Dictionary<Vector3, int> _pendingBc   = new Dictionary<Vector3, int>();
        static readonly Dictionary<Vector3, int> _pendingCyst = new Dictionary<Vector3, int>();
        // Node dedup is per-BIOTICS-TARGET (not per placement) — we want at most one
        // outstanding node placement per uncovered patch at any time.
        static readonly Dictionary<Vector3, int> _pendingNodeForBiotics = new Dictionary<Vector3, int>();
        // Biotics we tried to BC directly but the placement landed too far → force node
        // path next tick. Cleared when a node lands closer OR when the biotics finally
        // gets a nearby BC.
        static readonly HashSet<Vector3> _forceNodeForBiotics = new HashSet<Vector3>();
        // Safety net: cap nodes we fire toward any single biotics. If we've already
        // laid 4 nodes at the same target, further node fires won't help — either the
        // path is blocked or the game's placement API is returning stale spots.
        static readonly Dictionary<Vector3, int> _nodesFiredForBiotics = new Dictionary<Vector3, int>();
        const int MAX_NODES_PER_BIOTICS = 4;

        // Counters for round summary.
        internal static int BcAttempts, BcSuccesses, BcFailures;
        internal static int CystAttempts, CystSuccesses, CystFailures;
        internal static int NodeAttempts, NodeSuccesses, NodeFailures;
        // Diagnostics: how often the new intelligence rules fired.
        internal static int BcDelayedNotSaturated;   // eco below capacity → wait
        internal static int BcDelayedNotAffordable;  // cash below safety → wait
        internal static int CystSkippedClusterFull;  // cluster already has its Cyst quota
        internal static int ClustersDetected;        // largest cluster count seen this round

        // Scratch
        static readonly List<ResourceArea> _biotics = new List<ResourceArea>(16);

        // ---- The takeover ----

        [HarmonyPatch(typeof(AIConstructionHandler), nameof(AIConstructionHandler.Think))]
        static class Patch_Think
        {
            static bool Prefix(AIConstructionHandler __instance)
            {
                try { return HandleTick(__instance); }
                catch (Exception ex)
                {
                    MelonLogger.Warning("[RTSA/P32] AlienConstruction threw: " + ex.Message);
                    return true;   // if we blow up, fall back to stock so game doesn't freeze
                }
            }
        }

        /// <summary>Returns true = let stock run; false = we own this tick.</summary>
        static bool HandleTick(AIConstructionHandler h)
        {
            var team = h?.Commander?.Team;
            if (team == null) return true;
            if (!(team.name ?? "").Contains("Alien")) return true;
            // Master switch — if our Alien AI is disabled, let stock run.
            if (!FactionControl.IsEnabled(team)) return true;

            // Resolve ConstructionData and derive the planner's constants BEFORE
            // any early return.
            //
            // This used to sit below the ExecutionEnabled check, which meant it
            // NEVER RAN in the mode we actually play: the eco planner owns
            // execution, so HandleTick returned at that check every single tick
            // and [ECO/COSTS] never once appeared in a log. The planner spent
            // its whole life on the hardcoded fallbacks — BC=500, Cyst=1500,
            // Node=100, and chain reach 200m.
            //
            // Node COST is the damaging one. Si_UnitBalance sets Node to
            // base 100 with cost_mult 2, so a Node really costs 200 — the
            // planner has been budgeting 100 and paying 200 all along. Every
            // cash guard, every sequence cost and the beam's whole cost side
            // were built on half the true price of the most frequently placed
            // structure on the board.
            //
            // Chain reach happens to be 200m, matching the fallback, so that
            // one was right by luck. Reading it rather than assuming it is the
            // point: the spawn layout carries alienNodeChainRange=150 and the
            // mod overrides it, and only the game knows which won.
            // ONE-SHOT. These values cannot change mid-round, so once the read
            // has landed this whole block is skipped — no per-tick cost for a
            // lookup that only needs to happen at round start. The flag lives
            // in EcoSimulator and is cleared by its ResetForNewRound.
            if (!Planning.EcoSimulator.CostsResolved)
            {
                EnsureConstructionData(h, team);
                if (_bcCd != null)
                {
                    Perception.GameConstantsDumper.MaybeDumpAlien(team, _bcCd, _cystCd, _nodeCd);
                    Planning.EcoSimulator.SetCostsFromCds(_bcCd, _cystCd, _nodeCd);
                }
            }

            // Eco-planner takeover: when the planner is executing, block vanilla
            // AI construction AND our own phased-rule code path. The planner is
            // the sole decision-maker for alien construction in this mode —
            // otherwise the two AIs spend from the same cash pool and step on
            // each other (observed symptom: vanilla auto-nodes eating the cash
            // the planner needed for a Cyst).
            if (Planning.EcoPlanner.ExecutionEnabled) return false;

            if (!Suppression.Phase31_Production.OverrideByTeam.TryGetValue(team, out bool ov) || !ov) return true;

            // We own it from here.
            _tickCounter++;
            EnsureConstructionData(h, team);   // cheap + cached; needed by the phased placer below
            if (_bcCd == null) return false;   // BC essential; without it we can't do anything useful

            var anchor = FindStructureThatCanBuild(team, _bcCd);
            if (anchor == null) return false;

            PruneExpired(_pendingBc);
            PruneExpired(_pendingCyst);
            PruneExpired(_pendingNodeForBiotics);

            // Snapshot on EVERY alien tick — used to only fire in Phase B (post-
            // starter), which meant the starter phase had no layer telemetry and no
            // Alien layers appeared in the viewer until well into the round.
            Perception.MapLayers.LayerReplay.MaybeSnapshot(_tickCounter, team);
            Perception.BcMetrics.TickAlien(team);

            int firedBcThisTick = 0;
            int firedCystThisTick = 0;
            int firedNodeThisTick = 0;

            // ---- Rule 1: BC at each uncovered biotics ----
            // v0.7.55: switched from GetKnownResourcesAreas → AllResourceAreas.
            // The "known" list depends on team FoW reveal timing which is unreliable
            // (user reported Nest-adjacent biotics being reported as "unknown" until
            // scouted, even though the game reveals them at spawn per config
            // Resources_Aliens_RevealClosestAreaOnStart=true). Since this is an AI
            // planner (not a UI showing what the human can see), reading the full
            // map is fine and matches how a competent player would treat starter
            // biotics: they're always there, no need to scout.
            _biotics.Clear();
            int patchCount = 0;
            try
            {
                var all = ResourceArea.AllResourceAreas;
                if (all != null)
                {
                    var uType = team.UsableResource;
                    for (int i = 0; i < all.Count; i++)
                    {
                        var ra = all[i];
                        if (ra == null) continue;
                        if (ra.IsEmpty) continue;
                        if (ra.ResourceType != uType) continue;
                        _biotics.Add(ra);
                    }
                    patchCount = _biotics.Count;
                }
            }
            catch (Exception ex) { MelonLogger.Warning("[RTSA/P32] biotics enum threw: " + ex.Message); return false; }

            // Sort ascending by "distance to closest owned structure" — closest patches first.
            _biotics.Sort((a, b) =>
                MinDistOwnedStructure(team, a.SignalCenter).CompareTo(
                MinDistOwnedStructure(team, b.SignalCenter)));

            int bcCount = CountOwnedStructuresOfType(team, "Bio Cache");
            int nodeCount = CountOwnedStructuresOfType(team, "Node");

            // Once-per-tick eco snapshot for Rule 1b (BC delay).
            int shrimpCount = CountUnitsByName(team, "Shrimp");
            int shrimpCapacity = SHRIMPS_PER_BC_STAT * bcCount;   // 12/BC, matches AlienShrimpProducer
            bool ecoAtCapacity = shrimpCapacity > 0
                && shrimpCount * 100 >= shrimpCapacity * ECO_SATURATION_PCT;
            int cashOnHand = SafeTotalResources(team);
            bool canAffordExpansion = cashOnHand >= (BC_COST_ESTIMATE + RESERVE_SAFETY_BUFFER);

            foreach (var patch in _biotics)
            {
                if (bcCount >= ABSOLUTE_MAX_BCS) break;
                if (patch == null) continue;

                Vector3 pos = patch.SignalCenter;
                if (HasStructureTypeNear(team, "Bio Cache", pos, BC_ASSIGNED_RADIUS)) continue;
                if (_pendingBc.ContainsKey(pos)) continue;

                // Rule 3: if the patch is too far for direct BC placement, chain nodes
                // toward it. Each Node placement search lands the node at the nearest
                // reachable spot toward the biotics — over successive ticks the chain
                // extends until the patch comes within BC_IN_REACH_RADIUS and rule 1
                // takes over.
                float distToOwned = MinDistOwnedStructure(team, pos);

                // Auto-clear a stale force-node flag once a node has landed tight
                // enough that a new BC placement should succeed tight-to-biotics.
                // Without this, one rejected BC placement early-round leaves the flag
                // stuck ON forever → we keep laying nodes at a biotics whose
                // distToOwned is already 6m (visible in the round log as a pile of
                // Node fires at the same target).
                if (_forceNodeForBiotics.Contains(pos) && distToOwned < BC_PLACEMENT_QUALITY_RADIUS)
                {
                    _forceNodeForBiotics.Remove(pos);
                    Si_RTS_AI.AppendToRound(
                        $"[P32] team={team.name} cleared force-node patchAt=({pos.x:F0},{pos.z:F0}) " +
                        $"distToOwned={distToOwned:F0}m — retrying BC placement");
                }

                if (distToOwned > BC_IN_REACH_RADIUS || _forceNodeForBiotics.Contains(pos))
                {
                    if (_nodeCd == null) continue;
                    if (nodeCount >= MAX_NODES) continue;
                    if (firedNodeThisTick >= MAX_NODE_PER_TICK) continue;
                    if (_pendingNodeForBiotics.ContainsKey(pos)) continue;
                    if (_nodesFiredForBiotics.TryGetValue(pos, out int firedForThisBiotics)
                        && firedForThisBiotics >= MAX_NODES_PER_BIOTICS) continue;

                    // Anchor at the CLOSEST builder to the target, not just any structure.
                    // Otherwise placement lands somewhere in team coverage, but the
                    // Construct call runs against a distant anchor whose coverage doesn't
                    // reach that spot → construction stalls "out of chain range."
                    var nodeAnchor = FindClosestStructureThatCanBuild(team, _nodeCd, pos);
                    if (nodeAnchor == null) continue;

                    _pendingNodeForBiotics[pos] = _tickCounter;
                    _nodesFiredForBiotics[pos] = (_nodesFiredForBiotics.TryGetValue(pos, out int prev) ? prev : 0) + 1;
                    NodeAttempts++;
                    Vector3 firedForNode = pos;
                    try
                    {
                        ConstructionPlacement.QueueFirstValidPlacementAroundPoint(
                            _nodeCd.ObjectPreviewSetup, team, nodeAnchor, firedForNode,
                            _nodeCd.GridSnapXZ, _nodeCd.GridSnapY,
                            NODE_SEARCH_RADIUS,   // deliberately WIDE for nodes → slide toward biotics
                            8f, 300,   // maxTime 40s → 8s: fail-fast for retry
                            (cd, callingTeam, callingStructure, gotPos, gotRot) =>
                            {
                                NodeSuccesses++;
                                _pendingNodeForBiotics.Remove(firedForNode);
                                try { callingStructure?.Construct(cd, gotPos, gotRot); } catch { }
                            },
                            (cd, callingTeam, callingStructure) =>
                            {
                                NodeFailures++;
                                _pendingNodeForBiotics.Remove(firedForNode);
                            });
                        nodeCount++;
                        firedNodeThisTick++;
                        Si_RTS_AI.AppendToRound(
                            $"[P32] team={team.name} fire=Node towardPatchAt=({pos.x:F0},{pos.z:F0}) " +
                            $"distToOwned={distToOwned:F0}m nodesOwned={nodeCount} " +
                            $"anchorAt=({nodeAnchor.transform.position.x:F0},{nodeAnchor.transform.position.z:F0})");
                    }
                    catch (Exception ex) { MelonLogger.Warning("[RTSA/P32] Node fire threw: " + ex.Message); }
                    continue;   // don't fall through to BC path for this patch
                }

                // Rule 1b — BC delay for the expansion phase (bcCount ≥ STARTER_BC_COUNT).
                //   Starter BCs go up unconditionally so we have some eco to spend.
                //   Beyond the starter count: only build another BC when
                //     (a) existing infrastructure is at capacity — otherwise adding more
                //         eco is wasted on unused shrimp slots, and
                //     (b) we can afford it without dropping below the safety reserve —
                //         cash saved here funds Cortex tech-up / defense / military
                //         (future slices).
                //   If either check fails, we intentionally skip and let the node chain
                //   keep growing toward this patch; a future tick that satisfies both
                //   conditions gets the BC placed instantly at biotics.
                if (bcCount >= STARTER_BC_COUNT)
                {
                    if (!ecoAtCapacity)
                    {
                        BcDelayedNotSaturated++;
                        continue;
                    }
                    if (!canAffordExpansion)
                    {
                        BcDelayedNotAffordable++;
                        continue;
                    }
                }

                if (firedBcThisTick >= MAX_BC_PER_TICK) continue;

                _pendingBc[pos] = _tickCounter;
                BcAttempts++;

                // Use the CLOSEST BC-capable structure as anchor so the callback's
                // Construct() runs against a nearby structure whose coverage reaches
                // the placement — otherwise BC placements far from the Nest may stall.
                var bcAnchor = FindClosestStructureThatCanBuild(team, _bcCd, pos) ?? anchor;

                // Local closure capture — 'pos' is read-only here so identity is stable.
                Vector3 firedFor = pos;
                FirePlacement(_bcCd, team, bcAnchor, firedFor,
                    onSuccess: (cd, callingTeam, callingStructure, gotPos, gotRot) =>
                    {
                        // Placement quality gate — if BC landed too far from the biotics,
                        // reject and force the node path so we extend coverage first and
                        // retry BC placement tight-to-biotics.
                        float dx = gotPos.x - firedFor.x, dz = gotPos.z - firedFor.z;
                        float d2 = dx * dx + dz * dz;
                        if (d2 > BC_PLACEMENT_QUALITY_RADIUS * BC_PLACEMENT_QUALITY_RADIUS)
                        {
                            BcFailures++;
                            _pendingBc.Remove(firedFor);
                            _forceNodeForBiotics.Add(firedFor);
                            Si_RTS_AI.AppendToRound(
                                $"[P32] team={team.name} reject=BC-too-far patchAt=({firedFor.x:F0},{firedFor.z:F0}) " +
                                $"landedAt=({gotPos.x:F0},{gotPos.z:F0}) dist={Mathf.Sqrt(d2):F0}m → will node instead");
                            return;   // don't Construct — skipping this callback ends the attempt
                        }
                        BcSuccesses++;
                        // DO NOT remove _pendingBc[firedFor] here — the callback fires
                        // BEFORE the Structure appears in team.Structures. If we cleared
                        // now, next tick would see no BC nearby and fire another one at
                        // the same patch → spam. The tick loop clears it via
                        // ClearPendingForCompletedStructures() once a real BC lands
                        // near the target.
                        // v0.7.54: log Construct result for diagnostic — mirrors the
                        // refinery pattern that revealed AreaObstructed/InsufficientResource.
                        string bcRes = "no-call";
                        try
                        {
                            if (callingStructure != null)
                                bcRes = callingStructure.Construct(cd, gotPos, gotRot).ToString();
                        }
                        catch (Exception cx) { bcRes = "throw:" + cx.Message; }
                        Si_RTS_AI.AppendToRound(
                            $"[P32] team={team.name} constructed=BC atPatch=({firedFor.x:F0},{firedFor.z:F0}) " +
                            $"landedAt=({gotPos.x:F0},{gotPos.z:F0}) result={bcRes}");

                        // Fire Cyst placement IMMEDIATELY at the BC's landed spot —
                        // don't wait for the next Alien tick. Cuts starter Cyst latency
                        // from ~3-5s (next tick) to ~0s (this callback). Shrimp production
                        // starts as fast as the game allows.
                        try { FireStarterCystAt(team, gotPos); }
                        catch (Exception ex) { MelonLogger.Warning("[RTSA/P32] eager Cyst threw: " + ex.Message); }
                    },
                    onFail: (cd, callingTeam, callingStructure) =>
                    {
                        BcFailures++;
                        _pendingBc.Remove(firedFor);
                    });

                bcCount++;               // optimistic — corrected next tick if the placement actually fails
                firedBcThisTick++;

                Si_RTS_AI.AppendToRound(
                    $"[P32] team={team.name} fire=BC patchAt=({pos.x:F0},{pos.z:F0}) " +
                    $"minDistToOwned={MinDistOwnedStructure(team, pos):F0}m bcAttempts={BcAttempts}");
            }

            // ---- Rule 2: Cyst per BC ----
            //   Phase A (starter): the first STARTER_CYST_COUNT Cysts are fired
            //     one-per-BC as fast as possible → fast early shrimp ramp.
            //   Phase B (post-starter): cluster BCs by CLUSTER_JOIN_RADIUS, place
            //     ceil(size/3) Cysts per cluster (map-adaptive) — tight clusters
            //     share shrimps, sparse clusters each get their own Cyst.
            if (_cystCd != null)
            {
                // Collect our completed BC positions (in-progress ones count too —
                // otherwise we'd double-Cyst while BCs finish).
                var bcPositions = CollectStructurePositionsIncludingSites(team, "Bio Cache");
                int cystOwned = CountOwnedStructuresOfType(team, "Lesser Spawning Cyst");
                bool inStarterCystPhase = cystOwned < STARTER_CYST_COUNT;

                if (inStarterCystPhase && bcPositions.Count > 0)
                {
                    // Phase A — one Cyst per BC, cluster geometry ignored. Walk the
                    // BCs and fire at the first one that doesn't already have a Cyst
                    // right on top of it (or a pending placement). One per tick.
                    foreach (var bcPos in bcPositions)
                    {
                        if (firedCystThisTick >= MAX_CYST_PER_TICK) break;
                        if (HasStructureTypeNear(team, "Lesser Spawning Cyst", bcPos, BC_HAS_LOCAL_CYST_RADIUS)) continue;
                        // Radius-based dedup (see FireStarterCystAt) — Vector3 exact
                        // match misses on Y-precision differences, causing eager +
                        // starter to both fire for the same BC.
                        if (HasPendingCystNear(bcPos, 50f)) continue;

                        _pendingCyst[bcPos] = _tickCounter;
                        CystAttempts++;
                        Vector3 firedForStarter = bcPos;
                        // Anchor at the closest Cyst-capable structure to the target —
                        // usually the BC itself once it exists, otherwise the Nest.
                        var starterCystAnchor = FindClosestStructureThatCanBuild(team, _cystCd, bcPos) ?? anchor;
                        FirePlacement(_cystCd, team, starterCystAnchor, firedForStarter,
                            onSuccess: (cd, callingTeam, callingStructure, gotPos, gotRot) =>
                            {
                                CystSuccesses++;
                                string res = "no-call";
                                try { if (callingStructure != null) res = callingStructure.Construct(cd, gotPos, gotRot).ToString(); }
                                catch (Exception ex) { res = "throw:" + ex.Message; }
                                Si_RTS_AI.AppendToRound(
                                    $"[P32] team={team.name} constructed=Cyst phase=starter atBcAt=({firedForStarter.x:F0},{firedForStarter.z:F0}) result={res}");
                            },
                            onFail: (cd, callingTeam, callingStructure) =>
                            {
                                CystFailures++;
                                _pendingCyst.Remove(firedForStarter);
                            });
                        firedCystThisTick++;

                        Si_RTS_AI.AppendToRound(
                            $"[P32] team={team.name} fire=Cyst phase=starter " +
                            $"atBcAt=({bcPos.x:F0},{bcPos.z:F0}) cystOwned={cystOwned}/{STARTER_CYST_COUNT}");
                    }
                }
                else if (bcPositions.Count > 0)
                {
                    // Phase B — map-layer-driven placement (v0.7.4).
                    //   Rebuild layers → CystPressure encodes (biotics cluster density) ×
                    //   (BC reach mask) minus penalties around existing Cysts. Argmax gives
                    //   the highest-value cell to place a new Cyst. Fires up to
                    //   MAX_CYST_PER_TICK Cysts per tick, subtracting a coverage disk
                    //   after each fire so subsequent argmaxes don't stack.
                    // Lazy-rebuild path: GetCystPressure recomputes only the dirty
                    // layers up the dependency chain (biotics/mask/weighted/pressure).
                    var pressure = Perception.MapLayers.AlienEcoLayers.GetCystPressure(team);
                    Perception.MapLayers.LayerReplay.MaybeSnapshot(_tickCounter, team);

                    while (firedCystThisTick < MAX_CYST_PER_TICK)
                    {
                        var (cx, cz, val) = pressure.ArgMax();
                        if (cx < 0 || val <= 0f) break;   // no cell wants a Cyst

                        Vector3 target = Perception.MapLayers.GridWorld.CellCenter(cx, cz);
                        if (_pendingCyst.ContainsKey(target))
                        {
                            // Prevent the same argmax firing every tick before its
                            // callback runs — subtract this cell's coverage and re-argmax.
                            pressure.SubtractDiskAtWorld(target,
                                Perception.MapLayers.AlienEcoLayers.CYST_COVERAGE_METERS,
                                Perception.MapLayers.AlienEcoLayers.CYST_PENALTY);
                            continue;
                        }

                        _pendingCyst[target] = _tickCounter;
                        CystAttempts++;

                        Vector3 firedFor = target;
                        var clusterCystAnchor = FindClosestStructureThatCanBuild(team, _cystCd, target) ?? anchor;
                        FirePlacement(_cystCd, team, clusterCystAnchor, firedFor,
                            onSuccess: (cd, callingTeam, callingStructure, gotPos, gotRot) =>
                            {
                                CystSuccesses++;
                                try { callingStructure?.Construct(cd, gotPos, gotRot); } catch { }
                            },
                            onFail: (cd, callingTeam, callingStructure) =>
                            {
                                CystFailures++;
                                _pendingCyst.Remove(firedFor);
                            });

                        firedCystThisTick++;

                        Si_RTS_AI.AppendToRound(
                            $"[P32] team={team.name} fire=Cyst phase=layers " +
                            $"argmaxCell=({cx},{cz}) pressure={val:F1} atWorld=({target.x:F0},{target.z:F0})");

                        // Subtract this cell's Cyst-coverage disk so the next argmax
                        // picks a different cluster instead of the same one.
                        pressure.SubtractDiskAtWorld(target,
                            Perception.MapLayers.AlienEcoLayers.CYST_COVERAGE_METERS,
                            Perception.MapLayers.AlienEcoLayers.CYST_PENALTY);
                    }
                }
            }

            // Ride-along: keep Shrimp production topped up. Direct Cyst.Construct calls,
            // because relying on stock's AIUnitHandler → Cyst path only got us 9 shrimps
            // per round despite Phase 3.1 picking Shrimp 76 times.
            AlienShrimpProducer.Tick(team);
            Perception.EcoRateSampler.Tick(team);
            Perception.BcIncome.Sample(team, "Bio Cache");
            Perception.UnitCaps.Resolve(team);
            Planning.DefencePlanner.Tick(team);

            return false;   // skip stock Think entirely
        }

        // ---- Helpers ----

        // Free-construct BC (Alien eco storage buffer per user spec). Grant + restore
        // team.TotalResources around Construct so cost doesn't hit the bank. Mirrors
        // the pattern in HumanConstruction.ConstructFree.
        static System.Reflection.FieldInfo? _totalResourcesField;
        static void ConstructFreeBc(Structure? cs, ConstructionData cd, Vector3 pos, Quaternion rot, Team team)
        {
            if (cs == null) return;
            if (_totalResourcesField == null)
            {
                foreach (var name in new[] { "<TotalResources>k__BackingField", "m_TotalResources", "_totalResources" })
                {
                    var fi = typeof(Team).GetField(name,
                        System.Reflection.BindingFlags.Instance |
                        System.Reflection.BindingFlags.NonPublic |
                        System.Reflection.BindingFlags.Public);
                    if (fi != null && fi.FieldType == typeof(int)) { _totalResourcesField = fi; break; }
                }
            }
            if (_totalResourcesField == null) { cs.Construct(cd, pos, rot); return; }
            int saved = 0;
            try { saved = (int)_totalResourcesField.GetValue(team); } catch { }
            try { _totalResourcesField.SetValue(team, 999999); } catch { }
            try { cs.Construct(cd, pos, rot); }
            finally { try { _totalResourcesField.SetValue(team, saved); } catch { } }
        }

        static bool HasPendingCystNear(Vector3 pos, float radius)
        {
            float r2 = radius * radius;
            foreach (var kv in _pendingCyst)
            {
                float dx = kv.Key.x - pos.x, dz = kv.Key.z - pos.z;
                if (dx * dx + dz * dz < r2) return true;
            }
            return false;
        }

        /// <summary>
        /// Fire ONE Cyst placement targeted at a specific BC position — used from the
        /// BC success callback so Cyst placement starts the moment BC placement is
        /// accepted rather than the next tick. Idempotent: dedups via _pendingCyst
        /// and skips when a Cyst is already near.
        /// </summary>
        static void FireStarterCystAt(Team team, Vector3 bcPos)
        {
            if (_cystCd == null) return;
            // Radius-based dedup instead of exact-Vector3-key — eager callback's gotPos
            // and starter phase's construction-site position differ on Y (float precision),
            // so exact-key dedup misses and we double-fire (both fire=Cyst phase=eager-at-BC
            // and phase=starter appeared for the same BC in the round log). 50m tolerance
            // matches "clearly the same BC / clearly a different one".
            if (HasPendingCystNear(bcPos, 50f)) return;
            if (HasStructureTypeNear(team, "Lesser Spawning Cyst", bcPos, BC_HAS_LOCAL_CYST_RADIUS)) return;

            var anchor = FindClosestStructureThatCanBuild(team, _cystCd, bcPos);
            if (anchor == null) return;

            _pendingCyst[bcPos] = _tickCounter;
            CystAttempts++;
            Vector3 firedFor = bcPos;
            FirePlacement(_cystCd, team, anchor, firedFor,
                onSuccess: (cd, ct, cs, gotPos, gotRot) =>
                {
                    CystSuccesses++;
                    string res = "no-call";
                    try { if (cs != null) res = cs.Construct(cd, gotPos, gotRot).ToString(); }
                    catch (Exception ex) { res = "throw:" + ex.Message; }
                    Si_RTS_AI.AppendToRound(
                        $"[P32] team={team.name} constructed=Cyst atBcAt=({firedFor.x:F0},{firedFor.z:F0}) " +
                        $"landedAt=({gotPos.x:F0},{gotPos.z:F0}) result={res}");
                },
                onFail: (cd, ct, cs) =>
                {
                    CystFailures++;
                    _pendingCyst.Remove(firedFor);
                    Si_RTS_AI.AppendToRound(
                        $"[P32] team={team.name} fail=Cyst placement atBcAt=({firedFor.x:F0},{firedFor.z:F0})");
                });

            Si_RTS_AI.AppendToRound(
                $"[P32] team={team.name} fire=Cyst phase=eager-at-BC " +
                $"atBcAt=({bcPos.x:F0},{bcPos.z:F0})");
        }

        // Planner entry point — same commander-build path a real player uses.
        // ConstructionPlacement searches for a valid clear spot near targetPos,
        // and Structure.Construct starts construction with the game's own cost
        // deduction and chain-reach validation. Returns true iff we successfully
        // queued a placement search (the game callback later actually starts
        // the site, or fails silently if the spot is unbuildable).
        //
        // Kept in this file so it shares the _bcCd/_cystCd/_nodeCd cache and the
        // FirePlacement + FindClosestStructureThatCanBuild helpers — no reason
        // to duplicate them in the planner.
        // ---- Accepted construction orders ---------------------------------
        // (name, position, Time.time) for every Construct that returned Success.
        // The structure itself may not exist for some seconds afterwards, so
        // this is the only reliable "already ordered" signal.
        /// <summary>
        /// Pos is where the structure actually went; Want is where we asked for
        /// it. The placement search relocates a request by up to ~100m, so a
        /// radius match against the requested point alone misses its own order:
        /// NarakaCity v0.8.34, Cyst requested at (2269,1907) landed at
        /// (2310,1825) — 92m away, just outside the 90m guard — so a second
        /// Cyst went down 21m from the first.
        /// </summary>
        struct Ordered { public string Name; public Vector3 Pos; public Vector3 Want; public float At; }
        static readonly List<Ordered> _ordered = new List<Ordered>();
        const float ORDER_MEMORY_S = 240f;

        static void NoteConstructed(string name, Vector3 pos) { NoteConstructed(name, pos, pos); }

        static void NoteConstructed(string name, Vector3 pos, Vector3 want)
        {
            _ordered.Add(new Ordered { Name = name, Pos = pos, Want = want, At = Time.time });
        }

        /// <summary>
        /// Seconds since a structure of this name was ordered near here, or -1
        /// if none was. This is the only trustworthy build-START signal we have:
        /// a structure does not appear in team.Structures under its finished
        /// DisplayName until construction COMPLETES, so anything derived from
        /// the team's structure list is a completion time wearing a start's
        /// name. Measured on NarakaCity: Bio Caches ordered at roundT=21s first
        /// showed up in the list at 52s (= order + TotalConstructionTime 30s),
        /// Cysts ordered at 72s showed up at 108s.
        /// </summary>
        internal static float SecondsSinceOrderedNear(string name, Vector3 pos, float radiusM)
        {
            float now = Time.time;
            float best = -1f;
            float r2 = radiusM * radiusM;
            for (int i = 0; i < _ordered.Count; i++)
            {
                if (!string.Equals(_ordered[i].Name, name, StringComparison.OrdinalIgnoreCase)) continue;
                float wx = _ordered[i].Want.x - pos.x, wz = _ordered[i].Want.z - pos.z;
                float dx = _ordered[i].Pos.x - pos.x, dz = _ordered[i].Pos.z - pos.z;
                if (wx * wx + wz * wz > r2 && dx * dx + dz * dz > r2) continue;
                float age = now - _ordered[i].At;
                if (age > best) best = age;   // oldest match = the one most likely done
            }
            return best;
        }

        /// <summary>
        /// Positions of accepted orders of this name that are at least
        /// <paramref name="minAgeS"/> old — i.e. far enough along to be worth
        /// treating as a chain anchor before they finish outright.
        ///
        /// team.Structures only lists COMPLETED structures, so anchoring off it
        /// waits the full construction time (a Node measured 20s on NarakaCity)
        /// when the build-up that makes it usable is shorter (12s off the CDs).
        /// If the game disagrees the placement is simply refused and retried a
        /// second later, which costs a log line rather than a structure.
        /// </summary>
        internal static void CollectOrdersOlderThan(string name, float minAgeS, List<Vector3> into)
        {
            if (into == null) return;
            float now = Time.time;
            for (int i = 0; i < _ordered.Count; i++)
            {
                if (!string.Equals(_ordered[i].Name, name, StringComparison.OrdinalIgnoreCase)) continue;
                if (now - _ordered[i].At < minAgeS) continue;
                into.Add(_ordered[i].Pos);
            }
        }

        /// <summary>
        /// Radius within which a second structure of the same kind is a
        /// duplicate rather than a deliberate neighbour. Cysts and Bio Caches
        /// are one-per-patch; Nodes deliberately chain ~110m apart and are
        /// excluded entirely.
        /// </summary>
        const float DUP_RADIUS_M = 90f;

        static bool IsDuplicateNow(Team team, string name, Vector3 pos)
        {
            if (!string.Equals(name, "Lesser Spawning Cyst", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(name, "Bio Cache", StringComparison.OrdinalIgnoreCase))
                return false;

            if (WasOrderedNear(name, pos, DUP_RADIUS_M)) return true;

            try
            {
                var structs = team?.Structures;
                if (structs == null) return false;
                float r2 = DUP_RADIUS_M * DUP_RADIUS_M;
                for (int i = 0; i < structs.Count; i++)
                {
                    var st = structs[i];
                    if (st == null || st.ObjectInfo == null || st.IsDestroyed) continue;
                    if (!string.Equals(st.ObjectInfo.DisplayName ?? "", name,
                                       StringComparison.OrdinalIgnoreCase)) continue;
                    Vector3 q = st.transform.position;
                    float dx = q.x - pos.x, dz = q.z - pos.z;
                    if (dx * dx + dz * dz <= r2) return true;
                }
            }
            catch { }
            return false;
        }

        /// <summary>Was a structure of this name ordered near here recently?</summary>
        internal static bool WasOrderedNear(string name, Vector3 pos, float radiusM)
        {
            float now = Time.time;
            for (int i = _ordered.Count - 1; i >= 0; i--)
                if (now - _ordered[i].At > ORDER_MEMORY_S) _ordered.RemoveAt(i);

            float r2 = radiusM * radiusM;
            for (int i = 0; i < _ordered.Count; i++)
            {
                if (!string.Equals(_ordered[i].Name, name, StringComparison.OrdinalIgnoreCase)) continue;
                float wx = _ordered[i].Want.x - pos.x, wz = _ordered[i].Want.z - pos.z;
                if (wx * wx + wz * wz <= r2) return true;
                float dx = _ordered[i].Pos.x - pos.x, dz = _ordered[i].Pos.z - pos.z;
                if (dx * dx + dz * dz < r2) return true;
            }
            return false;
        }

        internal static void ClearOrderedForNewRound()
        {
            _ordered.Clear(); _inFlight.Clear();
            _consecutiveSearchFails = 0; _placementBackoffUntil = 0f;
            _currentBackoffS = BACKOFF_MIN_S;
        }

        internal static bool TryBuildStructureForPlanner(
            Team team,
            Planning.EcoPlanner.ActionKind kind,
            Vector3 targetPos)
        {
            EnsureConstructionDataFromTeam(team);
            ConstructionData? cd =
                kind == Planning.EcoPlanner.ActionKind.PlaceBc   ? _bcCd :
                kind == Planning.EcoPlanner.ActionKind.PlaceCyst ? _cystCd :
                kind == Planning.EcoPlanner.ActionKind.PlaceNode ? _nodeCd : null;
            if (cd == null) return false;
            return TryBuildStructureByCd(team, cd, targetPos);
        }

        // Same as above but takes a raw ConstructionData — used by
        // TechPlanner (which discovers Cortex/tech-tier CDs at runtime
        // from ConstructionOptions.IsTechTier, not from the eco enum).
        /// <summary>
        /// One outstanding placement search PER SPOT — not one per team.
        ///
        /// QueueFirstValidPlacementAroundPoint resolves ASYNCHRONOUSLY, and the
        /// retry loops re-fire every few seconds without waiting. When a target
        /// is briefly out of reach the requests pile up, and the moment reach
        /// opens they all resolve at once, each calling Construct. NarakaCity
        /// 2026-07-30: ten queued Cyst searches for (2279,735) produced
        /// structures at (2385,820), (2365,825) and an attempt at (2345,830) —
        /// three Cysts 20m apart on one patch. The step's own "confirmed
        /// ordered" landed between the first and second, far too late to matter.
        ///
        /// Keyed per spot rather than globally on purpose. A search that
        /// SUCCEEDS resolves in 25-100ms, but one that FAILS took 1.6s in the
        /// same log — a single team-wide lock would stall every other structure
        /// for that long behind a doomed request. Same-spot serialisation kills
        /// the duplicates without ever making an unrelated placement wait.
        /// </summary>
        const float INFLIGHT_TIMEOUT_S = 6f;
        const float INFLIGHT_RADIUS_M  = 120f;

        struct InFlight { public string Name; public Vector3 Want; public float At; }
        static readonly List<InFlight> _inFlight = new List<InFlight>();

        /// <summary>Is a placement search for this structure already resolving
        /// at roughly this spot?</summary>
        internal static bool SearchInFlightNear(string name, Vector3 want)
        {
            float now = Time.time;
            for (int i = _inFlight.Count - 1; i >= 0; i--)
                if (now - _inFlight[i].At > INFLIGHT_TIMEOUT_S) _inFlight.RemoveAt(i);

            float r2 = INFLIGHT_RADIUS_M * INFLIGHT_RADIUS_M;
            for (int i = 0; i < _inFlight.Count; i++)
            {
                if (!string.Equals(_inFlight[i].Name, name, StringComparison.OrdinalIgnoreCase)) continue;
                float dx = _inFlight[i].Want.x - want.x, dz = _inFlight[i].Want.z - want.z;
                if (dx * dx + dz * dz <= r2) return true;
            }
            return false;
        }

        static void SearchDone(string name, Vector3 want)
        {
            for (int i = _inFlight.Count - 1; i >= 0; i--)
            {
                if (!string.Equals(_inFlight[i].Name, name, StringComparison.OrdinalIgnoreCase)) continue;
                float dx = _inFlight[i].Want.x - want.x, dz = _inFlight[i].Want.z - want.z;
                if (dx * dx + dz * dz < 1f) { _inFlight.RemoveAt(i); return; }
            }
        }

        /// <summary>
        /// Back off when the game refuses everything.
        ///
        /// Construction can be impossible for reasons the planner cannot see —
        /// most importantly, ALIENS CANNOT BUILD AT ALL WHILE THE QUEEN IS OUT
        /// OF THE NEST. On 2026-07-30 a player un-nested the Queen at the start
        /// of a match and disconnected; the planner then queued placement
        /// searches for the same handful of targets for minutes, failing every
        /// one, while 81,000 cash piled up behind 3 shrimps. Seventeen of those
        /// searches were still outstanding when the map was changed to reset it,
        /// and resolving them against a dying scene took the server down.
        ///
        /// So: repeated failure means STOP ASKING for a while, not ask harder.
        /// Any success clears it — this must never latch on a genuinely
        /// temporary refusal such as a spot briefly out of chain reach.
        /// </summary>
        const int   BACKOFF_AFTER_FAILS = 6;
        const float BACKOFF_MIN_S       = 10f;
        const float BACKOFF_MAX_S       = 60f;

        static int   _consecutiveSearchFails;
        static float _placementBackoffUntil;
        static float _currentBackoffS = BACKOFF_MIN_S;

        static void NoteSearchFailed()
        {
            _consecutiveSearchFails++;
            if (_consecutiveSearchFails < BACKOFF_AFTER_FAILS) return;

            _placementBackoffUntil = Time.time + _currentBackoffS;
            MelonLogger.Warning(
                $"[PLAN/EXEC] {_consecutiveSearchFails} placement searches failed in a row — " +
                $"construction looks blocked (is the Queen out of the Nest?). " +
                $"Holding new placements for {_currentBackoffS:F0}s.");
            _consecutiveSearchFails = 0;
            _currentBackoffS = Mathf.Min(BACKOFF_MAX_S, _currentBackoffS * 2f);
        }

        /// <summary>
        /// Is it still safe to touch the objects a placement callback was handed?
        ///
        /// A queued placement search cannot be cancelled, and on a map change
        /// every outstanding one resolves at once against a scene that is being
        /// destroyed. The server died with an ACCESS VIOLATION (0xC0000005)
        /// doing exactly that on 2026-07-30: "[Mapcycle] Changing map to
        /// narakacity" at 23:45:43.273, seventeen placement searches flushed in
        /// the following two milliseconds, then the process went down.
        ///
        /// Reading cbTeam.name or cbStruct on a destroyed Il2Cpp object is a
        /// native read of freed memory — a managed try/catch cannot save it, so
        /// the check has to happen BEFORE the first dereference. IsRoundActive
        /// is the cheap first gate; the null and IsDestroyed tests cover a
        /// teardown already under way when the round flag has not flipped yet.
        /// </summary>
        static bool CallbackTargetsAlive(ConstructionData cd, Team team, Structure anchor)
        {
            try
            {
                if (!TestHarnessNs.TestHarness.IsRoundActive) return false;
                if (cd == null || team == null) return false;
                if (anchor == null || anchor.IsDestroyed) return false;
                return true;
            }
            catch { return false; }
        }

        internal static bool TryBuildStructureByCd(
            Team team,
            ConstructionData cd,
            Vector3 targetPos)
        {
            if (cd == null) return false;
            string cdName = cd.ObjectInfo?.DisplayName ?? "?";
            if (SearchInFlightNear(cdName, targetPos)) return false;
            if (Time.time < _placementBackoffUntil) return false;
            // Direct read of the state the game enforces: no docked Queen, no
            // construction. Cheaper and far more honest than the failure
            // counting below, which only infers it after six refusals.
            if (!Perception.QueenStatus.CanBuild(team)) return false;
            var anchor = FindClosestStructureThatCanBuild(team, cd, targetPos);
            if (anchor == null) return false;
            _inFlight.Add(new InFlight { Name = cdName, Want = targetPos, At = Time.time });
            FirePlacement(cd, team, anchor, targetPos,
                onSuccess: (thisCd, cbTeam, cbStruct, gotPos, gotRot) =>
                {
                    SearchDone(cdName, targetPos);
                    // SCENE TEARDOWN GUARD — see CallbackTargetsAlive.
                    _consecutiveSearchFails = 0;
                    _placementBackoffUntil = 0f;
                    if (!CallbackTargetsAlive(thisCd, cbTeam, cbStruct)) return;
                    try
                    {
                        // LAST-MOMENT DUPLICATE CHECK.
                        //
                        // This is the only place the duplicate can actually be
                        // stopped. A queued placement search cannot be cancelled
                        // and may resolve arbitrarily late — NarakaCity
                        // 2026-07-30, a Cyst search issued at 14:54:08 came back
                        // at 14:54:38, THIRTY seconds later, after its step had
                        // already been satisfied by a different search at
                        // 14:54:35 and marked confirmed. It built a second Cyst
                        // 22m from the first.
                        //
                        // Every earlier guard asked "should I request this?",
                        // which was legal at request time. This asks "is this
                        // still worth building?" at the moment of building, and
                        // against gotPos — where the search actually chose to
                        // put it — rather than where we asked for it.
                        string dupName = thisCd.ObjectInfo?.DisplayName ?? "?";
                        if (IsDuplicateNow(cbTeam, dupName, gotPos))
                        {
                            MelonLogger.Msg("[PLAN/EXEC] team=" + cbTeam.name +
                                            " SKIP stale " + dupName +
                                            " at (" + gotPos.x.ToString("F0") + "," +
                                            gotPos.z.ToString("F0") + ") — one is already " +
                                            "ordered or standing there");
                            return;
                        }

                        // Structure.Construct is what the vanilla commander UI
                        // calls at click-time — it deducts cost, spawns a
                        // ConstructionSite, and enforces every game rule
                        // (no-build zones, chain reach, cap limits).
                        // Capture what Construct actually RETURNS. It was being
                        // discarded, so a refusal looked identical to a success
                        // in the log — which is how 14 "built" lines at one spot
                        // over 70s produced no structure at all.
                        object res = null;
                        try { res = cbStruct?.Construct(thisCd, gotPos, gotRot); }
                        catch (Exception cex)
                        {
                            MelonLogger.Warning("[PLAN/EXEC] Construct threw: " + cex.Message);
                        }
                        // GROUND TRUTH. Construct tells us plainly whether the
                        // order was accepted; record it so callers stop guessing
                        // from structure lists. Two days of duplicate Cysts came
                        // from discarding this and trying to detect the result
                        // by watching team.Structures, which does not show an
                        // ordered-but-not-yet-built structure.
                        if (res != null && res.ToString() == "Success")
                            NoteConstructed(thisCd.ObjectInfo?.DisplayName ?? "?", gotPos, targetPos);

                        float anchorDist = cbStruct != null
                            ? Vector3.Distance(cbStruct.transform.position, gotPos) : -1f;
                        MelonLogger.Msg("[PLAN/EXEC] team=" + cbTeam.name +
                                        " Construct " + (thisCd.ObjectInfo?.DisplayName ?? "?") +
                                        " at (" + gotPos.x.ToString("F0") + "," + gotPos.z.ToString("F0") + ")" +
                                        " anchor=" + (cbStruct?.ObjectInfo?.DisplayName ?? "?") +
                                        " anchorDist=" + anchorDist.ToString("F0") + "m" +
                                        " result=" + (res?.ToString() ?? "void") +
                                        " cash=" + cbTeam.TotalResources);
                    }
                    catch (Exception ex) { MelonLogger.Warning("[PLAN/EXEC] Construct threw: " + ex.Message); }
                },
                onFail: (thisCd, cbTeam, cbStruct) =>
                {
                    SearchDone(cdName, targetPos);
                    NoteSearchFailed();
                    // Nothing was placed, so the planner's repeat-suppression
                    // around this spot is protecting a structure that does not
                    // exist. Release it now rather than after the timer.
                    Planning.EcoPlanner.NoteSearchFailedAt(targetPos);
                    if (!CallbackTargetsAlive(thisCd, cbTeam, cbStruct)) return;
                    MelonLogger.Msg("[PLAN/EXEC] team=" + cbTeam.name +
                                    " placement search FAILED for " + (thisCd.ObjectInfo?.DisplayName ?? "?") +
                                    " near (" + targetPos.x.ToString("F0") + "," + targetPos.z.ToString("F0") + ")");
                });
            return true;
        }

        // Version of EnsureConstructionData that doesn't need an AIConstructionHandler —
        // the planner has no `h` because our HandleTick prefix returns before stock
        // hands us one. Walks team.Structures.ConstructionOptions same as the
        // fallback path of the AIConstructionHandler-flavored EnsureConstructionData.
        static void EnsureConstructionDataFromTeam(Team team)
        {
            if (_bcCd != null && _cystCd != null && _nodeCd != null) return;
            var structs = team.Structures;
            if (structs == null) return;
            for (int i = 0; i < structs.Count; i++)
            {
                var s = structs[i];
                if (s == null || s.ConstructionOptions == null) continue;
                foreach (var opt in s.ConstructionOptions)
                {
                    if (opt?.ObjectInfo == null) continue;
                    string n = opt.ObjectInfo.DisplayName ?? "";
                    if (_bcCd   == null && string.Equals(n, "Bio Cache",            StringComparison.OrdinalIgnoreCase)) _bcCd   = opt;
                    if (_cystCd == null && string.Equals(n, "Lesser Spawning Cyst", StringComparison.OrdinalIgnoreCase)) _cystCd = opt;
                    if (_nodeCd == null && string.Equals(n, "Node",                 StringComparison.OrdinalIgnoreCase)) _nodeCd = opt;
                }
                if (_bcCd != null && _cystCd != null && _nodeCd != null) return;
            }
        }

        static void EnsureConstructionData(AIConstructionHandler h, Team team)
        {
            if (_bcCd != null && _cystCd != null && _nodeCd != null) return;

            // Try stock's list first (populated by prior Think runs, or by handler init).
            var list = h.BuildableStructuresAll;
            if (list != null)
            {
                for (int i = 0; i < list.Count; i++)
                {
                    var cd = list[i];
                    if (cd?.ObjectInfo == null) continue;
                    string n = cd.ObjectInfo.DisplayName ?? "";
                    if (_bcCd   == null && string.Equals(n, "Bio Cache",            StringComparison.OrdinalIgnoreCase)) _bcCd   = cd;
                    if (_cystCd == null && string.Equals(n, "Lesser Spawning Cyst", StringComparison.OrdinalIgnoreCase)) _cystCd = cd;
                    if (_nodeCd == null && string.Equals(n, "Node",                 StringComparison.OrdinalIgnoreCase)) _nodeCd = cd;
                }
                if (_bcCd != null && _cystCd != null && _nodeCd != null) return;
            }

            // Fall back: walk our own structures' ConstructionOptions.
            var structs = team.Structures;
            if (structs != null)
            {
                for (int i = 0; i < structs.Count; i++)
                {
                    var s = structs[i];
                    if (s == null || s.ConstructionOptions == null) continue;
                    foreach (var opt in s.ConstructionOptions)
                    {
                        if (opt?.ObjectInfo == null) continue;
                        string n = opt.ObjectInfo.DisplayName ?? "";
                        if (_bcCd   == null && string.Equals(n, "Bio Cache",            StringComparison.OrdinalIgnoreCase)) _bcCd   = opt;
                        if (_cystCd == null && string.Equals(n, "Lesser Spawning Cyst", StringComparison.OrdinalIgnoreCase)) _cystCd = opt;
                        if (_nodeCd == null && string.Equals(n, "Node",                 StringComparison.OrdinalIgnoreCase)) _nodeCd = opt;
                    }
                    if (_bcCd != null && _cystCd != null && _nodeCd != null) return;
                }
            }
        }

        static Structure? FindStructureThatCanBuild(Team team, ConstructionData targetCd)
        {
            try
            {
                var structs = team.Structures;
                if (structs == null) return null;
                for (int i = 0; i < structs.Count; i++)
                {
                    var s = structs[i];
                    if (s == null || s.IsDestroyed) continue;
                    if (s.ConstructionOptions != null && s.ConstructionOptions.Contains(targetCd))
                        return s;
                }
            }
            catch { }
            return null;
        }

        // Pick the CLOSEST owned structure to targetPos that can construct targetCd.
        // Callers use this as the placement anchor so the callback's
        // callingStructure.Construct(cd, position, rotation) call runs against a
        // structure whose coverage actually reaches the placement spot — otherwise
        // the placement succeeds visually but construction stalls "out of range".
        static Structure? FindClosestStructureThatCanBuild(Team team, ConstructionData targetCd, Vector3 targetPos)
        {
            Structure? best = null;
            float bestDist2 = float.MaxValue;
            try
            {
                var structs = team.Structures;
                if (structs != null)
                    for (int i = 0; i < structs.Count; i++)
                    {
                        var s = structs[i];
                        if (s == null || s.IsDestroyed) continue;
                        if (s.ConstructionOptions == null || !s.ConstructionOptions.Contains(targetCd)) continue;
                        Vector3 sp = s.transform.position;
                        float dx = sp.x - targetPos.x, dz = sp.z - targetPos.z;
                        float d2 = dx * dx + dz * dz;
                        if (d2 < bestDist2) { bestDist2 = d2; best = s; }
                    }
            }
            catch { }
            return best;
        }

        static void FirePlacement(ConstructionData cd, Team team, Structure anchor, Vector3 targetPos,
            Action<ConstructionData, Team, Structure, Vector3, Quaternion> onSuccess,
            Action<ConstructionData, Team, Structure> onFail)
        {
            try
            {
                ConstructionPlacement.QueueFirstValidPlacementAroundPoint(
                    cd.ObjectPreviewSetup,
                    team,
                    anchor,
                    targetPos,
                    cd.GridSnapXZ,
                    cd.GridSnapY,
                    PLACEMENT_SEARCH_RADIUS,
                    8f,                           // maxTime shrunk 40→8s so failed searches retry sooner
                    300,                          // minStepsPerSecond
                    onSuccess,
                    onFail);
            }
            catch (Exception ex) { MelonLogger.Warning("[RTSA/P32] QueueFirstValidPlacement threw: " + ex.Message); }
        }

        static void PruneExpired(Dictionary<Vector3, int> dict)
        {
            List<Vector3>? expired = null;
            foreach (var kv in dict)
            {
                if (_tickCounter - kv.Value > PENDING_TIMEOUT_TICKS)
                {
                    if (expired == null) expired = new List<Vector3>();
                    expired.Add(kv.Key);
                }
            }
            if (expired != null) foreach (var k in expired) dict.Remove(k);
        }

        // Kept in sync with AlienShrimpProducer's SHRIMPS_PER_BC — used to decide when
        // we're "eco-saturated" for the Rule 1b BC-delay check.
        const int SHRIMPS_PER_BC_STAT = 12;

        // ---- Cluster helpers (Rule 2, map-adaptive) ----

        // Collect the position of every completed OR in-progress structure with the
        // given display name. Needed for BC clustering: an in-progress BC still counts
        // as part of its cluster geometry, otherwise we'd add duplicate Cysts while a
        // fresh BC is still under construction.
        static readonly List<Vector3> _posScratch = new List<Vector3>(24);
        static List<Vector3> CollectStructurePositionsIncludingSites(Team team, string displayName)
        {
            _posScratch.Clear();
            try
            {
                var structs = team.Structures;
                if (structs != null)
                    for (int i = 0; i < structs.Count; i++)
                    {
                        var s = structs[i];
                        if (s == null || s.ObjectInfo == null || s.IsDestroyed) continue;
                        if (string.Equals(s.ObjectInfo.DisplayName, displayName, StringComparison.OrdinalIgnoreCase))
                            _posScratch.Add(s.transform.position);
                    }
            }
            catch { }
            try
            {
                var sites = ConstructionSite.ConstructionSites;
                if (sites != null)
                    for (int i = 0; i < sites.Count; i++)
                    {
                        var cs = sites[i];
                        if (cs == null || cs.IsDestroyed) continue;
                        if (cs.Team != team) continue;
                        if (cs.ObjectInfo == null) continue;
                        if (string.Equals(cs.ObjectInfo.DisplayName, displayName, StringComparison.OrdinalIgnoreCase))
                            _posScratch.Add(cs.transform.position);
                    }
            }
            catch { }
            return _posScratch;
        }

        // Group positions into clusters where any two positions belong to the same
        // cluster iff they are within joinRadius of each other (transitively). Simple
        // union-find-lite: for each input, join with the first cluster it touches,
        // then do a merge pass for transitivity across clusters that a new point
        // simultaneously reaches. O(N²) worst-case, fine for our N ≤ ~12 BCs.
        static List<List<Vector3>> BuildClusters(List<Vector3> positions, float joinRadius)
        {
            var clusters = new List<List<Vector3>>();
            float r2 = joinRadius * joinRadius;

            foreach (var p in positions)
            {
                int joined = -1;
                for (int ci = 0; ci < clusters.Count; ci++)
                {
                    var cluster = clusters[ci];
                    bool touches = false;
                    for (int qi = 0; qi < cluster.Count; qi++)
                    {
                        Vector3 q = cluster[qi];
                        float dx = p.x - q.x, dz = p.z - q.z;
                        if (dx * dx + dz * dz <= r2) { touches = true; break; }
                    }
                    if (touches) { joined = ci; break; }
                }
                if (joined >= 0) clusters[joined].Add(p);
                else clusters.Add(new List<Vector3>(4) { p });
            }

            // Transitive-closure merge: a point added to cluster A may bring it within
            // joinRadius of some cluster B → merge B into A.
            bool merged;
            do
            {
                merged = false;
                for (int i = 0; i < clusters.Count && !merged; i++)
                {
                    for (int j = i + 1; j < clusters.Count && !merged; j++)
                    {
                        if (ClustersOverlap(clusters[i], clusters[j], r2))
                        {
                            clusters[i].AddRange(clusters[j]);
                            clusters.RemoveAt(j);
                            merged = true;
                        }
                    }
                }
            } while (merged);

            return clusters;
        }

        static bool ClustersOverlap(List<Vector3> a, List<Vector3> b, float r2)
        {
            for (int i = 0; i < a.Count; i++)
                for (int j = 0; j < b.Count; j++)
                {
                    float dx = a[i].x - b[j].x, dz = a[i].z - b[j].z;
                    if (dx * dx + dz * dz <= r2) return true;
                }
            return false;
        }

        static Vector3 Centroid(List<Vector3> positions)
        {
            if (positions.Count == 0) return Vector3.zero;
            float x = 0, y = 0, z = 0;
            for (int i = 0; i < positions.Count; i++) { x += positions[i].x; y += positions[i].y; z += positions[i].z; }
            float n = positions.Count;
            return new Vector3(x / n, y / n, z / n);
        }

        static int CountCystsWithinRadius(Team team, Vector3 pos, float radius)
        {
            int n = 0;
            float r2 = radius * radius;
            try
            {
                var structs = team.Structures;
                if (structs != null)
                    for (int i = 0; i < structs.Count; i++)
                    {
                        var s = structs[i];
                        if (s == null || s.ObjectInfo == null || s.IsDestroyed) continue;
                        if (!string.Equals(s.ObjectInfo.DisplayName, "Lesser Spawning Cyst", StringComparison.OrdinalIgnoreCase)) continue;
                        Vector3 sp = s.transform.position;
                        float dx = sp.x - pos.x, dz = sp.z - pos.z;
                        if (dx * dx + dz * dz <= r2) n++;
                    }
            }
            catch { }
            try
            {
                var sites = ConstructionSite.ConstructionSites;
                if (sites != null)
                    for (int i = 0; i < sites.Count; i++)
                    {
                        var cs = sites[i];
                        if (cs == null || cs.IsDestroyed) continue;
                        if (cs.Team != team) continue;
                        if (cs.ObjectInfo == null) continue;
                        if (!string.Equals(cs.ObjectInfo.DisplayName, "Lesser Spawning Cyst", StringComparison.OrdinalIgnoreCase)) continue;
                        Vector3 sp = cs.transform.position;
                        float dx = sp.x - pos.x, dz = sp.z - pos.z;
                        if (dx * dx + dz * dz <= r2) n++;
                    }
            }
            catch { }
            return n;
        }

        static int SafeTotalResources(Team team)
        {
            try { return team.TotalResources; } catch { return 0; }
        }

        static int CountUnitsByName(Team team, string displayName)
        {
            int n = 0;
            try
            {
                var units = team.Units;
                if (units != null)
                {
                    for (int i = 0; i < units.Count; i++)
                    {
                        var u = units[i];
                        if (u?.ObjectInfo == null) continue;
                        if (string.Equals(u.ObjectInfo.DisplayName, displayName, StringComparison.OrdinalIgnoreCase))
                            n++;
                    }
                }
            }
            catch { }
            return n;
        }

        // Returns the Nest position (any Nest we own). Vector3.zero if we don't have one
        // yet (round just started, before starter Nest placement completes). Callers
        // treat zero as "no Nest known" and fall back to "everything is starter zone".
        static Vector3 FindNestPosition(Team team)
        {
            try
            {
                var structs = team.Structures;
                if (structs != null)
                {
                    for (int i = 0; i < structs.Count; i++)
                    {
                        var s = structs[i];
                        if (s == null || s.ObjectInfo == null || s.IsDestroyed) continue;
                        if (string.Equals(s.ObjectInfo.DisplayName, "Nest", StringComparison.OrdinalIgnoreCase))
                            return s.transform.position;
                    }
                }
            }
            catch { }
            return Vector3.zero;
        }

        static float Distance2D(Vector3 a, Vector3 b)
        {
            float dx = a.x - b.x, dz = a.z - b.z;
            return Mathf.Sqrt(dx * dx + dz * dz);
        }

        static float MinDistOwnedStructure(Team team, Vector3 pos)
        {
            float bestSq = float.MaxValue;

            // Completed structures
            try
            {
                var structs = team.Structures;
                if (structs != null)
                {
                    for (int i = 0; i < structs.Count; i++)
                    {
                        var s = structs[i];
                        if (s == null || s.IsDestroyed) continue;
                        Vector3 sp = s.transform.position;
                        float dx = sp.x - pos.x, dz = sp.z - pos.z;
                        float d2 = dx * dx + dz * dz;
                        if (d2 < bestSq) bestSq = d2;
                    }
                }
            }
            catch { }

            // Under-construction sites — critical for chaining. A Node that's been
            // placed but not yet finished still contributes team build coverage in
            // the game's placement layer, so we need to count it here too. Otherwise
            // subsequent Node/BC fires wouldn't see the extended reach until the
            // Node finishes ~30s later — chain would extend at completion rate
            // instead of placement rate.
            try
            {
                var sites = ConstructionSite.ConstructionSites;
                if (sites != null)
                {
                    for (int i = 0; i < sites.Count; i++)
                    {
                        var cs = sites[i];
                        if (cs == null || cs.IsDestroyed) continue;
                        if (cs.Team != team) continue;
                        Vector3 sp = cs.transform.position;
                        float dx = sp.x - pos.x, dz = sp.z - pos.z;
                        float d2 = dx * dx + dz * dz;
                        if (d2 < bestSq) bestSq = d2;
                    }
                }
            }
            catch { }

            return bestSq == float.MaxValue ? float.MaxValue : Mathf.Sqrt(bestSq);
        }

        static bool HasStructureTypeNear(Team team, string displayName, Vector3 pos, float radius)
        {
            float r2 = radius * radius;

            // Look at completed structures.
            try
            {
                var structs = team.Structures;
                if (structs != null)
                {
                    for (int i = 0; i < structs.Count; i++)
                    {
                        var s = structs[i];
                        if (s == null || s.ObjectInfo == null) continue;
                        if (!string.Equals(s.ObjectInfo.DisplayName, displayName, StringComparison.OrdinalIgnoreCase)) continue;
                        Vector3 sp = s.transform.position;
                        float dx = sp.x - pos.x, dz = sp.z - pos.z;
                        if (dx * dx + dz * dz < r2) return true;
                    }
                }
            }
            catch { }

            // Also look at UNDER-CONSTRUCTION sites — critical, otherwise between the
            // placement callback firing and the Structure appearing in team.Structures
            // (a window of many seconds), we'd fire a duplicate at the same position.
            try
            {
                var sites = ConstructionSite.ConstructionSites;
                if (sites != null)
                {
                    for (int i = 0; i < sites.Count; i++)
                    {
                        var cs = sites[i];
                        if (cs == null || cs.IsDestroyed) continue;
                        if (cs.Team != team) continue;
                        if (cs.ObjectInfo == null) continue;
                        if (!string.Equals(cs.ObjectInfo.DisplayName, displayName, StringComparison.OrdinalIgnoreCase)) continue;
                        Vector3 csp = cs.transform.position;
                        float dx = csp.x - pos.x, dz = csp.z - pos.z;
                        if (dx * dx + dz * dz < r2) return true;
                    }
                }
            }
            catch { }

            return false;
        }

        static int CountOwnedStructuresOfType(Team team, string displayName)
        {
            int n = 0;
            try
            {
                var structs = team.Structures;
                if (structs != null)
                {
                    for (int i = 0; i < structs.Count; i++)
                    {
                        var s = structs[i];
                        if (s?.ObjectInfo == null) continue;
                        if (string.Equals(s.ObjectInfo.DisplayName, displayName, StringComparison.OrdinalIgnoreCase))
                            n++;
                    }
                }
            }
            catch { }

            // Also count under-construction sites so caps (like MAX_NODES) don't get
            // exceeded by us firing more requests while the previous batch is still
            // being built.
            try
            {
                var sites = ConstructionSite.ConstructionSites;
                if (sites != null)
                {
                    for (int i = 0; i < sites.Count; i++)
                    {
                        var cs = sites[i];
                        if (cs == null || cs.IsDestroyed) continue;
                        if (cs.Team != team) continue;
                        if (cs.ObjectInfo == null) continue;
                        if (string.Equals(cs.ObjectInfo.DisplayName, displayName, StringComparison.OrdinalIgnoreCase))
                            n++;
                    }
                }
            }
            catch { }
            return n;
        }

        // ---- Round wiring ----

        internal static void ResetForNewRound()
        {
            _tickCounter = 0;
            _pendingBc.Clear();
            _pendingCyst.Clear();
            _pendingNodeForBiotics.Clear();
            _forceNodeForBiotics.Clear();
            _nodesFiredForBiotics.Clear();
            BcAttempts = BcSuccesses = BcFailures = 0;
            CystAttempts = CystSuccesses = CystFailures = 0;
            NodeAttempts = NodeSuccesses = NodeFailures = 0;
            BcDelayedNotSaturated = BcDelayedNotAffordable = 0;
            CystSkippedClusterFull = 0;
            ClustersDetected = 0;
            // Don't clear _bcCd / _cystCd / _nodeCd — those are game-wide references, cache across rounds.
        }

        internal static string BuildRoundSummaryFragment()
        {
            if (BcAttempts == 0 && CystAttempts == 0 && NodeAttempts == 0) return "";
            return "--- Phase 3.2 slice 1 (OWN Alien construction) ---\n" +
                   $"  BC fires:    attempts={BcAttempts} success={BcSuccesses} fail={BcFailures}\n" +
                   $"  Cyst fires:  attempts={CystAttempts} success={CystSuccesses} fail={CystFailures}\n" +
                   $"  Node fires:  attempts={NodeAttempts} success={NodeSuccesses} fail={NodeFailures}\n" +
                   $"  Intelligent decisions:\n" +
                   $"    BC delayed — eco not at capacity:    {BcDelayedNotSaturated}\n" +
                   $"    BC delayed — cash below buffer:      {BcDelayedNotAffordable}\n" +
                   $"    Cyst skipped — cluster already full: {CystSkippedClusterFull}\n" +
                   $"    Max BC clusters detected on map:     {ClustersDetected}\n";
        }
    }
}
