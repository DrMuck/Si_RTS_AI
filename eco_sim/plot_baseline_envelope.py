"""Ten identical rounds on one map: what is signal and what is noise.

These rounds all ran the same unlabelled config on NarakaCity from the same
spawn, which makes them worthless as an experiment and valuable as a BASELINE —
they measure how much two runs of the same bot differ, which is the floor any
future A/B result has to clear.

Three panels:
  1. every round's income curve, with the median through them
  2. spread between rounds over time — where a round's fate gets decided
  3. worker count, same treatment

Usage: python plot_baseline_envelope.py [since-ISO]
"""
import csv, glob, json, os, re, statistics, sys
from datetime import datetime
import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt
from matplotlib.ticker import FuncFormatter

RTSA = os.path.join("E:" + os.sep, "Steam", "steamapps", "common",
                    "Silica Dedicated Server", "UserData", "RTSA")
OUT = os.path.dirname(os.path.abspath(__file__))
SINCE = sys.argv[1] if len(sys.argv) > 1 else "2026-08-05T19"

SURFACE, INK, INK_2, INK_MUTED = "#fcfcfb", "#0b0b0b", "#52514e", "#8a8983"
SERIES, SPREAD, GRID = "#2a78d6", "#eb6834", "#e6e5e1"
TARGET_T, TARGET_V = 10, 100_000

# ---- income checkpoints per round ----------------------------------------
bench = [json.loads(l) for l in open(os.path.join(RTSA, "benchmarks.jsonl"), encoding="utf-8") if l.strip()]
MAP = os.environ.get("BASELINE_MAP", "NarakaCity")
bench = [b for b in bench if b["team"] == "Team_Alien" and b["ts"] >= SINCE
         and b["checkpoints"] and b["map"] == MAP]
bench.sort(key=lambda b: b["ts"])

# ---- worker curves from the per-second cashflow ---------------------------
def stamp(name):
    m = re.search(r"(\d{8}_\d{6})", name)
    return datetime.strptime(m.group(1), "%Y%m%d_%H%M%S") if m else None


# Match cashflow files to the benchmark rounds by start time. A bare time
# filter picked up 28 files against 10 rounds — aborted loads, and the
# NorthPolarCap detour from the map-rotation bug — which would have put other
# maps into a NarakaCity baseline.
from datetime import timedelta
cash_files = sorted((stamp(os.path.basename(f)), f)
                    for f in glob.glob(os.path.join(RTSA, "cashflow_*.csv")))
workers = []
for b_ in bench:
    end = datetime.strptime(b_["ts"], "%Y-%m-%dT%H:%M:%S")
    start = end - timedelta(seconds=b_["elapsedS"])
    f = next((f for t, f in cash_files
              if t and abs((t - start).total_seconds()) < 180), None)
    if not f:
        continue
    rows = list(csv.DictReader(open(f)))
    if len(rows) < 600:                      # a complete round, not a stub
        continue
    t0 = float(rows[0]["t_sec"])
    series = [((float(r["t_sec"]) - t0) / 60, int(r["shrimps"])) for r in rows]
    workers.append([q for q in series if q[0] <= 35])

if not bench:
    raise SystemExit("no rounds since " + SINCE)


def k_fmt(v, _):
    return f"{v:,.0f}k" if v else "0"


def style(ax, last=False):
    ax.set_facecolor(SURFACE)
    ax.grid(True, color=GRID, linewidth=0.8)
    ax.set_axisbelow(True)
    for s in ("top", "right"):
        ax.spines[s].set_visible(False)
    for s in ("left", "bottom"):
        ax.spines[s].set_color(GRID)
    ax.tick_params(colors=INK_2, labelsize=9, length=0)
    ax.set_xlim(0, 35)
    ax.set_xticks([0, 10, 20, 30])
    if not last:
        ax.tick_params(labelbottom=False)


fig, axes = plt.subplots(3, 1, figsize=(9.5, 10), facecolor=SURFACE,
                         gridspec_kw=dict(height_ratios=[2.2, 1.3, 1.8], hspace=0.16))

# --- 1. income envelope ---------------------------------------------------
ax = axes[0]
mins = [1, 2, 3, 5, 10, 15, 20, 25, 30]
curves = []
for b in bench:
    xs, ys = [], []
    for m in mins:
        v = b["checkpoints"].get(str(m * 60))
        if v is not None:
            xs.append(m); ys.append(v / 1000)
    if b["elapsedS"] > 30 * 60:
        xs.append(b["elapsedS"] / 60); ys.append(b["cumulIncome"] / 1000)
    curves.append((xs, ys))
    ax.plot(xs, ys, color=SERIES, lw=1.2, alpha=0.35)

