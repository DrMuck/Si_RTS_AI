"""Per-arm curves: income, workers, and how many sites are earning RIGHT NOW.

Averages mislead here — patches deplete and shrimps relocate, so a mean over a
round hides the thing that matters. DrMuck: "a bit tricky to average that
because biotics get depleted and shrimps are relocated. Need a curve."

Three panels on one time axis, one line per round, coloured by arm:

  1. cumulative income   — benchmark checkpoints, the authoritative series
  2. workers             — per second from the cashflow CSV
  3. ACTIVELY earning Bio Caches — sites whose deposits grew since the previous
     30s sample, which is the working front. Ever-earned is cumulative and only
     ever rises; it cannot show a patch running dry. Total built is dashed
     behind it, so the gap between them IS the idle capital.

Panel 3 needs rounds recorded after per-BC attribution started working
(v0.24.1); earlier rounds are skipped there and noted.

Usage: python plot_arms_curves.py [since-ISO]
"""
import csv, glob, json, os, re, sys
from datetime import datetime, timedelta
import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt
from matplotlib.ticker import FuncFormatter

RTSA = os.path.join("E:" + os.sep, "Steam", "steamapps", "common",
                    "Silica Dedicated Server", "UserData", "RTSA")
OUT = os.path.dirname(os.path.abspath(__file__))
SINCE = sys.argv[1] if len(sys.argv) > 1 else "2026-08-06T11"

SURFACE, INK, INK_2, INK_MUTED, GRID = "#fcfcfb", "#0b0b0b", "#52514e", "#8a8983", "#e6e5e1"
# Categorical slots 1,2,3,5 — skipping yellow, which sits beside orange.
ARM_COLOUR = {"adaptive": "#2a78d6", "ratio2": "#eb6834",
              "ratio3": "#1baf7a", "ratio5": "#e87ba4"}
ARMS = ["adaptive", "ratio2", "ratio3", "ratio5"]


def stamp(n):
    m = re.search(r"(\d{8}_\d{6})", n)
    return datetime.strptime(m.group(1), "%Y%m%d_%H%M%S") if m else None


bench = [json.loads(l) for l in open(os.path.join(RTSA, "benchmarks.jsonl"), encoding="utf-8") if l.strip()]
bench = [b for b in bench if b["team"] == "Team_Alien" and b["ts"] >= SINCE
         and b.get("configId") in ARM_COLOUR and b["checkpoints"]]
bench.sort(key=lambda b: b["ts"])
if not bench:
    raise SystemExit("no arm-tagged rounds since " + SINCE)

cash_files = sorted((stamp(os.path.basename(p)), p)
                    for p in glob.glob(os.path.join(RTSA, "cashflow_*.csv")))
bm = [json.loads(l) for l in open(os.path.join(RTSA, "bc_metrics.jsonl"), encoding="utf-8") if l.strip()]
bm = [l for l in bm if l.get("team") == "Team_Alien" and l.get("bcs")]

fig, axes = plt.subplots(3, 1, figsize=(10, 11), facecolor=SURFACE, sharex=True,
                         gridspec_kw=dict(height_ratios=[1.6, 1.2, 1.6], hspace=0.14))


def style(ax, last=False):
    ax.set_facecolor(SURFACE)
    ax.grid(True, color=GRID, linewidth=0.8)
    ax.set_axisbelow(True)
    for s in ("top", "right"):
        ax.spines[s].set_visible(False)
    for s in ("left", "bottom"):
        ax.spines[s].set_color(GRID)
    ax.tick_params(colors=INK_2, labelsize=9, length=0, labelbottom=last)
    ax.set_xlim(0, 36)
    ax.set_xticks([0, 10, 20, 30])


mins = [1, 2, 3, 5, 10, 15, 20, 25, 30]
seen, no_meter = set(), 0

