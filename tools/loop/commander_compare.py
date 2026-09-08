"""
Side-by-side view of every commander in one round log (player or AI): build order, tech timing,
units queued by type, cash curve and idle-cash share, orders per minute.

Reads the [CMD] / [CASH] / [ORDER] lines of a UserData/RTSA round-*.log.

Usage:
    python tools/loop/commander_compare.py [round.log]      (default: newest round log on the server)
"""
import collections, glob, os, re, sys

RTSA = r"E:\Steam\steamapps\common\Silica Dedicated Server\UserData\RTSA"
CMD = re.compile(r"\[CMD\] t=(\d+) team=(\S+) by=(\w+)(?: who=(\S+) steam=\S+)? what=(.+?) kind=(\w+)(?: at=\(([-\d]+),([-\d]+)\) yaw=\d+)? from=(.+?) client=\w+ result=(\w+)")
CASH = re.compile(r"\[CASH\] t=(\d+) team=(\S+) by=(\w+)(?: who=(\S+) steam=\S+)? cash=(\d+) units=(\d+) structures=(\d+)")
ORDER = re.compile(r"\[ORDER\] t=(\d+) commander=(\w+) team=(\S+) unit=(.+?) kind=(\w+)")


def short(team):
    return team.replace("Team_", "").replace("Human_", "")


def main():
    path = sys.argv[1] if len(sys.argv) > 1 else max(
        (f for f in glob.glob(os.path.join(RTSA, "round-*.log")) if not re.search(r"Loading|Intro|MainMenu|melon", f)),
        key=os.path.getmtime)
    teams = collections.defaultdict(lambda: {"who": "ai", "cmd": [], "cash": [], "orders": collections.Counter(), "order_minutes": collections.Counter()})
    with open(path, encoding="utf-8", errors="replace") as fh:
        for line in fh:
            m = CMD.search(line)
            if m:
                t, team, by, who, what, kind, x, z, frm, res = m.groups()
                d = teams[team]
                if by == "player" and who: d["who"] = who
                d["cmd"].append((int(t), what, kind, res, x, z))
                continue
            m = CASH.search(line)
            if m:
                t, team, by, who, cash, units, structs = m.groups()
                d = teams[team]
                if by == "player" and who: d["who"] = who
                d["cash"].append((int(t), int(cash), int(units), int(structs)))
                continue
            m = ORDER.search(line)
            if m:
                t, cmdr, team, unit, kind = m.groups()
                d = teams[team]
                d["orders"][kind] += 1
                d["order_minutes"][int(t) // 60] += 1

    print(f"# {os.path.basename(path)}\n")
    for team in sorted(teams):
        d = teams[team]
        ok = [c for c in d["cmd"] if c[3] == "Success"]
        print(f"## {short(team)}  commander: {d['who']}")
        # build order: structures and tech in order, first 25
        bo = [c for c in ok if c[2] in ("structure", "tech")]
        print("build order (t, what, at):")
        for t, what, kind, _, x, z in bo[:25]:
            at = f"({x},{z})" if kind == "structure" else "tech"
            print(f"  {t//60:2d}:{t%60:02d}  {what:<22} {at}")
        if len(bo) > 25:
            print(f"  ... {len(bo) - 25} more")
        # units by type
        units = collections.Counter(c[1] for c in ok if c[2] == "unit")
        tot = sum(units.values()) or 1
        print("units queued: " + ", ".join(f"{u} {n} ({n*100//tot}%)" for u, n in units.most_common(12)))
        # rejections
        rej = collections.Counter(c[3] for c in d["cmd"] if c[3] != "Success")
        if rej:
            print("rejected: " + ", ".join(f"{k} {n}" for k, n in rej.most_common()))
        # cash curve
        if d["cash"]:
            pts = d["cash"]
            samples = [p for i, p in enumerate(pts) if i % 10 == 0]
            print("cash every 5 min: " + " ".join(f"{t//60}m={c}" for t, c, _, _ in samples))
            peak = max(c for _, c, _, _ in pts)
            idle = sum(1 for _, c, _, _ in pts if c >= 10000) / len(pts)
            print(f"peak cash {peak}, share of samples above 10k: {idle*100:.0f}%, final units {pts[-1][2]} structures {pts[-1][3]}")
        # orders
        if d["orders"]:
            mins = d["order_minutes"]
            span = (max(mins) - min(mins) + 1) if mins else 1
            print("orders: " + ", ".join(f"{k} {n}" for k, n in d["orders"].most_common()) + f"; {sum(d['orders'].values())/span:.0f} per minute")
        print()


if __name__ == "__main__":
    main()
