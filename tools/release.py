#!/usr/bin/env python3
"""
Cut a version, or jump back to one. One command each way.

    python tools/release.py                  # tag + build + archive + deploy HEAD
    python tools/release.py --to v0.87.0     # put that build back on the server
    python tools/release.py --list           # what can I jump back to

WHY THIS EXISTS
---------------
DrMuck, 2026-08-12: *"we need to be safe to jump back in case we mess something
up or run against a wall."*

The repo has 216 commits whose message names a version and 14 tags, so most
versions can only be found by reading log messages. `_archive/` stopped
collecting DLLs at v0.41.1 on 2026-08-07, and the 45 versions since then were
deployed over the top of each other — including v0.87.0, which was overwritten
by the v0.88.0 build before anyone thought to keep it.

Rebuilding an old version is cheap (a worktree and about a second), so the
archive is not what protects the SOURCE — git already does. What the archive
buys is jumping back **without a build step and without touching the working
tree**, at the moment something is on fire and the working tree is mid-edit.
That is the case worth engineering for, because it is the case where you are
least inclined to be careful.

WHAT A RELEASE RECORDS, AND WHY IT IS MORE THAN THE DLL
-------------------------------------------------------
Behaviour is the DLL *and* the files it reads. A round is not reproducible from
`Si_RTS_AI.dll` alone: `rtsai.json` decides which planners run at all,
`rtsai_units.json` is the production prior, `mil_doctrine.json` carries the
fitted values and the commit bands. Restoring a DLL against the wrong config
reproduces neither version. So a release snapshots all four and the manifest
records a hash of each.

MelonPreferences is deliberately NOT snapshotted: MelonLoader rewrites that file
from memory on shutdown, so a copy taken while the server runs is a copy of
something that is about to be overwritten. `COOP_SERVER_SETUP.md` documents it.

WHAT THIS DOES NOT PROTECT AGAINST
-----------------------------------
The repo has no git remote. All 258 commits, every tag and this whole archive
live on one disk, so a disk failure loses the lot regardless of how carefully
versions are cut. That is a different problem from rolling back a bad build and
this tool does not solve it.
"""

import argparse
import hashlib
import json
import os
import re
import shutil
import subprocess
import sys
from datetime import datetime, timezone

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
ARCHIVE = os.path.join(ROOT, "_archive")
PROJECT = os.path.join(ROOT, "Si_RTS_AI")
BUILT = os.path.join(PROJECT, "bin", "Release", "Si_RTS_AI.dll")
MANIFEST = os.path.join(ARCHIVE, "releases.json")

SERVER = r"E:\Steam\steamapps\common\Silica Dedicated Server"
MODS = os.path.join(SERVER, "Mods")
USERDATA = os.path.join(SERVER, "UserData")

# Files that decide behaviour alongside the DLL. Source is the repo copy where
# there is one, because that is the version-controlled truth; rtsai.json has no
# repo copy (it is the live edit) so it is taken from the server.
CONFIGS = [
    ("rtsai_units.json", ROOT),
    ("mil_doctrine.json", ROOT),
    ("rtsai.json", USERDATA),
]

VERSION_RE = re.compile(r"\bv(\d+\.\d+(?:\.\d+)?)\b")


def run(cmd, cwd=ROOT, check=True):
    r = subprocess.run(cmd, cwd=cwd, capture_output=True, text=True, shell=False)
    if check and r.returncode != 0:
        sys.exit(f"failed: {' '.join(cmd)}\n{r.stdout}\n{r.stderr}")
    return r.stdout.strip()


