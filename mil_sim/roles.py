#!/usr/bin/env python3
"""
Which units count as FORCE in a fight, and which are just standing there.

THE BUG THIS EXISTS TO FIX
--------------------------
The first census counted every unit inside the radius as force. At an alien nest
that is dozens of Shrimps; at a human base it is Harvesters at 1,500 cash each.
Ten percent of all census cash turned out to be things that cannot shoot, and it
lands asymmetrically — on whichever side happened to be fighting at home. A
force ratio built from it is not a force ratio.

WHY THE BALANCE DUMP ALONE CANNOT DECIDE THIS
---------------------------------------------
The obvious rule — "no weapon in the dump means no weapon" — is wrong, and
wrong in the direction that matters. Infantry weapons live on `HumanHandsAnimator`
components and are NOT captured by the dump (Si_UnitBalance's own notes say so),
so that rule files Juggernaut, Templar, Marksman, Sniper, Rifleman, Militia,
Trooper, Commando and Heavy as non-combatants. Juggernaut kills at a measured
689 m. It is the human anti-tank answer, and calling it furniture would corrupt
every human force estimate in the dataset.

So the classification is EVIDENCE-FIRST: a type that shows up as an attacker in
the archive is a combatant, whatever the dump says about it. The dump is only
consulted for types the archive has never seen kill anything, which is the case
where absence of evidence really is evidence of absence — a Shrimp has had two
thousand rounds to score a kill.

    python mil_sim/roles.py       # print the classification and its evidence
"""

import collections
import csv
import io
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from unit_stats import load_units, resolve            # noqa: E402

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
KILLS = os.path.join(ROOT, "analysis", "kills.csv")

# Kills needed before the archive is allowed to overrule the dump. Low, because
# a unit with no weapon in the dump scoring even a handful of kills is already
# telling us the dump is incomplete for it.
MIN_KILLS = 5

# ...but RAW kill counts alone let workers in. A Shrimp has 185 kills across
# 130,035 built, which is roadkill and attribution noise, not a weapon. Kills
# per unit BUILT separates them cleanly, and the archive leaves an obvious gap
# to put the threshold in:
#
#     Shrimp 0.0014   Sports Car 0.0041   Hover Bike 0.0044
#     ---- gap ----
#     Worm 0.023   Horned Crab 0.051   Crab 0.058   Squid 0.086
#
MIN_KILLS_PER_BUILT = 0.01
SCORECARD = os.path.join(ROOT, "analysis", "srpl_unit_scorecard.csv")

# Harvesters are the exception the rate test cannot catch, and they are excluded
# BY ROLE rather than by evidence. A Harvester scores 0.32 kills per unit built —
# as many as a Rifleman — because players pilot them and run things over. It is
# still an economic unit: its 1,500 cash is income infrastructure, and counting
# it as army value would make every base defence look like a bigger force than
# was ever committed. Its kills stay in the kill data; its cash stays out of the
# force ratio.
ECO_BY_ROLE = {"Harvester", "Harvester (legacy)", "Sol Harvester",
               "Cent Harvester", "Shrimp"}


def _built_counts(path=SCORECARD):
    """Type name -> how many were ever built, from the replay scorecard."""
    out = {}
    try:
        with io.open(path, encoding="utf-8-sig", newline="") as fh:
            for row in csv.DictReader(fh):
                try:
                    out[row["unit"]] = float(row["built"])
                except (KeyError, ValueError):
                    continue
    except OSError:
        pass
    return out


def observed_attackers(path=KILLS, min_kills=MIN_KILLS):
    """
    Type names the archive has seen kill something, at a rate that means it.

    Both tests have to pass: enough kills to not be a fluke, and enough kills
    per unit built to not be roadkill.
    """
    counts = collections.Counter()
    try:
        with io.open(path, encoding="utf-8-sig", newline="") as fh:
            for row in csv.DictReader(fh):
                a = row.get("attacker", "")
                if a and a != "?":
                    counts[a] += 1
    except OSError:
        return counts, set()

    built = _built_counts()
    seen = set()
    for name, c in counts.items():
        if c < min_kills or name in ECO_BY_ROLE:
            continue
        b = built.get(name)
        if b and c / b < MIN_KILLS_PER_BUILT:
            continue                       # a worker that ran someone over
        seen.add(name)
    return counts, seen


def combatants(units=None):
    """
    Returns (is_combat, why) where is_combat maps type name -> bool.

    A type is force if it has a weapon in the dump OR the archive has seen it
    kill. Structures are excluded here and counted separately by the census —
    a turret that shoots is real, but folding buildings into army value makes
    every home defence look like a bigger army than it is.
    """
    units = units or load_units()
    counts, seen = observed_attackers()

    is_combat, why = {}, {}
    for name, u in units.items():
        if u["is_structure"]:
            is_combat[name] = False
            why[name] = "structure"
            continue
        if name in ECO_BY_ROLE:
            is_combat[name] = False
            why[name] = "economic by role (see ECO_BY_ROLE)"
            continue
        armed = any(w["damage"] > 0 for w in u["weapons"])
        if armed:
            is_combat[name] = True
            why[name] = "dump: has weapon"
        elif name in seen:
            is_combat[name] = True
            why[name] = f"archive: {counts[name]:,} kills, dump has no weapon"
        else:
            is_combat[name] = False
            why[name] = "no weapon, never seen killing"

    # Names the archive knows that the dump does not key on (display names such
    # as "Harvester" and "Headquarters"); resolve them through the same helper
    # the miner uses so they are classified rather than silently dropped.
    for name in seen:
        if name in is_combat:
            continue
        for team in ("Sol", "Centauri", ""):
            u = resolve(units, name, team)
            if u:
                break
        if u and u["is_structure"]:
            is_combat[name] = False
            why[name] = "structure (turret kills counted, value not)"
        else:
            is_combat[name] = True
            why[name] = f"archive: {counts[name]:,} kills"
    return is_combat, why


def is_force(is_combat, units, type_name, team_name=""):
    """Convenience: (counts_as_force, cash) for one census entry."""
    u = resolve(units, type_name, team_name)
    if u is None:
        return False, 0.0
    if u["is_structure"]:
        return False, u["cost"]
    key = type_name if type_name in is_combat else u["name"]
    return bool(is_combat.get(key, True)), u["cost"]


def main():
    units = load_units()
    is_combat, why = combatants(units)
    out = collections.defaultdict(list)
    for name, flag in is_combat.items():
        out[why[name].split(":")[0]].append((name, flag))
    print("NOT force (excluded from army value):")
    for name in sorted(n for n, f in is_combat.items() if not f):
        if why[name].startswith("structure"):
            continue
        print(f"    {name:<22}{why[name]}")
    print("\nforce ONLY because the archive says so (dump has no weapon):")
    for name in sorted(n for n, f in is_combat.items()
                       if f and why[n].startswith("archive")):
        print(f"    {name:<22}{why[name]}")


if __name__ == "__main__":
    main()
