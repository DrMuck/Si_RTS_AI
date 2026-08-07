#!/usr/bin/env python3
"""
Mine every .srpl replay for who killed what.

WHY THIS BEATS THE HAND-AUTHORED TABLE
--------------------------------------
UNIT_ROLES.xlsx was built to be filled in by hand, because the game's data says
what a Firebug costs and not what it is for. Then DrMuck: "I have a lot of game
logs! So we can already get the best weighting for unit formations there."

He is right, and the format carries it: SRPL records
`(tick, victim_id, attacker_id, is_building)` for every destruction, and the
entity table gives both ids a type and a team. So "what kills what" is written
down rather than estimated.

TWO THINGS DECIDE WHICH REPLAYS ARE WORTH READING, and both were found by
looking rather than assumed.

UNIT kills are missing from 2026-07-11 to 2026-08-01. A game update broke the
readout and it was fixed on 2026-08-02 (Si_ReplayLogging was rebuilt that day).
Structure kills were written throughout, so a file from the window looks healthy
and is useless for composition: 20260801_200736_NarakaCity is a real 50-minute
match with 18 players and 212 destructions, every one of them a structure. Those
replays are skipped by default and counted, because silently reading zero rows
out of three weeks of matches is exactly the kind of quiet failure this project
keeps paying for.

MOST REPLAYS ARE TEST MATCHES. Of 760 usable files before the window, 569 have a
single player and 695 are short or empty; 65 have four or more distinct players
and run past ten minutes, and those 65 hold 32,877 unit kills between them. The
--min-players filter is what separates a real engagement from me soaking the
economy alone, and the default is not 0.

This does not replace judgement, it aims it. Counts say a Behemoth kills
Refineries; only a player can say whether that is because a Behemoth is a siege
unit or because Behemoths are what happens to be alive by the time anyone
reaches a Refinery. The Confidence column in the sheet exists for that.

WHAT IT DOES NOT SEE
--------------------
Damage that did not kill. A unit that softens a target and dies before the last
hit is credited nothing while the finisher takes the row, so a pure duel matrix
overstates whatever arrives last and understates screens and artillery. Read the
value columns, not just the counts.

Attribution is also missing on some rows — 12 of 196 in one sampled file — where
the killer had already despawned or the field was never written. Those become
attacker "?" and are counted separately rather than dropped silently.

    python tools/mine_replay_kills.py [--limit N] [--since YYYYMMDD]
                                      [--min-players N] [--include-broken-window]

Writes analysis/kills.csv        one row per destruction
       analysis/matchups.csv     attacker type x victim type, with duel ratios
       analysis/summary.md       the part worth reading
"""

import argparse
import csv
import collections
import glob
import json
import os
import sys
import time

SERVER = r"E:\Steam\steamapps\common\Silica Dedicated Server"
REPLAYS = os.path.join(SERVER, "UserData", "ReplayLogs")
MAPREPLAY = os.path.join(SERVER, "Mod MapReplay")
BALANCE = os.path.join(SERVER, "UserData", "UnitBalance_cfg", "Si_UnitBalance_Dump.json")

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUTDIR = os.path.join(ROOT, "analysis")

sys.path.insert(0, os.path.join(MAPREPLAY, "modules"))
try:
    from srpl_reader import parse_srpl
except ImportError:
    sys.exit(f"cannot import srpl_reader from {MAPREPLAY}/modules")

# Neutral parties. Wildlife and the Great Worm kill plenty and tell us nothing
# about how two armies trade, so they are tagged and excluded from the matrix
# rather than quietly inflating every alien row.
NEUTRAL_TEAMS = {"Wildlife", "GM", "Unknown"}

# Unit destructions were not written between these dates. Structure destructions
# were, so the files parse cleanly and simply contain no duels.
BROKEN_FROM, BROKEN_TO = "20260711", "20260801"


def load_costs():
    """Unit costs, so an exchange can be read in cash and not only in bodies."""
    try:
        with open(BALANCE, encoding="utf-8-sig") as fh:
            data = json.load(fh)
    except OSError:
        print("  ! no balance dump — value columns will be zero")
        return {}, {}
    cost, is_structure = {}, {}
    for u in data.get("units", []):
        cost[u["name"]] = u.get("cost") or 0
        is_structure[u["name"]] = bool(u.get("is_structure"))
    return cost, is_structure


def pilot_flag(replay, entity_id, tick, entity):
    """
    Was a human driving it when it scored?

    IT MATTERS MORE THAN IT LOOKS. Silica lets a player take direct control of a
    unit the commander built, and a piloted Behemoth is a different weapon from
    the same Behemoth on commander AI — better aim, better retreats, and none of
    it available to the bot. Weighting the bot's compositions on rows a human
    piloted would tune it toward things it cannot actually do.

    Control is read at the tick of the kill rather than at registration, because
    a unit grabbed mid-fight registers as AI-controlled.
    """
    if entity is None:
        return "?"
    try:
        return "Y" if replay.get_controller_at_tick(entity_id, tick) else "N"
    except Exception:
        return "Y" if entity.controller_id else "N"


