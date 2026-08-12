#!/usr/bin/env python3
"""
Unit stats and WEAPON REACH, read from the live balance dump.

WHY THIS FILE EXISTS AND WHY IT IS NOT A TABLE OF CONSTANTS
-----------------------------------------------------------
Two facts make a hard-coded stat table wrong here, and both were pointed out by
DrMuck after a first pass got them wrong:

1. `atk_range` IS NOT THE WEAPON'S REACH. It is the distance at which the AI
   starts aiming and firing — `CreatureAttack.AttackProjectileAimDistMax`, the
   firing-range cap. The distance a shot actually covers is set by the
   projectile: its speed, its lifetime, and how fast drag and gravity take it
   down. A Shocker has `atk_range = 400` and a projectile that lives 0.25 s at
   800 m/s, so it cannot touch anything past ~200 m. Reading the range field as
   reach overstates half the roster by 2x and the aliens worst of all.

2. WE MOD THESE VALUES. Si_UnitBalance rewrites cost, health, damage, speed,
   projectile speed and lifetime, and it pushes changes between sessions. So a
   table baked into this repo is stale the moment DrMuck pushes a rebalance,
   and — worse — it is stale for the ARCHIVE too: a replay from March was
   played under different numbers than one from August.

The consequence for the model is the important part, and it is a design
decision rather than an implementation detail:

    FIT DIMENSIONLESS BEHAVIOUR, READ ABSOLUTE PHYSICS.

Anything the fitting stage learns from replays must be a RATIO — engagement
distance as a fraction of reach, share of a force that is actually shooting,
cash traded per cash lost. Those describe how the game's AI behaves and should
survive a rebalance. Everything absolute — hp, damage, cooldown, reach, speed —
is read from the dump at the time it is needed, in-game from the live values.
Rebalance the game and the model recalibrates itself instead of lying.

REACH, DERIVED
--------------
Three cases, and the first one is a trap that Si_UnitBalance's own notes flag:

  instant hit   `base_speed` IS THE RAYCAST DISTANCE, not a speed, and lifetime
                is the visual beam duration only. Behemoth's ray: reach 400.
  ballistic     integrate the projectile — drag and gravity — until it expires
                (`lifetime`), drops below `min_speed_pct` of launch speed, or
                returns to launch height. Ground range is what comes out.
  melee         no projectile: reach is contact, taken as 0 and flagged.

and then the weapon can only be used where the AI will pull the trigger:

    effective_reach = min(aim_cap, ballistic_reach)

`aim_cap` is `atk_range` for creatures and `target_dist` (Sensor targeting
distance) for vehicles.

THE DRAG CONSTANT IS FITTED, NOT ASSUMED
----------------------------------------
The dump gives `drag_coeff`, `mass` and `diameter` but not the units the engine
works in, so the deceleration is written as

    a_drag = -kappa * Cd * A * v^2 / m       A = pi (diameter/2)^2

with one unknown scale `kappa` shared by every projectile in the game. It is
calibrated in `calibrate_drag.py` against the longest kills actually observed in
the replay archive: reach must COVER the observed maximum for every unit, and
the best kappa is the one that does so with the least slack. A naive
`speed x lifetime` bound and a linear-drag bound are both kept so the fit can be
argued with rather than believed — the linear law already fails visibly on the
Barrage Truck, which is why it is not the default.

    python mil_sim/unit_stats.py            # print the reach table
"""

import csv
import io
import json
import math
import os

SERVER = r"E:\Steam\steamapps\common\Silica Dedicated Server"
BALANCE_DIR = os.path.join(SERVER, "UserData", "UnitBalance_cfg")
DUMP = os.path.join(BALANCE_DIR, "Si_UnitBalance_Dump.json")
PROJ = os.path.join(BALANCE_DIR, "ProjectileData_Dump.csv")
VERSION = os.path.join(BALANCE_DIR, "game_version.txt")

# Gravity the engine applies at gravity_scale 1. Unity default.
G = 9.81

# Air density, so the drag term is the ordinary physical one and `kappa` below
# is a pure correction rather than a unit conversion in disguise.
RHO = 1.225

