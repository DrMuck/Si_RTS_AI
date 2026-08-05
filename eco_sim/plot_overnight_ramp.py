"""Income and workers, side by side, for the overnight soak rounds.

The question this answers: the first five minutes are nearly flat on some maps —
is that a weak OPENING (few workers) or weak ground (workers with nothing good
to harvest)? Income alone cannot separate those; the worker count can.

Deliberately NOT a dual-axis chart. Two measures on one pair of axes forces a
made-up scale relationship between them and the eye reads crossings that mean
nothing. Each round gets two stacked panels sharing a time axis instead —
income above, workers below — so the two ramps can be compared without either
being distorted.

Sources: cumulative income from benchmarks.jsonl (checkpointed by
EcoRateSampler at round end); worker count per second from the round's
cashflow CSV. Both are keyed back to the round by start time.
"""
import csv, glob, json, os, re, sys
from datetime import datetime, timedelta
import matplotlib
matplotlib.use("Agg")
import matplotlib.pyplot as plt
from matplotlib.gridspec import GridSpec, GridSpecFromSubplotSpec
from matplotlib.ticker import FuncFormatter

RTSA = r"E:\Steam\steamapps\common\Silica Dedicated Server\UserData\RTSA"
OUT_DIR = os.path.dirname(os.path.abspath(__file__))
SINCE = sys.argv[1] if len(sys.argv) > 1 else "2026-08-04T20"

SURFACE, INK, INK_2, INK_MUTED = "#fcfcfb", "#0b0b0b", "#52514e", "#8a8983"
INCOME, WORKERS = "#2a78d6", "#eb6834"      # categorical slots 1 and 2
GOOD, CRITICAL, GRID = "#0ca30c", "#d03b3b", "#e6e5e1"
TARGET_T, TARGET_V = 600, 100_000
# Full round. The benchmark checkpoints stop at 1800s, so the tail of the income
# curve is drawn from the round's final cumulative total at elapsedS — which is
# what makes the post-10-minute tilt visible at all.
ZOOM_MIN = 35


def stamp(name):
    m = re.search(r"(\d{8}_\d{6})", name)
    return datetime.strptime(m.group(1), "%Y%m%d_%H%M%S") if m else None


rounds_on_disk = []
for p in glob.glob(os.path.join(RTSA, "round-*.log")):
    base = os.path.basename(p)
    mp = base.rsplit("-", 1)[-1][:-4]
    if mp in ("Loading", "Intro"):
        continue
    rounds_on_disk.append((stamp(base), mp))
cash_files = sorted((stamp(os.path.basename(p)), p)
                    for p in glob.glob(os.path.join(RTSA, "cashflow_*.csv")))

bench = [json.loads(l) for l in open(os.path.join(RTSA, "benchmarks.jsonl"), encoding="utf-8") if l.strip()]
bench = [b for b in bench if b["team"] == "Team_Alien" and b["ts"] >= SINCE and b["checkpoints"]]
bench.sort(key=lambda b: b["ts"])

matched = []
for b in bench:
    end = datetime.strptime(b["ts"], "%Y-%m-%dT%H:%M:%S")
    start = min((r for r in rounds_on_disk
                 if r[0] and r[1] == b["map"]
                 and timedelta(minutes=25) < end - r[0] < timedelta(minutes=45)),
                key=lambda r: end - r[0], default=None)
    if not start:
        continue
    cf = next((p for t, p in cash_files
               if t and timedelta(0) <= t - start[0] < timedelta(minutes=3)), None)
    if not cf:
        continue
    rows = list(csv.DictReader(open(cf)))
    if not rows:
        continue
    t0 = float(rows[0]["t_sec"])
    wt = [(float(r["t_sec"]) - t0) / 60 for r in rows]
    wv = [int(r["shrimps"]) for r in rows]
    matched.append(dict(map=b["map"], ts=b["ts"], cps=b["checkpoints"], wt=wt, wv=wv,
                        endS=b["elapsedS"], endV=b["cumulIncome"]))

if not matched:
    raise SystemExit("no rounds matched a cashflow CSV")

n = len(matched)
cols, pair_rows = 4, (n + 3) // 4
fig = plt.figure(figsize=(13.5, 3.6 * pair_rows), facecolor=SURFACE)
# Nested: generous space BETWEEN rounds, tight space between a round's own two
# panels, so the pairing is visible and nothing lands on the next title.
outer = GridSpec(pair_rows, cols, figure=fig, hspace=0.55, wspace=0.28,
                 top=0.90, bottom=0.06, left=0.06, right=0.985)

ymax_i = max(max(list(r["cps"].values()) + [r["endV"]]) for r in matched) / 1000
ymax_w = max(max(w for t, w in zip(r["wt"], r["wv"]) if t <= ZOOM_MIN) for r in matched)


def k_fmt(v, _):
    return f"{v:,.0f}k" if v else "0"


