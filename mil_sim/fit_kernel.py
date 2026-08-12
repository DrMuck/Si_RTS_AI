#!/usr/bin/env python3
"""
Fit what a unit is WORTH, and check it beats the cash it costs.

THE GATE, RESTATED AS A NUMBER
------------------------------
`MIL_V2_ARCHITECTURE.md` step 1: a combat kernel earns its place only if it
predicts held-out fights better than "more combat cash wins". On the current
dataset that baseline is **83%** on decisive fights (85% AI-vs-AI). Composition
has to beat it or the planner should keep using cash and we should stop here.

THE MODEL, AND WHY IT IS THIS SMALL
-----------------------------------
One learned number per unit type — a multiplier on its cash price:

    effective(force) = SUM over units of  count * cost * w[unit]
    P(A wins)        = sigmoid( k * ( log effective(A) - log effective(B) ) )

`w` is exactly the quantity the production planner needs and does not have: how
much a Behemoth is worth relative to what it costs. `w = 1` everywhere reduces
the model to the cash baseline, so the fit is a strict test of whether
composition carries information beyond price — and the regulariser pulls toward
`w = 1`, meaning a unit only moves off its cash price if the data insists.

`k` is the slope of the win curve, and it is NOT the same quantity as the
exchange exponent `summarize.py` fits. Beta describes how LOSSES scale with the
force ratio; k describes how the PROBABILITY OF WINNING does. They come out at
0.5 and 1.1 respectively and both can be right — a force ratio that only cuts
your losses by a third can still decide who is left holding the ground.

WHY THE SPLIT IS BY REPLAY AND NOT BY FIGHT
-------------------------------------------
Fights from one match share an army, a commander and a map. Splitting by fight
puts the same Behemoths on both sides of the train/test line and reports a score
that cannot be reproduced on a new round. Splitting by replay is the difference
between a measurement and a flattering one.

WHAT THE FITTED WEIGHTS ARE NOT
-------------------------------
They are not a counter matrix. This model says a Behemoth is worth 1.6x its
price ON AVERAGE across everything it met; it cannot say a Behemoth is worth 4x
against air and 0.5x against Siege Tanks. That is the next model and it needs
the same data cut by enemy class. Reading these as counters would be the same
mistake `rtsai_units.json` warns about with kill counts.

They are also not clean of confounding. Infantry comes out at 2.4-3.0x price,
which is the largest claim in the table and the one to trust least: infantry
stands in numbers at defended bases, and defended bases win fights for reasons
that have nothing to do with a Rifleman. The weight cannot separate "is good"
from "is present when things go well". Before acting on it, check whether it
survives conditioning on distance to the owner's own structures.

    python mil_sim/fit_kernel.py [--ai-only] [--min-elo 1650] [--seed 0]
"""

import argparse
import collections
import csv
import io
import json
import math
import os
import sys

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from unit_stats import load_units, resolve                  # noqa: E402

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(HERE, "out")

MIN_CASH = 1000.0
DECISIVE_LOSS, DECISIVE_SURVIVE = 0.70, 0.30

# A type needs this many fights on some side before it gets its own weight.
# Below it the weight is unidentifiable and would be fitted to noise; those
# types stay pinned at their cash price.
MIN_APPEARANCES = 40


def num(row, key, default=0.0):
    v = row.get(key, "")
    if v in ("", None):
        return default
    try:
        return float(v)
    except ValueError:
        return default


def load(path_fights, path_units):
    fights = {}
    with io.open(path_fights, encoding="utf-8", newline="") as fh:
        for r in csv.DictReader(fh):
            fights[r["fight_id"]] = r
    comp = collections.defaultdict(collections.Counter)
    with io.open(path_units, encoding="utf-8", newline="") as fh:
        for r in csv.DictReader(fh):
            comp[(r["fight_id"], r["side"])][r["unit"]] += int(r["count"])
    return fights, comp


def decisive(r):
    ca, cb = num(r, "a_cash"), num(r, "b_cash")
    fa = num(r, "a_lost_cash") / max(1.0, ca)
    fb = num(r, "b_lost_cash") / max(1.0, cb)
    return ((fa >= DECISIVE_LOSS and fb <= DECISIVE_SURVIVE)
            or (fb >= DECISIVE_LOSS and fa <= DECISIVE_SURVIVE))


