"""
Rule-based strategies for the eco simulator. Each strategy is a callable
that inspects the state and calls sim.try_* to fire actions.

Comparing:
  - `greedy_current`  — mimic the current planner: pair every BC with a Cyst
  - `cluster_aware`   — skip Cyst on isolated BCs (user rule 3)
  - `deep_reach`      — prefer Node-hop to skip a layer (experiment)
"""

from __future__ import annotations
import math
from typing import Optional

import params as P
import sim


# ------------------------------- shared helpers -------------------------------

def handoff_score(state: sim.State, bc_pos) -> float:
    """Same biotics-weighted density metric the mod uses."""
    total = 0.0
    for p in state.patches:
        if p.remaining <= 0:
            continue
        d = math.sqrt(sim.dsq((p.x, p.z), bc_pos))
        if d > 600:
            continue
        w = 1.0 / (1.0 + d / 200.0)
        total += p.remaining * w
    return total


def uncovered_patches_within_reach(state: sim.State):
    """Patches not yet covered by an existing BC (within 30m) AND
    chain-reachable from any structure."""
    out = []
    for pi, p in enumerate(state.patches):
        if p.remaining <= 0:
            continue
        # covered?
        covered = False
        for st in state.structs:
            if st.kind != "BC":
                continue
            if sim.dsq((st.x, st.z), (p.x, p.z)) <= 60 * 60:
                covered = True
                break
        if covered:
            continue
        if not sim.chain_reachable(state, (p.x, p.z)):
            continue
        out.append(pi)
    return out


def bc_needs_cyst(state: sim.State, bc_idx: int, handoff_min: float) -> bool:
    """Rule B: only pair a Cyst with a BC whose local cluster is rich."""
    bc = state.structs[bc_idx]
    for st in state.structs:
        if st.kind != "Cyst":
            continue
        if sim.dsq((st.x, st.z), (bc.x, bc.z)) <= 60 * 60:
            return False
    for qi in state.queue:
        if qi.kind == "Cyst" and sim.dsq((qi.x, qi.z), (bc.x, bc.z)) <= 60 * 60:
            return False
    return handoff_score(state, (bc.x, bc.z)) >= handoff_min


# ------------------------------- strategies -------------------------------

def _shrimp_topup(state: sim.State):
    """Keep every producer (Nest + BCs) ramping shrimps toward the cap.
    A producer only produces if there's a Cyst nearby OR it's the Nest
    itself (Nest produces starter shrimps natively)."""
    for bi in sim.producer_indices(state):
        prod = state.structs[bi]
        has_cyst = prod.kind == "Nest"   # Nest is always its own producer
        if not has_cyst:
            for st in state.structs:
                if st.kind == "Cyst" and sim.dsq((st.x, st.z), (prod.x, prod.z)) <= 60 * 60:
                    has_cyst = True
                    break
        if not has_cyst:
            continue
        sim.try_queue_shrimp(state, bi)


def _expansion_target(state: sim.State) -> Optional[tuple]:
    """Pick the highest-handoff uncovered patch reachable now, and return
    a BC position ~30m from it (proxy for the game's placement clearance)."""
    candidates = uncovered_patches_within_reach(state)
    if not candidates:
        return None
    candidates.sort(key=lambda pi: handoff_score(state, (state.patches[pi].x, state.patches[pi].z)),
                    reverse=True)
    p = state.patches[candidates[0]]
    return (p.x, p.z)


def _node_hop_target(state: sim.State, reserved: set = None) -> Optional[tuple]:
    """Return a Node position that extends chain reach toward the best out-of-
    reach patch. Multi-hop capable — if the target patch is >2× reach away
    we still place a Node at the maximum chain extension, so successive ticks
    build a chain toward the patch.
    If `reserved` is given, patches in that set aren't considered as targets."""
    reach = P.CHAIN_REACH_M
    anchors = sim.anchors_for_reach(state)
    # Also skip candidate Node positions that overlap an existing structure
    existing = [(st.x, st.z) for st in state.structs]

    best_node_pos, best_score = None, 0.0
    for pi, p in enumerate(state.patches):
        if p.remaining <= 0:
            continue
        if reserved and pi in reserved:
            continue
        pos = (p.x, p.z)
        # Already reachable? Skip.
        if any(sim.dsq(a, pos) <= reach * reach for a in anchors):
            continue
        # Closest anchor
        best_anchor, best_d2 = None, float("inf")
        for a in anchors:
            d2 = sim.dsq(a, pos)
            if d2 < best_d2:
                best_d2 = d2
                best_anchor = a
        if best_anchor is None:
            continue
        d = math.sqrt(best_d2)
        # Multi-hop: place Node at reach-10 from anchor toward patch. Even if
        # patch remains out-of-reach after this Node, next tick a further Node
        # extends the chain.
        frac = min(reach - 10, d - 5) / d
        node_x = best_anchor[0] + (pos[0] - best_anchor[0]) * frac
        node_z = best_anchor[1] + (pos[1] - best_anchor[1]) * frac
        # Skip if this Node would land on top of an existing structure.
        if any(sim.dsq((sx, sz), (node_x, node_z)) < 30 * 30 for sx, sz in existing):
            continue
        # Score = remaining biotics / distance-to-target (prefer bridges that
        # unlock rich patches with fewer hops remaining).
        score = p.remaining / (1 + d / 100)
        if score > best_score:
            best_score = score
            best_node_pos = (node_x, node_z)
    return best_node_pos