# `diameter` in the projectile dump is a CALIBRE IN MILLIMETRES, not a metre
# measurement: a Railgun round is 120, a Shocker bolt is 10, an Acidball 50.
# Taking it as metres inflates the frontal area by a factor of a million and
# stops every shell dead inside ten metres, which is how the first run produced
# a Crimson Tank with two metres of reach.
MM_TO_M = 0.001

# Shared drag scale, fitted in calibrate_drag.py against the longest kills in
# the archive. 1.0 is the plain physical form.
KAPPA = 1.0


def _f(row, key, default=0.0):
    v = row.get(key, default)
    if v in (None, "", "None"):
        return default
    if isinstance(v, str):
        if v in ("True", "False"):
            return v == "True"
        try:
            return float(v)
        except ValueError:
            return default
    return v


def load_projectiles(path=PROJ):
    """name -> projectile row, values coerced."""
    out = {}
    with io.open(path, encoding="utf-8-sig", newline="") as fh:
        for row in csv.DictReader(fh):
            out[row["name"]] = {
                "name": row["name"],
                "instant_hit": _f(row, "instant_hit") is True,
                "base_speed": _f(row, "base_speed"),
                "min_speed_pct": _f(row, "min_speed_pct", 0.01),
                "lifetime": _f(row, "lifetime"),
                "drag_coeff": _f(row, "drag_coeff"),
                "gravity_scale": _f(row, "gravity_scale"),
                "mass": _f(row, "mass", 1.0) or 1.0,
                "diameter": _f(row, "diameter", 1.0) or 1.0,
                "impact_damage": _f(row, "impact_damage"),
                "splash": _f(row, "splash") is True,
                "splash_dmg_max": _f(row, "splash_dmg_max"),
                "splash_radius_max": _f(row, "splash_radius_max"),
                "penetrating_damage": _f(row, "penetrating_damage"),
            }
    return out


def _flight(p, theta, kappa, dt):
    """Ground distance for one shot launched at `theta` radians."""
    v0 = p["base_speed"]
    vx, vy = v0 * math.cos(theta), v0 * math.sin(theta)
    x, y = 0.0, 0.0
    g = G * p["gravity_scale"]

    radius = p["diameter"] * MM_TO_M / 2.0
    area = math.pi * radius * radius
    k = kappa * 0.5 * RHO * p["drag_coeff"] * area / p["mass"]   # a = k v^2
    v_floor = v0 * p["min_speed_pct"]

    t = 0.0
    while t < p["lifetime"]:
        v = math.hypot(vx, vy)
        if v <= v_floor:
            break
        a = k * v * v
        vx -= a * (vx / v) * dt
        vy -= (a * (vy / v) + g) * dt
        x += vx * dt
        y += vy * dt
        t += dt
        if y < 0.0 and theta > 0.0:        # arced back down to launch height
            break
        if g > 0 and theta == 0.0 and y < -50.0:
            break                          # flat shot has fallen off the world
    return x


def ballistic_range(p, kappa=KAPPA, dt=0.005):
    """
    How far a shot can reach, in metres — an ENVELOPE, not a typical shot.

    The shot ends at whichever comes first: lifetime expiry, speed falling under
    `min_speed_pct` of launch, or the round arcing back to launch height.

    Launch angle is SEARCHED from flat to 45 degrees and the best taken, because
    reach is meant to answer "how far could this weapon possibly hit" — a lobbed
    rocket picks the angle that carries; a flat-firing shell is at its best
    level. Searching costs nothing and removes a guess about which weapons the
    game treats as artillery.
    """
    if p["instant_hit"]:
        # For instant-hit data `base_speed` IS the raycast distance, and
        # lifetime is only how long the beam is drawn. Si_UnitBalance scales
        # base_speed for range on these and lifetime for range on the others.
        return p["base_speed"]

    if p["base_speed"] <= 0 or p["lifetime"] <= 0:
        return 0.0

    if p["gravity_scale"] <= 0:
        return _flight(p, 0.0, kappa, dt)          # no drop: flat is optimal

    best = 0.0
    for deg in range(0, 46, 5):
        best = max(best, _flight(p, math.radians(deg), kappa, dt))
    return best


