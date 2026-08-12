#!/usr/bin/env python3
"""
What the engagement dataset actually says.

THE ONE QUESTION THAT DECIDES WHETHER THE KERNEL IS WORTH BUILDING
------------------------------------------------------------------
`MIL_V2_ARCHITECTURE.md` step 1 sets a gate: a combat kernel earns its place
only if it beats "more cash wins" on held-out fights. That baseline is not a
straw man — if outcome is a clean function of the cash ratio then the planner
needs a number it already has, and the whole L0/L1 investment collapses into
one multiplier.

So the first thing to print is the win rate against cash ratio. A curve that
saturates fast means cash is nearly sufficient. A curve that stays shallow, or
that separates when conditioned on composition, is the kernel's justification.

WHAT IS DELIBERATELY SPLIT OUT
------------------------------
Piloted fights. A human driving a unit is a different weapon from the same unit
on commander AI, and the bot cannot pilot. Every table is reported for the
AI-only subset as well as the whole, and where they disagree the AI-only column
is the one the planner may use.

    python mil_sim/summarize.py [--in out/engagements.csv]
"""

import argparse
import collections
import csv
import io
import math
import os
import statistics

HERE = os.path.dirname(os.path.abspath(__file__))
DEFAULT_IN = os.path.join(HERE, "out", "engagements.csv")

# A fight below this cash on either side is two scouts bumping into each other.
# Reported separately rather than dropped, because most of the archive is that.
SKIRMISH_CASH = 1000


def num(row, key, default=0.0):
    v = row.get(key, "")
    if v in ("", None):
        return default
    try:
        return float(v)
    except ValueError:
        return default


def load(path):
    with io.open(path, encoding="utf-8", newline="") as fh:
        return list(csv.DictReader(fh))


def classify(r):
    """Who won, in cash. None when neither side lost anything measurable."""
    la, lb = num(r, "a_lost_cash"), num(r, "b_lost_cash")
    if la + lb <= 0:
        return None
    return "a" if la < lb else ("b" if lb < la else "draw")


# A fight where one side was destroyed and the other walked away. This filter is
# not tidying — it is the difference between a signal and noise, and it was found
# by looking rather than assumed:
#
#   P(richer side wins), by cash ratio      0.8-1.25   1.25-2    2-4     >4
#     every fight                              51%       52%     54%     55%
#     DECISIVE fights only                     55%       70%     83%     82%
#
# On a fight neither side broke off from, "who lost less cash" is a coin flip
# decorated with a number, and those are 63% of the rows. Including them flattens
# every curve in this file and drove the exchange exponent to zero — which would
# have been reported as "mass does not pay in Silica" and been wrong.
DECISIVE_LOSS = 0.70
DECISIVE_SURVIVE = 0.30


def decisive(r):
    """Did one side actually break?"""
    fa = num(r, "a_lost_cash") / max(1.0, num(r, "a_cash"))
    fb = num(r, "b_lost_cash") / max(1.0, num(r, "b_cash"))
    return ((fa >= DECISIVE_LOSS and fb <= DECISIVE_SURVIVE)
            or (fb >= DECISIVE_LOSS and fa <= DECISIVE_SURVIVE))


def bucket_ratio(x):
    for lo, hi, name in ((0, 0.5, "<0.5"), (0.5, 0.8, "0.5-0.8"),
                         (0.8, 1.25, "0.8-1.25"), (1.25, 2.0, "1.25-2"),
                         (2.0, 4.0, "2-4"), (4.0, 1e9, ">4")):
        if lo <= x < hi:
            return name
    return ">4"


def win_vs_cash(rows, label):
    """P(the bigger-cash side wins) by how much bigger it was."""
    buckets = collections.defaultdict(lambda: [0, 0])
    for r in rows:
        w = classify(r)
        if w in (None, "draw"):
            continue
        ca, cb = num(r, "a_cash"), num(r, "b_cash")
        if ca <= 0 or cb <= 0:
            continue
        # Orient so 'ratio' is always the winner-agnostic A:B ratio, then ask
        # whether the side with more cash was the one that lost less.
        ratio = ca / cb
        bigger = "a" if ratio >= 1 else "b"
        b = bucket_ratio(max(ratio, 1 / ratio))
        buckets[b][1] += 1
        if w == bigger:
            buckets[b][0] += 1

    print(f"\n  P(richer side wins) — {label}")
    print(f"    {'cash ratio':<12}{'n':>8}{'win%':>8}")
    order = ["0.8-1.25", "1.25-2", "2-4", ">4"]
    tot_n = tot_w = 0
    for b in order:
        w, n = buckets.get(b, [0, 0])
        if n:
            print(f"    {b:<12}{n:>8,}{100.0 * w / n:>7.0f}%")
            tot_n += n
            tot_w += w
    if tot_n:
        print(f"    {'ALL':<12}{tot_n:>8,}{100.0 * tot_w / tot_n:>7.0f}%")
    return tot_w / tot_n if tot_n else 0.0


