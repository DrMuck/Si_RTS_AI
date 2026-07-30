"""
Per-map opening geometry: the N nearest biotics patches to the ALIEN spawn.

Why this exists: eco performance in the first ten minutes is dominated by how
much harvestable ground sits inside build range of the Nest, and that varies
enormously between maps. NarakaCity has only 13 of 107 patches within 1500m
(median 3415m); TheMaw has 31 patches total. A planner tuned on one shape will
be wrong on the other, so the shape has to be measurable.

Sources, both authoritative and both live-editable by the user:
  * UserData/Spawns/<Map>/layout_*.json    -> alien Nest position, per-patch
                                              resource overrides, chain ranges
  * UserData/MapBalance/dump_<Map>_*.txt   -> every ResourceArea with position,
                                              type and amount

Usage:
    python map_geometry.py                 # all maps, 20 nearest
    python map_geometry.py NarakaCity 30   # one map, 30 nearest
"""
import glob
import json
import math
import os
import re
import sys

SERVER = r"E:\Steam\steamapps\common\Silica Dedicated Server\UserData"
SPAWNS = os.path.join(SERVER, "Spawns")
BALANCE = os.path.join(SERVER, "MapBalance")


def alien_nest(map_name):
    """Alien spawn position from the map's layout JSON, or None."""
    for path in glob.glob(os.path.join(SPAWNS, map_name, "*.json")):
        try:
            with open(path, encoding="utf-8") as fh:
                data = json.load(fh)
        except Exception:
            continue
        spawns = (data.get("spawns") or {}).get("Alien") or []
        for entry in spawns:
            if entry.get("isSpawn"):
                return float(entry["x"]), float(entry["z"]), os.path.basename(path)
    return None


def biotics(map_name):
    """[(x, z, amount)] for every biotics ResourceArea, newest dump wins."""
    dumps = sorted(glob.glob(os.path.join(BALANCE, f"dump_{map_name}_*.txt")),
                   key=os.path.getmtime)
    if not dumps:
        return [], None
    text = open(dumps[-1], encoding="utf-8", errors="replace").read()
    out = []
    for block in re.split(r"\n\[\d+\] ", text)[1:]:
        if not re.match(r'"ResourceArea_\w+" \(Biotics\)', block):
            continue
        pos = re.search(r"Position: \(([-\d.]+), [-\d.]+, ([-\d.]+)\)", block)
        amt = re.search(r"Resources: \d+ / (\d+)", block)
        if pos:
            out.append((float(pos.group(1)), float(pos.group(2)),
                        int(amt.group(1)) if amt else 0))
    return out, os.path.basename(dumps[-1])


def report(map_name, top_n=20):
    nest = alien_nest(map_name)
    patches, dump = biotics(map_name)
    if nest is None or not patches:
        print(f"{map_name}: missing "
              f"{'spawn layout' if nest is None else ''}"
              f"{' and ' if nest is None and not patches else ''}"
              f"{'balance dump' if not patches else ''}")
        return None

    nx, nz, layout = nest
    ranked = sorted(((math.hypot(x - nx, z - nz), x, z, a) for x, z, a in patches))
    near = ranked[:top_n]

    print(f"\n=== {map_name}   nest=({nx:.0f},{nz:.0f})   "
          f"patches={len(patches)}   layout={layout}")
    print(f"{'#':>3} {'dist':>7} {'amount':>8}   cumulative biotics")
    running = 0
    for i, (d, x, z, a) in enumerate(near, 1):
        running += a
        print(f"{i:3d} {d:6.0f}m {a:8d}   {running:,}")

    within = lambda r: sum(1 for d, *_ in ranked if d <= r)
    print(f"    within  500m: {within(500):3d}    within 1000m: {within(1000):3d}"
          f"    within 1500m: {within(1500):3d}    within 2500m: {within(2500):3d}")
    dists = [d for d, *_ in ranked]
    print(f"    nearest {dists[0]:.0f}m   median {dists[len(dists)//2]:.0f}m   "
          f"p90 {dists[int(len(dists)*0.9)]:.0f}m   total biotics {sum(a for *_, a in ranked):,}")
    return {
        "map": map_name,
        "nest": [nx, nz],
        "patches": len(patches),
        "nearest_m": round(dists[0]),
        "median_m": round(dists[len(dists) // 2]),
        "within_1000m": within(1000),
        "within_1500m": within(1500),
        "top": [{"dist_m": round(d), "amount": a} for d, _, _, a in near],
    }


def main():
    args = sys.argv[1:]
    top_n = 20
    if args and args[-1].isdigit():
        top_n = int(args.pop())
    maps = args or sorted(
        {re.match(r"dump_(\w+?)_\d{8}_", os.path.basename(p)).group(1)
         for p in glob.glob(os.path.join(BALANCE, "dump_*.txt"))
         if re.match(r"dump_(\w+?)_\d{8}_", os.path.basename(p))})

    collected = [r for r in (report(m, top_n) for m in maps) if r]
    out = os.path.join(os.path.dirname(os.path.abspath(__file__)), "map_geometry.json")
    with open(out, "w", encoding="utf-8") as fh:
        json.dump(collected, fh, indent=2)
    print(f"\nwrote {out}  ({len(collected)} maps)")


if __name__ == "__main__":
    main()
