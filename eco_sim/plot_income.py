"""
Plot cumulative Alien income trajectories across all rounds/maps.

Data source: benchmarks.jsonl (one JSON line per team per round, written by
EcoRateSampler.WriteBenchmarkLines at round end). Contains cumulIncome
checkpoints at t = 60/120/180/300/600/900/1200 seconds.

Two figures:
  1. Per-map subplot grid — each map shows its rounds' trajectories
  2. Summary — median trajectory per map, overlaid, ranked by 10-min income

Usage:
  python plot_income.py                          # all Alien rounds
  python plot_income.py --map GreatErg           # single map focus
  python plot_income.py --since 2026-07-09       # from that date onward
"""

import json
import os
import sys
from collections import defaultdict
from pathlib import Path

try:
    import matplotlib.pyplot as plt
    import matplotlib.ticker as mticker
    import numpy as np
except ImportError:
    print("pip install matplotlib numpy")
    sys.exit(1)

BM_PATH = Path("E:/Steam/steamapps/common/Silica Dedicated Server/UserData/RTSA/benchmarks.jsonl")
CHECKPOINTS_S = [60, 120, 180, 300, 600, 900, 1200, 1500, 1800]


def load(map_filter=None, since=None, min_round_s=300, skip_cap_only=True):
    """Load Alien rounds. Returns {map_name: [(ts, [(t, cumIncome), ...]), ...]}.

    skip_cap_only: filter out crashed rounds where cumIncome equals starter cap
    (9000) throughout — those are killed rounds with no real eco progression.
    """
    by_map = defaultdict(list)
    with open(BM_PATH) as f:
        for line in f:
            try:
                d = json.loads(line)
            except Exception:
                continue
            if "Alien" not in d.get("team", ""):
                continue
            if map_filter and d["map"] != map_filter:
                continue
            if since and d["ts"] < since:
                continue
            if d.get("elapsedS", 0) < min_round_s:
                continue    # skip short/crashed rounds
            chk = d.get("checkpoints", {})
            # Suspicious "cap-only" filter — round crashed and cumulIncome stuck at ~9000
            if skip_cap_only:
                vals = [chk.get(str(t)) or chk.get(t) for t in CHECKPOINTS_S]
                vals = [v for v in vals if v is not None]
                if vals and max(vals) - min(vals) < 100 and max(vals) <= 12000:
                    continue    # flat trajectory around cap
            # keys are strings in json — canonicalize
            traj = []
            for t_s in CHECKPOINTS_S:
                if str(t_s) in chk:
                    traj.append((t_s, chk[str(t_s)]))
                elif t_s in chk:
                    traj.append((t_s, chk[t_s]))
            # Append round-end final point (elapsedS, cumulIncome) so plot
            # extends to whatever the actual round length was (older data
            # only has checkpoints up to 1200s, newer will have 1500/1800).
            elapsed = d.get("elapsedS")
            final = d.get("cumulIncome")
            if elapsed and final is not None:
                if not traj or elapsed > traj[-1][0] + 5:
                    traj.append((int(elapsed), int(final)))
            if len(traj) < 2:
                continue
            by_map[d["map"]].append((d["ts"], traj))
    return by_map


def median_at_checkpoint(rounds, t_s):
    """Median cumulIncome at checkpoint t_s across rounds (or None if missing).
    Also accepts final-elapsed points within ±30s of the target checkpoint."""
    vals = []
    for _, traj in rounds:
        for t, v in traj:
            if abs(t - t_s) < 30:
                vals.append(v)
                break
    return float(np.median(vals)) if vals else None