def greedy_current(state: sim.State):
    """Mimic current planner: every BC gets a Cyst (no cluster gate)."""
    _shrimp_topup(state)
    # BC expansion — if no BC target reachable, extend chain with a Node.
    target = _expansion_target(state)
    if target:
        sim.try_place_bc(state, target)
    else:
        node_target = _node_hop_target(state)
        if node_target:
            sim.try_place_node(state, node_target)
    # Pair Cyst with every uncysted BC
    for bi in sim.bc_indices(state):
        # only for finished BCs
        bc = state.structs[bi]
        # BC has to be finished (built_at != 0 means we know when it landed)
        # Actually all structs in state.structs are "finished"; queued builds
        # are in state.queue. So any bi is finished.
        already_cysted = any(
            sim.dsq((st.x, st.z), (bc.x, bc.z)) <= 60 * 60 and st.kind == "Cyst"
            for st in state.structs
        ) or any(
            qi.kind == "Cyst" and sim.dsq((qi.x, qi.z), (bc.x, bc.z)) <= 60 * 60
            for qi in state.queue
        )
        if not already_cysted:
            sim.try_place_cyst(state, (bc.x, bc.z))


def cluster_aware(state: sim.State):
    """User rules B: skip Cyst on isolated BCs (handoff < HANDOFF_CYST_MIN)."""
    _shrimp_topup(state)
    target = _expansion_target(state)
    if target:
        sim.try_place_bc(state, target)
    else:
        node_target = _node_hop_target(state)
        if node_target:
            sim.try_place_node(state, node_target)
    for bi in sim.bc_indices(state):
        if bc_needs_cyst(state, bi, P.HANDOFF_CYST_MIN):
            bc = state.structs[bi]
            sim.try_place_cyst(state, (bc.x, bc.z))


# ============================================================
# Reserve-nearest-patches experiment
# ============================================================
# User question 2026-07-06: "Is it useful e.g. tap into the 4 closest biotics
# at the start, and keep the 4 biotics that are closest to the 4 starter
# biotics free for starter shrimp allocation. Despite rather Node further and
# tap into the following biotics by building a biocache there. Or is a shrimp
# migration OK? [...] Maybe even interesting to sacrifice some starter shrimps
# to have more cap building shrimps at the outskirts."
#
# Model:
#   - Rank patches by distance from Nest.
#   - Reserve the k closest patches for starter shrimps only — no BC there.
#   - Starter shrimps naturally harvest reserved (nearest live) patches until
#     they deplete, then auto-migrate outward.
#   - New BCs go on patches ranked k+1 and beyond.

_nest_pos_cache = None

def _nest_pos(state):
    global _nest_pos_cache
    if _nest_pos_cache is None:
        for st in state.structs:
            if st.kind == "Nest":
                _nest_pos_cache = (st.x, st.z)
                break
    return _nest_pos_cache


def reset_caches():
    """Call between runs — Nest position is per-map."""
    global _nest_pos_cache
    _nest_pos_cache = None


def _reserved_patch_indices(state, reserve_k: int):
    """Indices of the k patches closest to the Nest (by initial position),
    which we treat as reserved-for-starter."""
    nest = _nest_pos(state)
    ranked = sorted(range(len(state.patches)),
                    key=lambda i: sim.dsq((state.patches[i].x, state.patches[i].z), nest))
    return set(ranked[:reserve_k])


