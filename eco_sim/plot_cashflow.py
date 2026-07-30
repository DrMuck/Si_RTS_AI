"""
Plot cash-flow prediction vs reality for a round.

Usage:
  python plot_cashflow.py                    # plot most recent CSV
  python plot_cashflow.py cashflow_XXXX.csv  # plot specific CSV

CSVs come from Si_RTS_AI mod's TechPlanner — one per round in:
  E:/Steam/steamapps/common/Silica Dedicated Server/UserData/RTSA/cashflow_*.csv

Two plots side-by-side:
  1. Actual cash trajectory vs "projected at deadline" (each row's prediction)
  2. Per-shrimp measured income + team income rate over time

Vertical markers show when Cortex was placed and each tier landed.
"""

import glob
import os
import sys
from pathlib import Path

try:
    import matplotlib.pyplot as plt
    import matplotlib.ticker as mticker
except ImportError:
    print("matplotlib required. Install: pip install matplotlib")
    sys.exit(1)

CSV_DIR = Path("E:/Steam/steamapps/common/Silica Dedicated Server/UserData/RTSA")


def latest_csv():
    files = sorted(glob.glob(str(CSV_DIR / "cashflow_*.csv")),
                   key=os.path.getmtime, reverse=True)
    if not files:
        print(f"No cashflow_*.csv in {CSV_DIR}")
        sys.exit(1)
    return files[0]


def load(path):
    rows = []
    with open(path) as f:
        header = next(f).strip().split(",")
        for line in f:
            parts = line.strip().split(",")
            if len(parts) != len(header):
                continue
            r = {}
            for i, k in enumerate(header):
                v = parts[i]
                if k in ("t_sec", "team_income_per_sec", "per_shrimp_income", "dt_to_15"):
                    r[k] = float(v) if v else None
                else:
                    r[k] = int(v) if v else 0
            rows.append(r)
    return rows


def plot(rows, path):
    t = [r["t_sec"] for r in rows]
    cash = [r["cash"] for r in rows]
    reserved = [r["reserved"] for r in rows]
    projected = [r["projected_at_deadline"] for r in rows]
    dt_to_15 = [r["dt_to_15"] for r in rows]
    # Projected-at-deadline point: plot at time = t + dt_to_15
    proj_deadline_t = [t[i] + (dt_to_15[i] or 0) for i in range(len(rows))]
    per_shrimp = [r["per_shrimp_income"] for r in rows]
    team_income = [r["team_income_per_sec"] for r in rows]
    shrimps = [r["shrimps"] for r in rows]
    committed = [r["committed_cysts"] for r in rows]

    # Find cortex placement + tier changes
    cortex_appears = None
    tier_change_times = {}
    prev_cortex = 0
    prev_tier = 0
    for r in rows:
        if prev_cortex == 0 and r["cortex_count"] > 0:
            cortex_appears = r["t_sec"]
        if r["tier"] > prev_tier:
            tier_change_times[r["tier"]] = r["t_sec"]
        prev_cortex = r["cortex_count"]
        prev_tier = r["tier"]

    fig, (ax1, ax2) = plt.subplots(2, 1, figsize=(14, 10), sharex=True)

    # ---- Cash trajectory ----
    ax1.plot(t, cash, "b-", linewidth=2, label="actual cash")
    ax1.plot(proj_deadline_t, projected, "g.", alpha=0.5, markersize=4,
             label="projected@15-shrimps deadline")
    ax1.plot(t, reserved, "r--", alpha=0.7, label="reserved (for Cortex)")
    ax1.axhline(2000, color="orange", linestyle=":", alpha=0.6, label="cortex target (2k)")
    if cortex_appears is not None:
        ax1.axvline(cortex_appears, color="purple", linestyle="-", alpha=0.5,
                    label=f"Cortex placed @ t={cortex_appears:.0f}s")
    for tier, tt in tier_change_times.items():
        ax1.axvline(tt, color="gray", linestyle=":", alpha=0.3)
        ax1.text(tt, ax1.get_ylim()[1] * 0.95, f"T{tier}", fontsize=8, ha="center")
    ax1.set_ylabel("cash")
    ax1.set_title(f"{os.path.basename(path)}: cash prediction vs actual")
    ax1.legend(loc="upper left", fontsize=9)
    ax1.grid(True, alpha=0.3)
    ax1.yaxis.set_major_formatter(mticker.FuncFormatter(lambda x, p: f"{int(x):,}"))

    # ---- Per-shrimp income and shrimp count ----
    ax2.plot(t, per_shrimp, "b-", linewidth=2, label="per-shrimp cash/s (measured)")
    ax2b = ax2.twinx()
    ax2b.plot(t, shrimps, "g-", linewidth=1.5, label="live shrimps")
    ax2b.plot(t, committed, "orange", linewidth=1, label="committed cysts")
    ax2b.axhline(15, color="green", linestyle=":", alpha=0.4)
    ax2b.axhline(3, color="orange", linestyle=":", alpha=0.4)
    ax2.set_xlabel("round time (s)")
    ax2.set_ylabel("cash / shrimp / s", color="b")
    ax2b.set_ylabel("count", color="g")
    ax2.legend(loc="upper left", fontsize=9)
    ax2b.legend(loc="upper right", fontsize=9)
    ax2.grid(True, alpha=0.3)

    plt.tight_layout()
    out_png = path.replace(".csv", ".png")
    plt.savefig(out_png, dpi=100, bbox_inches="tight")
    print(f"saved {out_png}")
    plt.show()


def main():
    if len(sys.argv) > 1:
        path = sys.argv[1]
        if not os.path.exists(path):
            path = str(CSV_DIR / sys.argv[1])
    else:
        path = latest_csv()
    print(f"loading {path}")
    rows = load(path)
    print(f"{len(rows)} rows, t = {rows[0]['t_sec']:.0f}s .. {rows[-1]['t_sec']:.0f}s")
    plot(rows, path)


if __name__ == "__main__":
    main()
