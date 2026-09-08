"""
Round extractor for the self-improvement loop.

Reads one Si_RTS_AI round log (UserData/RTSA/round-<stamp>-<Map>.log), its cumulative
melon copy (for the mod version) and the game's own log (Documents/Silica/Server_*.log,
for the Victory line) and writes one JSON record per round. Deterministic; no model.

Usage:
    python tools/loop/extract.py [--rtsa DIR] [--out analysis/loop] [--since 20260907]
"""
import argparse, collections, glob, json, os, re, sys, datetime

RTSA_DEFAULT = r"E:\Steam\steamapps\common\Silica Dedicated Server\UserData\RTSA"
GAMELOG_DIR = r"C:\Users\schwe\Documents\Silica"
SKIP_SCENES = {"Intro", "MainMenu", "Loading"}

T_RE = re.compile(r"^\[t=(\d+)s\] (.*)$")


def read(path):
    with open(path, encoding="utf-8", errors="replace") as fh:
        return fh.read().splitlines()


def parse_counts(s):
    """'Node×269, Bio Cache×67' -> {'Node': 269, ...}; '-' -> {}"""
    out = {}
    for part in s.split(","):
        part = part.strip()
        m = re.match(r"(.+?)×(\d+)$", part)
        if m:
            out[m.group(1).strip()] = int(m.group(2))
    return out


def victory_oracle():
    """{(date, hh:mm:ss) -> team} from every game log; returns a sorted list of (datetime, team)."""
    events = []
    for p in glob.glob(os.path.join(GAMELOG_DIR, "Server_*.log")):
        try:
            with open(p, encoding="utf-8", errors="replace") as fh:
                for line in fh:
                    if 'triggered "Victory"' not in line:
                        continue
                    m = re.search(r"L (\d\d)/(\d\d)/(\d{4}) - (\d\d):(\d\d):(\d\d): Team \"(\w+)\" triggered \"Victory\"", line)
                    if not m:
                        continue
                    mo, d, y, hh, mm, ss, team = m.groups()
                    events.append((datetime.datetime(int(y), int(mo), int(d), int(hh), int(mm), int(ss)), team))
        except OSError:
            pass
    events.sort()
    return events


def outcome_from_oracle(events, started, duration_s):
    if started is None:
        return None
    end = started + datetime.timedelta(seconds=duration_s)
    lo, hi = started + datetime.timedelta(seconds=30), end + datetime.timedelta(seconds=120)
    for when, team in events:
        if lo <= when <= hi:
            return team
    return None


