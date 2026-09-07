"""
Cross-round statistics for the self-improvement loop. Reads analysis/loop/rounds.jsonl
(from extract.py) and writes a markdown batch report. Deterministic; no model.

Usage:
    python tools/loop/stats.py [--in analysis/loop/rounds.jsonl] [--out analysis/loop/report.md]
"""
import argparse, collections, json, os, statistics


def load(path):
    rows = []
    with open(path, encoding="utf-8") as fh:
        for line in fh:
            line = line.strip()
            if line:
                rows.append(json.loads(line))
    return rows


def classify(r):
    """Rule-based failure classes for non-wins; several may apply."""
    tags = []
    if r["outcome"] == "win":
        return tags
    f = r.get("flags", {}); fo = r.get("forces", {}); a = r.get("alien", {})
    tl = r.get("timeline", [])
    if f.get("unmet_prereq_lines", 0) >= 30 and a.get("units_queued", 1) < 60:
        tags.append("research-hold")
    if f.get("siege_first_s") is not None and f["siege_first_s"] < 300 and r["outcome"] == "loss":
        tags.append("early-rush")
    idle = 0
    for s in tl:
        if s["enemy"] > 0 and s["army"] > 3 * s["enemy"]:
            idle += 1
    if idle >= 10 and fo.get("hq_gone_s") is None:
        tags.append("army-idle")
    if fo.get("withdrawals", 0) >= 4 and fo.get("hq_gone_s") is None:
        tags.append("withdrawal-churn")
    if fo.get("reach_refusals", 0) > 0:
        tags.append("path-refusals")
    if r["outcome"] == "loss" and r["duration_s"] < 1200:
        tags.append("early-loss")
    if r["outcome"] == "timeout" and not tags:
        tags.append("timeout-unclassified")
    return tags


def shares(counts):
    tot = sum(counts.values()) or 1
    return {k: v / tot for k, v in counts.items()}


def main():
    ap = argparse.ArgumentParser()
    here = os.path.dirname(__file__)
    ap.add_argument("--in", dest="inp", default=os.path.join(here, "..", "..", "analysis", "loop", "rounds.jsonl"))
    ap.add_argument("--out", default=os.path.join(here, "..", "..", "analysis", "loop", "report.md"))
    args = ap.parse_args()
    rows = load(args.inp)
    out = []
    out.append(f"# Loop report — {len(rows)} rounds\n")

    # by version
    out.append("## By mod version\n\n| version | rounds | wins | losses | timeouts | median win time |\n|---|---|---|---|---|---|")
    byv = collections.defaultdict(list)
    for r in rows:
        byv[r.get("version") or "?"].append(r)
    for v in sorted(byv):
        rs = byv[v]
        wins = [r for r in rs if r["outcome"] == "win"]
        med = statistics.median([r["duration_s"] for r in wins]) if wins else 0
        out.append(f"| {v} | {len(rs)} | {len(wins)} | {sum(r['outcome']=='loss' for r in rs)} | {sum(r['outcome']=='timeout' for r in rs)} | {med/60:.1f} min |")

    # by map
    out.append("\n## By map\n\n| map | rounds | wins | losses | timeouts | median win time |\n|---|---|---|---|---|---|")
    bym = collections.defaultdict(list)
    for r in rows:
        bym[r["scene"]].append(r)
    for m in sorted(bym):
        rs = bym[m]; wins = [r for r in rs if r["outcome"] == "win"]
        med = statistics.median([r["duration_s"] for r in wins]) if wins else 0
        out.append(f"| {m} | {len(rs)} | {len(wins)} | {sum(r['outcome']=='loss' for r in rs)} | {sum(r['outcome']=='timeout' for r in rs)} | {med/60:.1f} min |")

    # failures
    out.append("\n## Non-wins, classified\n\n| round | version | outcome | duration | tags |\n|---|---|---|---|---|")
    for r in rows:
        if r["outcome"] == "win":
            continue
        out.append(f"| {r['round']} | {r.get('version')} | {r['outcome']} | {r['duration_s']/60:.0f} min | {', '.join(classify(r)) or '-'} |")

    # composition: winners vs non-winners (alien units built shares)
    def comp(rs):
        c = collections.Counter()
        for r in rs:
            c.update(r["teams"].get("Team_Alien", {}).get("units_built_by", {}))
        c.pop("Shrimp", None); c.pop("Queen", None)
        return shares(c)
    wins = [r for r in rows if r["outcome"] == "win"]; others = [r for r in rows if r["outcome"] != "win"]
    cw, co = comp(wins), comp(others)
    units = sorted(set(cw) | set(co), key=lambda u: -(cw.get(u, 0) + co.get(u, 0)))
    out.append("\n## Alien composition, share of units built\n\n| unit | wins | non-wins |\n|---|---|---|")
    for u in units[:16]:
        out.append(f"| {u} | {cw.get(u,0)*100:.1f}% | {co.get(u,0)*100:.1f}% |")

    # timing
    fk = [r["forces"]["first_killhq_s"] for r in wins if r["forces"].get("first_killhq_s")]
    hg = [r["forces"]["hq_gone_s"] for r in wins if r["forces"].get("hq_gone_s")]
    out.append("\n## Timing (wins)\n")
    if fk:
        out.append(f"- first KillHQ staging: median {statistics.median(fk)/60:.1f} min (n={len(fk)})")
    if hg:
        out.append(f"- enemy HQ gone: median {statistics.median(hg)/60:.1f} min (n={len(hg)})")
    wd = [r["forces"].get("withdrawals", 0) for r in rows]
    if wd:
        out.append(f"- withdrawals per round: mean {statistics.mean(wd):.1f}, max {max(wd)}")
    fps = [r["fps"]["median"] for r in rows if r.get("fps")]
    if fps:
        out.append(f"- server fps median across rounds: {statistics.median(fps):.0f}")

    os.makedirs(os.path.dirname(args.out), exist_ok=True)
    with open(args.out, "w", encoding="utf-8") as fh:
        fh.write("\n".join(out) + "\n")
    print("\n".join(out))


if __name__ == "__main__":
    main()
