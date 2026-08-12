#!/usr/bin/env python3
"""
What counts as one fight, and who was actually in it.

TWO DEFECTS THIS FIXES, BOTH FOUND BY LOOKING AT THE FIRST DATASET
-------------------------------------------------------------------
The first pass produced 58,080 fights with a median duration of 44 s and 7
kills. Real battles are not 44 seconds long. Two things were wrong.

**1. Rolling battles fragmented.** Kills were matched against the running
centroid of every kill in the fight, so an engagement that MOVED — a push that
drives 400 m up a ridge, a retreat that is chased — walked out of its own
radius and started a new fight halfway through. Every fragment then got its own
force census, which is how a battle one side clearly won became six rows that
each looked like a coin flip.

Fixed twice over. Matching is against the mean of the **last few kills** rather
than all of them, so the cluster follows the battle as it moves; and afterwards
fights that SHARE AN ENTITY are merged if they are adjacent in time. The second
test needs no radius at all — if the same Behemoth is killing in both, they are
the same battle, whatever the geometry did.

**2. "Present" is not "fighting".** A 400 m circle around the centroid catches
everything nearby: a garrison guarding something else, units in transit, the
tail of a column that never closed. Force ratio built from that is noise, and it
is the direct cause of the exchange exponent reading 0.5 instead of something
believable.

A unit now counts as force only if **an enemy came within its own weapon reach**
at some point during the fight. That uses the reach table `unit_stats.py`
derives and `measure_range.py --emit-reach` completes for infantry, so it is the
same physics the rest of the pipeline runs on. The ratio of participants to
present is `trade_frac` — no longer an unknown to be fitted, but a measured
column.

WHAT THE PARTICIPATION TEST STILL GETS WRONG
---------------------------------------------
- It samples ticks rather than checking all of them, so a unit that was briefly
  in reach between two samples is missed. Sampling is even across the fight and
  the count is reported; at 2 s ticks and 6 samples a 40 s fight checks every
  ~7 s.
- Reach is the unit's own. A Crab standing 300 m from a Shocker that is shooting
  it is NOT counted as participating, because nothing it carries reaches that
  far — which is correct for what it contributes offensively and wrong for what
  it absorbs. Meatshields will therefore read as smaller forces than they are.
- Structures are excluded from force entirely, so a fight decided by turrets
  looks like a fight the attacker lost for no reason.
"""

import collections
import io
import json
import math
import os

import numpy as np

HERE = os.path.dirname(os.path.abspath(__file__))
REACH_JSON = os.path.join(HERE, "out", "reach.json")

# Fallback if the merged reach table has not been generated. Contact range, so
# an absent table makes the participation test STRICTER rather than looser — it
# will under-count force rather than quietly counting the whole map.
DEFAULT_REACH = 40.0


def load_reach(path=REACH_JSON):
    """type name -> metres. Empty dict if the table has not been built."""
    try:
        with io.open(path, encoding="utf-8") as fh:
            data = json.load(fh)
    except (OSError, ValueError):
        return {}
    return {k: v["effective"] for k, v in data.get("units", {}).items()}


class Fight:
    __slots__ = ("kills", "cx", "cz", "t0", "t1", "entities")

    def __init__(self, kill, x, z):
        self.kills = [kill]
        self.cx, self.cz = x, z
        self.t0 = self.t1 = kill["t"]
        self.entities = set()

    def add(self, kill, x, z, recent):
        self.kills.append(kill)
        self.t1 = max(self.t1, kill["t"])
        pts = [(k["vx"], k["vz"]) for k in self.kills[-recent:]]
        self.cx = sum(p[0] for p in pts) / len(pts)
        self.cz = sum(p[1] for p in pts) / len(pts)


def cluster(kills, radius, gap, recent=5, merge_gap=None):
    """
    Greedy spatiotemporal clustering that follows a moving battle, then a merge
    pass over shared participants. Kills must arrive time-ordered.
    """
    merge_gap = gap if merge_gap is None else merge_gap
    open_fights, done = [], []
    for k in kills:
        x, z = k["vx"], k["vz"]
        still = []
        for f in open_fights:
            (done if k["t"] - f.t1 > gap else still).append(f)
        open_fights = still

        best, best_d = None, radius
        for f in open_fights:
            d = math.hypot(x - f.cx, z - f.cz)
            if d < best_d:
                best, best_d = f, d
        if best is None:
            best = Fight(k, x, z)
            open_fights.append(best)
        else:
            best.add(k, x, z, recent)
    fights = done + open_fights

    for f in fights:
        for k in f.kills:
            if k.get("aid") is not None:
                f.entities.add(k["aid"])
            f.entities.add(k["vid"])
    return merge_shared(fights, merge_gap)


