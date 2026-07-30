"""
Run the eco simulator across all snapshotted maps and compare strategies.
Usage:
  python run.py                    — test all maps under maps/
  python run.py northpolarcap      — test one map, with timeline detail
"""

import json
import sys
from pathlib import Path

import params as P
import sim
import strategy


STRATEGIES = [
    ("greedy_near",     strategy.greedy_current),
    ("skip_8_fixed",    strategy.skip_first_8),
    ("radial_6x1_r500", strategy.radial_6x1_r500),
    ("radial_8x1_r500", strategy.radial_8x1_r500),
    ("radial_8x1_r700", strategy.radial_8x1_r700),
    ("radial_8x2_r500", strategy.radial_8x2_r500),
    ("radial_12x1_r500",strategy.radial_12x1_r500),
]


def run_map(map_data, detailed=False):
    """Return list of (label, earned, bcs, cysts, nodes, shrimps, log)."""
    horizon = P.DEFAULT_HORIZON_S
    results = []
    for label, strat in STRATEGIES:
        strategy.reset_caches()
        s = sim.new_state(map_data)
        log = sim.run(s, strat, horizon,
                      trace=detailed, trace_every_s=60)
        bc = sum(1 for st in s.structs if st.kind == "BC")
        cy = sum(1 for st in s.structs if st.kind == "Cyst")
        nd = sum(1 for st in s.structs if st.kind == "Node")
        results.append((label, s.cumulative_income, bc, cy, nd,
                        sim.total_shrimps(s), log))
    return results


def print_map_row(map_name, patches, biotics_total, results):
    print(f"\n=== {map_name}  ({len(patches)} patches, {biotics_total:,} biotics) ===")
    base = next((r[1] for r in results if r[0] == "greedy_near"), None)
    print(f"  {'strategy':16s} | {'earned':>10s} | {'vs base':>8s} | "
          f"{'BCs':>4s} {'Cy':>3s} {'Nd':>3s} {'Shr':>4s}")
    for label, earned, bc, cy, nd, shr, _ in results:
        vs = "" if base is None else f"{(earned - base) / base * 100:+.1f}%"
        print(f"  {label:16s} | {earned:>10,} | {vs:>8s} | "
              f"{bc:>4d} {cy:>3d} {nd:>3d} {shr:>4d}")


def main():
    maps_dir = Path(__file__).parent / "maps"
    map_files = sorted(maps_dir.glob("*.json"))
    if not map_files:
        print("No maps in maps/. Run auto_snapshot.py or fetch_map.py first.")
        sys.exit(1)

    if len(sys.argv) > 1:
        target = sys.argv[1].lower()
        map_files = [p for p in map_files if p.stem == target]
        if not map_files:
            print(f"Map '{target}' not found. Available:")
            for p in sorted(maps_dir.glob("*.json")):
                print(f"  {p.stem}")
            sys.exit(1)

    horizon = P.DEFAULT_HORIZON_S
    detailed = len(sys.argv) > 1
    print(f"Horizon: {horizon}s ({horizon/60:.1f} min).  Strategies: "
          + ", ".join(s[0] for s in STRATEGIES))

    all_summary = []
    for mf in map_files:
        map_data = json.loads(mf.read_text())
        biotics_total = sum(p["initial"] for p in map_data["patches"])
        results = run_map(map_data, detailed=detailed)
        print_map_row(map_data["map"], map_data["patches"], biotics_total, results)
        all_summary.append((map_data["map"], results))

        if detailed:
            for label, _, _, _, _, _, log in results:
                print(f"\n  ---- {label} timeline ----")
                for row in log:
                    print(f"    t={row['t']:>4.0f}s: cash={row['cash']:>6,} "
                          f"earned={row['earned']:>7,} "
                          f"BCs={row['bcs']:>2d} Cy={row['cysts']:>2d} shr={row['shrimps']:>3d}")

    # Cross-map winner tally
    if len(all_summary) > 1:
        print("\n\n=== Winner tally across maps ===")
        wins = {label: 0 for label, _ in STRATEGIES}
        for map_name, results in all_summary:
            best_label = max(results, key=lambda r: r[1])[0]
            wins[best_label] += 1
            best_earned = max(results, key=lambda r: r[1])[1]
            base = next(r[1] for r in results if r[0] == "greedy_near")
            print(f"  {map_name:20s}: winner={best_label:16s} "
                  f"({best_earned:>7,} = {(best_earned-base)/base*100:+.1f}% vs greedy)")
        print("\n  wins per strategy:")
        for label, w in wins.items():
            print(f"    {label:16s}: {w}")


if __name__ == "__main__":
    main()