def extract(path, events):
    lines = read(path)
    name = os.path.basename(path)
    m = re.match(r"round-(\d{8})_(\d{6})-(.+)\.log$", name)
    if not m:
        return None
    stamp, tstamp, scene = m.groups()
    if scene in SKIP_SCENES:
        return None
    started = None
    hm = re.match(r"# .*startedAt=(\d{4}-\d\d-\d\d \d\d:\d\d:\d\d)", lines[0]) if lines else None
    if hm:
        started = datetime.datetime.strptime(hm.group(1), "%Y-%m-%d %H:%M:%S")

    rec = {"round": name, "scene": scene, "started": started.isoformat() if started else None,
           "version": None, "duration_s": 0, "outcome": None, "winner": None,
           "teams": {}, "alien": {}, "timeline": [], "objectives": {}, "forces": {},
           "fps": {}, "flags": {}}

    # version from the melon copy (cumulative): the last banner before this round
    melon = path.replace(".log", ".melon.log")
    if os.path.exists(melon):
        ver = None
        for l in read(melon):
            vm = re.search(r"\] Si_RTS_AI v(0\.9\d\.\d+)$", l.rstrip())
            if vm:
                ver = vm.group(1)
        rec["version"] = ver

    last_t = 0
    obj_done = collections.Counter(); obj_failed = collections.Counter()
    withdrawals = 0; first_killhq = None; hq_gone = None; nest_gone = None
    siege_on = None; siege_s = 0.0; siege_first = None
    unmet = 0; reach_refusals = 0
    samples = {}
    prod_last = None
    for l in lines:
        tm = T_RE.match(l)
        if not tm:
            if "Headquarters" in l and "is gone" in l and "[INTEL]" in l:
                pass
            continue
        t = int(tm.group(1)); body = tm.group(2)
        last_t = max(last_t, t)
        if body.startswith("[OBJ] army"):
            am = re.match(r"\[OBJ\] army (\d+) eff \((\d+) cash\) enemy~(\d+)", body)
            if am and (t // 60) not in samples:
                samples[t // 60] = {"t": t, "army": int(am.group(1)), "army_cash": int(am.group(2)), "enemy": int(am.group(3))}
        elif body.startswith("[OBJ] #"):
            om = re.match(r"\[OBJ\] #\d+ (DONE|FAILED) (\w+)", body)
            if om:
                (obj_done if om.group(1) == "DONE" else obj_failed)[om.group(2)] += 1
            elif "UNDER SIEGE" in body:
                if siege_on is None:
                    siege_on = t
                    if siege_first is None:
                        siege_first = t
            elif "siege lifted" in body and siege_on is not None:
                siege_s += t - siege_on; siege_on = None
        elif body.startswith("[FORCE] KillHQ#") and "-> Staging" in body and first_killhq is None:
            first_killhq = t
        elif body.startswith("[FORCE]") and "-> Withdrawing" in body:
            withdrawals += 1
        elif body.startswith("[FORCE] no path"):
            reach_refusals += 1
        elif body.startswith("[INTEL]") and "Headquarters" in body and "is gone" in body and hq_gone is None:
            hq_gone = t
        elif body.startswith("[MIL/PROD] queued="):
            prod_last = body
        elif "UnmetPrerequisite" in body:
            unmet += 1
    if siege_on is not None:
        siege_s += last_t - siege_on
    rec["duration_s"] = last_t
    rec["complete"] = any("END ROUND SUMMARY" in l for l in lines) or any("ForceEndRound" in l for l in lines[-200:]) or last_t >= 3500
    rec["timeline"] = [samples[k] for k in sorted(samples)]
    rec["objectives"] = {"done": dict(obj_done), "failed": dict(obj_failed)}
    rec["forces"] = {"withdrawals": withdrawals, "first_killhq_s": first_killhq, "hq_gone_s": hq_gone,
                     "reach_refusals": reach_refusals}
    rec["flags"] = {"siege_s": round(siege_s), "siege_first_s": siege_first, "unmet_prereq_lines": unmet}
    if prod_last:
        pm = re.search(r"queued=(\d+) spent=(\d+) cash=(\d+) budget=(\d+) producers busy (\d+)/(\d+)", prod_last)
        if pm:
            rec["alien"]["units_queued"] = int(pm.group(1)); rec["alien"]["mil_spent"] = int(pm.group(2))
            rec["alien"]["producers"] = int(pm.group(6))

    # fps from budget lines
    fps = []; worst = 0
    for l in lines:
        if "[RTSA/PERF] budget" in l:
            fm = re.search(r"fps (\d+), frame ([\d.]+) ms", l)
            wm = re.search(r"worst frame (\d+) ms", l)
            if fm:
                fps.append(int(fm.group(1)))
            if wm:
                worst = max(worst, int(wm.group(1)))
    if fps:
        fps.sort()
        rec["fps"] = {"median": fps[len(fps) // 2], "min": fps[0], "worst_frame_ms": worst}

    # summary blocks per team
    team = None
    for l in lines:
        bm = re.match(r"^--- (Team_\w+) ---$", l)
        if bm:
            team = bm.group(1); rec["teams"][team] = {}; continue
        if team is None:
            continue
        if l.startswith("==========") or l.startswith("--- "):
            team = None; continue
        sm = re.match(r"^\s+structures: built=(\d+) lost=(\d+)", l)
        if sm:
            rec["teams"][team]["structures_built"] = int(sm.group(1)); rec["teams"][team]["structures_lost"] = int(sm.group(2)); rec["teams"][team]["_next"] = "s"; continue
        um = re.match(r"^\s+units:\s+built=(\d+) lost=(\d+)", l)
        if um:
            rec["teams"][team]["units_built"] = int(um.group(1)); rec["teams"][team]["units_lost"] = int(um.group(2)); rec["teams"][team]["_next"] = "u"; continue
        cm = re.match(r"^\s+(built|lost):\s+(.*)$", l)
        if cm:
            key = ("structures" if rec["teams"][team].get("_next") == "s" else "units") + "_" + cm.group(1) + "_by"
            rec["teams"][team][key] = parse_counts(cm.group(2)); continue
        om = re.match(r"^\s+orders:\s+total=(\d+)", l)
        if om:
            rec["teams"][team]["orders"] = int(om.group(1))
    for tname in rec["teams"]:
        rec["teams"][tname].pop("_next", None)
    for l in lines:
        mm = re.match(r"^\s+mix queued: (.*)$", l)
        if mm:
            rec["alien"]["mix_queued"] = parse_counts(mm.group(1).replace(" ", ", "))

    # OUR TEAM: a human team the mod commanded leaves [H1]/[H2]/[H3]/[HARV] lines
    # in the round log; otherwise the alien was ours.
    our = "Team_Alien"
    for l in lines:
        if l.startswith("[H1] team=") or l.startswith("[H2] team=") or l.startswith("[H3] team=") or l.startswith("[HARV]"):
            m2 = re.search(r"team=(Team_Human_\w+)", l)
            if m2:
                our = m2.group(1); break
    rec["our_team"] = our
    def lost(team, name):
        return rec["teams"].get(team, {}).get("structures_lost_by", {}).get(name, 0) > 0
    our_main = "Nest" if our == "Team_Alien" else "Headquarters"
    we_lost = lost(our, our_main)
    enemy_lost = any(lost(k, "Nest" if k == "Team_Alien" else "Headquarters") for k in rec["teams"] if k != our)
    winner = outcome_from_oracle(events, started, last_t)
    rec["winner"] = winner
    ours_short = "Alien" if our == "Team_Alien" else ("Sol" if "Sol" in our else "Centauri")
    if winner == ours_short:
        rec["outcome"] = "win"
    elif winner in ("Sol", "Centauri", "Alien", "Human"):
        rec["outcome"] = "loss"
    elif we_lost:
        rec["outcome"] = "loss"
    elif enemy_lost or (our == "Team_Alien" and hq_gone is not None):
        rec["outcome"] = "win"
    elif last_t >= 3500 or any("ForceEndRound" in l for l in lines[-80:]):
        rec["outcome"] = "timeout"
    else:
        rec["outcome"] = "unknown"
    return rec


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--rtsa", default=RTSA_DEFAULT)
    ap.add_argument("--out", default=os.path.join(os.path.dirname(__file__), "..", "..", "analysis", "loop"))
    ap.add_argument("--since", default="20260906")
    args = ap.parse_args()
    os.makedirs(os.path.join(args.out, "rounds"), exist_ok=True)
    events = victory_oracle()
    n = 0
    with open(os.path.join(args.out, "rounds.jsonl"), "w", encoding="utf-8") as out:
        for p in sorted(glob.glob(os.path.join(args.rtsa, "round-*.log"))):
            base = os.path.basename(p)
            if ".melon." in base or base[6:14] < args.since:
                continue
            try:
                rec = extract(p, events)
            except Exception as ex:  # one bad log must not stop the batch
                print("skip", base, ex, file=sys.stderr); continue
            if rec is None or rec["duration_s"] < 120 or not rec.get("complete"):
                continue
            with open(os.path.join(args.out, "rounds", base.replace(".log", ".json")), "w", encoding="utf-8") as fh:
                json.dump(rec, fh, indent=1)
            out.write(json.dumps(rec) + "\n"); n += 1
    print(f"extracted {n} rounds -> {os.path.join(args.out, 'rounds.jsonl')}")


if __name__ == "__main__":
    main()
