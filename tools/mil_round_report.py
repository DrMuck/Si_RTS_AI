#!/usr/bin/env python3
"""
Read one military round off its logs, the way MIL_V3_PLAN section 6 says a
round should be read: what the layer decided, whether its expectations held,
what the fights cost, what stood and what fell, and whether the economy was
robbed.

    python tools/mil_round_report.py                      # newest NarakaCity round
    python tools/mil_round_report.py <round-....log>      # a specific one

Sources, all under <server>/UserData/RTSA/:
  round-*.log        [OBJ] [FORCE] [MIL/PROD] [UTIL] [COMBAT] lines (military v3
                     writes them here as well as to the console)
  objectives.jsonl   one row per finished objective, filtered to the round's
                     wall-clock window
  combat.jsonl       one row per engagement (v2 carries the engaged census)
  <server>/UserData/logs/L<date>.log   structure kills, when present
"""
import collections
import glob
import json
import os
import re
import sys
from datetime import datetime, timedelta

SERVER = r"E:\Steam\steamapps\common\Silica Dedicated Server"
RTSA = os.path.join(SERVER, "UserData", "RTSA")


def newest_round():
    files = sorted(glob.glob(os.path.join(RTSA, "round-*-NarakaCity.log")))
    files = [f for f in files if os.path.getsize(f) > 100_000]
    return files[-1] if files else None


def window_of(path):
    m = re.search(r"round-(\d{8})_(\d{6})", os.path.basename(path))
    start = datetime.strptime(m.group(1) + m.group(2), "%Y%m%d%H%M%S")
    end = datetime.fromtimestamp(os.path.getmtime(path)) + timedelta(minutes=1)
    return start, end


def rows_in(path, start, end):
    out = []
    if not os.path.exists(path):
        return out
    with open(path, encoding="utf-8", errors="replace") as fh:
        for line in fh:
            if not line.startswith("{"):
                continue
            try:
                r = json.loads(line)
            except ValueError:
                continue
            try:
                ts = datetime.strptime(r.get("ts", ""), "%Y-%m-%dT%H:%M:%S")
            except ValueError:
                continue
            if start <= ts <= end:
                out.append(r)
    return out


