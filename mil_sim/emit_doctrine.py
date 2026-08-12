#!/usr/bin/env python3
"""
Emit the one file the mod reads: mil_doctrine.json.

    python mil_sim/emit_doctrine.py [--since 20260501] [--faction Alien]

WHY A GENERATOR AND NOT A HAND-WRITTEN FILE
--------------------------------------------
Every number in here was measured, and each one has a script that produced it
and an argument in MIL_V2_ARCHITECTURE.md for why it is shaped that way. A
hand-maintained copy drifts from its source the first time anything is refitted,
and then nobody can say which is right. So this reads the fitted artefacts and
writes the deployable one.

It also stamps the era into the file. That is not decoration: `check_epochs.py`
measures unit values drifting 1.97x between months against a 1.38x noise floor,
and the build order moved even harder — Alien Greater Cysts by minute 10 went
0.41 in March to 1.75 in June when the tier requirement dropped. A doctrine file
without an era on it is a file nobody can date, and a stale one looks exactly
like a fresh one.

WHAT GOES IN, AND WHAT EACH PART IS FOR
----------------------------------------
  commit      the force-ratio bands. Below `refuseBelow` nothing predicts the
              outcome, so the fight is a coin flip and should be declined;
              `commitAt` is where the win rate peaks; past `wastefulAbove` the
              curve is flat and the surplus belongs on another objective.
  unitValue   multiplier on cash price, from the AI-vs-AI fit. Effective force
              is SUM(count * cost * w), and that is what the bands compare.
  trajectory  what a top-quartile commander has built by each minute. NOT a
              script to follow — a yardstick to notice being behind on.

The mod treats all three as advisory and logs against them before anything acts.
"""

import argparse
import collections
import csv
import io
import json
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
OUT = os.path.join(HERE, "out")
ANALYSIS = os.path.join(ROOT, "analysis")

# From MIL_V2_ARCHITECTURE section 4c, measured rather than chosen: P(richer
# side wins) by force ratio runs 57 / 78 / 92 / 89 percent across these bands on
# AI-vs-AI decisive fights, and only 8% of fights land in the first one.
COMMIT_BANDS = {"refuseBelow": 1.25, "commitAt": 2.0, "wastefulAbove": 4.0}


def load_model(path):
    with io.open(path, encoding="utf-8") as fh:
        return json.load(fh)


def load_trajectory(path, faction, tier="top"):
    """faction -> {name: {minute: mean cumulative}} plus a structure flag."""
    traj = collections.defaultdict(dict)
    is_struct = {}
    cost = {}
    with io.open(path, encoding="utf-8", newline="") as fh:
        for r in csv.DictReader(fh):
            if r["faction"] != faction or r["tier"] != tier:
                continue
            traj[r["unit"]][int(r["minute"])] = float(r["mean_cum"])
            is_struct[r["unit"]] = r["is_structure"] == "1"
            cost[r["unit"]] = float(r["cost"] or 0)
    return traj, is_struct, cost


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--model", default=os.path.join(OUT, "mil_model_ai.json"))
    ap.add_argument("--phases", default=os.path.join(ANALYSIS, "build_phases.csv"))
    ap.add_argument("--faction", default="Alien")
    # TWO ERAS, ON PURPOSE. The build order moved hard and recently (Greater
    # Cysts by minute 10: 0.41 in March, 1.75 in June, after the tier dropped
    # 2->1), so the trajectory is cut tight. Unit VALUES did not move nearly as
    # much — the all-archive and since-April fits agree inside the 1.38x noise
    # floor check_epochs measured — and cutting them to May costs half the
    # roster: 587 fights leaves only six alien units above the identifiability
    # threshold, and the regulariser then shrinks every survivor toward 1.0.
    # That shrinkage reads like an era effect and is not one.
    ap.add_argument("--since", default="20260501",
                    help="era for the build trajectory")
    ap.add_argument("--model-since", default="20260401",
                    help="era for the unit values; wider, see the note above")
    ap.add_argument("--out", default=os.path.join(OUT, "mil_doctrine.json"))
    args = ap.parse_args()

    model = load_model(args.model)
    traj, is_struct, cost = load_trajectory(args.phases, args.faction)
    if not traj:
        sys.exit(f"no {args.faction} rows in {args.phases} — run "
                 f"tools/serverdata/build_phases.py --since {args.since}")

    if model.get("since") != args.model_since:
        print(f"  ! model was fitted --since {model.get('since')!r} but this "
              f"doctrine claims {args.model_since!r}; refit to match")

    # Only structures whose count is a decision the planner makes. Units are in
    # the trajectory too but the planner does not choose a Shocker count
    # directly — it chooses producers, and the producers decide.
    KEEP = {"Lesser Spawning Cyst", "Greater Spawning Cyst",
            "Grand Spawning Cyst", "Colossal Spawning Cyst",
            "Bio Cache", "Node", "Quantum Cortex", "Hive Spire", "Thorn Spire",
            "Nest"}
    producers = {n: {str(m): round(v, 2) for m, v in sorted(d.items())}
                 for n, d in traj.items() if n in KEEP}

    payload = {
        "_readme": [
            "MEASURED DOCTRINE for the military layer. Copy to <server>/UserData/.",
            "Advisory: the mod logs against this before it acts on it.",
            "",
            "commit     - force-ratio bands in EFFECTIVE cash. Below refuseBelow",
            "             nothing predicts the fight; past wastefulAbove the win",
            "             curve is flat and the surplus belongs elsewhere.",
            "unitValue  - multiplier on cash price, AI-vs-AI fit. A unit missing",
            "             here is UNMEASURED, not bad, and counts at 1.0.",
            "trajectory - what a top-quartile commander had built by each minute.",
            "             A yardstick, not a script.",
            "",
            "Regenerate with mil_sim/emit_doctrine.py. Delete the file and the",
            "mod falls back to raw cash and its own constants.",
        ],
        "era": {
            "trajectorySince": args.since,
            "unitValueSince": args.model_since,
            "faction": args.faction,
            "note": "the meta moves; see check_epochs.py and build_phases.py",
        },
        "source": {
            "model": os.path.basename(args.model),
            "fights": model.get("fights"),
            "cv_accuracy": model.get("cv_accuracy"),
            "cv_baseline": model.get("cv_baseline"),
            "ai_only": model.get("ai_only"),
            "k": model.get("k"),
        },
        "commit": COMMIT_BANDS,
        "unitValue": {k: round(v, 3) for k, v in sorted(model["w"].items())},
        "trajectory": producers,
    }

    with io.open(args.out, "w", encoding="utf-8") as fh:
        json.dump(payload, fh, indent=1)
    print(f"wrote {args.out}")
    print(f"  trajectory since {args.since}, unit values since "
          f"{args.model_since}, faction {args.faction}")
    print(f"  {len(payload['unitValue'])} unit values "
          f"(model cv {model.get('cv_accuracy')} vs baseline "
          f"{model.get('cv_baseline')})")
    print(f"  {len(producers)} structures on the trajectory")


if __name__ == "__main__":
    main()
