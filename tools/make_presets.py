#!/usr/bin/env python3
"""
Generate the shipped configuration presets: configs/presets/<name>.json.

    python tools/make_presets.py            # write all presets
    python tools/make_presets.py --deploy   # ...and copy them to the server's UserData/RTSAI/configs/

Every preset is BASE (the tuned economy, measured 2026-08-11, plus the
former MelonPreferences values) with a handful of overrides, so the presets
differ only where they are meant to and a change to the economy is made in one
place. Each preset starts with a "_readme" that says what it is for; keys
starting with "_" are ignored by the mod.

The mod reads one of these at a time (UserData/RTSAI/state.json names it); pick
it in game with /rtsai, or /rtsai config <name>.
"""

import argparse
import copy
import json
import os
import sys

ROOT    = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUT     = os.path.join(ROOT, "configs", "presets")
SERVER  = r"E:\Steam\steamapps\common\Silica Dedicated Server"
SRV_CFG = os.path.join(SERVER, "UserData", "RTSAI", "configs")

# ---------------------------------------------------------------------------
# The base: a normal served round, alien under the mod, military on, logging on.
# ---------------------------------------------------------------------------
BASE = {
    "enabled": True,
    "commanderLog": True,
    "roundLog": True,
    "factions": {"alien": True, "sol": False, "centauri": False},
    "ecoAssistWithHumanCommander": False,
    "scoutAssistWithHumanCommander": False,
    "lockAlienCommander": False,
    "coopBlockVanillaUnitOrders": True,
    "scout": {"enabled": True, "maxUnits": 20},
    "openerExecute": True,
    "ecoPlannerActive": True,

    "testMode": False,
    "suppressCombat": False,
    "enemyBroke": False,
    "autoResourceDrain": False,
    "configCycle": "off",
    "endRoundAfterMinutes": 40,
    "roundsPerMap": 24,
    "harness": {
        "autoOverrideProduction": False,
        "preventEmptyEndround": True,
        "autoStartRound": False,
        "autoStartTimeoutSeconds": 45,
        "forceAssignAICommanders": True,
        "fakeTeamJoin": False,
        "autoRotateMap": False,
        "shrimpStateSampler": False,
        "mapRotation": "NorthPolarCap,NarakaCity,WhisperingPlains",
        "configId": "v0.94",
    },
    "telemetryPort": 8765,
    "serverFpsCap": 0,
    "perfTimers": False,
    "perfProbes": "",
    "layerSnapshots": True,
    "timeScale": 1,
    "storageBufferCaches": 0,
    "storageBufferAtMinute": 10,
    "storageBufferOffsetM": 150,

    "military": {
        "enabled": True, "execute": True, "produce": True, "offence": True,
        "blockVanillaAttackOrders": True,
        "strengthMargin": 1.5, "homeShare": 0.25, "homeCapShare": 0.5, "homeFloorCash": 12000,
        "maxDefendMissions": 3, "maxProducersPerType": 10, "ecoReserve": 15000, "lesserCystShare": 0.25,
        "pushGrowthFloor": 20.0, "pushMargin": 1.5, "pushRetreatFraction": 0.4, "structureRazeShare": 0.5,
    },
    "mil": {
        "shadow": True, "homeFloorCash": 12000, "forecastHorizonS": 180, "raidShare": 0.25,
        "stagingPatienceS": 45, "fobProducers": 3, "defenceWeight": 1.0, "screenMemoryHalfLifeS": 300,
        "recon": True, "standoffM": 450,
        "spires": {"enabled": True, "execute": True, "cashFloor": 4000, "perSite": 1, "heavyThreat": 2000, "nestByMin": 8},
    },

    # the tuned economy (configs/README.md, "Tuning carried in rtsai.json")
    "maxUnbuiltNodesPerFront": 3, "maxNodeFronts": 5,
    "pileUpMaxPerPatch": 10, "pileUpMaxDetourM": 0,
    "bridgeMode": "loop", "chainExtendPerTick": 2, "chainExtendDemandPerTick": 3,
    "tapReachMode": "measured",
    "blueprintDrivesPhase2": True, "replanIntervalS": 30, "workersByTenMinutes": 100,
    "cystStrategyAuto": True, "maxCystsPerPlan": 4, "cystQueueMax": 1,
    "workerCapPerBioCache": 10, "producerPerSites": 3,
    "openerDoubleCyst": True, "openerTailSeconds": 240,
    "remoteSupplyEnabled": True, "remoteSupplyMaxWalkS": 60,
    "crowdSoften": 0, "openerCrowdSoften": 1,
    "eco": {
        "phase2CystCoverageRadiusM": 250.0, "phase2CystMinClusterPatches": 1,
        "phase2CystTargetFarthestInCluster": False, "phase2MaxUncystedBcQueue": 4, "phase1MinTappedPatches": 2,
    },
    "blueprint": {
        "maxSitesPerPlan": 128, "cystStaffedEnough": 0.8, "cystRelocationSpacings": 3.5,
        "noCystsFromShrimpCount": 180, "persistPlans": True,
    },
}

