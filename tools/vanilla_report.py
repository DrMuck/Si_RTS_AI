#!/usr/bin/env python3
"""
Morning read of a headless VANILLA survey (preset headless-vanilla-3way): one
block per round log written since a start time, built only from what the mod
records about teams it does not command.

    python tools/vanilla_report.py [--since 2026-10-28_23:30] [--csv out.csv]

Per round: map, how it ended (force-end / natural, from the melon log copy),
per team the cash/units/structures trajectory at checkpoints ([CASH] lines),
every structure the commander asked for ([CMD] kind=structure: name counts,
refused placements), units queued and tech tiers requested.
"""
import argparse, collections, csv, glob, os, re, sys
from datetime import datetime

SERVER = r"E:\Steam\steamapps\common\Silica Dedicated Server"
RTSA = os.path.join(SERVER, "UserData", "RTSA")
CHECKPOINTS = (300, 600, 900, 1200, 1800, 2400, 3000, 3600)
TEAMS = ("Team_Alien", "Team_Human_Sol", "Team_Human_Centauri")

def read(path):
    with open(path, encoding="utf-8", errors="replace") as fh:
        return fh.read().splitlines()

def short(team):
    return team.replace("Team_Human_", "").replace("Team_", "")

def analyse(path):
    lines = read(path)
    m = re.search(r"scene=(\S+) startedAt=(\S+ \S+)", lines[0] if lines else "")
    scene, started = (m.group(1), m.group(2)) if m else ("?", "?")
    cash = collections.defaultdict(list)          # team -> [(t, cash, units, structures)]
    structs = collections.defaultdict(collections.Counter)
    refused = collections.defaultdict(collections.Counter)
    units = collections.defaultdict(collections.Counter)
    tech = collections.defaultdict(list)
    by_player = collections.Counter()
    last_t = 0
    for ln in lines:
        if ln.startswith("[CASH]"):
            mm = re.match(r"\[CASH\] t=(\d+) team=(\S+) by=(\S+).*?cash=(-?\d+) units=(\d+) structures=(\d+)", ln)
            if mm:
                t = int(mm.group(1)); last_t = max(last_t, t)
                cash[mm.group(2)].append((t, int(mm.group(4)), int(mm.group(5)), int(mm.group(6))))
                if mm.group(3) == "player": by_player[mm.group(2)] += 1
        elif ln.startswith("[CMD]"):
            mm = re.match(r"\[CMD\] t=(\d+) team=(\S+) by=(\S+) .*?what=(.+?) kind=(\w+)(?: at=\(([-\d]+),([-\d]+)\) yaw=(\d+))? from=.*? client=\w+ result=(\w+)", ln)
            if not mm: continue
            t, team, what, kind, res = int(mm.group(1)), mm.group(2), mm.group(4), mm.group(5), mm.group(9)
            last_t = max(last_t, t)
            if kind == "structure":
                (structs if res == "True" else refused)[team][what] += 1
            elif kind == "unit":
                units[team][what] += 1
            elif kind == "tech":
                tech[team].append((t, what, res))
    melon = path.replace(".log", ".melon.log")
    end = "?"
    if os.path.exists(melon):
        mt = "\n".join(read(melon))
        if "ForceEndRound: elapsed" in mt: end = "force-end 60min"
        elif "MissionState=ENDED" in mt:
            e = re.search(r"MissionState=ENDED .*?at t\+([\d.]+)s", mt)
            end = f"natural end at {float(e.group(1))/60:.0f} min" if e else "natural end"
        else: end = "still running / no end seen"
    return dict(path=path, scene=scene, started=started, cash=cash, structs=structs, refused=refused,
                units=units, tech=tech, by_player=by_player, last_t=last_t, end=end)

def at(series, t):
    best = None
    for row in series:
        if row[0] <= t: best = row
        else: break
    return best

def print_round(r):
    print(f"\n== {r['scene']}  {r['started']}  lasted {r['last_t']/60:.0f} min  ({r['end']})  {os.path.basename(r['path'])}")
    if r["by_player"]: print("   !! player commander samples:", dict(r["by_player"]))
    for team in TEAMS:
        s = r["cash"].get(team)
        if not s: print(f"   {short(team):9s} no samples"); continue
        cps = []
        for cp in CHECKPOINTS:
            row = at(s, cp)
            if row and cp <= r["last_t"] + 30: cps.append(f"{cp//60:>2}m:{row[1]:>6}/{row[2]:>3}u/{row[3]:>3}s")
        print(f"   {short(team):9s} cash/units/structures  " + "  ".join(cps))
        st = r["structs"].get(team, {}); rf = r["refused"].get(team, {})
        if st or rf:
            print(f"   {'':9s} built: " + ", ".join(f"{k} x{v}" for k, v in sorted(st.items(), key=lambda kv: -kv[1]))
                  + (("  | REFUSED: " + ", ".join(f"{k} x{v}" for k, v in rf.items())) if rf else ""))
        u = r["units"].get(team, {})
        if u:
            top = sorted(u.items(), key=lambda kv: -kv[1])[:8]
            print(f"   {'':9s} units queued ({sum(u.values())}): " + ", ".join(f"{k} x{v}" for k, v in top))
        tt = r["tech"].get(team, [])
        if tt: print(f"   {'':9s} tech: " + ", ".join(f"{w}@{t//60}m{'' if res=='True' else '(refused)'}" for t, w, res in tt))

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--since", default="2026-10-28_23:30")
    ap.add_argument("--csv", default="")
    a = ap.parse_args()
    since = datetime.strptime(a.since, "%Y-%m-%d_%H:%M")
    rounds = []
    for p in sorted(glob.glob(os.path.join(RTSA, "round-*.log"))):
        if p.endswith(".melon.log"): continue
        mm = re.search(r"round-(\d{8}_\d{6})-", os.path.basename(p))
        if not mm or datetime.strptime(mm.group(1), "%Y%m%d_%H%M%S") < since: continue
        if re.search(r"-(Intro|Loading|MainMenu).log$", p): continue
        rounds.append(analyse(p))
    print(f"{len(rounds)} round(s) since {since}")
    for r in rounds: print_round(r)
    if a.csv:
        with open(a.csv, "w", newline="", encoding="utf-8") as fh:
            w = csv.writer(fh); w.writerow(["scene", "started", "end", "team", "t", "cash", "units", "structures"])
            for r in rounds:
                for team, s in r["cash"].items():
                    for row in s: w.writerow([r["scene"], r["started"], r["end"], short(team), *row])
        print("csv ->", a.csv)

if __name__ == "__main__":
    sys.exit(main())