def build(fights, comp, units, ai_only, min_elo, since=""):
    """Design matrices in CASH, so a weight of 1 is the baseline exactly."""
    rows = []
    for fid, r in fights.items():
        # THE META MOVES. check_epochs measures value multipliers drifting
        # 1.97x between months against a 1.38x noise floor, so a fit pooled
        # over the whole archive describes an average of games that were not
        # the same game. Anything shipped to the mod is cut to the era it will
        # run in.
        if since and r.get("date", "") < since:
            continue
        ca, cb = num(r, "a_cash"), num(r, "b_cash")
        if min(ca, cb) < MIN_CASH or not decisive(r):
            continue
        la, lb = num(r, "a_lost_cash"), num(r, "b_lost_cash")
        if la == lb:
            continue
        if ai_only and (num(r, "a_piloted_frac") > 0
                        or num(r, "b_piloted_frac") > 0):
            continue
        if min_elo and max(num(r, "best_cmd_elo"),
                           num(r, "best_fps_elo")) < min_elo:
            continue
        rows.append((fid, r, 1.0 if la < lb else 0.0))

    seen = collections.Counter()
    for fid, _r, _y in rows:
        for side in "ab":
            for u in comp[(fid, side)]:
                seen[u] += 1
    keep = sorted(u for u, c in seen.items() if c >= MIN_APPEARANCES)
    idx = {u: i for i, u in enumerate(keep)}

    n, m = len(rows), len(keep)

    A = np.zeros((n, m))
    B = np.zeros((n, m))
    y = np.zeros(n)
    other = np.zeros((n, 2))          # cash of types too rare to weight
    replays = []
    for i, (fid, r, label) in enumerate(rows):
        y[i] = label
        replays.append(r["replay"])
        for j, side in enumerate("ab"):
            M = A if side == "a" else B
            tot = 0.0
            for u, cnt in comp[(fid, side)].items():
                rec = resolve(units, u, r[f"{side}_team"])
                cost = rec["cost"] if rec else 0.0
                if u in idx:
                    M[i, idx[u]] += cnt * cost
                else:
                    tot += cnt * cost
            other[i, j] = tot
    return A, B, y, other, keep, np.array(replays), rows


def sigmoid(x):
    return 1.0 / (1.0 + np.exp(-np.clip(x, -60, 60)))


def fit(A, B, y, other, lam=1.0, iters=4000, lr=0.15):
    """
    Maximum likelihood on theta, where w = exp(theta), regularised toward w = 1.

    Gradient descent rather than anything clever: the problem is 60-odd
    parameters over a few thousand rows and converges in seconds.
    """
    m = A.shape[1]
    theta = np.zeros(m)
    k = 1.0
    for it in range(iters):
        w = np.exp(theta)
        ea = A @ w + other[:, 0] + 1e-9
        eb = B @ w + other[:, 1] + 1e-9
        x = np.log(ea) - np.log(eb)
        p = sigmoid(k * x)
        err = p - y

        dk = float(err @ x) / len(y)
        # d/dtheta_u of x  =  (A[:,u]/ea - B[:,u]/eb) * w_u
        g = ((A / ea[:, None] - B / eb[:, None]) * w[None, :])
        dtheta = (k * (err @ g)) / len(y) + lam * theta / len(y)

        theta -= lr * dtheta
        k -= lr * dk
        k = float(np.clip(k, 0.05, 5.0))
    return theta, k


def accuracy(A, B, y, other, theta, k):
    w = np.exp(theta)
    x = np.log(A @ w + other[:, 0] + 1e-9) - np.log(B @ w + other[:, 1] + 1e-9)
    p = sigmoid(k * x)
    return float((((p >= 0.5).astype(float)) == y).mean()), p


def baseline(A, B, y, other):
    """w = 1 everywhere: more combat cash wins."""
    ca = A.sum(axis=1) + other[:, 0]
    cb = B.sum(axis=1) + other[:, 1]
    return float(((ca > cb).astype(float) == y).mean())