COOP_MIL_OBSERVE = {
    "enabled": True, "execute": False, "produce": False, "offence": False, "blockVanillaAttackOrders": False,
}

HEADLESS = {
    "testMode": True,
    "lockAlienCommander": True,
    "harness": {"autoStartRound": True, "fakeTeamJoin": True, "autoOverrideProduction": False},
    "endRoundAfterMinutes": 60,
    "roundsPerMap": 24,
    "perfTimers": True,
}

# name -> (readme lines, overrides). Overrides merge recursively into BASE.
PRESETS = {
    "public-play": ([
        "PUBLIC SERVER. Players play; the RTS alien AI commands the aliens only while",
        "nobody holds the alien commander seat, exactly as vanilla's AI would. A player",
        "who takes the seat gets the whole team: every planner stands down.",
        "No harness, no forced round end. Admins: /rtsai (menu), /rtsai off, /rtsai on.",
        "Layer snapshots off, no profiling - the performance settings of the 2026-09-05",
        "Saturday rounds. The frame cap is the game's own MaxFPS (GameSettings.xml).",
    ], {
        "layerSnapshots": False,
    }),

    "coop-eco": ([
        "CO-OP, ECONOMY ONLY. A player holds the alien commander seat and runs the",
        "military; the AI runs the economy underneath them (Bio Caches, Cysts, Nodes,",
        "shrimp assignment). Scouts are NOT recruited - the player keeps every unit.",
        "The military layer only observes and logs ([MISSION]/[BATTALION] intel).",
        "coopBlockVanillaUnitOrders stops Silica's own commander re-tasking units the",
        "players drive, by order origin, so player orders are never caught by it.",
    ], {
        "ecoAssistWithHumanCommander": True,
        "scout": {"enabled": False},
        "military": COOP_MIL_OBSERVE,
        "mil": {"spires": {"execute": False}},
        "maxUnbuiltNodesPerFront": 1, "maxNodeFronts": 10,
    }),

    "coop-eco-scout": ([
        "CO-OP, ECONOMY + SCOUTING. As coop-eco, plus ScoutPlanner builds and sweeps",
        "up to 20 Crabs/Squids for the human commander so Bio Cache sites get revealed",
        "(they are fog-gated). The player may still re-task a scout; the planner takes",
        "it back on its next waypoint.",
    ], {
        "ecoAssistWithHumanCommander": True,
        "scoutAssistWithHumanCommander": True,
        "scout": {"enabled": True, "maxUnits": 20},
        "military": COOP_MIL_OBSERVE,
        "mil": {"spires": {"execute": False}},
        "maxUnbuiltNodesPerFront": 1, "maxNodeFronts": 10,
    }),

    "coop-ai-commander": ([
        "CO-OP, FULL AI (eco + scouts + military + spires). The AI HOLDS the alien",
        "commander seat and anyone who takes it is returned to the field within a",
        "second (lockAlienCommander). Players play aliens as units alongside the bot.",
        "This is the only way to get the military layer in co-op: every military",
        "planner stands down while a human holds the seat, by design, so the AI never",
        "fights a player for the same units.",
    ], {
        "lockAlienCommander": True,
    }),

    "headless-alien-only": ([
        "HEADLESS TEST: alien under the mod, Sol and Centauri on VANILLA AI. The",
        "harness auto-joins a fake client, force-ends each round at 60 minutes and",
        "reloads the map (24 rounds per map). Combat on. VersusAutoSelectMode in",
        "MelonPreferences.cfg [Silica] decides 2-way or 3-way (server stopped to edit).",
    ], dict(HEADLESS, **{"harness": dict(HEADLESS["harness"], configId="headless-alien-only")})),

    "headless-all-factions": ([
        "HEADLESS TEST: every faction under the mod (alien, Sol and Centauri). Same",
        "harness as headless-alien-only. The human economy is HumanConstruction, the",
        "military layer is shared.",
    ], dict(HEADLESS, **{
        "factions": {"alien": True, "sol": True, "centauri": True},
        "harness": dict(HEADLESS["harness"], configId="headless-all-factions"),
    })),

    "headless-eco-only": ([
        "HEADLESS ECO SOAK, the baseline every economy benchmark was measured on.",
        "No military at all (military.enabled false), combat suppressed, humans",
        "bankrupted every second (enemyBroke) so the alien survives to the 1500 s",
        "checkpoint. 25 minutes per round - changing it breaks comparability with",
        "every earlier benchmark row.",
    ], dict(HEADLESS, **{
        "suppressCombat": True, "enemyBroke": True,
        "endRoundAfterMinutes": 25,
        "military": {"enabled": False, "execute": False, "produce": False, "offence": False, "blockVanillaAttackOrders": False},
        "mil": {"spires": {"execute": False}},
        "harness": dict(HEADLESS["harness"], configId="headless-eco-only"),
    })),

    "off": ([
        "MOD OFF. Nothing runs: no planners, no Harmony patch acts, no round log, no",
        "telemetry, no time scale. Vanilla commands every team. Select this to park the",
        "mod without removing the DLL; /rtsai on overrides it from chat.",
        "serverFpsCap is a TEST knob (fps-drop correlation runs). 0 here, so the",
        "game's own MaxFPS (GameSettings.xml / console 'MaxFPS <n>') decides.",
    ], {
        "enabled": False,
    }),
}


