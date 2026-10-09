#!/usr/bin/env python3
"""
Make a configuration the server's active one, from outside the game.

    python tools/select_config.py <name>          # preset from configs/presets/ or an existing server config
    python tools/select_config.py --list

Copies configs/presets/<name>.json to <server>/UserData/RTSAI/configs/ when the
repo has it (so the server always runs the committed version), then points
UserData/RTSAI/state.json at it. The mod re-reads the active config at every
map load; a running server picks it up at the next round. In game the same
thing is /rtsai config <name>.
"""
import json, os, sys

ROOT    = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
PRESETS = os.path.join(ROOT, "configs", "presets")
SERVER  = os.environ.get("SILICA_SERVER", r"E:\Steam\steamapps\common\Silica Dedicated Server")
RTSAI   = os.path.join(SERVER, "UserData", "RTSAI")
CFGDIR  = os.path.join(RTSAI, "configs")
STATE   = os.path.join(RTSAI, "state.json")


def server_configs():
    if not os.path.isdir(CFGDIR):
        return []
    return sorted(f[:-5] for f in os.listdir(CFGDIR) if f.endswith(".json") and not f.startswith("_"))


def main(argv):
    if len(argv) < 2 or argv[1] in ("-h", "--help"):
        print(__doc__); return 2
    if argv[1] == "--list":
        state = {}
        if os.path.exists(STATE):
            state = json.load(open(STATE, encoding="utf-8"))
        for n in server_configs():
            print(("* " if n == state.get("activeConfig") else "  ") + n)
        return 0
    name = argv[1]
    os.makedirs(CFGDIR, exist_ok=True)
    src = os.path.join(PRESETS, name + ".json")
    if os.path.exists(src):
        with open(src, "rb") as a, open(os.path.join(CFGDIR, name + ".json"), "wb") as b:
            b.write(a.read())
    elif name not in server_configs():
        print(f"no preset {src} and no {name}.json on the server"); return 1
    state = {}
    if os.path.exists(STATE):
        try: state = json.load(open(STATE, encoding="utf-8"))
        except Exception: state = {}
    state["activeConfig"] = name
    state.setdefault("_readme", "Written by Si_RTS_AI. activeConfig names a file in configs/; the other keys are chat overrides (absent = the config decides).")
    with open(STATE, "w", encoding="utf-8", newline="\n") as f:
        json.dump(state, f, indent=2); f.write("\n")
    print(f"active config -> {name} ({STATE})")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
