"""
Background poller: watches the running Silica server and snapshots each new
map it sees to maps/<mapname>.json. Snapshots use `max` amounts for the
"initial" field so patches represent fresh-round state regardless of when
the poller happens to see the map (see fetch_map.py).

Run this in the background and let the map rotation cycle through maps —
after a few hours you have data on every map the server rotates through.
Skips maps we've already saved (unless FORCE_REFRESH is set).
"""

import json
import time
import urllib.request
from pathlib import Path

HOST = "http://localhost:8765"
POLL_S = 30
FORCE_REFRESH = False   # set True to overwrite existing snapshots

MAPS_DIR = Path(__file__).parent / "maps"
MAPS_DIR.mkdir(exist_ok=True)


def fetch(endpoint):
    try:
        with urllib.request.urlopen(f"{HOST}{endpoint}", timeout=5) as r:
            return json.load(r)
    except Exception:
        return None


def snapshot_current(state, patches, entities):
    map_name = state["map"]
    round_t = state["roundTime"]

    nests = [s for s in entities.get("structures", [])
             if "Alien" in s.get("team", "") and "Nest" in (s.get("name") or "")]
    if not nests:
        return None
    nest = nests[0]

    biotics = [p for p in patches["patches"] if "Biotics" in p.get("type", "")]
    if not biotics:
        return None

    return {
        "map": map_name,
        "capturedAtRoundT": round_t,
        "alienNest": {"x": nest["x"], "z": nest["z"]},
        "patches": [
            {"x": p["x"], "z": p["z"], "initial": p["max"],
             "atSnapshot": p["current"], "max": p["max"]}
            for p in biotics
        ],
    }


def already_snapshotted(map_name):
    if FORCE_REFRESH:
        return False
    return (MAPS_DIR / f"{map_name.lower()}.json").exists()


def main():
    print(f"Polling {HOST} every {POLL_S}s. Ctrl+C to stop.")
    last_map = None
    while True:
        try:
            state = fetch("/state")
            if not state:
                time.sleep(POLL_S)
                continue
            map_name = state["map"]
            if map_name != last_map:
                print(f"[{time.strftime('%H:%M:%S')}] map={map_name} t={state['roundTime']:.0f}s "
                      f"(saved={already_snapshotted(map_name)})")
                last_map = map_name

            if not already_snapshotted(map_name):
                patches = fetch("/patches")
                entities = fetch("/entities")
                if patches and entities:
                    snap = snapshot_current(state, patches, entities)
                    if snap:
                        out = MAPS_DIR / f"{map_name.lower()}.json"
                        out.write_text(json.dumps(snap, indent=2))
                        tot = sum(p["initial"] for p in snap["patches"])
                        print(f"  -> saved {map_name}: {len(snap['patches'])} patches, {tot:,} biotics")
        except KeyboardInterrupt:
            break
        except Exception as e:
            print(f"  ! error: {e}")
        time.sleep(POLL_S)


if __name__ == "__main__":
    main()
