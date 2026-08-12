#!/usr/bin/env python3
"""
Does a unit's value depend on WHAT IT IS FIGHTING? Fit it and find out.

THE QUESTION, AND WHY IT IS THE LAST ONE BEFORE ANY CODE SHIPS
--------------------------------------------------------------
`fit_kernel.py` fits one number per unit — what it is worth on average across
everything it met — and beats the cash baseline by +1.5 points on the AI-vs-AI
subset. That is a pass, but a thin one, and the model it fits structurally
cannot say the thing a production planner most wants to hear: *a Behemoth is
worth four times its price against air and half of it against Siege Tanks.*

So this is the fork. If letting the weight depend on the enemy mix lifts
materially, composition matters and the planner should build to counter. If it
does not, the honest reading is that in Silica **concentration beats
composition**, and the military layer should spend its attention on where and
when to fight rather than on what to build. Either answer is worth having, and
the second one would save a great deal of work.

THE MODEL
---------
    w[u | mix]  =  exp( theta[u]  +  SUM over classes c of  mix[c] * delta[u][c] )

    effective(A) = SUM over u of  count * cost * w[u | mix of B]
    P(A wins)    = sigmoid( k * ( log effective(A) - log effective(B) ) )

`theta` is the average weight `fit_kernel` already fits. `delta` is the counter
term — a DEVIATION from that average — and it is regularised an order of
magnitude harder, so a matchup only moves off the unit's average value if the
data insists. Setting every delta to zero recovers `fit_kernel` exactly, which
makes the comparison a strict nesting rather than two models waved at each other.

CLASSES ARE THE GAME'S OWN FACTORIES
------------------------------------
Barracks, Light, Heavy, Ultra Heavy, Air for the humans; Lesser, Greater and the
top-tier Cysts for the aliens. This is the taxonomy `rtsai_units.json` already
uses, and it beats anything hand-drawn because the game groups units by what
builds them and that correlates with role, cost and tier at once.

Known weakness: alien fliers (Dragonfly, Wasp) come out of the Lesser Cyst and
so land in the same class as a Crab. Anti-air value against them is therefore
smeared into the Lesser class and will read weaker than it is.

WHAT WOULD MAKE THIS RESULT A LIE
---------------------------------
The same confound `fit_kernel` carries, one level worse: with 8 classes per unit
there are ~400 free parameters over a few thousand fights. Held-out accuracy
split BY REPLAY is the only thing standing between this and a very well fitted
description of noise, which is why the number reported is cross-validated and
the in-sample fit is printed next to it. If train and test diverge, believe test.

    python mil_sim/fit_counters.py [--ai-only] [--min-elo 1650]
"""

import argparse
import collections
import csv
import io
import json
import os
import sys

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from unit_stats import load_units, resolve                  # noqa: E402
from fit_kernel import (load, decisive, num, sigmoid, MIN_CASH,   # noqa: E402
                        MIN_APPEARANCES)

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(HERE, "out")

# Producer building -> class. The game's own grouping; see the module docstring.
CLASS_OF_PRODUCER = {
    "Barracks": "Infantry",
    "Light Factory": "Light",
    "Heavy Factory": "Heavy",
    "Ultra Heavy Factory": "UltraHeavy",
    "Air Factory": "Air",
    "Lesser Spawning Cyst": "Lesser",
    "Greater Spawning Cyst": "Greater",
    "Grand Spawning Cyst": "Top",
    "Colossal Spawning Cyst": "Top",
}
CLASSES = ["Infantry", "Light", "Heavy", "UltraHeavy", "Air",
           "Lesser", "Greater", "Top", "Other"]
CIDX = {c: i for i, c in enumerate(CLASSES)}


def class_of(units, type_name, team_name):
    u = resolve(units, type_name, team_name)
    if u is None:
        return "Other"
    return CLASS_OF_PRODUCER.get(u.get("built_at", ""), "Other")


