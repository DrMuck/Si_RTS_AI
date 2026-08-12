#!/usr/bin/env python3
"""
Turn the replay archive into a dataset of FIGHTS, not kills.

WHY THE UNIT OF ANALYSIS IS A FIGHT
-----------------------------------
`tools/mine_replay_kills.py` already counts who kills what, and the counter it
produces has a known blind spot written into its own header: a unit that softens
a target and dies before the last hit is credited nothing, so a duel matrix
overstates whatever arrives last and understates screens and artillery. That
blind spot is not fixable at the level of a kill, because a kill has no denominator
— it does not know how many units were standing there, on either side, when it
happened.

A fight does. Once the engagement is the row, every quantity the military
planner actually wants to ask about becomes measurable:

    "if I commit 12k of these against 9k of those, do I win, what does it cost,
     and how long does it take"

which is the only question a `MilSimulator` is ever asked, and it cannot be
answered from a kill table at all.

WHAT A FIGHT IS, MECHANICALLY
-----------------------------
Kills are clustered greedily in space and time: a kill joins an open fight if it
lands within `--radius` metres of that fight's running centroid and within
`--gap` seconds of its last kill. Anything else opens a new fight. Two constants,
both physical, both printed in the output so a later reader can see what was
assumed.

PRESENCE IS THE HARD PART AND IT IS WHY THIS READS SRPL RATHER THAN A CSV
-------------------------------------------------------------------------
Losses are easy — they are the kills. The denominator is the whole point, and it
needs the position of every entity alive at the moment the fight started, which
only the replay's tick stream has. For each side we record what was standing
within `--presence` metres at the opening tick: the composition, the cash, and
how much of it a human was piloting.

That last column is not decoration. 81% of kills in real games are scored by
piloted units, and a bot cannot pilot. A model fitted without splitting on it
would learn how well HUMANS fight and hand the number to something that cannot.
Rows carry `piloted_frac` per side so the fit can condition on it, and the
AI-vs-AI subset is the one the planner is entitled to believe.

WHAT THIS STILL CANNOT SEE, AND SO NEITHER CAN ANY MODEL FITTED ON IT
----------------------------------------------------------------------
- Damage that did not kill. A fight broken off with both sides at half health
  reads as a small exchange. Retreats are therefore under-weighted.
- Terrain and elevation. Two fights at the same range across a ridge and across
  open ground are one row each and look identical.
- Reinforcement mid-fight. The census is taken twice — at the opening tick
  (`*_n`, `*_cash`) and at the closing tick (`*_end_n`, `*_end_cash`) — so a
  fight fed by a stream of arrivals shows it as an end count that exceeds the
  start minus the losses. That gap is the reinforcement signal; it is not
  corrected for, and a fit that ignores it will read reinforced fights as
  cheaper wins than they were.
- Structures shooting. A turret that does the killing is an entity like any
  other and lands in the composition; a turret that only absorbs is invisible.

    python mil_sim/mine_engagements.py [--limit N] [--since YYYYMMDD]
                                       [--out engagements.csv]

Writes mil_sim/out/engagements.csv   one row per fight
       mil_sim/out/fight_units.csv   one row per fight per side per unit type
"""

import argparse
import collections
import csv
import glob
import io
import json
import math
import os
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from unit_stats import load_units, balance_epoch, resolve   # noqa: E402

SERVER = r"E:\Steam\steamapps\common\Silica Dedicated Server"
MAPREPLAY = os.path.join(SERVER, "Mod MapReplay")
sys.path.insert(0, os.path.join(MAPREPLAY, "modules"))
from srpl_reader import parse_srpl                     # noqa: E402

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
ARCHIVE = os.path.join(ROOT, "Serverdata", "ReplayLogs - Copy")
OUTDIR = os.path.join(os.path.dirname(os.path.abspath(__file__)), "out")

BROKEN_FROM, BROKEN_TO = "20260715", "20260731"
NEUTRAL = {"Wildlife", "GM", "Unknown"}

# Type names the balance dump does not price. Printed at the end rather than
# absorbed, because an unpriced unit is a hole in every cash column it touches.
UNMATCHED = collections.Counter()


