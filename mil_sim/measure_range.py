#!/usr/bin/env python3
"""
How far from its target does each unit actually kill — against its real reach.

THE QUESTION THIS ANSWERS, AND THE ONE IT DOES NOT
--------------------------------------------------
`unit_stats.py` derives what a weapon CAN reach. This measures what it DOES,
from the replay archive: the distance between attacker and victim at the tick a
kill lands, per attacker type.

The ratio of the two is the first dimensionless behaviour parameter the combat
model needs — `engage_frac = measured / reach`. It is dimensionless on purpose:
absolute reach moves every time Si_UnitBalance is pushed, but "the AI closes to
a third of its reach before it kills anything" is a statement about targeting
and pathing, and should hold across a rebalance. That is testable here by
splitting the archive by date, and `--by-epoch` does it.

WHAT DEGRADES THE MEASUREMENT, STATED SO IT IS NOT MISREAD
----------------------------------------------------------
- SRPL samples positions every 2 s. A Shocker at 16 m/s moves 32 m between
  samples, so a single distance carries roughly +/-30 m of quantisation. That is
  fatal for a mean over ten shots and harmless for a median over hundreds, which
  is why only medians and quantiles are reported and n is always printed.
- The kill tick is when the victim DIED, not when the shot was fired. For a slow
  projectile the shooter has moved; for artillery that gap is seconds. Distances
  for arcing weapons therefore read short by roughly speed x flight time.
- It measures killing distance, not engagement distance. A unit that fires from
  reach, closes, and finishes the target up close is recorded at the close
  distance. This biases every number DOWNWARD, and it is the reason the
  measurement is a floor on aggression rather than a description of it.
- Only kills where BOTH entities have a position sample within one tick of the
  death are usable; that is about 83% of them.

    python mil_sim/measure_range.py [--limit N] [--since YYYYMMDD] [--by-epoch]
"""

import argparse
import collections
import json
import glob
import math
import os
import statistics
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from unit_stats import load_units                      # noqa: E402

SERVER = r"E:\Steam\steamapps\common\Silica Dedicated Server"
MAPREPLAY = os.path.join(SERVER, "Mod MapReplay")
sys.path.insert(0, os.path.join(MAPREPLAY, "modules"))
from srpl_reader import parse_srpl                     # noqa: E402

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
ARCHIVE = os.path.join(ROOT, "Serverdata", "ReplayLogs - Copy")

# Unit destructions were not written in this window; see mine_replay_kills.py.
BROKEN_FROM, BROKEN_TO = "20260715", "20260731"

NEUTRAL = {"Wildlife", "GM", "Unknown"}


def usable(paths, since):
    out = []
    for p in paths:
        stamp = os.path.basename(p)[:8]
        if BROKEN_FROM <= stamp <= BROKEN_TO:
            continue
        if since and stamp < since:
            continue
        out.append(p)
    return out


def collect(paths):
    """(attacker, target_kind, epoch) -> [distance]"""
    obs = collections.defaultdict(list)
    paired = unpaired = 0
    for path in paths:
        try:
            r = parse_srpl(path)
        except Exception:
            continue
        epoch = os.path.basename(path)[:6]              # YYYYMM
        for tick, vid, aid, is_building in r.destructions:
            if aid == vid:                              # despawn, not a kill
                continue
            a, v = r.entities.get(aid), r.entities.get(vid)
            if a is None or v is None:
                continue
            if a.team_name in NEUTRAL or v.team_name in NEUTRAL:
                continue
            if a.team_name == v.team_name:              # team kill
                continue
            for t in (tick, tick - 1, tick + 1):
                snap = r.ticks.get(t)
                if not snap:
                    continue
                pa, pv = snap.get(aid), snap.get(vid)
                if pa and pv:
                    kind = "bldg" if is_building else "unit"
                    obs[(a.type_name, kind, epoch)].append(math.dist(pa, pv))
                    paired += 1
                    break
            else:
                unpaired += 1
    return obs, paired, unpaired


