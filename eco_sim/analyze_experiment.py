"""Per-arm results for the producer-density experiment.

Groups rounds by the configId the harness stamped on them, after throwing out
any round that fails a validity check — a wrong spawn makes a round
incomparable, and an unstamped round belongs to no arm.

Usage:  python analyze_experiment.py [since-ISO]     e.g. 2026-08-05T19
"""
import csv, glob, json, os, re, statistics, sys
from datetime import datetime, timedelta

RTSA = r"E:\Steam\steamapps\common\Silica Dedicated Server\UserData\RTSA"
SINCE = sys.argv[1] if len(sys.argv) > 1 else "2026-08-05T19"
EXPECTED_SPAWN = (2520, 1275)      # NarakaCity; None disables the check
SPAWN_TOLERANCE_M = 50


def stamp(name):
    m = re.search(r"(\d{8}_\d{6})", name)
    return datetime.strptime(m.group(1), "%Y%m%d_%H%M%S") if m else None


def nest_of(round_dir):
    fs = sorted(glob.glob(os.path.join(round_dir, "rev-*.json")))
    for f in fs:
        try:
            j = json.load(open(f))
        except Exception:
            continue
        n = next((b for b in j.get("built", []) if b["kind"] == "Nest"), None)
        if n:
            return (round(n["x"]), round(n["z"]))
    return None


# ---- collect rounds -------------------------------------------------------
bench = [json.loads(l) for l in open(os.path.join(RTSA, "benchmarks.jsonl"), encoding="utf-8") if l.strip()]
bench = [b for b in bench if b["team"] == "Team_Alien" and b["ts"] >= SINCE and b["checkpoints"]]
bench.sort(key=lambda b: b["ts"])

bp_dirs = [(stamp(os.path.basename(d)), d) for d in glob.glob(os.path.join(RTSA, "blueprint", "round-*"))]
bp_dirs = [(t, d) for t, d in bp_dirs if t]
cash_files = sorted((stamp(os.path.basename(p)), p) for p in glob.glob(os.path.join(RTSA, "cashflow_*.csv")))

rounds, discarded = [], []
for b in bench:
    end = datetime.strptime(b["ts"], "%Y-%m-%dT%H:%M:%S")
    cand = [(t, d) for t, d in bp_dirs
            if b["map"] in d and timedelta(minutes=20) < end - t < timedelta(minutes=60)]
    bp = max(cand, key=lambda td: td[0])[1] if cand else None
    nest = nest_of(bp) if bp else None

    arm = b.get("configId", "")
    if not arm or arm in ("baseline", "planner_active_2026-07-05"):
        discarded.append((b["ts"], b["map"], f"no arm tag ('{arm}')"))
        continue
    if EXPECTED_SPAWN and nest and (abs(nest[0] - EXPECTED_SPAWN[0]) > SPAWN_TOLERANCE_M
                                    or abs(nest[1] - EXPECTED_SPAWN[1]) > SPAWN_TOLERANCE_M):
        discarded.append((b["ts"], b["map"], f"spawn {nest} != {EXPECTED_SPAWN}"))
        continue

    # workers/producers from the round's cashflow csv
    start = min(cand, key=lambda td: end - td[0])[0] if cand else None
    cf = next((p for t, p in cash_files
               if start and timedelta(minutes=-3) < t - start < timedelta(minutes=3)), None)
    workers = {}
    if cf:
        try:
            rows = list(csv.DictReader(open(cf)))
            t0 = float(rows[0]["t_sec"])
            for m in (10, 20, 30, 40):
                r = next((r for r in rows if float(r["t_sec"]) - t0 >= m * 60), None)
                if r:
                    workers[m] = (int(r["shrimps"]), int(r["committed_cysts"]))
        except Exception:
            pass

    # claimed-but-untapped, from the last bc_metrics line of this round
    untapped = tapped = None
    rounds.append(dict(arm=arm, ts=b["ts"], map=b["map"], cps=b["checkpoints"],
                       final=b["cumulIncome"], elapsed=b["elapsedS"],
                       workers=workers, untapped=untapped, tapped=tapped))

# ---- claimed-but-untapped from bc_metrics --------------------------------
try:
    lines = [json.loads(l) for l in open(os.path.join(RTSA, "bc_metrics.jsonl"), encoding="utf-8") if l.strip()]
    lines = [l for l in lines if l.get("team") == "Team_Alien" and l.get("ts", "") >= SINCE]
    for r in rounds:
        end = datetime.strptime(r["ts"], "%Y-%m-%dT%H:%M:%S")
        mine = [l for l in lines
                if timedelta(0) < end - datetime.strptime(l["ts"], "%Y-%m-%dT%H:%M:%S") < timedelta(minutes=45)]
        if not mine:
            continue
        last = max(mine, key=lambda l: l["roundT"])
        bcs = last.get("bcs", [])
        r["tapped"] = sum(1 for b in bcs if b.get("workersInRange", 0) > 0)
        r["untapped"] = sum(1 for b in bcs if b.get("workersInRange", 0) == 0)
except FileNotFoundError:
    pass

# ---- report ---------------------------------------------------------------
def k(v):
    return f"{v/1000:,.0f}k"


print(f"rounds usable: {len(rounds)}   discarded: {len(discarded)}   since {SINCE}")
for ts, mp, why in discarded:
    print(f"  DISCARDED {ts} {mp:<16} {why}")
if not rounds:
    raise SystemExit("\nnothing to report yet")

arms = {}
for r in rounds:
    arms.setdefault(r["arm"], []).append(r)

print(f"\n{'arm':<10}{'n':>3}{'inc@20m':>10}{'inc@30m':>10}{'final':>10}"
      f"{'w@20m':>8}{'cyst@20m':>10}{'untapped':>10}")
for arm, rs in sorted(arms.items()):
    def mean(f):
        vals = [f(r) for r in rs if f(r) is not None]
        return statistics.mean(vals) if vals else 0

    print(f"{arm:<10}{len(rs):>3}"
          f"{k(mean(lambda r: r['cps'].get('1200', 0))):>10}"
          f"{k(mean(lambda r: r['cps'].get('1800', 0))):>10}"
          f"{k(mean(lambda r: r['final'])):>10}"
          f"{mean(lambda r: r['workers'].get(20, (None, None))[0]):>8.0f}"
          f"{mean(lambda r: r['workers'].get(20, (None, None))[1]):>10.1f}"
          f"{mean(lambda r: r['untapped']):>10.1f}")

print("\nspread within arm (final income, so noise is visible next to the means):")
for arm, rs in sorted(arms.items()):
    vals = sorted(r["final"] for r in rs)
    span = (max(vals) - min(vals)) / max(1, statistics.mean(vals)) * 100
    print(f"  {arm:<10} {[k(v) for v in vals]}  spread {span:.0f}% of mean"
          + ("   <-- fewer than 3 rounds, treat as anecdote" if len(rs) < 3 else ""))