class Fight:
    __slots__ = ("kills", "cx", "cz", "t0", "t1")

    def __init__(self, t, x, z):
        self.kills = []
        self.cx, self.cz = x, z
        self.t0 = self.t1 = t

    def add(self, kill, x, z):
        self.kills.append(kill)
        n = len(self.kills)
        self.cx += (x - self.cx) / n            # running centroid
        self.cz += (z - self.cz) / n
        self.t1 = max(self.t1, kill["t"])


def cluster(kills, radius, gap):
    """Greedy spatiotemporal clustering. Kills must arrive time-ordered."""
    open_fights, done = [], []
    for k in kills:
        x, z = k["vx"], k["vz"]
        # Retire fights whose last kill is older than the gap.
        still = []
        for f in open_fights:
            if k["t"] - f.t1 > gap:
                done.append(f)
            else:
                still.append(f)
        open_fights = still

        best, best_d = None, radius
        for f in open_fights:
            d = math.hypot(x - f.cx, z - f.cz)
            if d < best_d:
                best, best_d = f, d
        if best is None:
            best = Fight(k["t"], x, z)
            open_fights.append(best)
        best.add(k, x, z)
    return done + open_fights


def census(replay, tick, cx, cz, radius, units):
    """
    What stood within `radius` of (cx, cz) at `tick`, per team.

    Returns team -> {"n", "cash", "piloted_n", "piloted_cash", "types": Counter}.
    Structures are counted separately; a Refinery inside the radius is context,
    not a combatant, and lumping it into force value inflates whichever side
    happened to be fighting at home.
    """
    snap = replay.ticks.get(tick)
    if not snap:
        return {}
    out = collections.defaultdict(
        lambda: {"n": 0, "cash": 0.0, "piloted_n": 0, "piloted_cash": 0.0,
                 "struct_n": 0, "struct_cash": 0.0,
                 "types": collections.Counter()})
    r2 = radius * radius
    for eid, pos in snap.items():
        dx, dz = pos[0] - cx, pos[1] - cz
        if dx * dx + dz * dz > r2:
            continue
        e = replay.entities.get(eid)
        if e is None or e.team_name in NEUTRAL:
            continue
        u = resolve(units, e.type_name, e.team_name)
        if u is None:
            UNMATCHED[e.type_name] += 1
        cost = u["cost"] if u else 0.0
        rec = out[e.team_name]
        if not e.is_unit or (u and u["is_structure"]):
            rec["struct_n"] += 1
            rec["struct_cash"] += cost
            continue
        rec["n"] += 1
        rec["cash"] += cost
        rec["types"][e.type_name] += 1
        try:
            piloted = bool(replay.get_controller_at_tick(eid, tick))
        except Exception:
            piloted = bool(e.controller_id)
        if piloted:
            rec["piloted_n"] += 1
            rec["piloted_cash"] += cost
    return out