def _weapon(name, proj_name, projectiles, aim_cap, damage, cooldown, spread,
            speed_override=0.0, lifetime_override=0.0, kappa=KAPPA):
    """
    One weapon's reach and damage, with the unit-level overrides applied.

    The unit dump carries per-unit projectile speed and lifetime because
    Si_UnitBalance scales them per unit; those beat the shared projectile row.
    """
    p = projectiles.get(proj_name)
    if p is None:
        # No projectile data: melee, or a weapon whose data was not dumped.
        return {
            "name": name, "proj": proj_name or "", "melee": not proj_name,
            "reach": 0.0, "ballistic": 0.0, "naive": 0.0, "aim_cap": aim_cap,
            "damage": damage, "cooldown": cooldown, "spread": spread,
            "instant": False,
        }

    p = dict(p)
    if speed_override:
        p["base_speed"] = speed_override
    if lifetime_override:
        p["lifetime"] = lifetime_override

    ball = ballistic_range(p, kappa=kappa)
    naive = p["base_speed"] if p["instant_hit"] else p["base_speed"] * p["lifetime"]
    reach = min(aim_cap, ball) if aim_cap > 0 else ball

    dmg = damage or p["impact_damage"]
    return {
        "name": name, "proj": proj_name, "melee": False,
        "reach": reach, "ballistic": ball, "naive": naive, "aim_cap": aim_cap,
        "damage": dmg, "cooldown": cooldown, "spread": spread,
        "instant": p["instant_hit"],
        "splash_dmg": p["splash_dmg_max"] if p["splash"] else 0.0,
        "splash_radius": p["splash_radius_max"] if p["splash"] else 0.0,
    }


def load_units(dump=DUMP, proj=PROJ, kappa=KAPPA):
    """
    name -> unit record with a weapon list, reach and a sustained DPS estimate.

    Creatures carry their weapons on `atk*` fields; vehicles on `vt*`. Both are
    read, because a few units have one of each and the roster mixes freely.
    """
    projectiles = load_projectiles(proj)
    with io.open(dump, encoding="utf-8-sig") as fh:
        data = json.load(fh)

    units = {}
    for u in data["units"]:
        name = u["name"]
        weapons = []

        # --- creature attacks: aim cap is atk_range -----------------------
        # `atk_damage` is the MELEE number and reads 0 on a ranged creature —
        # a Defiler's swarm damage sits in `proj_impact_dmg`. Taking atk_damage
        # alone files every ranged alien as melee, which is what the first run
        # did to the Defiler.
        for idx, pre in ((1, "atk"), (2, "atk2")):
            sfx = "" if idx == 1 else "2"
            pj = u.get(f"{pre}_proj") or ""
            cap = _f(u, f"{pre}_range")
            melee_dmg = _f(u, f"{pre}_damage")
            proj_dmg = (_f(u, f"proj{sfx}_impact_dmg")
                        + _f(u, f"proj{sfx}_splash_dmg"))
            dmg = proj_dmg if pj and proj_dmg else melee_dmg
            if not dmg and not pj:
                continue
            weapons.append(_weapon(
                f"atk{idx}", pj, projectiles, cap, dmg,
                _f(u, f"{pre}_cooldown", 1.0), _f(u, f"{pre}_spread"),
                _f(u, f"proj{sfx}_speed"), _f(u, f"proj{sfx}_lifetime"),
                kappa))

        # --- vehicle turrets: aim cap is the sensor's target distance ------
        # Three turret slots, not two: a Combat Tank's vt3 is a separate gun
        # with its own magazine, and stopping at vt2 loses it.
        veh_cap = _f(u, "target_dist")
        for idx, pre in ((1, "vt"), (2, "vt2"), (3, "vt3")):
            pj = u.get(f"{pre}_proj") or ""
            dmg = _f(u, f"{pre}_impact_dmg")
            if not pj and not dmg:
                continue
            # rate of fire: interval between shots, magazine and reload
            interval = _f(u, f"{pre}_fire_interval", 1.0) or 1.0
            mag = _f(u, f"{pre}_magazine")
            reload_s = _f(u, f"{pre}_reload")
            shots = _f(u, f"{pre}_shot_count", 1.0) or 1.0
            cooldown = interval
            if mag > 0 and reload_s > 0:
                cooldown = (mag * interval + reload_s) / mag     # sustained
            w = _weapon(
                f"{pre}", pj, projectiles, veh_cap, dmg, cooldown,
                _f(u, f"{pre}_spread"),
                _f(u, f"{pre}_proj_speed"), _f(u, f"{pre}_proj_lifetime"),
                kappa)
            w["damage"] = (dmg or w["damage"]) * shots
            w["pen_dmg"] = _f(u, f"{pre}_pen_dmg")
            w["splash_dmg"] = _f(u, f"{pre}_splash_dmg")
            weapons.append(w)

        ranged = [w for w in weapons if w["reach"] > 1.0]
        best = max(ranged, key=lambda w: w["reach"], default=None)
        dps = sum(w["damage"] / w["cooldown"] for w in weapons
                  if w["cooldown"] > 0)

        units[name] = {
            "name": name,
            "faction": u.get("faction", ""),
            "is_structure": bool(u.get("is_structure")),
            "cost": _f(u, "cost"),
            "build_time": _f(u, "build_time"),
            "hp": _f(u, "hp"),
            "cap_type": u.get("unit_cap_type", ""),
            "cap": _f(u, "unit_cap_value"),
            "move_speed": _f(u, "move_speed") or _f(u, "run_speed"),
            "fow_view": _f(u, "fow_view"),
            "aim_cap": max(_f(u, "atk_range"), _f(u, "target_dist")),
            "reach": best["reach"] if best else 0.0,
            "reach_source": best["name"] if best else "melee",
            "melee_only": not ranged,
            "dps": dps,
            "weapons": weapons,
            "built_at": u.get("built_at", ""),
            "min_tier": _f(u, "min_tier", -1),
        }
    return units


