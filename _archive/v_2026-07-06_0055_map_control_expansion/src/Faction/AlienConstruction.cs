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

            // Lazily resolve ConstructionData for the structures we manage.
            EnsureConstructionData(h, team);
            if (_bcCd == null) return false;   // BC essential; without it we can't do anything useful

            // One-shot per scene: reflect every field/property on the game types
            // we plan the analytical shrimp-income model against. Fired here because
            // ConstructionData is now populated AND the team has spawned units.
            Perception.GameConstantsDumper.MaybeDumpAlien(team, _bcCd, _cystCd, _nodeCd);

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
            var anchor = FindClosestStructureThatCanBuild(team, cd, targetPos);
            if (anchor == null) return false;
            FirePlacement(cd, team, anchor, targetPos,
                onSuccess: (thisCd, cbTeam, cbStruct, gotPos, gotRot) =>
                {
                    try
                    {
                        // Structure.Construct is what the vanilla commander UI
                        // calls at click-time — it deducts cost, spawns a
                        // ConstructionSite, and enforces every game rule
                        // (no-build zones, chain reach, cap limits).
                        cbStruct?.Construct(thisCd, gotPos, gotRot);
                        MelonLogger.Msg("[PLAN/EXEC] team=" + cbTeam.name +
                                        " built " + (thisCd.ObjectInfo?.DisplayName ?? "?") +
                                        " at (" + gotPos.x.ToString("F0") + "," + gotPos.z.ToString("F0") + ")" +
                                        " via anchor=" + (cbStruct?.ObjectInfo?.DisplayName ?? "?"));
                    }
                    catch (Exception ex) { MelonLogger.Warning("[PLAN/EXEC] Construct threw: " + ex.Message); }
                },
                onFail: (thisCd, cbTeam, cbStruct) =>
                {
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