def report(obs, units, by_epoch, min_n):
    if by_epoch:
        keys = sorted({(k[0], k[2]) for k in obs if k[1] == "unit"})
        print(f"\n{'unit':<18}{'epoch':<9}{'n':>6}{'p50':>7}{'p90':>7}"
              f"{'reach':>7}{'p50/reach':>11}")
        print("-" * 65)
        for nm, ep in keys:
            ds = obs[(nm, "unit", ep)]
            if len(ds) < min_n:
                continue
            u = units.get(nm)
            reach = u["reach"] if u else 0
            s = sorted(ds)
            p50, p90 = statistics.median(s), s[int(len(s) * 0.9)]
            frac = f"{p50 / reach:.2f}" if reach > 1 else "-"
            print(f"{nm:<18}{ep:<9}{len(ds):>6}{p50:>7.0f}{p90:>7.0f}"
                  f"{reach:>7.0f}{frac:>11}")
        return

    merged = collections.defaultdict(list)
    for (nm, kind, _), ds in obs.items():
        merged[(nm, kind)].extend(ds)

    print(f"\n{'unit':<18}{'tgt':<6}{'n':>6}{'p50':>7}{'p90':>7}{'p99':>7}"
          f"{'max':>7}{'reach':>7}{'aim':>6}{'p50/reach':>11}  limit")
    print("-" * 96)
    for (nm, kind), ds in sorted(merged.items(), key=lambda kv: -len(kv[1])):
        if len(ds) < min_n or kind != "unit":
            continue
        u = units.get(nm)
        reach = u["reach"] if u else 0.0
        aim = u["aim_cap"] if u else 0.0
        s = sorted(ds)
        p50 = statistics.median(s)
        p90, p99 = s[int(len(s) * 0.9)], s[min(len(s) - 1, int(len(s) * 0.99))]
        frac = f"{p50 / reach:.2f}" if reach > 1 else "-"
        # What actually stops this weapon: the AI's firing cap, the projectile,
        # or neither (it closes far inside both and the limit is behaviour).
        if not u or reach <= 1:
            limit = "melee/unknown"
        elif p99 < 0.5 * reach:
            limit = "BEHAVIOUR"
        elif reach < aim - 1:
            limit = "projectile"
        else:
            limit = "aim cap"
        print(f"{nm:<18}{kind:<6}{len(ds):>6}{p50:>7.0f}{p90:>7.0f}{p99:>7.0f}"
              f"{max(s):>7.0f}{reach:>7.0f}{aim:>6.0f}{frac:>11}  {limit}")


# Melee reach. Measured, not guessed: Crab tops out at 13 m, Hunter at 32,
# Goliath at 36 across 40 replays. 40 m covers contact plus one tick of closing
# at 2 s sampling.
CONTACT_M = 40.0


def emit_reach(obs, units, path):
    """
    Write the reach every later stage should use: derived where the dump can
    derive it, measured where it cannot.

    Infantry is the reason this exists. Soldier weapons live on
    `HumanHandsAnimator` and are not in the balance dump, so Juggernaut,
    Templar, Sniper and the rest derive to zero — and a Juggernaut kills at
    689 m. For those the archive's p99 kill distance is the only estimate
    available, and it is a FLOOR: it is the furthest they were seen killing,
    not the furthest they could. Each entry says which it is so a consumer can
    weight them differently.
    """
    merged = collections.defaultdict(list)
    for (nm, kind, _), ds in obs.items():
        if kind == "unit":
            merged[nm].extend(ds)

    table = {}
    for nm, u in units.items():
        if u["is_structure"]:
            continue
        ds = sorted(merged.get(nm, []))
        p99 = ds[min(len(ds) - 1, int(len(ds) * 0.99))] if ds else None
        derived = u["reach"]
        if derived > 1.0:
            eff, src = derived, "derived"
        elif p99 is not None and len(ds) >= 25 and p99 > CONTACT_M:
            eff, src = p99, "measured"
        else:
            eff, src = CONTACT_M, "contact"
        table[nm] = {
            "effective": round(eff, 1), "source": src,
            "derived": round(derived, 1), "aim_cap": round(u["aim_cap"], 1),
            "measured_p99": round(p99, 1) if p99 is not None else None,
            "n": len(ds),
        }
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "w", encoding="utf-8") as fh:
        json.dump({"contact_m": CONTACT_M, "units": table}, fh, indent=1)
    by_src = collections.Counter(v["source"] for v in table.values())
    print(f"\nwrote {path}: {len(table)} units  {dict(by_src)}")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--limit", type=int, default=40)
    ap.add_argument("--since", default="20260802")
    ap.add_argument("--min-n", type=int, default=25)
    ap.add_argument("--by-epoch", action="store_true")
    ap.add_argument("--emit-reach", default="",
                    help="write the merged reach table to this path")
    ap.add_argument("--archive", default=ARCHIVE)
    args = ap.parse_args()

    paths = usable(sorted(glob.glob(os.path.join(args.archive, "*.srpl"))),
                   args.since)
    paths.sort(key=os.path.getsize, reverse=True)       # biggest = real matches
    paths = paths[:args.limit]
    print(f"{len(paths)} replays, since {args.since or 'the beginning'}")

    units = load_units()
    obs, paired, unpaired = collect(paths)
    total = paired + unpaired
    pct = 100.0 * paired / total if total else 0.0
    print(f"{paired:,} kills with both positions ({pct:.0f}% of {total:,})")
    report(obs, units, args.by_epoch, args.min_n)
    if args.emit_reach:
        emit_reach(obs, units, args.emit_reach)


if __name__ == "__main__":
    main()
