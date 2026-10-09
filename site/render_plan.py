#!/usr/bin/env python3
"""Render MIL_V3_PLAN.md into site/mil-v3-plan.html (the shareable page).

    pip install markdown
    python site/render_plan.py
"""
import html
import os
import re

import markdown

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SRC = os.path.join(ROOT, "docs", "military", "MIL_V3_PLAN.md")
OUT = os.path.join(ROOT, "site", "mil-v3-plan.html")

src = open(SRC, encoding="utf-8").read()
body = markdown.markdown(src, extensions=["tables", "fenced_code"])
toc = []


def slug(t):
    return re.sub(r"[^a-z0-9]+", "-", t.lower()).strip("-")


def addid(m):
    lvl, txt = m.group(1), m.group(2)
    s = slug(re.sub("<[^>]+>", "", txt))
    if lvl == "2":
        toc.append((s, re.sub("<[^>]+>", "", txt)))
    return f'<h{lvl} id="{s}">{txt}</h{lvl}>'


body = re.sub(r"<h([23])>(.*?)</h\1>", addid, body)
tochtml = "".join(f'<li><a href="#{s}">{html.escape(t)}</a></li>' for s, t in toc)
page = f"""<title>Military v3 Plan</title>
<link rel="preconnect" href="https://fonts.googleapis.com">
<link rel="stylesheet" href="https://fonts.googleapis.com/css2?family=Newsreader:ital,opsz,wght@0,6..72,400;0,6..72,600;1,6..72,400&family=IBM+Plex+Sans:wght@400;500;600&family=IBM+Plex+Mono:wght@400;500&display=swap">
<style>
:root{{--bg:#f3f2ec;--bg2:#e9e8df;--ink:#1f2419;--ink2:#4c5245;--rule:#d5d3c6;--acc:#4f7d3a;--acc2:#8a5f22;--code:#eceadf;}}
@media (prefers-color-scheme: dark){{:root:not([data-theme="light"]){{--bg:#151813;--bg2:#1d211a;--ink:#e3e5da;--ink2:#a7ab9d;--rule:#33382e;--acc:#8fc06a;--acc2:#d1a05b;--code:#20241d;}}}}
:root[data-theme="dark"]{{--bg:#151813;--bg2:#1d211a;--ink:#e3e5da;--ink2:#a7ab9d;--rule:#33382e;--acc:#8fc06a;--acc2:#d1a05b;--code:#20241d;}}
body{{margin:0;background:var(--bg);color:var(--ink);font-family:"IBM Plex Sans",system-ui,Segoe UI,sans-serif;font-size:16px;line-height:1.55}}
.wrap{{max-width:1180px;margin:0 auto;padding:40px 24px 80px;display:grid;grid-template-columns:230px minmax(0,72ch);gap:48px}}
@media (max-width:900px){{.wrap{{grid-template-columns:minmax(0,1fr)}}nav.toc{{position:static}}}}
nav.toc{{position:sticky;top:24px;align-self:start;font-size:13px}}
nav.toc .eyebrow{{font-family:"IBM Plex Mono",monospace;font-size:11px;letter-spacing:.12em;text-transform:uppercase;color:var(--acc);margin-bottom:10px}}
nav.toc ul{{list-style:none;margin:0;padding:0;border-left:2px solid var(--rule)}}
nav.toc li a{{display:block;padding:5px 12px;color:var(--ink2);text-decoration:none;border-left:2px solid transparent;margin-left:-2px}}
nav.toc li a:hover,nav.toc li a:focus{{color:var(--ink);border-left-color:var(--acc);outline:none}}
main h1{{font-family:"Newsreader",Georgia,serif;font-weight:600;font-size:2.4rem;line-height:1.1;margin:0 0 8px;text-wrap:balance}}
main h2{{font-family:"Newsreader",Georgia,serif;font-weight:600;font-size:1.6rem;margin:2.6em 0 .6em;padding-top:.6em;border-top:1px solid var(--rule);text-wrap:balance}}
main h3{{font-family:"IBM Plex Sans",sans-serif;font-weight:600;font-size:1.05rem;margin:2em 0 .5em;color:var(--acc)}}
main p{{margin:0 0 1em}}
main a{{color:var(--acc)}}
main code{{font-family:"IBM Plex Mono",monospace;font-size:.88em;background:var(--code);padding:1px 5px;border-radius:3px}}
main pre{{background:var(--code);padding:14px 16px;overflow-x:auto;border-radius:4px;font-size:.84rem;line-height:1.45}}
main pre code{{background:none;padding:0}}
main table{{border-collapse:collapse;width:100%;font-size:.9rem;margin:1em 0 1.4em;font-variant-numeric:tabular-nums;display:block;overflow-x:auto}}
main th,main td{{text-align:left;padding:7px 10px;border-bottom:1px solid var(--rule);vertical-align:top}}
main th{{font-family:"IBM Plex Mono",monospace;font-size:.72rem;letter-spacing:.08em;text-transform:uppercase;color:var(--ink2);font-weight:500}}
main blockquote{{margin:1em 0;padding:.2em 1em;border-left:3px solid var(--acc2);color:var(--ink2)}}
main ol,main ul{{padding-left:1.4em}}
main li{{margin:.3em 0}}
main hr{{border:0;border-top:1px solid var(--rule);margin:2em 0}}
main strong{{font-weight:600}}
.meta{{font-family:"IBM Plex Mono",monospace;font-size:12px;color:var(--ink2);margin-bottom:28px}}
</style>
<div class="wrap">
<nav class="toc"><div class="eyebrow">Si_RTS_AI · military v3</div><ul>{tochtml}</ul></nav>
<main>
<div class="meta">MIL_V3_PLAN.md · branch beta · Silica 0.9.46 · 2026-09-05</div>
{body}
</main></div>"""
open(OUT, "w", encoding="utf-8").write(page)
print(OUT, len(page))
