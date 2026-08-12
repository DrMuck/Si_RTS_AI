#!/usr/bin/env python3
"""
Turn the replay archive into a dataset of FIGHTS, not kills.

WHY THE UNIT OF ANALYSIS IS A FIGHT
-----------------------------------
`tools/mine_replay_kills.py` already counts who kills what, and the counter it
produces has a known blind spot written into its own header: a unit that softens
a target and dies before the last hit is credited nothing, so a duel matrix
overstates whatever arrives last and understates screens and artillery. That
blind spot is not fixable at the level of a kill, because a kill has no
denominator — it does not know how many units were standing there.

A fight does. Once the engagement is the row, the only question the military
planner ever asks becomes measurable:

    "if I commit 12k of these against 9k of those, do I win, what does it cost,
     and how long does it take"

HOW A FIGHT IS BUILT, AND WHAT COUNTS AS FORCE
----------------------------------------------
Both live in `fights.py`, which documents why each is shaped the way it is:
kills cluster against the battle's RECENT centroid so a rolling engagement
follows itself, then fights sharing a unit are merged; and force is what had an
enemy inside its own weapon reach, not what happened to stand in a circle.

Both were rewritten after the first dataset: fragmentation and radius-presence
between them flattened P(richer side wins) to 53% and drove the exchange
exponent to 0.01, which would have been published as "mass does not pay".

WHAT THE ARCHIVE IS, AND IS NOT
-------------------------------
The project snapshot only. The live dedicated-server folder is deliberately not
read — that box is DrMuck's own, and most of what it holds is soaks and bring-up
rounds where the economy runs unopposed.

Rows carry per-side commander and FPS ELO (`skill.py`) so a fit can require that
someone who can play was in the round, and `piloted_frac` per side because 81%
of kills in real games are scored by piloted units and the bot cannot pilot.

WHAT NO MODEL FITTED ON THIS CAN SEE
------------------------------------
- Damage that did not kill. A fight both sides walked away from reads as a small
  exchange, so retreats are under-weighted and survivors over-valued.
- Terrain. Two fights at the same range, one across a ridge, are one row each.
- Structures. Turret kills are counted, turret VALUE is not, so a fight decided
  by static defence looks like one the attacker lost for no reason.
- Absorption. Participation is by a unit's OWN reach, so a meatshield soaking
  fire from beyond its range does not count as force. Crabs will read small.

    python mil_sim/mine_engagements.py [--limit N] [--since YYYYMMDD]
                                       [--min-commander-elo 1650]

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
from roles import combatants, is_force                      # noqa: E402
from skill import Skill, report_unmatched                   # noqa: E402
import fights as F                                          # noqa: E402

SERVER = r"E:\Steam\steamapps\common\Silica Dedicated Server"
MAPREPLAY = os.path.join(SERVER, "Mod MapReplay")
sys.path.insert(0, os.path.join(MAPREPLAY, "modules"))
from srpl_reader import parse_srpl                          # noqa: E402

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
ARCHIVES = [os.path.join(ROOT, "Serverdata", "ReplayLogs - Copy")]
OUTDIR = os.path.join(os.path.dirname(os.path.abspath(__file__)), "out")

BROKEN_FROM, BROKEN_TO = "20260715", "20260731"
NEUTRAL = {"Wildlife", "GM", "Unknown"}

UNMATCHED = collections.Counter()
IS_COMBAT = {}
REACH = {}


def force_test(units):
    """Closure the participation pass calls for every entity it sees."""
    def test(type_name, team_name):
        return is_force(IS_COMBAT, units, type_name, team_name)
    return test


def piloted_at(replay, eid, tick, entity):
    try:
        return bool(replay.get_controller_at_tick(eid, tick))
    except Exception:
        return bool(entity.controller_id) if entity else False


def radius_census(replay, tick, cx, cz, radius, units, test):
    """The old presence census, kept for the reinforcement signal."""
    snap = replay.ticks.get(tick)
    if not snap:
        return {}
    out = collections.defaultdict(lambda: {"n": 0, "cash": 0.0})
    r2 = radius * radius
    for eid, pos in snap.items():
        dx, dz = pos[0] - cx, pos[1] - cz
        if dx * dx + dz * dz > r2:
            continue
        e = replay.entities.get(eid)
        if e is None or e.team_name in NEUTRAL:
            continue
        u = resolve(units, e.type_name, e.team_name)
        if u is None or u["is_structure"]:
            continue
        ok, cost = test(e.type_name, e.team_name)
        if not ok:
            continue
        out[e.team_name]["n"] += 1
        out[e.team_name]["cash"] += cost
    return out


def read_kills(replay, units, tick_s):
    """Kills worth clustering: two real teams, a known victim, a position."""
    rows = []
    for tick, vid, aid, is_building in replay.destructions:
        if aid == vid:
            continue
        v = replay.entities.get(vid)
        if v is None or v.team_name in NEUTRAL:
            continue
        a = replay.entities.get(aid)
        if a is not None and (a.team_name in NEUTRAL
                              or a.team_name == v.team_name):
            continue
        pos = None
        for t in (tick, tick - 1, tick + 1):
            snap = replay.ticks.get(t)
            if snap and vid in snap:
                pos = snap[vid]
                break
        if pos is None:
            continue
        snap = replay.ticks.get(tick) or {}
        apos = snap.get(aid)
        au = resolve(units, a.type_name, a.team_name) if a else None
        vu = resolve(units, v.type_name, v.team_name)
        if vu is None:
            UNMATCHED[v.type_name] += 1
        rows.append({
            "t": tick * tick_s, "tick": tick,
            "vx": pos[0], "vz": pos[1],
            "aid": aid if a else None, "vid": vid,
            "attacker": a.type_name if a else "?",
            "a_team": a.team_name if a else "?",
            "a_piloted": piloted_at(replay, aid, tick, a) if a else False,
            "victim": v.type_name, "v_team": v.team_name,
            "v_cost": vu["cost"] if vu else 0.0,
            "v_struct": bool(is_building),
            "range": math.dist(apos, pos) if apos else None,
        })
    rows.sort(key=lambda k: k["t"])
    return rows


def mine_replay(path, units, args, skill, test):
    r = parse_srpl(path)
    base = os.path.basename(path)
    tick_s = (getattr(r, "tick_interval_ms", 2000) or 2000) / 1000.0
    map_name = getattr(r, "map_name", "") or "?"
    players = len({n for _, n in r.players.values() if n and n.strip()})
    if skill and skill.ok():
        overall, per_team = skill.rate(r)
    else:
        overall, per_team = {"cmd": 0.0, "fps": 0.0}, {}

    kills = read_kills(r, units, tick_s)
    battles = F.cluster(kills, args.radius, args.gap, recent=args.recent,
                        merge_gap=args.merge_gap)

    fight_rows, unit_rows = [], []
    for i, f in enumerate(battles):
        if len(f.kills) < args.min_kills:
            continue
        t0_tick, t1_tick = f.kills[0]["tick"], f.kills[-1]["tick"]

        eng, sampled, engaged = F.participation(
            r, t0_tick, t1_tick, f.cx, f.cz, args.presence, units, resolve,
            test, REACH, NEUTRAL, samples=args.samples)
        end = radius_census(r, t1_tick, f.cx, f.cz, args.presence, units, test)

        lost = collections.defaultdict(
            lambda: {"units": 0, "cash": 0.0, "structs": 0, "struct_cash": 0.0})
        scored = collections.defaultdict(lambda: {"kills": 0, "cash": 0.0,
                                                  "piloted": 0})
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

        teams = sorted({t for t in list(eng) + list(lost) if t not in NEUTRAL})
        if len(teams) != 2:
            continue

        # Piloted share of the force that actually engaged, at the first tick
        # we sampled — control read then, not at registration, because a unit
        # grabbed mid-fight registers as AI-controlled.
        pil = collections.defaultdict(lambda: [0, 0.0])
        for eid, (team, tname, cost) in engaged.items():
            if piloted_at(r, eid, sampled[0], r.entities.get(eid)):
                pil[team][0] += 1
                pil[team][1] += cost

        fid = f"{base}#{i}"
        ranges = sorted(k["range"] for k in f.kills if k["range"] is not None)
        row = {
            "fight_id": fid, "date": base[:8], "map": map_name, "replay": base,
            "players": players,
            "t0": round(f.t0), "t1": round(f.t1),
            "duration": round(f.t1 - f.t0 + tick_s),
            "x": round(f.cx), "z": round(f.cz),
            "n_kills": len(f.kills), "n_sampled": len(sampled),
            "median_range": round(ranges[len(ranges) // 2]) if ranges else "",
            "best_cmd_elo": round(overall["cmd"]),
            "best_fps_elo": round(overall["fps"]),
        }
        for side, team in (("a", teams[0]), ("b", teams[1])):
            e = eng.get(team, {})
            cash = e.get("cash", 0.0)
            pres = e.get("present_cash", 0.0)
            L, S = lost[team], scored[team]
            row[f"{side}_team"] = team
            row[f"{side}_n"] = e.get("n", 0)
            row[f"{side}_cash"] = round(cash)
            row[f"{side}_present_n"] = e.get("present_n", 0)
            row[f"{side}_present_cash"] = round(pres)
            row[f"{side}_trade_frac"] = round(cash / pres, 3) if pres else ""
            row[f"{side}_piloted_n"] = pil[team][0]
            row[f"{side}_piloted_frac"] = round(pil[team][1] / cash, 3) if cash else ""
            row[f"{side}_cmd_elo"] = round(per_team.get(team, {}).get("cmd", 0.0))
            row[f"{side}_fps_elo"] = round(per_team.get(team, {}).get("fps", 0.0))
            row[f"{side}_end_n"] = end.get(team, {}).get("n", 0)
            row[f"{side}_end_cash"] = round(end.get(team, {}).get("cash", 0.0))
            row[f"{side}_lost_units"] = L["units"]
            row[f"{side}_lost_cash"] = round(L["cash"])
            row[f"{side}_lost_structs"] = L["structs"]
            row[f"{side}_lost_struct_cash"] = round(L["struct_cash"])
            row[f"{side}_killed_cash"] = round(S["cash"])
            row[f"{side}_kill_piloted_frac"] = (
                round(S["piloted"] / S["kills"], 3) if S["kills"] else "")
            for tname, cnt in e.get("types", {}).items():
                unit_rows.append({"fight_id": fid, "side": side, "team": team,
                                  "unit": tname, "count": cnt})
        fight_rows.append(row)
    return fight_rows, unit_rows


FIELDS = ["fight_id", "date", "map", "replay", "players", "t0", "t1", "duration",
          "x", "z", "n_kills", "n_sampled", "median_range",
          "best_cmd_elo", "best_fps_elo"]
for _s in ("a", "b"):
    FIELDS += [f"{_s}_team", f"{_s}_n", f"{_s}_cash", f"{_s}_present_n",
               f"{_s}_present_cash", f"{_s}_trade_frac", f"{_s}_piloted_n",
               f"{_s}_piloted_frac", f"{_s}_cmd_elo", f"{_s}_fps_elo",
               f"{_s}_end_n", f"{_s}_end_cash", f"{_s}_lost_units",
               f"{_s}_lost_cash", f"{_s}_lost_structs",
               f"{_s}_lost_struct_cash", f"{_s}_killed_cash",
               f"{_s}_kill_piloted_frac"]


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--limit", type=int, default=0, help="0 = all")
    ap.add_argument("--since", default="")
    ap.add_argument("--radius", type=float, default=350.0,
                    help="metres from the battle's recent centroid")
    ap.add_argument("--gap", type=float, default=45.0,
                    help="seconds of quiet that closes a cluster")
    ap.add_argument("--merge-gap", type=float, default=60.0,
                    help="seconds within which shared-unit clusters merge")
    ap.add_argument("--recent", type=int, default=5,
                    help="kills averaged for the centre a rolling battle tracks")
    ap.add_argument("--presence", type=float, default=600.0,
                    help="metres scanned for candidates; reach decides force")
    ap.add_argument("--samples", type=int, default=6,
                    help="ticks sampled across a fight for the reach test")
    ap.add_argument("--min-kills", type=int, default=3)
    ap.add_argument("--min-players", type=int, default=0)
    ap.add_argument("--min-commander-elo", type=float, default=0.0)
    ap.add_argument("--min-fps-elo", type=float, default=0.0)
    ap.add_argument("--archive", action="append", default=None)
    ap.add_argument("--out", default=os.path.join(OUTDIR, "engagements.csv"))
    args = ap.parse_args()

    os.makedirs(OUTDIR, exist_ok=True)
    seen, paths = set(), []
    for d in (args.archive or ARCHIVES):
        for p in sorted(glob.glob(os.path.join(d, "*.srpl"))):
            b = os.path.basename(p)
            if b not in seen:
                seen.add(b)
                paths.append(p)
    paths = [p for p in paths
             if not (BROKEN_FROM <= os.path.basename(p)[:8] <= BROKEN_TO)
             and os.path.basename(p)[:8] >= (args.since or "0")]
    if args.limit:
        paths.sort(key=os.path.getsize, reverse=True)
        paths = paths[:args.limit]

    units = load_units()
    IS_COMBAT.update(combatants(units)[0])
    REACH.update(F.load_reach())
    if not REACH:
        print("  ! no reach table — run measure_range.py --emit-reach first; "
              "force will be under-counted at contact range")
    test = force_test(units)
    skill = Skill()

    print(f"{len(paths)} replays | radius={args.radius}m gap={args.gap}s "
          f"merge={args.merge_gap}s presence={args.presence}m "
          f"samples={args.samples} | reach entries {len(REACH)} | "
          f"balance {balance_epoch()['game_version']}", flush=True)

    unit_path = os.path.join(OUTDIR, "fight_units.csv")
    n_fights = n_bad = n_dropped = 0
    t_start = time.time()
    with io.open(args.out, "w", encoding="utf-8", newline="") as fh, \
         io.open(unit_path, "w", encoding="utf-8", newline="") as uh:
        w = csv.DictWriter(fh, fieldnames=FIELDS)
        w.writeheader()
        uw = csv.DictWriter(uh, fieldnames=["fight_id", "side", "team", "unit",
                                            "count"])
        uw.writeheader()
        for i, p in enumerate(paths, 1):
            try:
                rows, urows = mine_replay(p, units, args, skill, test)
            except Exception as exc:
                n_bad += 1
                if n_bad <= 5:
                    print(f"  ! {os.path.basename(p)}: {exc}", flush=True)
                continue
            before = len(rows)
            if args.min_players:
                rows = [r for r in rows if r["players"] >= args.min_players]
            if args.min_commander_elo or args.min_fps_elo:
                rows = [r for r in rows
                        if r["best_cmd_elo"] >= args.min_commander_elo
                        or r["best_fps_elo"] >= args.min_fps_elo]
            n_dropped += before - len(rows)
            keep = {r["fight_id"] for r in rows}
            w.writerows(rows)
            uw.writerows([u for u in urows if u["fight_id"] in keep])
            n_fights += len(rows)
            if i % 200 == 0:
                el = time.time() - t_start
                print(f"  {i}/{len(paths)} replays, {n_fights:,} fights, "
                      f"{el:.0f}s ({el / i:.2f}s/replay)", flush=True)

    meta = {"replays": len(paths), "fights": n_fights, "unreadable": n_bad,
            "radius_m": args.radius, "gap_s": args.gap,
            "merge_gap_s": args.merge_gap, "recent_kills": args.recent,
            "presence_m": args.presence, "samples": args.samples,
            "min_kills": args.min_kills,
            "min_commander_elo": args.min_commander_elo,
            "min_fps_elo": args.min_fps_elo, "dropped_by_filter": n_dropped,
            "balance": balance_epoch(),
            "unpriced": dict(UNMATCHED.most_common(30)),
            "seconds": round(time.time() - t_start)}
    with io.open(os.path.join(OUTDIR, "engagements_meta.json"), "w",
                 encoding="utf-8") as fh:
        json.dump(meta, fh, indent=2)
    print(f"\n{n_fights:,} fights from {len(paths)} replays "
          f"({n_bad} unreadable) -> {args.out}")
    if n_dropped:
        print(f"  {n_dropped:,} fights dropped by the player/skill filters")
    report_unmatched()
    if UNMATCHED:
        print(f"  unpriced type names: {UNMATCHED.most_common(8)}")


if __name__ == "__main__":
    main()
