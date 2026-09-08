"""
Replay stage of the loop: per-round unit efficiency from the .srpl replay.

For each record in analysis/loop/rounds.jsonl, find the replay written for that round
(UserData/ReplayLogs/<yyyymmdd>_<hhmmss>_<Map>.srpl, started within a few minutes of the
round) and compute, for the alien team:

  per unit type : built, deaths, kills (units / structures), cash killed, cash lost,
                  exchange = cash killed / cash lost, median lifetime (s)
  army level    : clumping (share of 30-s samples where >= 60% of the living military
                  units stand within 300 m of their centroid), idle share (share of
                  unit-minutes with < 15 m displacement while > 400 m from the Nest)

The values are written back into rounds.jsonl under "replay". Deterministic; no model.

Usage:
    python tools/loop/replay.py [--in analysis/loop/rounds.jsonl] [--replays DIR]
"""
import argparse, collections, datetime, glob, json, math, os, statistics, sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, r"C:\Users\schwe\Projects\Silica-MapReplay\modules")
sys.path.insert(0, os.path.join(HERE, ".."))
from srpl_reader import parse_srpl          # noqa: E402
from mine_replay_kills import load_costs    # noqa: E402

REPLAYS_DEFAULT = r"E:\Steam\steamapps\common\Silica Dedicated Server\UserData\ReplayLogs"
WORKERS = {"Shrimp", "Queen", "Harvester", "Sol Harvester", "Cent Harvester"}
CLUMP_M = 300.0
IDLE_M = 15.0
NEST_M = 400.0


def find_replay(rec, replay_dir):
    if not rec.get("started"):
        return None
    started = datetime.datetime.fromisoformat(rec["started"])
    best, best_dt = None, None
    for p in glob.glob(os.path.join(replay_dir, f"*_{rec['scene']}.srpl")):
        base = os.path.basename(p)
        try:
            when = datetime.datetime.strptime(base[:15], "%Y%m%d_%H%M%S")
        except ValueError:
            continue
        dt = abs((when - started).total_seconds())
        if dt <= 300 and (best_dt is None or dt < best_dt):
            best, best_dt = p, dt
    return best


def analyse(path, cost, is_structure, team_prefix="alien"):
    rep = parse_srpl(path)
    dt = rep.tick_interval_ms / 1000.0
    ticks = rep.tick_numbers
    ents = rep.entities
    ours = {eid for eid, e in ents.items() if e.team_name.lower().startswith(team_prefix)}
    mil = {eid for eid in ours if ents[eid].is_unit and ents[eid].type_name not in WORKERS}

    # lifetime: first and last tick each unit is seen
    first, last = {}, {}
    for t in ticks:
        snap = rep.ticks[t]
        for eid in snap:
            if eid in ours:
                first.setdefault(eid, t); last[eid] = t

    per = collections.defaultdict(lambda: {"built": 0, "deaths": 0, "kills_units": 0, "kills_structures": 0,
                                           "cash_killed": 0, "cash_lost": 0, "lifetimes": []})
    for eid in ours:
        e = ents[eid]
        if not e.is_unit or e.type_name in WORKERS:
            continue
        p = per[e.type_name]; p["built"] += 1
        if eid in first:
            p["lifetimes"].append((last[eid] - first[eid]) * dt)
    dead = set()
    for tick, vid, aid, is_building in rep.destructions:
        v = ents.get(vid); a = ents.get(aid) if aid is not None else None
        if v is not None and vid in ours and v.is_unit and v.type_name not in WORKERS:
            per[v.type_name]["deaths"] += 1
            per[v.type_name]["cash_lost"] += cost.get(v.type_name, 0)
            dead.add(vid)
        if a is not None and aid in ours and a.is_unit and v is not None and vid not in ours:
            p = per[a.type_name]
            if is_building or is_structure.get(v.type_name, False):
                p["kills_structures"] += 1
            else:
                p["kills_units"] += 1
            p["cash_killed"] += cost.get(v.type_name, 0)

    # army-level samples every 30 s
    nest = None
    for eid, e in ents.items():
        if eid in ours and not e.is_unit and e.type_name in ("Nest", "Headquarters"):
            for t in ticks:
                p = rep.ticks[t].get(eid)
                if p:
                    nest = p; break
            if nest:
                break
    step = max(1, int(round(30.0 / dt)))
    clump_samples = 0; clumped = 0
    idle_minutes = 0.0; alive_minutes = 0.0
    prev_pos = {}
    for i in range(0, len(ticks), step):
        t = ticks[i]; snap = rep.ticks[t]
        pts = [snap[eid] for eid in mil if eid in snap]
        if len(pts) >= 6:
            cx = sum(p[0] for p in pts) / len(pts); cz = sum(p[1] for p in pts) / len(pts)
            near = sum(1 for p in pts if math.hypot(p[0] - cx, p[1] - cz) <= CLUMP_M)
            clump_samples += 1
            if near >= 0.6 * len(pts):
                clumped += 1
        for eid in mil:
            p = snap.get(eid)
            if p is None:
                prev_pos.pop(eid, None); continue
            q = prev_pos.get(eid)
            if q is not None:
                alive_minutes += 0.5
                far = nest is None or math.hypot(p[0] - nest[0], p[1] - nest[1]) > NEST_M
                if far and math.hypot(p[0] - q[0], p[1] - q[1]) < IDLE_M:
                    idle_minutes += 0.5
            prev_pos[eid] = p

    out = {"replay": os.path.basename(path), "units": {}}
    for name, p in per.items():
        lt = p.pop("lifetimes")
        p["median_life_s"] = round(statistics.median(lt)) if lt else None
        p["exchange"] = round(p["cash_killed"] / p["cash_lost"], 2) if p["cash_lost"] > 0 else None
        out["units"][name] = p
    tot_k = sum(p["cash_killed"] for p in per.values()); tot_l = sum(p["cash_lost"] for p in per.values())
    out["army"] = {"cash_killed": tot_k, "cash_lost": tot_l, "exchange": round(tot_k / tot_l, 2) if tot_l else None,
                   "clumping": round(clumped / clump_samples, 2) if clump_samples else None,
                   "idle_share": round(idle_minutes / alive_minutes, 2) if alive_minutes else None}
    return out


