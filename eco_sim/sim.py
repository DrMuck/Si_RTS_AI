"""
Deterministic eco simulator for Alien on Silica maps.

Time-step: 1 second. Everything is aggregated per BC — we don't simulate
individual shrimp micro-AI. That's fine because we're comparing macro-strategies
(build order, Cyst pairing rules, expansion direction) not micro-decisions.

Per-BC income model:
  income/s = min(N_shrimps, PER_BC_CAP) * per_shrimp_income
  per_shrimp_income = 1 / cycle_time * SHRIMP_CARRY * conv_rate

  cycle_time = travel_to_patch + harvest_fill + travel_back + deposit_full
    = 2*d_bc_to_patch/SPEED + CARRY/HARVEST_RATE + CARRY/DEPOSIT_RATE

Simplifications vs the real game:
  - Patches deplete only against the BC's OWN nearest patch (no cross-BC
    contention). This is OK when patches are far enough apart that BCs are
    each on their own patch.
  - Shrimp auto-relocation on depletion IS modelled: when BC's local patch
    empties, its shrimps are re-pointed at the next-nearest live patch that
    another BC isn't already exclusively claiming.
  - No queen respawn, no combat, no map control. Pure eco.
  - Chain reach is a hard yes/no gate (BC must have a chain path to Nest).

State:
  t          — elapsed seconds since round start
  cash
  cap        — resource capacity (grows with BCs)
  structures — list of Struct records
  patches    — list of PatchState  (mutable — remaining decreases)
  shrimp_asn — dict {bc_idx: n_live_shrimps}
  queue      — list of QueueItem (in-flight builds / shrimp productions)
"""

from __future__ import annotations
import math
from dataclasses import dataclass, field
from typing import List, Dict, Tuple, Optional

import params as P


# ------------------------------- data types -------------------------------

@dataclass
class Struct:
    kind: str         # "Nest" | "BC" | "Cyst" | "Node"
    x: float
    z: float
    built_at: float   # sim time when construction completes (0 for starter Nest)


@dataclass
class PatchState:
    x: float
    z: float
    remaining: float
    initial: float


@dataclass
class QueueItem:
    kind: str          # same as Struct.kind, or "Shrimp"
    x: float
    z: float
    cost: int
    complete_at: float
    bc_idx: int = -1   # for Shrimp — the BC this shrimp is bound to


@dataclass
class State:
    t: float = 0.0
    cash: int = P.START_CASH
    cap: int = P.START_CAP
    cumulative_income: int = 0
    structs: List[Struct] = field(default_factory=list)
    patches: List[PatchState] = field(default_factory=list)
    shrimp_asn: Dict[int, int] = field(default_factory=dict)   # bc_idx → live shrimps
    queue: List[QueueItem] = field(default_factory=list)


# ------------------------------- geometry -------------------------------

def dist(a, b):
    dx = a[0] - b[0]; dz = a[1] - b[1]
    return math.sqrt(dx * dx + dz * dz)


def dsq(a, b):
    dx = a[0] - b[0]; dz = a[1] - b[1]
    return dx * dx + dz * dz


def bc_positions(s: State) -> List[Tuple[float, float]]:
    return [(st.x, st.z) for st in s.structs if st.kind == "BC"]


def bc_indices(s: State) -> List[int]:
    return [i for i, st in enumerate(s.structs) if st.kind == "BC"]


def producer_indices(s: State) -> List[int]:
    """Any structure that can host shrimps AND deposit — i.e. Nest + BCs.
    Cyst can produce shrimps but doesn't deposit; sim treats Cyst-based
    shrimps as belonging to their nearest BC (or Nest if no BC yet)."""
    return [i for i, st in enumerate(s.structs) if st.kind in ("BC", "Nest")]


# ------------------------------- init -------------------------------

def new_state(map_data: dict) -> State:
    s = State()
    # Starter Nest at index 0
    n = map_data["alienNest"]
    s.structs.append(Struct("Nest", n["x"], n["z"], 0.0))
    # Starter shrimps assigned to the Nest so bootstrap income flows.
    s.shrimp_asn[0] = P.START_NEST_SHRIMPS
    # Patches
    for p in map_data["patches"]:
        s.patches.append(PatchState(p["x"], p["z"], p["initial"], p["max"]))
    return s


# ------------------------------- income model -------------------------------

def nearest_live_patch(s: State, pos) -> Optional[int]:
    best_i, best_d = -1, float("inf")
    for i, p in enumerate(s.patches):
        if p.remaining <= 0:
            continue
        d = dsq((p.x, p.z), pos)
        if d < best_d:
            best_d, best_i = d, i
    return best_i if best_i >= 0 else None


def per_shrimp_income_at(bc_pos, patch_pos) -> float:
    """Cash/s per shrimp when this BC harvests this patch."""
    d = dist(bc_pos, patch_pos)
    travel = 2.0 * d / P.SHRIMP_SPEED
    harvest = P.SHRIMP_CARRY / P.HARVEST_RATE
    deposit = P.SHRIMP_CARRY / P.DEPOSIT_RATE
    cycle = travel + harvest + deposit
    # cash per cycle = carried amount (biotics deposits 1:1 to cash for Alien)
    return P.SHRIMP_CARRY / cycle


def bc_income_and_drain(s: State, bc_idx: int) -> Tuple[float, float, Optional[int]]:
    """Return (cash_per_sec, biotics_drain_per_sec, patch_idx) for this BC."""
    bc = s.structs[bc_idx]
    pi = nearest_live_patch(s, (bc.x, bc.z))
    if pi is None:
        return 0.0, 0.0, None
    n = min(s.shrimp_asn.get(bc_idx, 0), P.PER_BC_SHRIMP_CAP)
    if n == 0:
        return 0.0, 0.0, pi
    per = per_shrimp_income_at((bc.x, bc.z), (s.patches[pi].x, s.patches[pi].z))
    return per * n, P.HARVEST_RATE * n, pi