def merge_shared(fights, merge_gap):
    """
    Union fights that share a unit and sit close in time.

    The same Behemoth killing in two clusters means one battle, whatever the
    geometry did in between. This is the half of the fix that needs no radius.
    """
    if len(fights) < 2:
        return fights
    fights = sorted(fights, key=lambda f: f.t0)
    parent = list(range(len(fights)))

    def find(i):
        while parent[i] != i:
            parent[i] = parent[parent[i]]
            i = parent[i]
        return i

    def union(i, j):
        ri, rj = find(i), find(j)
        if ri != rj:
            parent[max(ri, rj)] = min(ri, rj)

    # entity -> the fights it appears in, walked in time order
    where = collections.defaultdict(list)
    for i, f in enumerate(fights):
        for e in f.entities:
            where[e].append(i)
    for idxs in where.values():
        for a, b in zip(idxs, idxs[1:]):
            if fights[b].t0 - fights[a].t1 <= merge_gap:
                union(a, b)

    groups = collections.defaultdict(list)
    for i in range(len(fights)):
        groups[find(i)].append(i)

    out = []
    for idxs in groups.values():
        if len(idxs) == 1:
            out.append(fights[idxs[0]])
            continue
        base = fights[idxs[0]]
        for i in idxs[1:]:
            base.kills.extend(fights[i].kills)
            base.entities |= fights[i].entities
            base.t1 = max(base.t1, fights[i].t1)
        base.kills.sort(key=lambda k: k["t"])
        pts = [(k["vx"], k["vz"]) for k in base.kills]
        base.cx = sum(p[0] for p in pts) / len(pts)
        base.cz = sum(p[1] for p in pts) / len(pts)
        out.append(base)
    return out


def participation(replay, t0_tick, t1_tick, cx, cz, pad, units, resolve,
                  is_force, reach, neutral, samples=6):
    """
    Who had an enemy inside their own weapon reach at any sampled tick.

    Returns team -> {"n", "cash", "types", "present_n", "present_cash"} where
    the `present_*` pair is the old radius census, kept so the ratio between
    them — `trade_frac` — is a column rather than a parameter to fit.
    """
    ticks = sorted({int(round(t)) for t in
                    np.linspace(t0_tick, max(t0_tick, t1_tick),
                                max(1, min(samples, t1_tick - t0_tick + 1)))})

    engaged = {}                    # eid -> (team, type_name, cost)
    present = {}
    pad2 = pad * pad

    for tick in ticks:
        snap = replay.ticks.get(tick)
        if not snap:
            continue
        by_team = collections.defaultdict(list)     # team -> [(eid, x, z, r)]
        for eid, pos in snap.items():
            dx, dz = pos[0] - cx, pos[1] - cz
            if dx * dx + dz * dz > pad2:
                continue
            e = replay.entities.get(eid)
            if e is None or e.team_name in neutral:
                continue
            u = resolve(units, e.type_name, e.team_name)
            if u is None or u["is_structure"]:
                continue
            fights_, cost = is_force(e.type_name, e.team_name)
            if not fights_:
                continue
            present[eid] = (e.team_name, e.type_name, cost)
            by_team[e.team_name].append(
                (eid, pos[0], pos[1],
                 reach.get(e.type_name) or reach.get(u["name"]) or DEFAULT_REACH))

        if len(by_team) < 2:
            continue
        for team, mine in by_team.items():
            others = [o for t, lst in by_team.items() if t != team for o in lst]
            if not others or not mine:
                continue
            mp = np.array([(m[1], m[2]) for m in mine], dtype=np.float64)
            op = np.array([(o[1], o[2]) for o in others], dtype=np.float64)
            # min distance from each of mine to any enemy
            d = np.sqrt(((mp[:, None, :] - op[None, :, :]) ** 2).sum(axis=2))
            closest = d.min(axis=1)
            rs = np.array([m[3] for m in mine], dtype=np.float64)
            for i in np.nonzero(closest <= rs)[0]:
                eid = mine[int(i)][0]
                engaged[eid] = present[eid]

    out = collections.defaultdict(
        lambda: {"n": 0, "cash": 0.0, "present_n": 0, "present_cash": 0.0,
                 "types": collections.Counter()})
    for team, tname, cost in present.values():
        rec = out[team]
        rec["present_n"] += 1
        rec["present_cash"] += cost
    for team, tname, cost in engaged.values():
        rec = out[team]
        rec["n"] += 1
        rec["cash"] += cost
        rec["types"][tname] += 1
    return dict(out), ticks, engaged