def mine_replay(path, units, radius, gap, presence, min_kills):
    """Yield (fight_row, [unit_rows]) for one replay."""
    r = parse_srpl(path)
    base = os.path.basename(path)
    stamp = base[:8]
    tick_s = (getattr(r, "tick_interval_ms", 2000) or 2000) / 1000.0
    map_name = getattr(r, "map_name", "") or "?"
    players = len({n for _, n in r.players.values() if n and n.strip()})

    kills = []
    for tick, vid, aid, is_building in r.destructions:
        if aid == vid:                                   # despawn, not a kill
            continue
        v = r.entities.get(vid)
        if v is None or v.team_name in NEUTRAL:
            continue
        a = r.entities.get(aid)
        if a is not None and (a.team_name in NEUTRAL
                              or a.team_name == v.team_name):
            continue
        pos = None
        for t in (tick, tick - 1, tick + 1):
            snap = r.ticks.get(t)
            if snap and vid in snap:
                pos = snap[vid]
                break
        if pos is None:
            continue
        apos = None
        snap = r.ticks.get(tick) or {}
        if aid in snap:
            apos = snap[aid]
        try:
            piloted = bool(r.get_controller_at_tick(aid, tick)) if a else False
        except Exception:
            piloted = bool(a.controller_id) if a else False

        au = resolve(units, a.type_name, a.team_name) if a else None
        vu = resolve(units, v.type_name, v.team_name)
        if vu is None:
            UNMATCHED[v.type_name] += 1
        kills.append({
            "t": tick * tick_s, "tick": tick,
            "vx": pos[0], "vz": pos[1],
            "attacker": a.type_name if a else "?",
            "a_team": a.team_name if a else "?",
            "a_piloted": piloted,
            "a_cost": au["cost"] if au else 0.0,
            "victim": v.type_name, "v_team": v.team_name,
            "v_cost": vu["cost"] if vu else 0.0,
            "v_struct": bool(is_building),
            "range": math.dist(apos, pos) if apos else None,
        })

    kills.sort(key=lambda k: k["t"])
    fights = cluster(kills, radius, gap)

    fight_rows, unit_rows = [], []
    for i, f in enumerate(fights):
        if len(f.kills) < min_kills:
            continue
        t0_tick = f.kills[0]["tick"]
        t1_tick = f.kills[-1]["tick"]
        start = census(r, t0_tick, f.cx, f.cz, presence, units)
        end = census(r, t1_tick, f.cx, f.cz, presence, units)

        # Losses, by the team that OWNED the dead thing.
        lost = collections.defaultdict(
            lambda: {"units": 0, "cash": 0.0, "structs": 0, "struct_cash": 0.0})
        scored = collections.defaultdict(
            lambda: {"kills": 0, "cash": 0.0, "piloted": 0})
        for k in f.kills:
            L = lost[k["v_team"]]
            if k["v_struct"]:
                L["structs"] += 1
                L["struct_cash"] += k["v_cost"]
            else:
                L["units"] += 1
                L["cash"] += k["v_cost"]
            S = scored[k["a_team"]]
            S["kills"] += 1
            S["cash"] += k["v_cost"]
            S["piloted"] += 1 if k["a_piloted"] else 0

        teams = sorted(set(list(start.keys()) + list(lost.keys())))
        teams = [t for t in teams if t not in NEUTRAL]
        if len(teams) != 2:
            continue                       # 3-way brawls are a different animal

        fid = f"{base}#{i}"
        ranges = [k["range"] for k in f.kills if k["range"] is not None]
        row = {
            "fight_id": fid, "date": stamp, "map": map_name, "replay": base,
            "players": players,
            "t0": round(f.t0), "t1": round(f.t1),
            "duration": round(f.t1 - f.t0 + tick_s),
            "x": round(f.cx), "z": round(f.cz),
            "n_kills": len(f.kills),
            "median_range": round(sorted(ranges)[len(ranges) // 2]) if ranges else "",
        }
        for side, team in (("a", teams[0]), ("b", teams[1])):
            s, e, L, S = start.get(team, {}), end.get(team, {}), lost[team], scored[team]
            n = s.get("n", 0)
            cash = s.get("cash", 0.0)
            row[f"{side}_team"] = team
            row[f"{side}_n"] = n
            row[f"{side}_cash"] = round(cash)
            row[f"{side}_piloted_n"] = s.get("piloted_n", 0)
            row[f"{side}_piloted_frac"] = round(s.get("piloted_cash", 0.0) / cash, 3) if cash else ""
            row[f"{side}_struct_cash"] = round(s.get("struct_cash", 0.0))
            row[f"{side}_end_n"] = e.get("n", 0)
            row[f"{side}_end_cash"] = round(e.get("cash", 0.0))
            row[f"{side}_lost_units"] = L["units"]
            row[f"{side}_lost_cash"] = round(L["cash"])
            row[f"{side}_lost_structs"] = L["structs"]
            row[f"{side}_lost_struct_cash"] = round(L["struct_cash"])
            row[f"{side}_killed_cash"] = round(S["cash"])
            row[f"{side}_kill_piloted_frac"] = round(S["piloted"] / S["kills"], 3) if S["kills"] else ""
            for tname, cnt in s.get("types", {}).items():
                unit_rows.append({"fight_id": fid, "side": side, "team": team,
                                  "unit": tname, "count": cnt})
        fight_rows.append(row)
    return fight_rows, unit_rows


FIELDS = ["fight_id", "date", "map", "replay", "players", "t0", "t1", "duration",
          "x", "z", "n_kills", "median_range"]
for _s in ("a", "b"):
    FIELDS += [f"{_s}_team", f"{_s}_n", f"{_s}_cash", f"{_s}_piloted_n",
               f"{_s}_piloted_frac", f"{_s}_struct_cash", f"{_s}_end_n",
               f"{_s}_end_cash", f"{_s}_lost_units", f"{_s}_lost_cash",
               f"{_s}_lost_structs", f"{_s}_lost_struct_cash",
               f"{_s}_killed_cash", f"{_s}_kill_piloted_frac"]


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--limit", type=int, default=0, help="0 = all")
    ap.add_argument("--since", default="")
    ap.add_argument("--radius", type=float, default=300.0,
                    help="metres; kills further than this open a new fight")
    ap.add_argument("--gap", type=float, default=30.0,
                    help="seconds of quiet that ends a fight")
    ap.add_argument("--presence", type=float, default=400.0,
                    help="metres; census radius for who was standing there")
    ap.add_argument("--min-kills", type=int, default=3)
    ap.add_argument("--min-players", type=int, default=0)
    ap.add_argument("--archive", default=ARCHIVE)
    ap.add_argument("--out", default=os.path.join(OUTDIR, "engagements.csv"))
    args = ap.parse_args()

    os.makedirs(OUTDIR, exist_ok=True)
    paths = sorted(glob.glob(os.path.join(args.archive, "*.srpl")))
    paths = [p for p in paths
             if not (BROKEN_FROM <= os.path.basename(p)[:8] <= BROKEN_TO)
             and os.path.basename(p)[:8] >= (args.since or "0")]
    if args.limit:
        paths.sort(key=os.path.getsize, reverse=True)
        paths = paths[:args.limit]

    units = load_units()
    print(f"{len(paths)} replays | radius={args.radius}m gap={args.gap}s "
          f"presence={args.presence}m | balance {balance_epoch()['game_version']}",
          flush=True)

    unit_path = os.path.join(OUTDIR, "fight_units.csv")
    n_fights = n_bad = 0
    t_start = time.time()
    with io.open(args.out, "w", encoding="utf-8", newline="") as fh, \
         io.open(unit_path, "w", encoding="utf-8", newline="") as uh:
        w = csv.DictWriter(fh, fieldnames=FIELDS)
        w.writeheader()
        uw = csv.DictWriter(uh, fieldnames=["fight_id", "side", "team", "unit", "count"])
        uw.writeheader()
        for i, p in enumerate(paths, 1):
            try:
                rows, urows = mine_replay(p, units, args.radius, args.gap,
                                          args.presence, args.min_kills)
            except Exception as exc:
                n_bad += 1
                if n_bad <= 5:
                    print(f"  ! {os.path.basename(p)}: {exc}", flush=True)
                continue
            if args.min_players:
                rows = [r for r in rows if r["players"] >= args.min_players]
                keep = {r["fight_id"] for r in rows}
                urows = [u for u in urows if u["fight_id"] in keep]
            w.writerows(rows)
            uw.writerows(urows)
            n_fights += len(rows)
            if i % 100 == 0:
                el = time.time() - t_start
                print(f"  {i}/{len(paths)} replays, {n_fights:,} fights, "
                      f"{el:.0f}s ({el / i:.2f}s/replay)", flush=True)

    meta = {"replays": len(paths), "fights": n_fights, "unreadable": n_bad,
            "radius_m": args.radius, "gap_s": args.gap,
            "presence_m": args.presence, "min_kills": args.min_kills,
            "balance": balance_epoch(),
            "unpriced": dict(UNMATCHED.most_common(30)),
            "seconds": round(time.time() - t_start)}
    with io.open(os.path.join(OUTDIR, "engagements_meta.json"), "w",
                 encoding="utf-8") as fh:
        json.dump(meta, fh, indent=2)
    print(f"\n{n_fights:,} fights from {len(paths)} replays "
          f"({n_bad} unreadable) -> {args.out}")


if __name__ == "__main__":
    main()