def merge(dst, src):
    for k, v in src.items():
        if isinstance(v, dict) and isinstance(dst.get(k), dict):
            merge(dst[k], v)
        else:
            dst[k] = copy.deepcopy(v)
    return dst


def build(name):
    readme, over = PRESETS[name]
    cfg = {"_readme": [f"{name} - generated by tools/make_presets.py; edit there, not here."] + readme}
    body = copy.deepcopy(BASE)
    merge(body, over)
    cfg.update(body)
    return cfg


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--deploy", action="store_true", help="also copy into the server's UserData/RTSAI/configs/")
    args = ap.parse_args()

    os.makedirs(OUT, exist_ok=True)
    for name in PRESETS:
        path = os.path.join(OUT, name + ".json")
        with open(path, "w", encoding="utf-8", newline="\n") as f:
            json.dump(build(name), f, indent=2)
            f.write("\n")
        print("wrote", os.path.relpath(path, ROOT))

    if args.deploy:
        os.makedirs(SRV_CFG, exist_ok=True)
        for name in PRESETS:
            src = os.path.join(OUT, name + ".json")
            dst = os.path.join(SRV_CFG, name + ".json")
            with open(src, "rb") as a, open(dst, "wb") as b:
                b.write(a.read())
            print("deployed", dst)
    return 0


if __name__ == "__main__":
    sys.exit(main())