def sha(path):
    if not os.path.exists(path):
        return None
    h = hashlib.sha256()
    with open(path, "rb") as fh:
        for chunk in iter(lambda: fh.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()[:16]


def load_manifest():
    try:
        with open(MANIFEST, encoding="utf-8") as fh:
            return json.load(fh)
    except (OSError, ValueError):
        return {"releases": {}}


def save_manifest(m):
    os.makedirs(ARCHIVE, exist_ok=True)
    with open(MANIFEST, "w", encoding="utf-8") as fh:
        json.dump(m, fh, indent=1, sort_keys=True)


def version_of_head():
    subject = run(["git", "log", "-1", "--format=%s"])
    m = VERSION_RE.search(subject)
    if not m:
        sys.exit(f"the top commit does not name a version:\n  {subject}\n"
                 f"pass --version explicitly, or commit as 'vX.Y.Z: ...'")
    return "v" + m.group(1)


def cut(args):
    version = args.version or version_of_head()
    commit = run(["git", "rev-parse", "--short", "HEAD"])

    dirty = run(["git", "status", "--porcelain"], check=False)
    if dirty and not args.allow_dirty:
        sys.exit("working tree is dirty — commit first, or pass --allow-dirty.\n"
                 "A release cut from uncommitted work cannot be jumped back to.\n"
                 + dirty)

    existing = run(["git", "tag", "-l", version], check=False)
    if not existing:
        run(["git", "tag", "-a", version, "-m", f"{version} (cut by release.py)"])
        print(f"  tagged {version} at {commit}")
    else:
        at = run(["git", "rev-list", "-n1", "--abbrev-commit", version])
        if at != commit:
            sys.exit(f"tag {version} already points at {at}, not HEAD ({commit}). "
                     f"Bump the version or delete the tag deliberately.")
        print(f"  {version} already tagged at {commit}")

    print("  building…")
    run(["dotnet", "build", "-c", "Release"], cwd=PROJECT)
    if not os.path.exists(BUILT):
        sys.exit(f"build reported success but {BUILT} is missing")

    os.makedirs(ARCHIVE, exist_ok=True)
    dll_dst = os.path.join(ARCHIVE, f"Si_RTS_AI_{version}.dll")
    shutil.copy2(BUILT, dll_dst)

    cfg_dir = os.path.join(ARCHIVE, f"cfg_{version}")
    os.makedirs(cfg_dir, exist_ok=True)
    saved = {}
    for name, src_dir in CONFIGS:
        src = os.path.join(src_dir, name)
        if os.path.exists(src):
            shutil.copy2(src, os.path.join(cfg_dir, name))
            saved[name] = sha(src)
        else:
            saved[name] = None

    m = load_manifest()
    m["releases"][version] = {
        "commit": commit,
        "cut_at": datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
        "subject": run(["git", "log", "-1", "--format=%s"]),
        "dll_sha256_16": sha(dll_dst),
        "configs": saved,
    }
    save_manifest(m)

    print(f"  archived {os.path.relpath(dll_dst, ROOT)} "
          f"({os.path.getsize(dll_dst):,} bytes, sha {sha(dll_dst)})")
    print(f"  archived configs -> {os.path.relpath(cfg_dir, ROOT)}")

    if args.deploy:
        deploy_files(dll_dst, cfg_dir, version)
    else:
        print("  not deployed (--no-deploy)")


def deploy_files(dll, cfg_dir, version):
    if not os.path.isdir(MODS):
        print(f"  ! server not found at {SERVER} — skipping deploy")
        return
    shutil.copy2(dll, os.path.join(MODS, "Si_RTS_AI.dll"))
    n = 0
    for name, _ in CONFIGS:
        src = os.path.join(cfg_dir, name)
        if os.path.exists(src):
            shutil.copy2(src, os.path.join(USERDATA, name))
            n += 1
    print(f"  deployed {version}: DLL + {n} config files")
    print("  (restart the server — MelonLoader loads mods at start)")


def jump(args):
    version = args.to
    m = load_manifest()
    rec = m["releases"].get(version)
    dll = os.path.join(ARCHIVE, f"Si_RTS_AI_{version}.dll")
    cfg_dir = os.path.join(ARCHIVE, f"cfg_{version}")

    if not os.path.exists(dll):
        # Not archived — rebuild from the tag in a throwaway worktree, so the
        # working tree is never touched. This is the path that matters when
        # something is broken and the tree is mid-edit.
        print(f"  {version} is not archived; rebuilding from the tag")
        tag = run(["git", "tag", "-l", version], check=False)
        if not tag:
            sys.exit(f"no tag {version} and no archived DLL — nothing to jump to.\n"
                     f"Try: python tools/release.py --list")
        wt = os.path.join(ROOT, ".rollback-worktree")
        shutil.rmtree(wt, ignore_errors=True)
        run(["git", "worktree", "add", "--detach", wt, version])
        try:
            run(["dotnet", "build", "-c", "Release"],
                cwd=os.path.join(wt, "Si_RTS_AI"))
            shutil.copy2(os.path.join(wt, "Si_RTS_AI", "bin", "Release",
                                      "Si_RTS_AI.dll"), dll)
        finally:
            run(["git", "worktree", "remove", wt, "--force"], check=False)
            shutil.rmtree(wt, ignore_errors=True)
        print(f"  rebuilt and archived {os.path.relpath(dll, ROOT)}")

    if rec and rec.get("dll_sha256_16") and sha(dll) != rec["dll_sha256_16"]:
        print(f"  ! archived DLL hash {sha(dll)} does not match the manifest "
              f"({rec['dll_sha256_16']}) — a rebuild is not byte-identical, "
              f"which is expected; behaviour should still match the tag")

    if not os.path.isdir(cfg_dir):
        print(f"  ! no config snapshot for {version} — deploying the DLL only. "
              f"Behaviour depends on rtsai.json too; check it matches the era.")
        cfg_dir = None
    deploy_files(dll, cfg_dir or os.path.join(ARCHIVE, "__none__"), version)


def show(args):
    m = load_manifest()
    tags = run(["git", "tag", "--sort=-creatordate"], check=False).splitlines()
    archived = {f[len("Si_RTS_AI_"):-len(".dll")]
                for f in os.listdir(ARCHIVE) if f.startswith("Si_RTS_AI_v")
                and f.endswith(".dll")} if os.path.isdir(ARCHIVE) else set()
    print(f"{'version':<12}{'tag':>5}{'dll':>6}{'cfg':>5}  commit  cut at")
    seen = set()
    for v in tags + sorted(archived - set(tags)):
        if v in seen:
            continue
        seen.add(v)
        rec = m["releases"].get(v, {})
        has_cfg = os.path.isdir(os.path.join(ARCHIVE, f"cfg_{v}"))
        print(f"{v:<12}{'yes' if v in tags else '-':>5}"
              f"{'yes' if v in archived else '-':>6}"
              f"{'yes' if has_cfg else '-':>5}  "
              f"{rec.get('commit', '-'):<8}{rec.get('cut_at', '')}")
    print(f"\n{len(archived)} DLLs archived, {len(tags)} tags. "
          f"Anything tagged can be rebuilt even if its DLL is missing.")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--version", help="override the version read from the commit")
    ap.add_argument("--to", help="jump back to this version and deploy it")
    ap.add_argument("--list", action="store_true")
    ap.add_argument("--no-deploy", dest="deploy", action="store_false",
                    help="archive but do not touch the server")
    ap.add_argument("--allow-dirty", action="store_true")
    args = ap.parse_args()

    if args.list:
        show(args)
    elif args.to:
        jump(args)
    else:
        cut(args)


if __name__ == "__main__":
    main()
