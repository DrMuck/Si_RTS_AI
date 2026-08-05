"""Income curves for the overnight soak rounds.

Reads UserData/RTSA/benchmarks.jsonl — one record per team per round, with
cumulative income checkpointed at 60/120/180/300/600/900/1200/1500/1800s — and
draws two views:

  1. Small multiples, one panel per round. Sixteen lines in one axes is
     spaghetti; sixteen panels sharing a scale lets the SHAPES be compared,
     which is the actual question (does this map ramp early or late).
  2. The 10-minute benchmark per round against DrMuck's 100k target, sorted,
     because that is the number being chased.

One series per panel, so colour carries no identity here and the categorical
palette is not in play — a single blue from the reference ramp, grey reference
marks, status colour only in the summary where pass/fail is the point (and
position already encodes it, so colour is secondary).
"""
import json, os, sys
import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt
from matplotlib.ticker import FuncFormatter

BENCH = r"E:\Steam\steamapps\common\Silica Dedicated Server\UserData\RTSA\benchmarks.jsonl"
OUT_DIR = os.path.dirname(os.path.abspath(__file__))
SINCE = sys.argv[1] if len(sys.argv) > 1 else "2026-08-04T20"

SURFACE = "#fcfcfb"
INK = "#0b0b0b"
INK_2 = "#52514e"
INK_MUTED = "#8a8983"
SERIES = "#2a78d6"
GOOD = "#0ca30c"
CRITICAL = "#d03b3b"
GRID = "#e6e5e1"

TARGET_T, TARGET_V = 600, 100_000

rows = [json.loads(l) for l in open(BENCH, encoding="utf-8") if l.strip()]
rounds = [r for r in rows
          if r["team"] == "Team_Alien" and r["ts"] >= SINCE and r["checkpoints"]]
rounds.sort(key=lambda r: r["ts"])
if not rounds:
    raise SystemExit("no alien rounds since " + SINCE)


# The benchmark window is the first quarter of hour; on a 30-minute axis the
# whole ramp collapses into the bottom-left corner and the shapes stop being
# comparable, which is the one thing small multiples are for.
ZOOM_MIN = 15


def series(r, limit_s=None):
    cps = sorted((int(k), v) for k, v in r["checkpoints"].items())
    cps = [(t, v) for t, v in cps if t <= r["elapsedS"]]
    if limit_s:
        cps = [(t, v) for t, v in cps if t <= limit_s]
    return [t / 60 for t, _ in cps], [v / 1000 for _, v in cps]


def k_fmt(v, _):
    return f"{v:,.0f}k" if v else "0"


# ---- 1. Small multiples --------------------------------------------------
n = len(rounds)
cols = 4
rws = (n + cols - 1) // cols
fig, axes = plt.subplots(rws, cols, figsize=(13, 2.55 * rws), sharex=True, sharey=True,
                         facecolor=SURFACE)
axes = axes.ravel()
ymax = max(max(series(r, ZOOM_MIN * 60)[1]) for r in rounds)

for ax, r in zip(axes, rounds):
    x, y = series(r, ZOOM_MIN * 60)
    ax.set_facecolor(SURFACE)
    ax.grid(True, color=GRID, linewidth=0.8)
    ax.set_axisbelow(True)

    # The benchmark, drawn as a target the curve either clears or does not.
    ax.axhline(TARGET_V / 1000, color=INK_MUTED, lw=1, ls=(0, (4, 3)))
    ax.axvline(TARGET_T / 60, color=INK_MUTED, lw=1, ls=(0, (4, 3)))

    ax.plot(x, y, color=SERIES, lw=2, marker="o", ms=4.5,
            markerfacecolor=SURFACE, markeredgewidth=1.6, clip_on=False)

    at10 = r["checkpoints"].get("600", 0) / 1000
    hit = at10 >= TARGET_V / 1000
    ax.plot([TARGET_T / 60], [at10], marker="o", ms=8, zorder=5,
            color=GOOD if hit else CRITICAL,
            markeredgecolor=SURFACE, markeredgewidth=2, clip_on=False)
    ax.annotate(f"{at10:,.0f}k @10m", (TARGET_T / 60, at10),
                textcoords="offset points", xytext=(8, -12),
                color=INK_2, fontsize=8.5)

    ax.set_title(f"{r['map']}  ·  {r['ts'][11:16]}", color=INK, fontsize=10,
                 loc="left", pad=6)
    for s in ("top", "right"):
        ax.spines[s].set_visible(False)
    for s in ("left", "bottom"):
        ax.spines[s].set_color(GRID)
    ax.tick_params(colors=INK_2, labelsize=8.5, length=0)
    ax.yaxis.set_major_formatter(FuncFormatter(k_fmt))
    ax.set_ylim(0, ymax * 1.10)
    ax.set_xlim(0, ZOOM_MIN)
    ax.set_xticks([0, 5, 10, 15])