def plot_per_map(by_map):
    """Grid of subplots, one per map, with all rounds' trajectories."""
    maps = sorted(by_map.keys(), key=lambda m: -len(by_map[m]))
    n = len(maps)
    cols = 3
    rows = (n + cols - 1) // cols
    fig, axes = plt.subplots(rows, cols, figsize=(15, 4 * rows), squeeze=False)

    for i, m in enumerate(maps):
        ax = axes[i // cols][i % cols]
        rounds = by_map[m]
        for _, traj in rounds:
            xs = [t for t, _ in traj]
            ys = [v for _, v in traj]
            ax.plot(xs, ys, "-o", markersize=3, alpha=0.5, linewidth=0.8)
        # Median line (thick)
        med_xs, med_ys = [], []
        for t_s in CHECKPOINTS_S:
            m_val = median_at_checkpoint(rounds, t_s)
            if m_val is not None:
                med_xs.append(t_s)
                med_ys.append(m_val)
        if med_xs:
            ax.plot(med_xs, med_ys, "k-", linewidth=2.5, label=f"median (n={len(rounds)})")
        ax.set_title(f"{m}", fontsize=11)
        ax.set_xlabel("round time (s)")
        ax.set_ylabel("cumulative income")
        ax.legend(fontsize=8)
        ax.grid(True, alpha=0.3)
        ax.yaxis.set_major_formatter(mticker.FuncFormatter(lambda x, p: f"{int(x/1000)}k"))

    # Hide any unused axes
    for i in range(n, rows * cols):
        axes[i // cols][i % cols].set_visible(False)

    plt.tight_layout()
    fig.suptitle("Alien cumulative income — per map (each line = one round, thick = median)",
                 fontsize=14, y=1.01)
    out = f"income_per_map{TAG}.png"
    plt.savefig(out, dpi=100, bbox_inches="tight")
    print(f"saved {out}")


def plot_summary(by_map):
    """One plot, median trajectory per map, ranked by 10-min income."""
    fig, ax = plt.subplots(figsize=(13, 8))
    map_summary = []
    for m, rounds in by_map.items():
        med_xs, med_ys = [], []
        for t_s in CHECKPOINTS_S:
            v = median_at_checkpoint(rounds, t_s)
            if v is not None:
                med_xs.append(t_s)
                med_ys.append(v)
        if len(med_xs) < 2:
            continue
        # Rank by 10-min (600s) or best available
        rank_val = next((v for t, v in zip(med_xs, med_ys) if t == 600), med_ys[-1])
        map_summary.append((m, med_xs, med_ys, rank_val, len(rounds)))

    map_summary.sort(key=lambda x: -x[3])
    cmap = plt.get_cmap("tab20")
    for i, (m, xs, ys, r10, n) in enumerate(map_summary):
        ax.plot(xs, ys, "-o", linewidth=2, markersize=5, color=cmap(i % 20),
                label=f"{m} (n={n}, @600s={int(r10):,})")

    ax.set_xlabel("round time (s)")
    ax.set_ylabel("cumulative income (median across rounds)")
    ax.set_title("Alien cumulative income — median trajectory per map")
    ax.legend(loc="upper left", fontsize=9)
    ax.grid(True, alpha=0.3)
    ax.yaxis.set_major_formatter(mticker.FuncFormatter(lambda x, p: f"{int(x/1000)}k"))
    for cp in CHECKPOINTS_S:
        ax.axvline(cp, color="gray", linestyle=":", alpha=0.15)

    plt.tight_layout()
    out = f"income_summary{TAG}.png"
    plt.savefig(out, dpi=100, bbox_inches="tight")
    print(f"saved {out}")


def main():
    map_filter = None
    since = None
    i = 1
    while i < len(sys.argv):
        arg = sys.argv[i]
        if arg == "--map" and i + 1 < len(sys.argv):
            map_filter = sys.argv[i + 1]; i += 2
        elif arg == "--since" and i + 1 < len(sys.argv):
            since = sys.argv[i + 1]
            if len(since) == 10:   # date only, add midnight
                since += "T00:00:00"
            i += 2
        else:
            i += 1
    global TAG
    TAG = ""
    if since: TAG += f"_since{since[:10]}"
    if map_filter: TAG += f"_{map_filter}"
    by_map = load(map_filter=map_filter, since=since)
    if not by_map:
        print("no data")
        sys.exit(1)
    print(f"loaded {sum(len(v) for v in by_map.values())} rounds across {len(by_map)} maps")
    for m in sorted(by_map, key=lambda k: -len(by_map[k])):
        print(f"  {m}: {len(by_map[m])} rounds")
    plot_per_map(by_map)
    plot_summary(by_map)
    plt.show()


if __name__ == "__main__":
    main()