# SRPL writes the name the game DISPLAYS; the balance dump keys on the asset's
# name, and for the two units both human factions share the dump is prefixed.
# Left unresolved these read cost 0, which silently deflates the human side of
# every fight they stand in — a Refinery-adjacent census would lose a 1500-cash
# Harvester and call it free.
_TEAM_PREFIX = {"Sol": "Sol", "Centauri": "Cent"}


def resolve(units, type_name, team_name=""):
    """Balance-dump record for a replay's type name, or None."""
    u = units.get(type_name)
    if u is not None:
        return u
    pre = _TEAM_PREFIX.get(team_name)
    if pre:
        return units.get(f"{pre} {type_name}")
    return None


def balance_epoch():
    """Version string of the balance the dump was written under."""
    try:
        with io.open(VERSION, encoding="utf-8-sig") as fh:
            v = fh.read().strip()
    except OSError:
        v = "?"
    try:
        stamp = os.path.getmtime(DUMP)
    except OSError:
        stamp = 0
    return {"game_version": v, "dump_mtime": stamp}


def main():
    units = load_units()
    ep = balance_epoch()
    print(f"balance epoch: {ep['game_version']}  ({len(units)} units)\n")
    hdr = (f"{'unit':<20}{'fac':<9}{'cost':>6}{'hp':>7}{'spd':>6}"
           f"{'aim_cap':>9}{'ballistic':>11}{'reach':>8}{'naive':>8}  weapon")
    print(hdr)
    print("-" * len(hdr))
    rows = [u for u in units.values() if not u["is_structure"] and u["cost"] > 0]
    for u in sorted(rows, key=lambda r: (-r["reach"], r["name"])):
        w = next((w for w in u["weapons"] if w["name"] == u["reach_source"]), None)
        tag = "MELEE" if u["melee_only"] else (w["proj"] if w else "")
        print(f"{u['name']:<20}{u['faction']:<9}{u['cost']:>6.0f}{u['hp']:>7.0f}"
              f"{u['move_speed']:>6.1f}{u['aim_cap']:>9.0f}"
              f"{(w['ballistic'] if w else 0):>11.0f}{u['reach']:>8.0f}"
              f"{(w['naive'] if w else 0):>8.0f}  {tag}")


if __name__ == "__main__":
    main()