def _expansion_target_skipping(state, reserved: set) -> Optional[tuple]:
    """Same as _expansion_target but excludes reserved patch indices."""
    candidates = [pi for pi in uncovered_patches_within_reach(state)
                  if pi not in reserved]
    if not candidates:
        return None
    candidates.sort(
        key=lambda pi: handoff_score(state, (state.patches[pi].x, state.patches[pi].z)),
        reverse=True)
    p = state.patches[candidates[0]]
    return (p.x, p.z)


def _make_skip_layer_strategy(reserve_k: int):
    """Factory: strategy that reserves k nearest patches for Nest harvest,
    Node-hops out, and places BCs on layer k+1 and beyond."""
    def strat(state: sim.State):
        _shrimp_topup(state)
        reserved = _reserved_patch_indices(state, reserve_k)
        target = _expansion_target_skipping(state, reserved)
        if target:
            sim.try_place_bc(state, target)
        else:
            node_target = _node_hop_target(state, reserved=reserved)
            if node_target:
                sim.try_place_node(state, node_target)
        for bi in sim.bc_indices(state):
            if bc_needs_cyst(state, bi, P.HANDOFF_CYST_MIN):
                bc = state.structs[bi]
                sim.try_place_cyst(state, (bc.x, bc.z))
    return strat


skip_first_4 = _make_skip_layer_strategy(4)
skip_first_8 = _make_skip_layer_strategy(8)
skip_first_12 = _make_skip_layer_strategy(12)


# ============================================================
# Radial-sector reserve model
# ============================================================
# User idea 2026-07-07: instead of a fixed K, use a MODEL based on
# distance-to-nest + distance-between-biotics, expressed via radial sectors.
# Divide the plane around the Nest into N angular sectors; reserve the
# closest M patches in each sector. This automatically adapts to map
# geometry:
#   - Maps with patches clustered in one direction only reserve in that
#     sector; other sectors have nothing to reserve
#   - Uniformly-scattered maps reserve one patch per direction, which
#     matches the ~K=8 finding from the fixed-K experiment
#
# Parameters:
#   sectors      — number of angular slices (8 = 45° each is a natural default)
#   per_sector   — closest patches per sector to reserve (usually 1)
#   inner_radius — patches beyond this from Nest are never reserved
#                   (long-cycle patches aren't efficient for starter Nest)


def _radial_reserved(state, sectors: int = 8, per_sector: int = 1,
                     inner_radius: float = 500.0):
    """Compute reserved patch indices via radial sector partitioning."""
    nest = _nest_pos(state)
    buckets = [[] for _ in range(sectors)]
    for pi, p in enumerate(state.patches):
        dx = p.x - nest[0]
        dz = p.z - nest[1]
        r = math.sqrt(dx * dx + dz * dz)
        if r > inner_radius:
            continue
        angle = math.atan2(dz, dx)                 # -π..π
        sec = int(((angle + math.pi) / (2 * math.pi)) * sectors) % sectors
        buckets[sec].append((r, pi))
    reserved = set()
    for b in buckets:
        b.sort(key=lambda x: x[0])
        for _, pi in b[:per_sector]:
            reserved.add(pi)
    return reserved


def _make_radial_strategy(sectors: int, per_sector: int, inner_radius: float):
    def strat(state: sim.State):
        _shrimp_topup(state)
        reserved = _radial_reserved(state, sectors=sectors,
                                    per_sector=per_sector,
                                    inner_radius=inner_radius)
        target = _expansion_target_skipping(state, reserved)
        if target:
            sim.try_place_bc(state, target)
        else:
            node_target = _node_hop_target(state, reserved=reserved)
            if node_target:
                sim.try_place_node(state, node_target)
        for bi in sim.bc_indices(state):
            if bc_needs_cyst(state, bi, P.HANDOFF_CYST_MIN):
                bc = state.structs[bi]
                sim.try_place_cyst(state, (bc.x, bc.z))
    return strat


# Named variants — sectors=8 with per_sector=1 is a natural default (matches
# the +20% skip_8 win but adapts to map geometry).
radial_8x1_r500 = _make_radial_strategy(sectors=8, per_sector=1, inner_radius=500)
radial_8x1_r700 = _make_radial_strategy(sectors=8, per_sector=1, inner_radius=700)
radial_6x1_r500 = _make_radial_strategy(sectors=6, per_sector=1, inner_radius=500)
radial_12x1_r500 = _make_radial_strategy(sectors=12, per_sector=1, inner_radius=500)
radial_8x2_r500 = _make_radial_strategy(sectors=8, per_sector=2, inner_radius=500)
