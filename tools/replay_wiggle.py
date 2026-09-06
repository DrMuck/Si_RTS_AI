#!/usr/bin/env python3
"""
Does low server fps make units wiggle? Reads a replay (.srpl, true server
positions once a tick) and the round's fps CSV, and reports per minute:
server fps, and for moving alien military units the median tortuosity of
their 30-second paths (path length / straight-line displacement; 1.0 is a
straight walk, 1.5 is a third of the walk spent going sideways) and the
share of windows above 1.5.

    python tools/replay_wiggle.py <replay.srpl> <fps.csv> [type,type,...]
"""
import csv
import math
import os
import sys

sys.path.insert(0, r"C:\Users\schwe\Projects\Silica-MapReplay\modules")
from srpl_reader import parse_srpl  # noqa: E402

MILITARY = {"Goliath", "Behemoth", "Horned Crab", "Hunter", "Shocker", "Defiler", "Dragonfly", "Squid"}


def main():
    replay = parse_srpl(sys.argv[1])
    fps_rows = list(csv.DictReader(open(sys.argv[2])))
    types = set(sys.argv[3].split(",")) if len(sys.argv) > 3 else MILITARY
    dt = replay.tick_interval_ms / 1000.0
    ticks = replay.tick_numbers
    ents = {eid: e for eid, e in replay.entities.items()
            if e.is_unit and e.team_name.lower().startswith("alien") and e.type_name in types}
    print(f"replay {os.path.basename(sys.argv[1])}: {len(ticks)} ticks x {dt:.1f}s, {len(ents)} alien units of {sorted(types)}")

    # fps by round second
    fps_at = {}
    for r in fps_rows:
        try:
            fps_at[int(float(r["t_sec"]))] = float(r["smoothed_fps"])
        except (KeyError, ValueError):
            pass

    window = max(1, int(round(30.0 / dt)))
    per_min = {}
    for eid in ents:
        track = []
        for t in ticks:
            p = replay.ticks[t].get(eid)
            track.append((t, p))
        # windows of consecutive present ticks
        i = 0
        while i + window < len(track):
            seg = track[i:i + window + 1]
            if any(p is None for _, p in seg):
                i += 1
                continue
            pts = [p for _, p in seg]
            path = sum(math.hypot(pts[k + 1][0] - pts[k][0], pts[k + 1][1] - pts[k][1]) for k in range(len(pts) - 1))
            disp = math.hypot(pts[-1][0] - pts[0][0], pts[-1][1] - pts[0][1])
            sec = int(seg[0][0] * dt)
            if path >= 40.0:                       # a moving unit, not one standing in a fight
                tort = path / max(disp, 1.0)
                per_min.setdefault(sec // 60, []).append(min(tort, 10.0))
            i += window
    print(" min   fps   windows  median-tortuosity  share>1.5  share>3")
    for m in sorted(per_min):
        vals = sorted(per_min[m])
        if len(vals) < 8:
            continue
        fps_vals = [fps_at[s] for s in range(m * 60, m * 60 + 60) if s in fps_at]
        fps = sum(fps_vals) / len(fps_vals) if fps_vals else float("nan")
        med = vals[len(vals) // 2]
        s15 = sum(1 for v in vals if v > 1.5) / len(vals)
        s3 = sum(1 for v in vals if v > 3.0) / len(vals)
        print(f"{m:4d}  {fps:5.0f}  {len(vals):7d}  {med:16.2f}  {s15:9.0%}  {s3:8.0%}")


if __name__ == "__main__":
    main()