for ax in axes[n:]:
    ax.set_visible(False)
for ax in axes[max(0, n - cols):n]:
    ax.set_xlabel("minutes", color=INK_2, fontsize=9)

fig.suptitle("Alien economy — cumulative income, first 15 minutes", color=INK,
             fontsize=15, x=0.008, ha="left", y=0.998)
fig.text(0.008, 0.963,
         f"{n} soak rounds, {rounds[0]['ts'][:16].replace('T', ' ')} to "
         f"{rounds[-1]['ts'][:16].replace('T', ' ')}. "
         f"Dashed cross marks the 100k-by-10-minutes target; the dot is where each "
         f"round actually was.
Rounds ran 30 minutes — the tail is cut so the "
         f"ramps stay comparable.",
         color=INK_2, fontsize=9.5, ha="left")
fig.tight_layout(rect=(0, 0, 1, 0.935))
p1 = os.path.join(OUT_DIR, "income_overnight_curves.png")
fig.savefig(p1, dpi=140, facecolor=SURFACE)

# ---- 2. The benchmark itself --------------------------------------------
fig2, ax = plt.subplots(figsize=(9.5, 0.42 * n + 2.0), facecolor=SURFACE)
ax.set_facecolor(SURFACE)
by10 = sorted(rounds, key=lambda r: r["checkpoints"].get("600", 0))
labels = [f"{r['map']}  {r['ts'][11:16]}" for r in by10]
vals = [r["checkpoints"].get("600", 0) / 1000 for r in by10]
ypos = range(len(vals))

ax.grid(True, axis="x", color=GRID, linewidth=0.8)
ax.set_axisbelow(True)
ax.hlines(list(ypos), 0, vals, color=GRID, lw=2)
for i, v in zip(ypos, vals):
    hit = v >= TARGET_V / 1000
    ax.plot([v], [i], marker="o", ms=9, color=GOOD if hit else CRITICAL,
            markeredgecolor=SURFACE, markeredgewidth=2, zorder=4)
    ax.annotate(f"{v:,.0f}k", (v, i), textcoords="offset points", xytext=(12, -3.5),
                color=INK_2, fontsize=9)

ax.axvline(TARGET_V / 1000, color=INK_MUTED, lw=1.2, ls=(0, (4, 3)))
ax.annotate("target 100k", (TARGET_V / 1000, len(vals) - 0.35),
            textcoords="offset points", xytext=(6, 0), color=INK_2, fontsize=9)

ax.set_yticks(list(ypos))
ax.set_yticklabels(labels, color=INK_2, fontsize=9)
ax.set_xlim(0, max(max(vals) * 1.18, TARGET_V / 1000 * 1.15))
ax.xaxis.set_major_formatter(FuncFormatter(k_fmt))
for s in ("top", "right", "left"):
    ax.spines[s].set_visible(False)
ax.spines["bottom"].set_color(GRID)
ax.tick_params(colors=INK_2, labelsize=9, length=0)
ax.set_xlabel("cumulative income at 10 minutes", color=INK_2, fontsize=9.5)
fig2.suptitle("Where each round stood at 10 minutes", color=INK, fontsize=14,
              x=0.008, ha="left")
fig2.tight_layout(rect=(0, 0, 1, 0.96))
p2 = os.path.join(OUT_DIR, "income_overnight_10min.png")
fig2.savefig(p2, dpi=140, facecolor=SURFACE)

hits = sum(1 for v in vals if v >= TARGET_V / 1000)
print(f"rounds={n} hit100k={hits} median10min={sorted(vals)[len(vals)//2]:.0f}k "
      f"best={max(vals):.0f}k worst={min(vals):.0f}k")
print(p1)
print(p2)
