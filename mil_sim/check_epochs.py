#!/usr/bin/env python3
"""
Is the archive still valid, or has the balance moved out from under it?

THE CLAIM THIS TESTS
--------------------
`MIL_V2_ARCHITECTURE.md` §2 says: fit dimensionless behaviour, read absolute
physics, and a rebalance then recalibrates the model instead of invalidating it.
That was an argument, not a measurement, and it decides something practical —
whether five months of replays can be pooled, or whether only the rounds played
under the current numbers count and we need to record more.

So: cut the dataset by month, fit the same model in each, and see whether the
answers agree. Three things are checked and they fail differently.

  n            how much data each month actually holds. A month with 60
               decisive fights cannot disagree with anything meaningfully, and
               reading drift off it would be reading noise.
  accuracy     does the model still work in that month, held out within it
  weights      do the per-unit values agree between months

Weights are the real test. If a Behemoth is worth 1.8x its price in April and
0.9x in August, the "dimensionless" claim is false, the pooled fit is an average
of two different games, and everything downstream needs re-cutting by epoch.
If they agree, the archive pools and only genuinely new BEHAVIOUR — our own bot
commanding, which no replay in the archive contains — needs recording.

    python mil_sim/check_epochs.py [--ai-only] [--min-n 150]
"""

import argparse
import collections
import os
import sys

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from unit_stats import load_units                           # noqa: E402
from fit_kernel import (load, build, fit, accuracy, baseline)   # noqa: E402

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(HERE, "out")

# Units worth printing: the ones the alien production planner chooses between.
WATCH = ["Behemoth", "Shocker", "Scorpion", "Defiler", "Colossus", "Crab",
         "Hunter", "Horned Crab", "Firebug", "Dragonfly"]


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--fights", default=os.path.join(OUT, "engagements.csv"))
    ap.add_argument("--units", default=os.path.join(OUT, "fight_units.csv"))
    ap.add_argument("--ai-only", action="store_true")
    ap.add_argument("--min-n", type=int, default=150,
                    help="months below this are listed but not compared")
    ap.add_argument("--lam", type=float, default=1.0)
    ap.add_argument("--seed", type=int, default=0)
    args = ap.parse_args()

    units = load_units()
    fights, comp = load(args.fights, args.units)
    A, B, y, other, keep, replays, rows = build(
        fights, comp, units, args.ai_only, 0.0)
    idx = {u: i for i, u in enumerate(keep)}
    months = np.array([r["date"][:6] for _fid, r, _y in rows])

    print(f"{len(y):,} decisive fights"
          f"{' (AI vs AI only)' if args.ai_only else ''}\n")

    counts = collections.Counter(months)
    usable = sorted(m for m, c in counts.items() if c >= args.min_n)
    print(f"{'month':<9}{'fights':>8}{'replays':>9}{'baseline':>10}{'kernel':>9}")
    per_month = {}
    for m in sorted(counts):
        sel = months == m
        n = int(sel.sum())
        nrep = len(set(replays[sel]))
        if m not in usable:
            print(f"{m:<9}{n:>8,}{nrep:>9,}{'--':>10}{'too thin':>9}")
            continue
        # held out INSIDE the month, split by replay, so the number is honest
        rng = np.random.default_rng(args.seed)
        uniq = np.array(sorted(set(replays[sel])))
        rng.shuffle(uniq)
        half = set(uniq[: max(1, len(uniq) // 2)])
        tr = sel & np.array([r in half for r in replays])
        te = sel & ~np.array([r in half for r in replays])
        if te.sum() < 30 or tr.sum() < 30:
            print(f"{m:<9}{n:>8,}{nrep:>9,}{'--':>10}{'too thin':>9}")
            continue
        theta, k = fit(A[tr], B[tr], y[tr], other[tr], lam=args.lam)
        acc, _ = accuracy(A[te], B[te], y[te], other[te], theta, k)
        base = baseline(A[te], B[te], y[te], other[te])
        print(f"{m:<9}{n:>8,}{nrep:>9,}{100 * base:>9.1f}%{100 * acc:>8.1f}%")
        # weights from the WHOLE month, for the stability table
        th_all, _ = fit(A[sel], B[sel], y[sel], other[sel], lam=args.lam)
        per_month[m] = np.exp(th_all)

    if len(per_month) < 2:
        sys.exit("\nnot enough months above the threshold to compare")

    print(f"\nvalue multiplier by month — the stability test that matters")
    hdr = f"  {'unit':<14}" + "".join(f"{m[4:]:>8}" for m in sorted(per_month))
    print(hdr + f"{'spread':>9}")
    print("  " + "-" * (len(hdr) + 5))
    spreads = []
    for u in WATCH:
        if u not in idx:
            continue
        vals = [per_month[m][idx[u]] for m in sorted(per_month)]
        spread = max(vals) / min(vals) if min(vals) > 0 else float("inf")
        spreads.append(spread)
        print(f"  {u:<14}" + "".join(f"{v:>8.2f}" for v in vals)
              + f"{spread:>9.2f}x")

    between = float(np.median(spreads))
    print(f"\n  median spread BETWEEN months: {between:.2f}x")

    # ---- the null: how much does the weight move for no reason at all? ----
    #
    # A 2x spread over five months means nothing on its own. Each month holds
    # ~500 fights and the fit has 50 parameters, so the weights wobble from
    # sampling alone. The honest comparison is against a split that CANNOT
    # contain drift: cut one month's replays into random halves and fit both.
    # Whatever spread that produces is the noise floor, and only the excess
    # over it is evidence that the balance moved.
    nulls = []
    rng = np.random.default_rng(args.seed + 1)
    for m in sorted(per_month):
        sel = months == m
        uniq = np.array(sorted(set(replays[sel])))
        for trial in range(3):
            rng.shuffle(uniq)
            half = set(uniq[: len(uniq) // 2])
            in_h = np.array([r in half for r in replays])
            s1, s2 = sel & in_h, sel & ~in_h
            if s1.sum() < 60 or s2.sum() < 60:
                continue
            w1 = np.exp(fit(A[s1], B[s1], y[s1], other[s1], lam=args.lam)[0])
            w2 = np.exp(fit(A[s2], B[s2], y[s2], other[s2], lam=args.lam)[0])
            for u in WATCH:
                if u not in idx:
                    continue
                a, b = w1[idx[u]], w2[idx[u]]
                if min(a, b) > 0:
                    nulls.append(max(a, b) / min(a, b))
    null = float(np.median(nulls)) if nulls else float("nan")
    print(f"  median spread WITHIN a month (noise floor): {null:.2f}x"
          f"   n={len(nulls)} random splits")

    print()
    if not nulls:
        print("  could not build a noise floor; treat the between-month number")
        print("  as uninterpretable rather than as drift.")
    elif between <= null * 1.15:
        print("  The between-month spread is at or under the noise floor: the")
        print("  weights are NOT drifting with the balance, the archive pools,")
        print("  and old replays stay usable. What is missing is not older data")
        print("  under other numbers -- it is behaviour no replay contains.")
    else:
        print(f"  Between-month spread exceeds the noise floor by "
              f"{between / null:.2f}x: the weights ARE moving with the balance.")
        print("  Pooling five months is averaging games that were not the same")
        print("  game, and the fit should be cut by epoch -- which means the")
        print("  usable archive is only the current balance, and it is thin.")


if __name__ == "__main__":
    main()
