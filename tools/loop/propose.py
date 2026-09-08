"""
Proposal step of the self-improvement loop: a model reads the batch report and the current
composition / doctrine data and returns structured proposals for compositions and strategies.
Runs on Opus by default (Fable is reserved for consulting). Nothing is applied automatically;
the output is a markdown file for review.

Usage:
    set ANTHROPIC_API_KEY=...            (never stored in the repo)
    python tools/loop/propose.py [--model claude-opus-5] [--notes path.md] [--dry-run]

Inputs (all optional except the report):
    analysis/loop/report.md              from stats.py
    UserData/commander_compositions.csv  composition target in use
    UserData/mil_doctrine.json           fitted unit values (unitValue)
    --notes                              DrMuck's match notes, free text
Output:
    analysis/loop/proposals-<stamp>.md
"""
import argparse, datetime, json, os, sys, urllib.request

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.abspath(os.path.join(HERE, "..", ".."))
SERVER_USERDATA = r"E:\Steam\steamapps\common\Silica Dedicated Server\UserData"

SYSTEM = """You are the development analyst for Si_RTS_AI, a MelonLoader mod that commands the alien
faction in the RTS/FPS game Silica. You read batch statistics over many headless rounds and propose
changes to UNIT COMPOSITION and STRATEGY (openers, when to push, what to defend), not parameter
tweaks. Every proposal must cite the evidence in the report that motivates it, say what metric
should move and by how much, and name the risk. Prefer few, solid rules over many tuned ones.
Answer in markdown with these sections: Summary; Evidence; Proposals (numbered; each with
Evidence / Change / Expected effect / Risk / How to measure); Open questions."""


def read(path, limit=None):
    try:
        with open(path, encoding="utf-8", errors="replace") as fh:
            s = fh.read()
        return s[:limit] if limit else s
    except OSError:
        return ""


def build_prompt(args):
    parts = []
    parts.append("# Batch report\n\n" + read(os.path.join(ROOT, "analysis", "loop", "report.md"), 20000))
    comp = read(os.path.join(SERVER_USERDATA, "commander_compositions.csv"), 6000)
    if comp:
        parts.append("# Composition target in use (top-quartile human commanders, per round)\n\n```\n" + comp + "\n```")
    doc = read(os.path.join(SERVER_USERDATA, "mil_doctrine.json"), 4000)
    if doc:
        try:
            d = json.loads(doc)
            parts.append("# Fitted unit values (multiplier on cash price)\n\n```json\n" + json.dumps(d.get("unitValue", d), indent=1) + "\n```")
        except ValueError:
            pass
    if args.notes:
        parts.append("# Match notes from the human commander (DrMuck)\n\n" + read(args.notes, 12000))
    parts.append("# Task\n\nPropose composition and strategy changes for the alien AI supported by this data. "
                 "Where the data is too thin, say what to measure next instead of guessing.")
    return "\n\n".join(parts)


def call_model(model, prompt, max_tokens=4000):
    key = os.environ.get("ANTHROPIC_API_KEY")
    if not key:
        sys.exit("ANTHROPIC_API_KEY is not set")
    body = json.dumps({"model": model, "max_tokens": max_tokens, "system": SYSTEM,
                       "messages": [{"role": "user", "content": prompt}]}).encode("utf-8")
    req = urllib.request.Request("https://api.anthropic.com/v1/messages", data=body, method="POST",
                                 headers={"content-type": "application/json", "x-api-key": key,
                                          "anthropic-version": "2023-06-01"})
    with urllib.request.urlopen(req, timeout=300) as resp:
        data = json.loads(resp.read().decode("utf-8"))
    return "".join(block.get("text", "") for block in data.get("content", []))


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--model", default="claude-opus-5")
    ap.add_argument("--notes", default=None)
    ap.add_argument("--dry-run", action="store_true", help="print the prompt, call no model")
    args = ap.parse_args()
    prompt = build_prompt(args)
    if args.dry_run:
        print(prompt); return
    text = call_model(args.model, prompt)
    stamp = datetime.datetime.now().strftime("%Y%m%d_%H%M")
    out = os.path.join(ROOT, "analysis", "loop", f"proposals-{stamp}.md")
    with open(out, "w", encoding="utf-8") as fh:
        fh.write(f"<!-- model: {args.model} -->\n\n" + text)
    print(out)


if __name__ == "__main__":
    main()
