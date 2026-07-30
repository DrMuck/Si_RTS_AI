"""
Pull the current map's initial state from the running Silica server and cache
it under maps/<mapname>.json. Run this once at the START of a round on the
target map (patches at max), then the simulator/optimizer read from the cache.

Requires the server to be running with the Si_RTS_AI mod's telemetry endpoints:
  /state    → team info, cash, map name
  /patches  → all ResourceArea entries (biotics + balterium)
  /entities → structures/units (used here just to grab the Alien Nest position)
"""

import json
import sys
import urllib.request
from pathlib import Path

HOST = "http://localhost:8765"


def fetch(endpoint):
    with urllib.request.urlopen(f"{HOST}{endpoint}", timeout=5) as r:
        return json.load(r)


def main():
    state = fetch("/state")
    patches = fetch("/patches")
    entities = fetch("/entities")

    map_name = state["map"]
    round_t = state["roundTime"]
    if round_t > 30:
        print(f"WARNING: round time = {round_t:.0f}s. Patches may be partially depleted.")
        print("For clean baseline data, run this within ~30s of round start.")

    # Alien Nest
    nests = [s for s in entities.get("structures", [])
             if "Alien" in s.get("team", "") and "Nest" in (s.get("name") or "")]
    if not nests:
        print("ERROR: No Alien Nest found in current entities.")
        sys.exit(1)
    nest = nests[0]

    # Biotics patches (Alien fuel)
    biotics = [p for p in patches["patches"]
               if "Biotics" in p.get("type", "")]

    # Store both max (for round-start simulation) and current (for
    # mid-round replay). The sim uses "initial" — set that to max so we
    # always simulate a fresh round.
    snapshot = {
        "map": map_name,
        "capturedAtRoundT": round_t,
        "alienNest": {"x": nest["x"], "z": nest["z"]},
        "patches": [
            {"x": p["x"], "z": p["z"], "initial": p["max"],
             "atSnapshot": p["current"], "max": p["max"]}
            for p in biotics
        ],
    }

    out_dir = Path(__file__).parent / "maps"
    out_dir.mkdir(exist_ok=True)
    out_path = out_dir / f"{map_name.lower()}.json"
    out_path.write_text(json.dumps(snapshot, indent=2))

    print(f"Saved {map_name}: nest=({nest['x']:.0f},{nest['z']:.0f})  "
          f"patches={len(biotics)}  totalBiotics={sum(p['current'] for p in biotics):,}")
    print(f"-> {out_path}")


if __name__ == "__main__":
    main()