for b in bench:
    arm = b["configId"]
    col = ARM_COLOUR[arm]
    lab = arm if arm not in seen else None
    seen.add(arm)
    end = datetime.strptime(b["ts"], "%Y-%m-%dT%H:%M:%S")
    start = end - timedelta(seconds=b["elapsedS"])

    # 1. cumulative income
    xs, ys = [], []
    for m in mins:
        v = b["checkpoints"].get(str(m * 60))
        if v is not None:
            xs.append(m); ys.append(v / 1000)
    xs.append(b["elapsedS"] / 60); ys.append(b["cumulIncome"] / 1000)
    axes[0].plot(xs, ys, color=col, lw=1.8, alpha=0.85, label=lab)

    # 2. workers
    f = next((p for t, p in cash_files if t and abs((t - start).total_seconds()) < 180), None)
    if f:
        rows = list(csv.DictReader(open(f)))
        if len(rows) > 600:
            t0 = float(rows[0]["t_sec"])
            axes[1].plot([(float(r["t_sec"]) - t0) / 60 for r in rows],
                         [int(r["shrimps"]) for r in rows], color=col, lw=1.5, alpha=0.85)

    # 3. actively earning vs built
    # Bound by the round START as well as its end. A 45-minute lookback reaches
    # into the PREVIOUS round, whose samples carry overlapping roundT values —
    # sorting then interleaves two rounds and every second delta reads negative,
    # which is what produced the sawtooth and the jump at minute 26.
    mine = sorted([l for l in bm
                   if start <= datetime.strptime(l["ts"], "%Y-%m-%dT%H:%M:%S") <= end],
                  key=lambda l: l["roundT"])
    mine = [l for l in mine if any("deposited" in x for x in l["bcs"])]
    if len(mine) < 5:
        no_meter += 1
        continue
    prev, ts, active, built = {}, [], [], []
    for l in mine:
        now = {(round(x["x"]), round(x["z"])): x.get("deposited", 0) for x in l["bcs"]}
        if prev:
            ts.append(l["roundT"] / 60)
            active.append(sum(1 for k, v in now.items() if v > prev.get(k, 0)))
            built.append(len(now))
        prev = now
    axes[2].plot(ts, built, color=col, lw=1.1, alpha=0.45, ls=(0, (4, 3)))
    axes[2].plot(ts, active, color=col, lw=1.9, alpha=0.9)

axes[0].yaxis.set_major_formatter(FuncFormatter(lambda v, _: f"{v:,.0f}k" if v else "0"))
axes[0].set_ylabel("cumulative income", color=INK_2, fontsize=9.5)
axes[0].legend(frameon=False, fontsize=9, labelcolor=INK_2, loc="upper left", ncol=4)
axes[1].set_ylabel("workers", color=INK_2, fontsize=9.5)
axes[2].set_ylabel("Bio Caches", color=INK_2, fontsize=9.5)
axes[2].set_xlabel("minutes", color=INK_2, fontsize=9.5)
axes[2].annotate("dashed = built     solid = delivering in the last 30s",
                 (0.4, 0.95), xycoords="axes fraction", color=INK_2, fontsize=9,
                 va="top")
for i, ax in enumerate(axes):
    style(ax, last=(i == 2))

fig.suptitle("Producer-density arms — the curves, not the averages", color=INK,
             fontsize=15, x=0.01, ha="left", y=0.995)
sub = (f"NarakaCity, {len(bench)} arm-tagged rounds. Panel 3 shows sites DELIVERING "
       f"right now, not ever-earned — only that can fall when a patch runs dry.")
if no_meter:
    sub += f"\n{no_meter} round(s) predate working per-Bio-Cache attribution and are absent from panel 3."
fig.text(0.01, 0.973, sub, color=INK_2, fontsize=9.5, ha="left", va="top")
fig.subplots_adjust(top=0.93, left=0.09, right=0.98, bottom=0.06)
out = os.path.join(OUT, "arms_curves.png")
fig.savefig(out, dpi=140, facecolor=SURFACE)
print(f"rounds={len(bench)} without per-BC meter={no_meter}")
print(out)