def build(fights, comp, units, ai_only, min_elo):
    rows = []
    for fid, r in fights.items():
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

    n, m, C = len(rows), len(keep), len(CLASSES)
    A = np.zeros((n, m))
    B = np.zeros((n, m))
    MA = np.zeros((n, C))            # class mix of side A, by cash share
    MB = np.zeros((n, C))
    other = np.zeros((n, 2))
    y = np.zeros(n)
    replays = []
    for i, (fid, r, label) in enumerate(rows):
        y[i] = label
        replays.append(r["replay"])
        for side in "ab":
            M = A if side == "a" else B
            X = MA if side == "a" else MB
            team = r[f"{side}_team"]
            rare = 0.0
            for u, cnt in comp[(fid, side)].items():
                rec = resolve(units, u, team)
                cost = (rec["cost"] if rec else 0.0) * cnt
                X[i, CIDX[class_of(units, u, team)]] += cost
                if u in idx:
                    M[i, idx[u]] += cost
                else:
                    rare += cost
            other[i, 0 if side == "a" else 1] = rare
        for X in (MA, MB):
            s = X[i].sum()
            if s > 0:
                X[i] /= s
    return A, B, MA, MB, y, other, keep, np.array(replays)


def forward(A, B, MA, MB, other, theta, delta, k):
    """Returns p, and the pieces the gradient needs."""
    # Clipped: a unit worth 500x or 1/500th of its price is not a finding, it is
    # a diverged optimiser, and without this the first overflow turns every
    # downstream number into a silent NaN.
    WA = np.exp(np.clip(theta[None, :] + MB @ delta.T, -6.0, 6.0))
    WB = np.exp(np.clip(theta[None, :] + MA @ delta.T, -6.0, 6.0))
    ea = (A * WA).sum(axis=1) + other[:, 0] + 1e-9
    eb = (B * WB).sum(axis=1) + other[:, 1] + 1e-9
    x = np.log(ea) - np.log(eb)
    return sigmoid(k * x), x, WA, WB, ea, eb


def fit(A, B, MA, MB, y, other, lam_theta=1.0, lam_delta=30.0,
        iters=4000, lr=0.15, counters=True):
    """
    `counters=False` FREEZES the counter terms at zero rather than regularising
    them to death. Those are not the same thing: a huge lambda plus a fixed step
    size makes the update oscillate and diverge, which is how the nested
    comparison first reported 58% and looked like a finding about composition.
    """
    m, C = A.shape[1], MA.shape[1]
    theta = np.zeros(m)
    delta = np.zeros((m, C))
    k = 1.0
    n = len(y)
    for _ in range(iters):
        p, x, WA, WB, ea, eb = forward(A, B, MA, MB, other, theta, delta, k)
        err = p - y

        GA = (A * WA) / ea[:, None]                 # d log ea / d theta_u
        GB = (B * WB) / eb[:, None]
        d = GA - GB

        dtheta = (k * (err @ d)) / n + lam_theta * theta / n
        ddelta = (k * ((err[:, None] * GA).T @ MB
                       - (err[:, None] * GB).T @ MA)) / n \
            + lam_delta * delta / n
        dk = float(err @ x) / n

        theta -= lr * dtheta
        if counters:
            delta -= lr * ddelta
        k = float(np.clip(k - lr * dk, 0.05, 5.0))
    return theta, delta, k