def distinct_players(replay):
    """Named humans who were present. The discriminator between a match and a
    soak: a test round has one player or none."""
    return len({n for _, n in replay.players.values() if n and n.strip()})


def mine(paths, cost, min_players):
    rows = []
    bad = 0
    selfkills = 0
    thin = 0
    for n, path in enumerate(paths, start=1):
        base = os.path.basename(path)
        try:
            r = parse_srpl(path)
        except Exception:
            bad += 1
            continue

        if distinct_players(r) < min_players:
            thin += 1
            continue

        stamp = base.split("_")[0]
        map_name = getattr(r, "map_name", "") or "?"
        tick_s = (getattr(r, "tick_interval_ms", 2000) or 2000) / 1000.0

        for tick, vid, aid, is_building in r.destructions:
            v = r.entities.get(vid)
            if v is None:
                continue                      # victim unknown: nothing to learn
            # SELF-ATTRIBUTION IS A DESPAWN, NOT A KILL. A unit removed by
            # something other than combat is written with itself as its own
            # attacker. On a 60-replay soak sample that was 2,410 of 4,020 rows —
            # Scout kills Scout 354 times, Militia kills Militia 354 times, which
            # is the EnemyBroke starter-unit purge, exactly once per round per
            # unit type. Left in, it would have been the largest single "matchup"
            # in the archive.
            if aid == vid:
                selfkills += 1
                continue

            a = r.entities.get(aid)
            rows.append({
                "date": stamp,
                "map": map_name,
                "replay": base,
                "t_s": round(tick * tick_s),
                "attacker": a.type_name if a else "?",
                "attacker_team": a.team_name if a else "?",
                # Control AT THE MOMENT OF THE KILL, not at registration. A
                # unit a player grabbed mid-fight registers as AI-controlled and
                # would be filed under the bot's record otherwise.
                "attacker_piloted": pilot_flag(r, aid, tick, a),
                "victim": v.type_name,
                "victim_team": v.team_name,
                "victim_is_building": "Y" if is_building else "N",
                "attacker_cost": cost.get(a.type_name, 0) if a else 0,
                "victim_cost": cost.get(v.type_name, 0),
            })

        if n % 100 == 0:
            print(f"  {n}/{len(paths)} files, {len(rows)} kills", flush=True)

    if bad:
        print(f"  {bad} files could not be parsed")
    if selfkills:
        print(f"  {selfkills:,} self-attributed rows dropped (despawns, purges)")
    if thin:
        print(f"  {thin:,} replays skipped with fewer than {min_players} players")
    return rows


def write_kills(rows):
    path = os.path.join(OUTDIR, "kills.csv")
    with open(path, "w", newline="", encoding="utf-8") as fh:
        w = csv.DictWriter(fh, fieldnames=list(rows[0].keys()))
        w.writeheader()
        w.writerows(rows)
    return path


def build_matchups(rows, is_structure):
    """
    Unit against unit only. A row where either side is a structure answers a
    different question — what sieges well, what a base loses first — and mixing
    the two would let "Behemoth kills Refinery" outvote every real duel in the
    archive.
    """
    duels = collections.Counter()
    for r in rows:
        if r["victim_is_building"] == "Y":
            continue
        a, v = r["attacker"], r["victim"]
        if a == "?" or is_structure.get(a) or is_structure.get(v):
            continue
        if r["attacker_team"] in NEUTRAL_TEAMS or r["victim_team"] in NEUTRAL_TEAMS:
            continue
        if r["attacker_team"] == r["victim_team"]:
            continue                          # friendly fire is not a matchup
        duels[(a, v)] += 1

    path = os.path.join(OUTDIR, "matchups.csv")
    with open(path, "w", newline="", encoding="utf-8") as fh:
        w = csv.writer(fh)
        w.writerow(["attacker", "victim", "kills", "reverse_kills", "duel_ratio",
                    "value_taken", "value_lost", "value_ratio"])
        for (a, v), n in duels.most_common():
            rev = duels.get((v, a), 0)
            w.writerow([a, v, n, rev,
                        round(n / rev, 2) if rev else "",
                        "", "", ""])
    return path, duels