def style(ax, last_row):
    ax.set_facecolor(SURFACE)
    ax.grid(True, color=GRID, linewidth=0.8)
    ax.set_axisbelow(True)
    for s in ("top", "right"):
        ax.spines[s].set_visible(False)
    for s in ("left", "bottom"):
        ax.spines[s].set_color(GRID)
    ax.set_xlim(0, ZOOM_MIN)
    ax.set_xticks([0, 10, 20, 30])
    # The two panels of a round SHARE their x axis, so set_xticklabels([]) on
    # the upper one blanks the lower one as well — the locator is shared. Tick
    # visibility is per-axes; labels are not.
    ax.tick_params(colors=INK_2, labelsize=8.5, length=0, labelbottom=last_row)


for i, r in enumerate(matched):
    row, col = i // cols, i % cols
    inner = GridSpecFromSubplotSpec(2, 1, subplot_spec=outer[row, col],
                                    height_ratios=[2.0, 1.1], hspace=0.10)
    ax_i = fig.add_subplot(inner[0])
    ax_w = fig.add_subplot(inner[1], sharex=ax_i)

    cps = sorted((int(k), v) for k, v in r["cps"].items() if int(k) <= ZOOM_MIN * 60)
    if r["endS"] > (cps[-1][0] if cps else 0):
        cps.append((r["endS"], r["endV"]))     # the round's own final total
    ax_i.plot([t / 60 for t, _ in cps], [v / 1000 for _, v in cps], color=INCOME,
              lw=2, marker="o", ms=3.5, markerfacecolor=SURFACE, markeredgewidth=1.4)
    ax_i.annotate(f"{cps[-1][1] / 1000:,.0f}k", (cps[-1][0] / 60, cps[-1][1] / 1000),
                  textcoords="offset points", xytext=(-4, 6), ha="right",
                  color=INK_2, fontsize=8.5)
    ax_i.axhline(TARGET_V / 1000, color=INK_MUTED, lw=1, ls=(0, (4, 3)))
    ax_i.axvline(TARGET_T / 60, color=INK_MUTED, lw=1, ls=(0, (4, 3)))
    at10 = r["cps"].get("600", 0) / 1000
    ax_i.plot([10], [at10], marker="o", ms=7.5, zorder=5,
              color=GOOD if at10 >= 100 else CRITICAL,
              markeredgecolor=SURFACE, markeredgewidth=2)
    ax_i.annotate(f"{at10:,.0f}k", (10, at10), textcoords="offset points",
                  xytext=(7, 5), color=INK_2, fontsize=8.5)
    ax_i.set_ylim(0, ymax_i * 1.12)
    ax_i.yaxis.set_major_formatter(FuncFormatter(k_fmt))
    ax_i.set_title(f"{r['map']}  ·  {r['ts'][11:16]}", color=INK, fontsize=10,
                   loc="left", pad=5)
    style(ax_i, False)

    ax_w.plot(r["wt"], r["wv"], color=WORKERS, lw=1.8)
    ax_w.axvline(TARGET_T / 60, color=INK_MUTED, lw=1, ls=(0, (4, 3)))
    at5 = next((w for t, w in zip(r["wt"], r["wv"]) if t >= 5), 0)
    ax_w.plot([5], [at5], marker="o", ms=6, color=WORKERS,
              markeredgecolor=SURFACE, markeredgewidth=1.8, zorder=5)
    ax_w.annotate(f"{at5} @5m", (5, at5), textcoords="offset points",
                  xytext=(7, 3), color=INK_2, fontsize=8.5)
    ax_w.set_ylim(0, ymax_w * 1.12)
    style(ax_w, True)
    if col == 0:
        ax_i.set_ylabel("income", color=INCOME, fontsize=9)
        ax_w.set_ylabel("workers", color=WORKERS, fontsize=9)
    ax_w.set_xlabel("minutes", color=INK_2, fontsize=8.5, labelpad=1)

fig.suptitle("Is the flat opening a weak start, or weak ground?", color=INK,
             fontsize=15, x=0.008, ha="left", y=0.997)
fig.text(0.008, 0.978,
         "Cumulative income (blue) over worker count (orange), same time axis, "
         "whole round; the dashed line and red dot mark the 100k-by-10-minutes "
         "target.\nSeparate panels rather than two y-axes — a shared "
         "scale between them would be invented, and the crossings would mean nothing.",
         color=INK_2, fontsize=9.5, ha="left", va="top")
out = os.path.join(OUT_DIR, "income_vs_workers.png")
fig.savefig(out, dpi=140, facecolor=SURFACE)

print(f"{'map':<18}{'@5m workers':>12}{'@10m workers':>13}{'@5m income':>12}{'@10m income':>12}")
for r in sorted(matched, key=lambda r: -r["cps"].get("600", 0)):
    w5 = next((w for t, w in zip(r["wt"], r["wv"]) if t >= 5), 0)
    w10 = next((w for t, w in zip(r["wt"], r["wv"]) if t >= 10), 0)
    print(f"{r['map']:<18}{w5:>12}{w10:>13}{r['cps'].get('300',0)/1000:>11.0f}k"
          f"{r['cps'].get('600',0)/1000:>11.0f}k")
print(out)