def score(A, B, MA, MB, y, other, theta, delta, k):
    p, *_ = forward(A, B, MA, MB, other, theta, delta, k)
    acc = float((((p >= 0.5).astype(float)) == y).mean())
    return acc, float(((p - y) ** 2).mean())


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--fights", default=os.path.join(OUT, "engagements.csv"))
    ap.add_argument("--units", default=os.path.join(OUT, "fight_units.csv"))
    ap.add_argument("--ai-only", action="store_true")
    ap.add_argument("--min-elo", type=float, default=0.0)
    ap.add_argument("--lam-theta", type=float, default=1.0)
    ap.add_argument("--lam-delta", type=float, default=30.0)
    ap.add_argument("--folds", type=int, default=5)
    ap.add_argument("--seed", type=int, default=0)
    ap.add_argument("--emit", default="")
    args = ap.parse_args()

    units = load_units()
    fights, comp = load(args.fights, args.units)
    A, B, MA, MB, y, other, keep, replays = build(
        fights, comp, units, args.ai_only, args.min_elo)
    print(f"{len(y):,} decisive fights, {len(keep)} units x {len(CLASSES)} "
          f"classes = {len(keep) * len(CLASSES)} counter terms, "
          f"{len(set(replays)):,} replays")
    if len(y) < 200:
        sys.exit("not enough fights to fit anything honest")

    rng = np.random.default_rng(args.seed)
    uniq = np.array(sorted(set(replays)))
    rng.shuffle(uniq)
    fold_of = {r: i % args.folds for i, r in enumerate(uniq)}
    folds = np.array([fold_of[r] for r in replays])

    base, flat, cnt, tr_cnt, briers = [], [], [], [], []
    for f in range(args.folds):
        te, trn = folds == f, folds != f
        ca = A[te].sum(axis=1) + other[te, 0]
        cb = B[te].sum(axis=1) + other[te, 1]
        base.append(float(((ca > cb).astype(float) == y[te]).mean()))

        # nested: same code path, counter terms frozen at zero
        th0, dl0, k0 = fit(A[trn], B[trn], MA[trn], MB[trn], y[trn], other[trn],
                           lam_theta=args.lam_theta, counters=False)
        flat.append(score(A[te], B[te], MA[te], MB[te], y[te], other[te],
                          th0, dl0, k0)[0])

        th, dl, k = fit(A[trn], B[trn], MA[trn], MB[trn], y[trn], other[trn],
                        lam_theta=args.lam_theta, lam_delta=args.lam_delta)
        acc, br = score(A[te], B[te], MA[te], MB[te], y[te], other[te],
                        th, dl, k)
        cnt.append(acc)
        briers.append(br)
        tr_cnt.append(score(A[trn], B[trn], MA[trn], MB[trn], y[trn],
                            other[trn], th, dl, k)[0])

    print(f"\n{args.folds}-fold, split by replay:")
    print(f"  cash baseline            {100 * np.mean(base):5.1f}%")
    print(f"  flat weights (nested)    {100 * np.mean(flat):5.1f}%  "
          f"{100 * (np.mean(flat) - np.mean(base)):+.1f} vs cash")
    print(f"  counter-aware            {100 * np.mean(cnt):5.1f}%  "
          f"{100 * (np.mean(cnt) - np.mean(flat)):+.1f} vs flat  "
          f"(+/- {100 * np.std(cnt):.1f})")
    print(f"  counter-aware, IN SAMPLE {100 * np.mean(tr_cnt):5.1f}%  "
          f"<- gap to held-out is the overfit")
    print(f"  Brier                    {np.mean(briers):.4f}")

    theta, delta, k = fit(A, B, MA, MB, y, other,
                          lam_theta=args.lam_theta, lam_delta=args.lam_delta)
    w0 = np.exp(theta)
    print(f"\nvalue multiplier by enemy class (blank = that matchup is unseen)")
    hdr = f"  {'unit':<18}{'avg':>6}" + "".join(f"{c[:6]:>8}" for c in CLASSES[:-1])
    print(hdr)
    order = np.argsort(-w0)
    for i in order:
        u = keep[i]
        if units.get(u, {}).get("faction") != "Alien":
            continue
        cells = ""
        for j, c in enumerate(CLASSES[:-1]):
            v = w0[i] * np.exp(delta[i, j])
            cells += f"{v:>8.2f}" if abs(delta[i, j]) > 1e-3 else f"{'-':>8}"
        print(f"  {u:<18}{w0[i]:>6.2f}{cells}")

    if args.emit:
        payload = {
            "_readme": [
                "Counter-aware value multipliers, fitted from decisive fights.",
                "w[unit][class] = exp(theta[unit] + delta[unit][class]).",
                "Classes are the producing building; see fit_counters.py.",
            ],
            "k": round(float(k), 4),
            "classes": CLASSES,
            "cv_counter": round(float(np.mean(cnt)), 4),
            "cv_flat": round(float(np.mean(flat)), 4),
            "cv_baseline": round(float(np.mean(base)), 4),
            "fights": int(len(y)),
            "ai_only": bool(args.ai_only),
            "w": {keep[i]: {c: round(float(w0[i] * np.exp(delta[i, j])), 4)
                            for j, c in enumerate(CLASSES)}
                  for i in range(len(keep))},
        }
        with io.open(args.emit, "w", encoding="utf-8") as fh:
            json.dump(payload, fh, indent=1)
        print(f"\nwrote {args.emit}")


if __name__ == "__main__":
    main()
