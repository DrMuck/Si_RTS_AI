#!/usr/bin/env python3
"""
Last place counters could hide: winning CHEAPER rather than winning.

WHY THIS TEST EXISTS
--------------------
`fit_counters.py` finds that letting a unit's value depend on the enemy's class
mix adds nothing to held-out win prediction — +0.0 to +0.4 points across every
subset and every regularisation strength, while in-sample accuracy climbs, which
is the shape of overfitting rather than of a finding.

That is not yet enough to conclude counters do not matter, because it tests the
wrong target. A counter's classic payoff is not that it wins a fight it would
otherwise lose — it is that it wins the SAME fight for less. The binary outcome
cannot see that; the exchange ratio can.

So the target here is `log(lossA / lossB)`, and the question is whether the
class matchup explains any of it once the force ratio is accounted for.

    log(lossA/lossB)  ~  b0 * log(forceA/forceB)  +  SUM  M[i][j] * (mixA_i mixB_j - mixB_i mixA_j)

The interaction block is antisymmetric on purpose. `M[i][j]` reads as "class i
beating class j", and the construction forces `M[i][j] = -M[j][i]` implicitly:
swapping the two sides must flip the sign of the predicted exchange, because
which side we called A is arbitrary. A model that can violate that would happily
fit a matchup where both sides come out ahead.

Ridge regression, five-fold, SPLIT BY REPLAY, scored by out-of-sample R^2
against the force-ratio-only model. If the matchup block cannot beat that, the
result stands: in Silica the outcome of a fight is set by how much effective
force arrives, and composition is not measurably part of it at this resolution.

    python mil_sim/fit_exchange.py [--ai-only] [--min-elo 1650]
"""

import argparse
import collections
import math
import os
import sys

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from unit_stats import load_units, resolve                  # noqa: E402
from fit_kernel import load, decisive, num, MIN_CASH        # noqa: E402
from fit_counters import class_of, CLASSES, CIDX            # noqa: E402

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(HERE, "out")


def build(fights, comp, units, ai_only, min_elo, decisive_only):
    C = len(CLASSES)
    X, y, reps = [], [], []
    for fid, r in fights.items():
        ca, cb = num(r, "a_cash"), num(r, "b_cash")
        la, lb = num(r, "a_lost_cash"), num(r, "b_lost_cash")
        if min(ca, cb) < MIN_CASH or min(la, lb) <= 0:
            continue
        if decisive_only and not decisive(r):
            continue
        if ai_only and (num(r, "a_piloted_frac") > 0
                        or num(r, "b_piloted_frac") > 0):
            continue
        if min_elo and max(num(r, "best_cmd_elo"),
                           num(r, "best_fps_elo")) < min_elo:
            continue

        mix = {}
        for side in "ab":
            v = np.zeros(C)
            team = r[f"{side}_team"]
            for u, cnt in comp[(fid, side)].items():
                rec = resolve(units, u, team)
                v[CIDX[class_of(units, u, team)]] += (rec["cost"] if rec else 0.0) * cnt
            s = v.sum()
            mix[side] = v / s if s > 0 else v
        ma, mb = mix["a"], mix["b"]

        inter = np.outer(ma, mb) - np.outer(mb, ma)     # antisymmetric
        iu = np.triu_indices(C, k=1)                    # upper triangle only
        X.append(np.concatenate(([math.log(ca / cb)], inter[iu])))
        y.append(math.log(la / lb))
        reps.append(r["replay"])
    return np.array(X), np.array(y), np.array(reps)


def ridge(X, y, lam):
    Xb = np.hstack([X, np.ones((len(X), 1))])
    P = np.eye(Xb.shape[1]) * lam
    P[-1, -1] = 0.0                                    # do not shrink intercept
    return np.linalg.solve(Xb.T @ Xb + P, Xb.T @ y)


def r2(X, y, beta):
    Xb = np.hstack([X, np.ones((len(X), 1))])
    pred = Xb @ beta
    ss = ((y - pred) ** 2).sum()
    return 1.0 - ss / ((y - y.mean()) ** 2).sum()


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--fights", default=os.path.join(OUT, "engagements.csv"))
    ap.add_argument("--units", default=os.path.join(OUT, "fight_units.csv"))
    ap.add_argument("--ai-only", action="store_true")
    ap.add_argument("--min-elo", type=float, default=0.0)
    ap.add_argument("--all-fights", action="store_true",
                    help="include indecisive fights (noisier, far more rows)")
    ap.add_argument("--lam", type=float, default=1.0)
    ap.add_argument("--folds", type=int, default=5)
    ap.add_argument("--seed", type=int, default=0)
    args = ap.parse_args()

    units = load_units()
    fights, comp = load(args.fights, args.units)
    X, y, reps = build(fights, comp, units, args.ai_only, args.min_elo,
                       not args.all_fights)
    n_inter = X.shape[1] - 1
    print(f"{len(y):,} fights, {n_inter} matchup terms, "
          f"{len(set(reps)):,} replays")
    if len(y) < 200:
        sys.exit("not enough rows")

    rng = np.random.default_rng(args.seed)
    uniq = np.array(sorted(set(reps)))
    rng.shuffle(uniq)
    fold_of = {r: i % args.folds for i, r in enumerate(uniq)}
    folds = np.array([fold_of[r] for r in reps])

    base_r2, full_r2, betas = [], [], []
    for f in range(args.folds):
        te, tr = folds == f, folds != f
        b0 = ridge(X[tr][:, :1], y[tr], args.lam)
        base_r2.append(r2(X[te][:, :1], y[te], b0))
        b1 = ridge(X[tr], y[tr], args.lam)
        full_r2.append(r2(X[te], y[te], b1))
        betas.append(b1)

    print(f"\nout-of-sample R^2, {args.folds}-fold split by replay:")
    print(f"  force ratio only        {np.mean(base_r2):+.4f}")
    print(f"  + class matchup block   {np.mean(full_r2):+.4f}   "
          f"({np.mean(full_r2) - np.mean(base_r2):+.4f})")
    print(f"  exchange exponent beta  {-np.mean([b[0] for b in betas]):.2f}")

    beta = ridge(X, y, args.lam)
    iu = np.triu_indices(len(CLASSES), k=1)
    pairs = sorted(
        ((abs(beta[1 + t]), beta[1 + t], CLASSES[i], CLASSES[j])
         for t, (i, j) in enumerate(zip(*iu))), reverse=True)
    print("\nstrongest matchup terms (negative = the FIRST class loses less):")
    for mag, v, ci, cj in pairs[:8]:
        print(f"  {ci:<11} vs {cj:<11}{v:+.3f}")


if __name__ == "__main__":
    main()
