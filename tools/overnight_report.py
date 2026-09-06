#!/usr/bin/env python3
"""
Morning read of an overnight plan: one block per round from its round log.

    python tools/overnight_report.py [~/overnight.log.rounds]

Per round: mod version, variant, frame time against unit count every five
minutes, phase changes and the engaged/advancing flips, orders our layer
issued against alien military order events, anchor-check and late-update
guard counters, and — for rounds with probes — the top of the last PerfMon
table from the melon log copied beside the round log.
"""
import collections
import glob
import os
import re
import sys

SERVER = r"E:\Steam\steamapps\common\Silica Dedicated Server"
RTSA = os.path.join(SERVER, "UserData", "RTSA")
ROUNDS = sys.argv[1] if len(sys.argv) > 1 else os.path.expanduser("~/overnight.log.rounds")


def read(path):
    with open(path, encoding="utf-8", errors="replace") as fh:
        return fh.read().splitlines()


def round_block(name, logname):
    path = os.path.join(RTSA, logname)
    if not os.path.exists(path):
        print(f"== {name}: {logname} missing"); return
    lines = read(path)
    text = "\n".join(lines)
    ver = re.search(r"Si_RTS_AI v(0\.\d+\.\d+)", text)
    if not ver:
        melon0 = path.replace(".log", ".melon.log")
        if os.path.exists(melon0):
            ver = re.search(r"Si_RTS_AI v(0\.\d+\.\d+)", "
".join(read(melon0)[:40]))
    print(f"\n== {name}  {logname}  mod {ver.group(1) if ver else '?'}")

    # frame time vs units, every 5 minutes
    budget = [l for l in lines if "[RTSA/PERF] budget" in l]
    print("  min  fps  frame   units  structs  sites   ours%  anchor-ran  anim-skips")
    for i, l in enumerate(budget):
        if i % 5 != 4:
            continue
        m = re.search(r"\((\d+\.\d)%\).*fps (\d+), frame ([\d.]+) ms", l)
        u = re.search(r"units (\d+) structures (\d+) sites (\d+)", l)
        a = re.search(r"anchor-checks ran (\d+)", l)
        g = re.search(r"animator skips (\d+)", l)
        if m and u:
            print(f"  {i+1:3d} {int(m.group(2)):4d} {float(m.group(3)):6.1f} {int(u.group(1)):6d} {int(u.group(2)):8d} {int(u.group(3)):6d}  {float(m.group(1)):5.1f}  {a.group(1) if a else '-':>10s}  {g.group(1) if g else '-':>10s}")

    # phases
    trans = [l for l in lines if re.search(r"\[FORCE\] \S+ -> ", l)]
    last = {}; flips = 0; fast = 0
    for l in trans:
        m = re.match(r"\[t=(\d+)s\] \[FORCE\] (\S+) -> (\w+)", l)
        if not m:
            continue
        t, f, st = int(m.group(1)), m.group(2), m.group(3)
        if f in last and {last[f][1], st} in ({"Engaged", "Advancing"}, {"Engaged", "Staging"}):
            flips += 1
            if t - last[f][0] <= 10:
                fast += 1
        last[f] = (t, st)
    kinds = collections.Counter(re.search(r"-> (\w+)", l).group(1) for l in trans)
    print(f"  phase changes {len(trans)} ({dict(kinds)}); engaged<->advancing/staging flips {flips}, within 10 s {fast}")

    # orders
    ours = [re.search(r"orders=(\d+)", l) for l in lines if "[FORCE] reserve" in l and "orders=" in l]
    ours = int(ours[-1].group(1)) if ours and ours[-1] else -1
    events = collections.Counter()
    for l in lines:
        if not l.startswith("[ORDER]") or "team=Team_Alien" not in l:
            continue
        m = re.search(r"unit=(.+?) kind=(\w+)", l)
        if m:
            events[m.group(1)] += 1
    mil = sum(v for k, v in events.items() if k not in ("Shrimp", "Crab"))
    print(f"  orders: our layer issued {ours}; alien order events {sum(events.values())} (military {mil}, shrimps {events['Shrimp']}, crabs {events['Crab']})")

    # outcome
    for tag in ("--- Military production ---", "--- Utilisation (final) ---"):
        if tag in lines:
            i = lines.index(tag)
            print("  " + " | ".join(x.strip() for x in lines[i + 1:i + 4]))

    # perf table, if probes were on
    melon = path.replace(".log", ".melon.log")
    if os.path.exists(melon):
        ml = read(melon)
        starts = [i for i, l in enumerate(ml) if "PerfMon] INTERVAL report" in l]
        if starts:
            # the report nearest to the 600-unit mark: take the last one
            i = starts[-1]
            print("  last PerfMon table (top 12):")
            n = 0
            for l in ml[i + 2:i + 40]:
                if "---- per-owner" in l:
                    break
                l = re.sub(r", Version=[\d.]+, Culture=neutral, PublicKeyToken=null", "", l)
                l = l[l.find("]") + 2:] if "]" in l else l
                l = l.replace("[Si_ModPerfMonitor] ", "")
                print("    " + l.strip()[:120])
                n += 1
                if n >= 12:
                    break


def main():
    if not os.path.exists(ROUNDS):
        sys.exit(f"no rounds file at {ROUNDS}")
    for line in read(ROUNDS):
        if "|" in line:
            name, logname = line.split("|", 1)
            round_block(name.strip(), logname.strip())


if __name__ == "__main__":
    main()