def brier(p, y):
    return float(((p - y) ** 2).mean())


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--fights", default=os.path.join(OUT, "engagements.csv"))
    ap.add_argument("--units", default=os.path.join(OUT, "fight_units.csv"))
    ap.add_argument("--ai-only", action="store_true")
    ap.add_argument("--min-elo", type=float, default=0.0)
    ap.add_argument("--since", default="",
                    help="YYYYMMDD; the meta moves, see check_epochs")
    ap.add_argument("--lam", type=float, default=1.0)
    ap.add_argument("--folds", type=int, default=5)
    ap.add_argument("--seed", type=int, default=0)
    ap.add_argument("--emit", default="")
    args = ap.parse_args()

    units = load_units()
    fights, comp = load(args.fights, args.units)
    A, B, y, other, keep, replays, _rows = build(
        fights, comp, units, args.ai_only, args.min_elo, args.since)
    print(f"{len(y):,} decisive fights, {len(keep)} unit types weighted "
          f"(>= {MIN_APPEARANCES} appearances), "
          f"{len(set(replays)):,} distinct replays")
    if len(y) < 200:
        sys.exit("not enough fights to fit anything honest")

    # ---- cross-validation, GROUPED BY REPLAY ----------------------------
    rng = np.random.default_rng(args.seed)
    uniq = np.array(sorted(set(replays)))
    rng.shuffle(uniq)
    fold_of = {r: i % args.folds for i, r in enumerate(uniq)}
    folds = np.array([fold_of[r] for r in replays])

    accs, bases, briers, ks = [], [], [], []
    for f in range(args.folds):
        te = folds == f
        tr = ~te
        theta, k = fit(A[tr], B[tr], y[tr], other[tr], lam=args.lam)
        acc, p = accuracy(A[te], B[te], y[te], other[te], theta, k)
        accs.append(acc)
        bases.append(baseline(A[te], B[te], y[te], other[te]))
        briers.append(brier(p, y[te]))
        ks.append(k)

    print(f"\n{args.folds}-fold, split by replay:")
    print(f"  baseline (more cash wins) {100 * np.mean(bases):.1f}%  "
          f"+/- {100 * np.std(bases):.1f}")
    print(f"  kernel   (fitted weights) {100 * np.mean(accs):.1f}%  "
          f"+/- {100 * np.std(accs):.1f}")
    print(f"  lift                      {100 * (np.mean(accs) - np.mean(bases)):+.1f} pts")
    print(f"  Brier score               {np.mean(briers):.4f}   "
          f"(0.25 = always guess 50%)")
    print(f"  k (exchange exponent)     {np.mean(ks):.2f} +/- {np.std(ks):.2f}")

    # ---- final fit on everything, for the table --------------------------
    theta, k = fit(A, B, y, other, lam=args.lam)
    w = np.exp(theta)
    order = np.argsort(-w)
    print(f"\nvalue multiplier on cash price (1.00 = worth exactly its cost):")
    print(f"  {'unit':<20}{'cost':>7}{'w':>8}{'eff cost':>11}{'fights':>8}")
    seen = collections.Counter()
    for fid, side in comp:
        for u in comp[(fid, side)]:
            seen[u] += 1
    for i in order:
        u = keep[i]
        rec = units.get(u)
        cost = rec["cost"] if rec else 0.0
        print(f"  {u:<20}{cost:>7.0f}{w[i]:>8.2f}"
              f"{(cost / w[i] if w[i] else 0):>11.0f}{seen[u]:>8,}")

    if args.emit:
        payload = {
            "_readme": [
                "Value multiplier per unit, fitted from decisive engagements.",
                "effective_value = cost * w. w=1 means the unit is worth its price.",
                "NOT a counter matrix: this is the average across all enemies.",
            ],
            "k": round(float(k), 4),
            "cv_accuracy": round(float(np.mean(accs)), 4),
            "cv_baseline": round(float(np.mean(bases)), 4),
            "fights": int(len(y)),
            "ai_only": bool(args.ai_only),
            "min_elo": args.min_elo,
            "since": args.since,
            "w": {keep[i]: round(float(w[i]), 4) for i in range(len(keep))},
        }
        with io.open(args.emit, "w", encoding="utf-8") as fh:
            json.dump(payload, fh, indent=1)
        print(f"\nwrote {args.emit}")


if __name__ == "__main__":
    main()
