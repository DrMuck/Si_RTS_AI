"""
Eco benchmark: income per round, keyed by MAP **and SPAWN**, alongside the
opening geometry of that spawn.

Why spawn-aware: the same map spawns the alien Nest in different places between
rounds — Badlands has been observed at both (-1110,1275) and (-675,-2475),
NarakaCity at three separate points. Those spawns have completely different
amounts of harvestable ground in early build range, so a per-map average mixes
incomparable rounds and hides both regressions and wins.

Two things are reported per (map, spawn):

  * cumulative income at 600s   — the comparable eco number, since rounds vary
                                  in length
  * opening geometry            — distance from the Nest to the N nearest
                                  biotics, and how many sit within 500/1000/
                                  1500m. This is what should predict the first
                                  ten minutes, and lets a weak result be read
                                  as "bad play" or "poor spawn".

Sources: round logs in UserData/RTSA (income + the Nest position actually used)
and the Si_MapBalance dumps (every ResourceArea). Amounts are read live rather
than assumed — they are a MapBalance setting and differ per map and spawn.

Usage:
    python benchmark.py                                   # every round on record
    python benchmark.py 20260729 20260730                 # only these days
    python benchmark.py 20260709-20260722 --save-baseline # freeze a baseline
    python benchmark.py 20260728-20260730                 # score against it
"""
import glob
import json
import math
import os
import re
import sys

SERVER = r"E:\Steam\steamapps\common\Silica Dedicated Server\UserData"
ROUNDS = os.path.join(SERVER, "RTSA")
BALANCE = os.path.join(SERVER, "MapBalance")
HERE = os.path.dirname(os.path.abspath(__file__))
BASELINE_PATH = os.path.join(HERE, "eco_baseline.json")

SCORE_AT_S = 600
NEAREST_N = 20
NON_MAPS = {"Loading", "MainMenu", "Intro"}


def biotics_for(map_name, _cache={}):
    """[(x, z, amount)] from the newest MapBalance dump for this map."""
    if map_name in _cache:
        return _cache[map_name]
    dumps = sorted(glob.glob(os.path.join(BALANCE, f"dump_{map_name}_*.txt")),
                   key=os.path.getmtime)
    out = []
    if dumps:
        text = open(dumps[-1], encoding="utf-8", errors="replace").read()
        for block in re.split(r"\n\[\d+\] ", text)[1:]:
            if not re.match(r'"ResourceArea_\w+" \(Biotics\)', block):
                continue
            pos = re.search(r"Position: \(([-\d.]+), [-\d.]+, ([-\d.]+)\)", block)
            amt = re.search(r"Resources: \d+ / (\d+)", block)
            if pos:
                out.append((float(pos.group(1)), float(pos.group(2)),
                            int(amt.group(1)) if amt else 0))
    _cache[map_name] = out
    return out


def geometry(map_name, nest):
    patches = biotics_for(map_name)
    if not patches:
        return None
    nx, nz = nest
    d = sorted(math.hypot(x - nx, z - nz) for x, z, _ in patches)
    return {
        "patches": len(patches),
        "nearest_m": round(d[0]),
        "median_m": round(d[len(d) // 2]),
        "within_500m": sum(1 for v in d if v <= 500),
        "within_1000m": sum(1 for v in d if v <= 1000),
        "within_1500m": sum(1 for v in d if v <= 1500),
        "nearest_n_m": [round(v) for v in d[:NEAREST_N]],
    }


def scan_round(path):
    """(map, nest, income@600) or None when the round is too short to compare."""
    name = os.path.basename(path)
    m = re.match(r"round-(\d{8})_\d{6}-(\w+)\.log$", name)
    if not m or m.group(2) in NON_MAPS:
        return None
    day, map_name = m.group(1), m.group(2)

    nest = None
    income = {}
    for line in open(path, encoding="utf-8", errors="replace"):
        if nest is None:
            mm = re.search(r"\[SPAWN_S\] team=Team_Alien structure=Nest at=\((-?\d+),(-?\d+)\)", line)
            if mm:
                nest = (int(mm.group(1)), int(mm.group(2)))
                continue
        mm = re.search(r"\[ECO\] team=Team_Alien t=(\d+)s .*cumulIncome=(\d+)", line)
        if mm:
            income[int(mm.group(1))] = int(mm.group(2))
    if nest is None or not income:
        return None
    # Require the round to have actually reached the scoring point.
    if max(income) < SCORE_AT_S - 40:
        return None
    at = [t for t in sorted(income) if t <= SCORE_AT_S]
    return {"day": day, "map": map_name, "nest": nest,
            "income600": income[max(at)], "file": name}


def key_of(r):
    return f"{r['map']}@{r['nest'][0]},{r['nest'][1]}"


def main():
    # Accept exact days (20260729) and inclusive ranges (20260709-20260722).
    lo = hi = None
    days = set()
    for a in sys.argv[1:]:
        if a.startswith("--"):
            continue
        if "-" in a:
            x, y = a.split("-", 1)
            lo, hi = x, y
        elif a.isdigit():
            days.add(a)

    def wanted(day):
        if lo and hi:
            return lo <= day <= hi
        return not days or day in days

    rows = []
    for path in sorted(glob.glob(os.path.join(ROUNDS, "round-*.log"))):
        r = scan_round(path)
        if r and wanted(r["day"]):
            rows.append(r)

    baseline = {}
    if os.path.exists(BASELINE_PATH):
        baseline = json.load(open(BASELINE_PATH, encoding="utf-8"))

    groups = {}
    for r in rows:
        groups.setdefault(key_of(r), []).append(r)

    print(f"{'map @ spawn':34s} {'n':>2} {'median@600s':>11} {'base':>8} {'vs':>7}"
          f" {'<500':>5} {'<1000':>6} {'<1500':>6} {'near':>6} {'med':>6}")
    out = {}
    for key in sorted(groups, key=lambda k: -len(groups[k])):
        g = groups[key]
        vals = sorted(r["income600"] for r in g)
        med = vals[len(vals) // 2]
        geo = geometry(g[0]["map"], g[0]["nest"]) or {}
        b = baseline.get(key, {}).get("income600")
        delta = f"{100 * (med - b) / b:+.0f}%" if b else "     -"
        print(f"{key:34s} {len(g):2d} {med:11,} {(f'{b:,}' if b else '-'):>8} {delta:>7}"
              f" {geo.get('within_500m', 0):5d} {geo.get('within_1000m', 0):6d}"
              f" {geo.get('within_1500m', 0):6d} {geo.get('nearest_m', 0):5d}m"
              f" {geo.get('median_m', 0):5d}m")
        out[key] = {"map": g[0]["map"], "nest": list(g[0]["nest"]),
                    "rounds": len(g), "income600": med,
                    "income600_all": vals, "geometry": geo}

    dest = BASELINE_PATH if "--save-baseline" in sys.argv         else os.path.join(HERE, "eco_results.json")
    json.dump(out, open(dest, "w", encoding="utf-8"), indent=2)
    print(f"\n{len(rows)} rounds over {len(groups)} map/spawn combinations -> {dest}")
    if not baseline:
        print(f"No baseline yet. To freeze the current numbers as the baseline:\n"
              f"    copy {os.path.basename(dest)} -> {os.path.basename(BASELINE_PATH)}")


if __name__ == "__main__":
    main()