# ------------------------------- tick -------------------------------

def tick(s: State) -> None:
    """One second of simulated time."""
    # 1) process build queue completions
    finished = []
    for qi in s.queue:
        if s.t >= qi.complete_at:
            finished.append(qi)
    for qi in finished:
        s.queue.remove(qi)
        if qi.kind == "BC":
            s.structs.append(Struct("BC", qi.x, qi.z, s.t))
            s.cap += P.BC_CAP_ADD
        elif qi.kind == "Cyst":
            s.structs.append(Struct("Cyst", qi.x, qi.z, s.t))
        elif qi.kind == "Node":
            s.structs.append(Struct("Node", qi.x, qi.z, s.t))
        elif qi.kind == "Shrimp":
            # Assign to the BC that queued it
            if qi.bc_idx >= 0:
                s.shrimp_asn[qi.bc_idx] = s.shrimp_asn.get(qi.bc_idx, 0) + 1

    # 2) collect income from each producer (Nest + BCs) + drain patches
    total_cash_delta = 0.0
    for bi in producer_indices(s):
        cps, dps, pi = bc_income_and_drain(s, bi)
        if pi is None:
            continue
        # If the patch would run out this tick, prorate.
        if s.patches[pi].remaining < dps:
            frac = s.patches[pi].remaining / dps if dps > 0 else 0
            cps *= frac
            dps = s.patches[pi].remaining
        s.patches[pi].remaining -= dps
        total_cash_delta += cps

    inc = int(round(total_cash_delta))
    s.cumulative_income += inc
    s.cash = min(s.cap, s.cash + inc)

    s.t += 1.0


# ------------------------------- helpers for strategies -------------------------------

def anchors_for_reach(s: State) -> List[Tuple[float, float]]:
    """All BCs, Cysts, Nodes AND the Nest — any of these can anchor a new
    chain build within CHAIN_REACH_M."""
    return [(st.x, st.z) for st in s.structs]


def chain_reachable(s: State, pos) -> bool:
    for a in anchors_for_reach(s):
        if dsq(a, pos) <= P.CHAIN_REACH_M * P.CHAIN_REACH_M:
            return True
    return False


def total_shrimps(s: State) -> int:
    return sum(s.shrimp_asn.values()) + sum(1 for qi in s.queue if qi.kind == "Shrimp")


def in_progress(s: State, kind: str, pos=None, radius=30) -> bool:
    """Is there already a build for this kind (optionally at pos) in the queue?"""
    for qi in s.queue:
        if qi.kind != kind:
            continue
        if pos is None:
            return True
        if dsq((qi.x, qi.z), pos) <= radius * radius:
            return True
    return False


# ------------------------------- action API for strategies -------------------------------

def try_place_bc(s: State, pos) -> bool:
    """Queue a BC placement. Returns True if fired."""
    if s.cash < P.BC_COST:
        return False
    if not chain_reachable(s, pos):
        return False
    if in_progress(s, "BC", pos):
        return False
    s.cash -= P.BC_COST
    s.queue.append(QueueItem("BC", pos[0], pos[1], P.BC_COST, s.t + P.BC_BUILD_S))
    return True


def try_place_cyst(s: State, pos) -> bool:
    if s.cash < P.CYST_COST:
        return False
    if not chain_reachable(s, pos):
        return False
    if in_progress(s, "Cyst", pos):
        return False
    s.cash -= P.CYST_COST
    s.queue.append(QueueItem("Cyst", pos[0], pos[1], P.CYST_COST, s.t + P.CYST_BUILD_S))
    return True


def try_place_node(s: State, pos) -> bool:
    if s.cash < P.NODE_COST:
        return False
    if not chain_reachable(s, pos):
        return False
    if in_progress(s, "Node", pos):
        return False
    s.cash -= P.NODE_COST
    s.queue.append(QueueItem("Node", pos[0], pos[1], P.NODE_COST, s.t + P.NODE_BUILD_S))
    return True


def try_queue_shrimp(s: State, bc_idx: int) -> bool:
    if s.cash < P.SHRIMP_COST:
        return False
    live = s.shrimp_asn.get(bc_idx, 0)
    queued_for_bc = sum(1 for qi in s.queue if qi.kind == "Shrimp" and qi.bc_idx == bc_idx)
    if live + queued_for_bc >= P.PER_BC_SHRIMP_CAP:
        return False
    if total_shrimps(s) >= P.TEAM_SHRIMP_CAP:
        return False
    s.cash -= P.SHRIMP_COST
    s.queue.append(QueueItem("Shrimp", 0, 0, P.SHRIMP_COST,
                             s.t + P.SHRIMP_BUILD_S, bc_idx=bc_idx))
    return True


# ------------------------------- run loop -------------------------------

def run(state: State, strategy, horizon_s: float,
        trace: bool = False, trace_every_s: int = 30) -> List[dict]:
    """Advance state to horizon_s under strategy(state) called each tick.
    strategy is a callable: strategy(state) → None (may fire actions)."""
    log = []
    while state.t < horizon_s:
        strategy(state)
        tick(state)
        if trace and int(state.t) % trace_every_s == 0:
            log.append({
                "t": state.t,
                "cash": state.cash,
                "earned": state.cumulative_income,
                "bcs": sum(1 for st in state.structs if st.kind == "BC"),
                "cysts": sum(1 for st in state.structs if st.kind == "Cyst"),
                "shrimps": total_shrimps(state),
            })
    return log