def exchange_exponent(rows, label):
    """
    Fit `loss_ratio = force_ratio ^ -beta`.

    Lanchester's square law is beta = 2 — double the army and you take a quarter
    of the losses. Partial engagement drags it down, because only the units in
    contact ever trade. Fitting it is the difference between believing
    concentration pays four-fold and knowing what it actually pays.
    """
    xs, ys = [], []
    for r in rows:
        ca, cb = num(r, "a_cash"), num(r, "b_cash")
        la, lb = num(r, "a_lost_cash"), num(r, "b_lost_cash")
        if min(ca, cb) < SKIRMISH_CASH or min(la, lb) <= 0:
            continue
        xs.append(math.log(ca / cb))
        ys.append(math.log(la / lb))
    if len(xs) < 50:
        print(f"\n  exchange exponent — {label}: only {len(xs)} usable fights")
        return None
    mx, my = statistics.mean(xs), statistics.mean(ys)
    sxy = sum((x - mx) * (y - my) for x, y in zip(xs, ys))
    sxx = sum((x - mx) ** 2 for x in xs)
    beta = -sxy / sxx if sxx else 0.0
    # correlation, so the slope is not quoted without saying how tight it is
    syy = sum((y - my) ** 2 for y in ys)
    rho = sxy / math.sqrt(sxx * syy) if sxx and syy else 0.0
    print(f"\n  exchange exponent — {label}")
    print(f"    beta = {beta:.2f}   (Lanchester square = 2.0, linear = 1.0)")
    print(f"    r    = {rho:.2f}   over {len(xs):,} two-sided fights")
    return beta


def trade_fraction(rows, label):
    """How much of the force that was standing there actually died."""
    fr = []
    for r in rows:
        for s in ("a", "b"):
            cash = num(r, f"{s}_cash")
            if cash < SKIRMISH_CASH:
                continue
            fr.append(min(1.0, num(r, f"{s}_lost_cash") / cash))
    if not fr:
        return
    fr.sort()
    print(f"\n  losses as a share of force present — {label}  (n={len(fr):,})")
    print(f"    p10 {fr[len(fr)//10]:.2f}   p50 {statistics.median(fr):.2f}   "
          f"p90 {fr[len(fr)*9//10]:.2f}   mean {statistics.mean(fr):.2f}")


def shape(rows):
    print(f"\ndataset: {len(rows):,} fights")
    by_players = collections.Counter()
    piloted = collections.Counter()
    sizes = []
    durations = []
    for r in rows:
        p = int(num(r, "players"))
        by_players["1 (soak)" if p <= 1 else ("2-3" if p < 4 else "4+")] += 1
        pf = max(num(r, "a_piloted_frac"), num(r, "b_piloted_frac"))
        piloted["none (AI vs AI)" if pf == 0 else
                ("<25%" if pf < 0.25 else ("25-75%" if pf < 0.75 else ">75%"))] += 1
        c = min(num(r, "a_cash"), num(r, "b_cash"))
        if c > 0:
            sizes.append(c)
        durations.append(num(r, "duration"))
    for k, v in by_players.most_common():
        print(f"    players {k:<12}{v:>8,}")
    print()
    for k, v in piloted.most_common():
        print(f"    piloted {k:<18}{v:>8,}")
    if sizes:
        sizes.sort()
        print(f"\n    smaller side's cash: p50 {statistics.median(sizes):,.0f}  "
              f"p90 {sizes[len(sizes)*9//10]:,.0f}  max {sizes[-1]:,.0f}")
    if durations:
        durations.sort()
        print(f"    duration s:          p50 {statistics.median(durations):.0f}  "
              f"p90 {durations[len(durations)*9//10]:.0f}")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--in", dest="path", default=DEFAULT_IN)
    args = ap.parse_args()

    rows = load(args.path)
    shape(rows)

    real = [r for r in rows
            if min(num(r, "a_cash"), num(r, "b_cash")) >= SKIRMISH_CASH]
    ai = [r for r in real
          if num(r, "a_piloted_frac") == 0 and num(r, "b_piloted_frac") == 0]
    print(f"\nfights above {SKIRMISH_CASH} combat cash a side: {len(real):,}  "
          f"of which AI-vs-AI: {len(ai):,}")

    # Skill tiers. A model fitted across every round averages "two good
    # commanders trading carefully" with "nobody was driving", and describes
    # neither. The commander ELO table's median is ~1500 and its p90 ~1670, so
    # those are the two cuts worth printing.
    skilled = [r for r in real
               if num(r, "best_cmd_elo") >= 1650 or num(r, "best_fps_elo") >= 1650]
    skilled_ai = [r for r in skilled
                  if num(r, "a_piloted_frac") == 0 and num(r, "b_piloted_frac") == 0]
    print(f"with a 1650+ commander or FPS player present: {len(skilled):,}  "
          f"of which AI-vs-AI: {len(skilled_ai):,}")

    dec = [r for r in real if decisive(r)]
    dec_ai = [r for r in ai if decisive(r)]
    dec_sk = [r for r in skilled if decisive(r)]
    print(f"of which DECISIVE (one side broke): {len(dec):,} "
          f"({100.0 * len(dec) / max(1, len(real)):.0f}%)")

    for label, subset in (("every fight (NOISE — see decisive() )", real),
                          ("decisive fights", dec),
                          ("decisive, AI vs AI", dec_ai),
                          ("decisive, skilled rounds (1650+)", dec_sk)):
        win_vs_cash(subset, label)
        exchange_exponent(subset, label)
        trade_fraction(subset, label)


if __name__ == "__main__":
    main()