def main():
    path = sys.argv[1] if len(sys.argv) > 1 else newest_round()
    if not path or not os.path.exists(path):
        sys.exit("no round log")
    start, end = window_of(path)
    print(f"== {os.path.basename(path)}  ({start:%Y-%m-%d %H:%M} .. {end:%H:%M})")

    text = open(path, encoding="utf-8", errors="replace").read().splitlines()

    # ---- decisions ----
    obj_lines = [l for l in text if "[OBJ] #" in l]
    active = collections.Counter()
    for l in obj_lines:
        m = re.search(r"\[OBJ\] #\d+ (ACTIVE|DONE|FAILED|ENGAGED|PRE-EMPTED) (\w+)", l)
        if m:
            active[(m.group(2), m.group(1))] += 1
    print("\n-- objectives (from the round log) --")
    kinds = sorted({k for k, _ in active})
    for k in kinds:
        a, d, f, e, p = (active[(k, s)] for s in ("ACTIVE", "DONE", "FAILED", "ENGAGED", "PRE-EMPTED"))
        print(f"  {k:16s} activated {a:3d}  done {d:3d}  failed {f:3d}  engaged {e:3d}  pre-empted {p:3d}")

    # ---- expectations, from objectives.jsonl ----
    orows = rows_in(os.path.join(RTSA, "objectives.jsonl"), start, end)
    if orows:
        print("\n-- expectations (objectives.jsonl) --")
        by = collections.defaultdict(lambda: [0, 0, 0.0, 0])
        for r in orows:
            d = by[r["kind"]]
            d[0] += 1
            if r["status"] == "Done":
                d[1] += 1
            d[2] += r.get("pWin", 0.0)
            if r.get("engaged"):
                d[3] += 1
        for k, d in sorted(by.items()):
            print(f"  {k:16s} n={d[0]:3d} done {d[1]:3d} ({100*d[1]/d[0]:.0f}%)  engaged {d[3]:3d}  mean pWin {d[2]/d[0]:.2f}")
        fails = collections.Counter(r["outcome"].split(":")[0][:50] for r in orows if r["status"] == "Failed")
        for why, n in fails.most_common(6):
            print(f"    failed: {n:3d} x {why}")

    # ---- forces ----
    withdrawals = sum(1 for l in text if "-> Withdrawing" in l)
    patience = sum(1 for l in text if "patience out" in l)
    assembled = sum(1 for l in text if "-> Advancing: assembled" in l)
    print(f"\n-- forces --\n  withdrawals {withdrawals}  advanced assembled {assembled}  advanced on patience {patience}")
    m = [l for l in text if "[FORCE] reserve" in l]
    if m:
        print("  last: " + m[-1][:200])

    # ---- combat ----
    crows = rows_in(os.path.join(RTSA, "combat.jsonl"), start, end)
    if crows:
        print(f"\n-- combat (combat.jsonl, {len(crows)} engagements) --")
        by = collections.defaultdict(lambda: [0, 0, 0, 0])
        two = 0
        for r in crows:
            lost = r.get("lost", {})
            a = lost.get("Team_Alien", {}).get("value", 0)
            e = sum(v.get("value", 0) for k, v in lost.items() if "Alien" not in k)
            if len(lost) > 1:
                two += 1
            d = by[r.get("missionType", "?")]
            d[0] += 1; d[1] += a; d[2] += e
            if r.get("staticDefence"):
                d[3] += 1
        ta = sum(d[1] for d in by.values()); te = sum(d[2] for d in by.values())
        print(f"  alien lost {ta}  enemy lost {te}  exchange {te/max(1,ta):.2f}  two-sided {two}")
        for k, d in sorted(by.items(), key=lambda kv: -kv[1][0]):
            print(f"  {k:16s} n={d[0]:3d}  alien lost {d[1]:6d}  enemy lost {d[2]:6d}  static {d[3]}")
        cal = [(r["pWinAlien"], r) for r in crows if "pWinAlien" in r]
        if cal:
            won = 0
            for p, r in cal:
                lost = r["lost"]
                a = lost.get("Team_Alien", {}).get("value", 0)
                e = sum(v.get("value", 0) for k, v in lost.items() if "Alien" not in k)
                if e > a:
                    won += 1
            print(f"  kernel: {len(cal)} rows with p(win); alien came out ahead in {won} ({100*won/len(cal):.0f}%), mean p {sum(p for p,_ in cal)/len(cal):.2f}")

    # ---- production and utilisation ----
    for tag in ("--- Military production ---", "--- Utilisation (final) ---"):
        try:
            i = text.index(tag)
            print("\n" + "\n".join(text[i:i + 6]))
        except ValueError:
            pass
    prod = [l for l in text if "[MIL/PROD] queued=" in l]
    if prod:
        print("  last: " + prod[-1][:220])

    # ---- economy check ----
    plans = [l for l in text if l.startswith("[PLAN] t=")]
    if plans:
        def at(t):
            best = None
            for l in plans:
                m2 = re.search(r"t=(\d+)", l)
                if m2 and int(m2.group(1)) <= t:
                    best = l
            return best
        print("\n-- economy --")
        for t in (600, 900, 1500, 2400):
            l = at(t)
            if l:
                m2 = re.search(r"cash=(\d+)/\d+ bcs=(\d+) cysts=(\d+) shrimps=(\d+)", l)
                if m2:
                    print(f"  t={t:5d}s cash {m2.group(1):>7s}  bcs {m2.group(2):>3s}  cysts {m2.group(3):>3s}  shrimps {m2.group(4):>4s}")
    util = [l for l in text if "[UTIL] cash" in l]
    if util:
        print("  last util: " + util[-1][util[-1].index("[UTIL]"):][:200])

    # ---- structures lost / killed, from the game's own log ----
    day = start.strftime("%Y%m%d")
    glog = os.path.join(SERVER, "UserData", "logs", f"L{day}.log")
    if os.path.exists(glog):
        lost = collections.Counter(); killed = collections.Counter()
        with open(glog, encoding="utf-8", errors="replace") as fh:
            for line in fh:
                if "structure_kill" not in line:
                    continue
                m2 = re.match(r"L (\d{2}/\d{2}/\d{4}) - (\d{2}:\d{2}:\d{2})", line)
                if not m2:
                    continue
                try:
                    ts = datetime.strptime(m2.group(1) + " " + m2.group(2), "%m/%d/%Y %H:%M:%S")
                except ValueError:
                    continue
                if not (start <= ts <= end):
                    continue
                victim = "Alien" if "<Alien>" in line.split("structure_kill")[-1][:80] else "Human"
                if "Alien" in line.split("structure_kill")[0][-60:]:
                    attacker = "Alien"
                else:
                    attacker = "Human"
                if victim == "Alien" and attacker != "Alien":
                    lost["alien structures lost"] += 1
                elif victim == "Human" and attacker == "Alien":
                    killed["enemy structures killed"] += 1
        print("\n-- structures (server kill log) --")
        for k, v in list(lost.items()) + list(killed.items()):
            print(f"  {k}: {v}")


if __name__ == "__main__":
    main()