def summarise(rows, duels, cost, is_structure, elapsed, n_files):
    lines = []
    add = lines.append

    kills = len(rows)
    unattributed = sum(1 for r in rows if r["attacker"] == "?")
    piloted = sum(1 for r in rows if r["attacker_piloted"] == "Y")
    dates = sorted({r["date"] for r in rows})
    maps = collections.Counter(r["map"] for r in rows)

    add("# What kills what — mined from the replay archive\n")
    add(f"{n_files} replays, {kills:,} destructions, {dates[0]} to {dates[-1]}, "
        f"parsed in {elapsed:.0f}s.\n")
    add(f"- **{unattributed:,} ({unattributed/kills:.0%}) have no attacker recorded** — the killer had "
        "already despawned, or the field was never written. Counted, not dropped.")
    add(f"- **{piloted:,} ({piloted/kills:.0%}) were killed by a player-piloted unit.** The rest are "
        "commander-controlled, which is the case the bot is actually in.")
    add(f"- maps: " + ", ".join(f"{m} {n:,}" for m, n in maps.most_common(6)) + "\n")

    # Per-side, what the aliens die to and what they kill. This is the question
    # the production rule needs answered.
    add("\n## What kills alien units\n")
    add("| Their unit | Alien losses to it | Alien value lost |")
    add("|---|---:|---:|")
    killers = collections.Counter()
    lost_value = collections.Counter()
    for r in rows:
        if r["victim_team"] != "Alien" or r["victim_is_building"] == "Y":
            continue
        if is_structure.get(r["victim"]):
            continue
        killers[r["attacker"]] += 1
        lost_value[r["attacker"]] += r["victim_cost"]
    for name, n in killers.most_common(15):
        add(f"| {name} | {n:,} | {lost_value[name]:,} |")

    add("\n## What alien units kill\n")
    add("| Alien unit | Kills | Enemy value destroyed |")
    add("|---|---:|---:|")
    scored = collections.Counter()
    taken = collections.Counter()
    for r in rows:
        if r["attacker_team"] != "Alien" or r["attacker"] == "?":
            continue
        scored[r["attacker"]] += 1
        taken[r["attacker"]] += r["victim_cost"]
    for name, n in scored.most_common(15):
        add(f"| {name} | {n:,} | {taken[name]:,} |")

    add("\n## Duels that ran both ways\n")
    add("Pairs where each killed the other at least five times, so the ratio is not one lucky engagement. "
        "Ratio above 1 means the attacker column wins the exchange.\n")
    add("| Attacker | Victim | A kills V | V kills A | Ratio |")
    add("|---|---|---:|---:|---:|")
    seen = set()
    pairs = []
    for (a, v), n in duels.items():
        if (v, a) in seen:
            continue
        rev = duels.get((v, a), 0)
        if n >= 5 and rev >= 5:
            pairs.append((a, v, n, rev, n / rev))
        seen.add((a, v))
    for a, v, n, rev, ratio in sorted(pairs, key=lambda x: -x[4])[:30]:
        add(f"| {a} | {v} | {n} | {rev} | {ratio:.2f} |")

    add("\n## Read this before trusting it\n")
    add("- **Only the killing blow is credited.** A unit that does most of the damage and dies before "
        "the last hit scores nothing. Screens, artillery and anything that trades itself to open a "
        "fight are systematically understated here.")
    add("- **These are the games that were played, not a designed experiment.** If nobody built Scorpions, "
        "Scorpions look neutral rather than bad.")
    add("- **Structures are excluded from the duel matrix** and counted separately, or "
        "\"Behemoth kills Refinery\" would outvote every real engagement in the archive.")

    path = os.path.join(OUTDIR, "summary.md")
    with open(path, "w", encoding="utf-8") as fh:
        fh.write("\n".join(lines) + "\n")
    return path


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--limit", type=int, default=0, help="only the newest N replays")
    ap.add_argument("--since", default="", help="only replays dated on/after YYYYMMDD")
    ap.add_argument("--min-players", type=int, default=4,
                    help="skip replays with fewer distinct players (0 = keep everything). "
                         "The default excludes solo soak rounds.")
    ap.add_argument("--include-broken-window", action="store_true",
                    help=f"keep replays from {BROKEN_FROM}-{BROKEN_TO}, which record no unit kills")
    args = ap.parse_args()

    os.makedirs(OUTDIR, exist_ok=True)
    paths = sorted(glob.glob(os.path.join(REPLAYS, "*.srpl")))
    if args.since:
        paths = [p for p in paths if os.path.basename(p).split("_")[0] >= args.since]
    if not args.include_broken_window:
        before = len(paths)
        paths = [p for p in paths
                 if not (BROKEN_FROM <= os.path.basename(p).split("_")[0] <= BROKEN_TO)]
        if before != len(paths):
            print(f"  skipping {before - len(paths)} replays from {BROKEN_FROM}-{BROKEN_TO} "
                  "(unit kills were not recorded — game update, fixed 2026-08-02)")
    if args.limit:
        paths = paths[-args.limit:]
    if not paths:
        sys.exit("no replays matched")

    print(f"mining {len(paths)} replays from {REPLAYS}")
    cost, is_structure = load_costs()
    t0 = time.time()
    rows = mine(paths, cost, args.min_players)
    elapsed = time.time() - t0
    if not rows:
        sys.exit("no destructions found")

    print(f"  {len(rows):,} destructions in {elapsed:.0f}s")
    print(" ", write_kills(rows))
    mpath, duels = build_matchups(rows, is_structure)
    print(" ", mpath)
    print(" ", summarise(rows, duels, cost, is_structure, elapsed, len(paths)))


if __name__ == "__main__":
    main()
