#!/usr/bin/env python3
"""
Who was playing, and how good were they — per replay and per team.

WHY THE ARCHIVE HAS TO BE FILTERED BY SKILL
-------------------------------------------
DrMuck, 2026-08-12: use the project archive only, and prefer matches with
skilled commanders and/or skilled FPS players.

Both halves matter for different reasons.

The **live server folder is excluded** because it is his own box: soaks, test
rounds and bring-up games. Those are the rounds where the economy runs alone and
nobody fights, and they are the majority of what is there. Fitting a combat
model on them teaches it what a bot does when unopposed.

**Skill filtering** is the deeper one. A combat model fitted across every round
learns the average of "two good commanders trading carefully" and "nobody was
driving". Those are not the same game and averaging them produces a model that
describes neither. The planner is being built to face the server's real players,
so the rounds worth learning from are the rounds those players were in.

There is a cost, and it is the usual one: filtering hard leaves fewer fights, and
the AI-vs-AI subset — the only subset whose behaviour the bot can actually copy —
is already the smallest slice. `--min-commander-elo` therefore reports what it
kept AND what it dropped, so the trade is visible rather than assumed.

MATCHING IS BY NAME AND THAT IS A KNOWN WEAKNESS
------------------------------------------------
The ELO tables carry a steam_id; SRPL records only the display name a player had
at the time. Renames therefore break the join silently. Unmatched names are
counted and the biggest are printed, because a join that quietly matches 30% of
players would look exactly like a strict filter.
"""

import collections
import csv
import io
import os

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
ANALYSIS = os.path.join(ROOT, "analysis")

UNMATCHED = collections.Counter()


def _load(path, key="elo"):
    out = {}
    try:
        with io.open(path, encoding="utf-8-sig", newline="") as fh:
            for row in csv.DictReader(fh):
                nm = (row.get("name") or "").strip()
                if not nm:
                    continue
                try:
                    out[nm] = float(row[key])
                except (KeyError, ValueError):
                    continue
    except OSError:
        pass
    return out


class Skill:
    """ELO lookups, loaded once."""

    def __init__(self, analysis=ANALYSIS):
        self.commander = _load(os.path.join(analysis, "commander_elo.csv"))
        self.fps = _load(os.path.join(analysis, "fps_elo.csv"))

    def ok(self):
        return bool(self.commander) or bool(self.fps)

    def rate(self, replay):
        """
        Per-team skill for one parsed SRPL.

        Returns (overall, per_team) where per_team maps team name -> dict with
        the best commander and FPS rating seen on that team. SRPL's player
        records carry a team index, so the split is real rather than a guess —
        which matters because a fight is between two sides and only one of them
        may have had the good player in it.
        """
        from srpl_reader import TEAM_NAMES        # local: same module the miner uses

        per_team = collections.defaultdict(
            lambda: {"cmd": 0.0, "fps": 0.0, "n": 0, "rated": 0})
        best_cmd = best_fps = 0.0
        for _pid, (team_idx, name) in replay.players.items():
            name = (name or "").strip()
            if not name:
                continue
            team = TEAM_NAMES.get(team_idx, "Unknown")
            rec = per_team[team]
            rec["n"] += 1
            c = self.commander.get(name, 0.0)
            f = self.fps.get(name, 0.0)
            if c or f:
                rec["rated"] += 1
            else:
                UNMATCHED[name] += 1
            rec["cmd"] = max(rec["cmd"], c)
            rec["fps"] = max(rec["fps"], f)
            best_cmd = max(best_cmd, c)
            best_fps = max(best_fps, f)
        return {"cmd": best_cmd, "fps": best_fps}, dict(per_team)


def _safe(text):
    """Player names carry emoji and the Windows console is cp1252."""
    return str(text).encode("ascii", "replace").decode("ascii")


def report_unmatched(limit=10):
    if not UNMATCHED:
        return
    tot = sum(UNMATCHED.values())
    names = ", ".join(_safe(n) for n, _ in UNMATCHED.most_common(limit))
    print(f"  {len(UNMATCHED):,} player names had no ELO row "
          f"({tot:,} appearances); most common: {names}")


def main():
    s = Skill()
    print(f"commander ELO rows: {len(s.commander):,}   "
          f"fps ELO rows: {len(s.fps):,}")
    for label, tbl in (("commander", s.commander), ("fps", s.fps)):
        if not tbl:
            continue
        vals = sorted(tbl.values(), reverse=True)
        n = len(vals)
        print(f"  {label:<10} max {vals[0]:.0f}  p90 {vals[n // 10]:.0f}  "
              f"median {vals[n // 2]:.0f}  min {vals[-1]:.0f}")


if __name__ == "__main__":
    main()