median = []
for i, m in enumerate(mins):
    vals = [b["checkpoints"].get(str(m * 60), 0) / 1000 for b in bench]
    median.append(statistics.median(vals))
ax.plot(mins, median, color=SERIES, lw=2.6, marker="o", ms=5,
        markerfacecolor=SURFACE, markeredgewidth=1.8, label=f"median of {len(bench)} rounds")
ax.plot([TARGET_T], [TARGET_V / 1000], marker="o", ms=9, color="#d03b3b",
        markeredgecolor=SURFACE, markeredgewidth=2, zorder=5)
ax.annotate("target 100k @10min", (TARGET_T, TARGET_V / 1000),
            textcoords="offset points", xytext=(10, -4), color=INK_2, fontsize=9)
ax.yaxis.set_major_formatter(FuncFormatter(k_fmt))
ax.set_ylabel("cumulative income", color=INK_2, fontsize=9.5)
ax.legend(frameon=False, loc="upper left", fontsize=9, labelcolor=INK_2)
style(ax)

# --- 2. spread over time --------------------------------------------------
ax = axes[1]
cvs = []
for m in mins:
    vals = [b["checkpoints"].get(str(m * 60), 0) for b in bench]
    vals = [v for v in vals if v > 0]
    cv = (statistics.pstdev(vals) / statistics.mean(vals) * 100) if len(vals) > 2 else 0
    cvs.append(cv)
ax.plot(mins, cvs, color=SPREAD, lw=2.4, marker="o", ms=5,
        markerfacecolor=SURFACE, markeredgewidth=1.8)
for m, c in zip(mins, cvs):
    if m in (5, 10, 20, 30):
        ax.annotate(f"{c:.0f}%", (m, c), textcoords="offset points",
                    xytext=(0, 8), ha="center", color=INK_2, fontsize=9)
ax.set_ylabel("spread between rounds", color=INK_2, fontsize=9.5)
ax.yaxis.set_major_formatter(FuncFormatter(lambda v, _: f"{v:.0f}%"))
ax.set_ylim(0, max(cvs) * 1.35 if cvs else 1)
style(ax)

# --- 3. workers -----------------------------------------------------------
ax = axes[2]
for w in workers:
    ax.plot([p[0] for p in w], [p[1] for p in w], color=SPREAD, lw=1.1, alpha=0.35)
if workers:
    grid = [m * 0.5 for m in range(0, 71)]
    med = []
    for g in grid:
        vals = []
        for w in workers:
            v = next((p[1] for p in w if p[0] >= g), None)
            if v is not None:
                vals.append(v)
        med.append(statistics.median(vals) if vals else None)
    xs = [g for g, v in zip(grid, med) if v is not None]
    ys = [v for v in med if v is not None]
    ax.plot(xs, ys, color=SPREAD, lw=2.6, label=f"median of {len(workers)} rounds")
    ax.legend(frameon=False, loc="upper left", fontsize=9, labelcolor=INK_2)
ax.axhline(100, color=INK_MUTED, lw=1, ls=(0, (4, 3)))
ax.annotate("100 workers reference", (0.4, 100), textcoords="offset points",
            xytext=(0, 6), color=INK_2, fontsize=9)
ax.set_ylabel("workers", color=INK_2, fontsize=9.5)
ax.set_xlabel("minutes", color=INK_2, fontsize=9.5)
style(ax, last=True)

fig.suptitle("Ten identical rounds — the baseline any A/B result has to beat",
             color=INK, fontsize=15, x=0.01, ha="left", y=0.995)
fig.text(0.01, 0.972,
         f"{MAP}, same spawn, same unlabelled config, {len(bench)} rounds. Not an experiment — a "
         f"measurement of how much two runs of the same bot differ.",
         color=INK_2, fontsize=9.5, ha="left", va="top")
fig.subplots_adjust(top=0.93, left=0.10, right=0.98, bottom=0.06)
out = os.path.join(OUT, "baseline_envelope.png")
fig.savefig(out, dpi=140, facecolor=SURFACE)

print("spread by minute:", {m: f"{c:.0f}%" for m, c in zip(mins, cvs)})
print(out)