def report(out, prefix):
    """The per-unit exchange table for one team, for a round read on its own."""
    a = out["army"]
    print("")
    print(f"## {prefix}  ({out['replay']})")
    print(f"army: killed {a['cash_killed']} lost {a['cash_lost']} exchange {a['exchange']} "
          f"| clumping {a['clumping']} idle {a['idle_share']}")
    rows = sorted(out["units"].items(), key=lambda kv: -kv[1]["cash_lost"])
    if not rows:
        print("  no military units")
        return
    print(f"  {'unit':<20}{'built':>6}{'died':>6}{'kills':>7}{'killed$':>10}{'lost$':>10}{'exch':>7}{'life_s':>8}")
    for name, p in rows:
        kills = p["kills_units"] + p["kills_structures"]
        exch = "-" if p["exchange"] is None else f"{p['exchange']:.2f}"
        life = "-" if p["median_life_s"] is None else str(p["median_life_s"])
        print(f"  {name:<20}{p['built']:>6}{p['deaths']:>6}{kills:>7}{p['cash_killed']:>10}"
              f"{p['cash_lost']:>10}{exch:>7}{life:>8}")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--in", dest="inp", default=os.path.join(HERE, "..", "..", "analysis", "loop", "rounds.jsonl"))
    ap.add_argument("--replays", default=REPLAYS_DEFAULT)
    ap.add_argument("--only", default=None, help="substring of a round name to process")
    ap.add_argument("--replay", default=None,
                    help="one .srpl to read on its own (or 'newest'); prints the table, writes no jsonl")
    ap.add_argument("--teams", default="sol,cent", help="team prefixes for --replay, comma separated")
    args = ap.parse_args()
    cost, is_structure = load_costs()
    if args.replay:
        path = args.replay
        if path == "newest":
            path = max(glob.glob(os.path.join(args.replays, "*.srpl")), key=os.path.getmtime)
        for prefix in [t.strip() for t in args.teams.split(",") if t.strip()]:
            report(analyse(path, cost, is_structure, team_prefix=prefix), prefix)
        return
    rows = [json.loads(l) for l in open(args.inp, encoding="utf-8") if l.strip()]
    n = 0
    for rec in rows:
        if args.only and args.only not in rec["round"]:
            continue
        if rec.get("replay") and not args.only:
            continue
        path = find_replay(rec, args.replays)
        if not path:
            continue
        try:
            our = rec.get("our_team", "Team_Alien")
            prefix = "alien" if our == "Team_Alien" else ("sol" if "Sol" in our else "cent")
            rec["replay"] = analyse(path, cost, is_structure, team_prefix=prefix)
            n += 1
            a = rec["replay"]["army"]
            print(f"{rec['round']}: exchange {a['exchange']} clumping {a['clumping']} idle {a['idle_share']} ({os.path.basename(path)})")
        except Exception as ex:
            print("skip", rec["round"], ex, file=sys.stderr)
    with open(args.inp, "w", encoding="utf-8") as fh:
        for rec in rows:
            fh.write(json.dumps(rec) + "\n")
    print(f"replay stage: {n} rounds analysed")


if __name__ == "__main__":
    main()
