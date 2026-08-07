#!/usr/bin/env python3
"""
Build UNIT_ROLES.xlsx — the hand-authored prior for alien unit composition.

WHY A SPREADSHEET AND NOT A TABLE IN THE SOURCE
-----------------------------------------------
MILITARY_DESIGN §2.6 deferred the learned counter-matrix with the note "ship the
hand-authored prior; half-built learning is worse than an honest table". This is
that table. The first played round showed why it is needed: the production rule
was "build the most expensive thing the producer offers", which bought 26
Dragonflies and 16 Firebugs — a guerrilla flyer and a raider — as the answer to
a Centauri ground push. DrMuck: "Most expensive the producer can offer is the
wrong choice! It needs rather to ask what units are needed to counter that
attack."

STATS COME FROM THE GAME, ROLES COME FROM THE PLAYER
----------------------------------------------------
Everything the game knows is read from Si_UnitBalance's dump and is REGENERATED,
never hand-edited — the balance mod rewrites these numbers and a copy in a
spreadsheet would rot. Everything the game does NOT know — what a unit is for,
what it beats, when to build it — is left blank for DrMuck, because it is
judgement from playing and no amount of stat-reading produces it.

Re-run after any balance change. Answers in the fill-in columns are preserved:
the script reads the existing workbook first and copies them back by unit name,
so retuning the mod never costs you your notes.

    python tools/build_unit_sheet.py

Source:  <server>/UserData/UnitBalance_cfg/Si_UnitBalance_Dump.json
         <server>/UserData/UnitBalance_cfg/ProjectileData_Dump.csv
Output:  UNIT_ROLES.xlsx  (repo root)
"""

import csv
import json
import os
import sys

from openpyxl import Workbook, load_workbook
from openpyxl.styles import Alignment, Font, PatternFill
from openpyxl.utils import get_column_letter
from openpyxl.worksheet.datavalidation import DataValidation

SERVER = r"E:\Steam\steamapps\common\Silica Dedicated Server"
CFG = os.path.join(SERVER, "UserData", "UnitBalance_cfg")
DUMP = os.path.join(CFG, "Si_UnitBalance_Dump.json")
PROJ = os.path.join(CFG, "ProjectileData_Dump.csv")
OUT = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), "UNIT_ROLES.xlsx")

# Fill-in columns, in order. Everything else is regenerated from the game.
ASK = [
    ("Domain", 12, "Ground / Air / Both — where it fights"),
    ("HitsGround", 11, "Y / N"),
    ("HitsAir", 9, "Y / N"),
    ("Role", 18, "Raid / Line / AntiTank / AntiAir / Siege / Scout / Support / Worker"),
    ("GoodAgainst", 30, "What it beats — unit names or classes"),
    ("WeakAgainst", 30, "What beats it"),
    ("BuildWhen", 34, "The situation that should make the AI want this"),
    ("Priority", 9, "0-10. How much the AI should want it when its situation applies"),
    ("Notes", 44, "Anything the columns above cannot hold"),
]

HDR = PatternFill("solid", fgColor="1F3864")
GAME = PatternFill("solid", fgColor="EAEFF7")   # read from the game
DERIVED = PatternFill("solid", fgColor="DCE6D5")   # computed here
FILLIN = PatternFill("solid", fgColor="FFF2CC")   # for DrMuck
ALIEN = PatternFill("solid", fgColor="E8DAEF")


def num(x, d=0.0):
    try:
        return float(x)
    except (TypeError, ValueError):
        return d


def dps(dmg, cooldown):
    """Damage per second. A cooldown of zero means the field is unset, not
    infinitely fast — those units simply have no attack on that slot."""
    d, c = num(dmg), num(cooldown)
    return round(d / c, 1) if d > 0 and c > 0 else 0.0


def load_previous():
    """Existing answers, keyed by (faction, name), so a regeneration after a
    balance change never costs DrMuck the part only he can write."""
    if not os.path.exists(OUT):
        return {}
    try:
        wb = load_workbook(OUT)
    except Exception as exc:                       # corrupt or open in Excel
        print(f"  ! could not read existing {OUT} ({exc}) — starting fresh")
        return {}
    kept = {}
    for sheet in ("Units", "Structures"):
        if sheet not in wb.sheetnames:
            continue
        ws = wb[sheet]
        head = [c.value for c in ws[1]]
        try:
            fi, ni = head.index("Faction"), head.index("Name")
        except ValueError:
            continue
        cols = {a: head.index(a) for a, _, _ in ASK if a in head}
        for row in ws.iter_rows(min_row=2, values_only=True):
            if not row or not row[ni]:
                continue
            answers = {a: row[i] for a, i in cols.items() if row[i] not in (None, "")}
            if answers:
                kept[(row[fi], row[ni])] = answers
    if kept:
        print(f"  preserved answers for {len(kept)} rows")
    return kept


def style_header(ws, headers, widths):
    for i, (h, w) in enumerate(zip(headers, widths), start=1):
        c = ws.cell(row=1, column=i, value=h)
        c.fill = HDR
        c.font = Font(bold=True, color="FFFFFF", size=10)
        c.alignment = Alignment(horizontal="center", vertical="center", wrap_text=True)
        ws.column_dimensions[get_column_letter(i)].width = w
    ws.row_dimensions[1].height = 30
    ws.freeze_panes = "C2"


def build_units(wb, units, prev):
    ws = wb.create_sheet("Units")

    game_cols = [
        ("Faction", 10), ("Name", 18), ("BuiltAt", 22), ("Tier", 6),
        ("Cost", 8), ("BuildTime", 10), ("HP", 9),
        ("CapType", 10), ("Cap", 6),
        ("MoveSpd", 9), ("FlySpd", 8), ("Range", 8), ("Sight", 8),
    ]
    derived_cols = [
        ("DPS", 8), ("Splash", 8), ("InstantHit", 10),
        ("DPS/1k$", 9), ("HP/1k$", 9),
        ("DPS/Cap", 9), ("HP/Cap", 9), ("$/Cap", 8),
    ]
    headers = [h for h, _ in game_cols] + [h for h, _ in derived_cols] + [a for a, _, _ in ASK]
    widths = [w for _, w in game_cols] + [w for _, w in derived_cols] + [w for _, w, _ in ASK]
    style_header(ws, headers, widths)

    # Alien first — it is the faction we command — then the two we fight.
    order = {"Alien": 0, "Sol": 1, "Centauri": 2, "Unknown": 3}
    rows = sorted(
        (u for u in units if not u.get("is_structure")),
        key=lambda u: (order.get(u["faction"], 9), -num(u.get("cost")), u["name"]),
    )

    n_game, n_derived = len(game_cols), len(derived_cols)
    for r, u in enumerate(rows, start=2):
        best = max(dps(u.get("atk_damage"), u.get("atk_cooldown")),
                   dps(u.get("atk2_damage"), u.get("atk2_cooldown")))
        splash = max(num(u.get("proj_splash_dmg")), num(u.get("proj2_splash_dmg")),
                     num(u.get("vt_splash_dmg")), num(u.get("vt2_splash_dmg")))
        cost = num(u.get("cost"))
        cap = num(u.get("unit_cap_value"))
        hp = num(u.get("hp"))
        rng = max(num(u.get("atk_range")), num(u.get("atk2_range")))

        vals = [
            u["faction"], u["name"], u.get("built_at") or "—",
            u.get("min_tier") if num(u.get("min_tier"), -1) > 0 else 0,
            int(cost), num(u.get("build_time")), int(hp),
            u.get("unit_cap_type") or "None", int(cap),
            num(u.get("move_speed")), num(u.get("fly_speed")), int(rng),
            int(num(u.get("fow_view"))),
            best, splash, "Y" if u.get("instant_hit") else "",
            round(best / cost * 1000, 1) if cost else 0,
            round(hp / cost * 1000, 1) if cost else 0,
            round(best / cap, 1) if cap else 0,
            round(hp / cap, 0) if cap else 0,
            round(cost / cap, 0) if cap else 0,
        ]
        for i, v in enumerate(vals, start=1):
            c = ws.cell(row=r, column=i, value=v)
            c.fill = ALIEN if (u["faction"] == "Alien" and i <= n_game) else (
                GAME if i <= n_game else DERIVED)
            c.font = Font(size=10, bold=(i == 2))

        keep = prev.get((u["faction"], u["name"]), {})
        for j, (name, _, _) in enumerate(ASK):
            c = ws.cell(row=r, column=n_game + n_derived + 1 + j, value=keep.get(name))
            c.fill = FILLIN
            c.font = Font(size=10)
            c.alignment = Alignment(wrap_text=True, vertical="top")

    # Dropdowns where the answer set is closed — typos in these columns would
    # break the exporter that later turns this sheet into mod config.
    base = n_game + n_derived
    last = len(rows) + 1
    for offset, formula in ((0, '"Ground,Air,Both"'),
                            (1, '"Y,N"'),
                            (2, '"Y,N"'),
                            (3, '"Raid,Line,AntiTank,AntiAir,Siege,Scout,Support,Worker"')):
        col = get_column_letter(base + 1 + offset)
        dv = DataValidation(type="list", formula1=formula, allow_blank=True)
        ws.add_data_validation(dv)
        dv.add(f"{col}2:{col}{last}")

    ws.auto_filter.ref = f"A1:{get_column_letter(len(headers))}{last}"
    return len(rows)


def build_structures(wb, units, prev):
    ws = wb.create_sheet("Structures")
    game_cols = [("Faction", 10), ("Name", 26), ("BuiltAt", 22), ("Tier", 6),
                 ("Cost", 8), ("BuildTime", 10), ("HP", 9), ("Range", 8), ("Sight", 8)]
    derived_cols = [("DPS", 8), ("Splash", 8), ("DPS/1k$", 9), ("HP/1k$", 9)]
    headers = [h for h, _ in game_cols] + [h for h, _ in derived_cols] + [a for a, _, _ in ASK]
    widths = [w for _, w in game_cols] + [w for _, w in derived_cols] + [w for _, w, _ in ASK]
    style_header(ws, headers, widths)

    order = {"Alien": 0, "Sol": 1, "Centauri": 2, "Unknown": 3}
    rows = sorted((u for u in units if u.get("is_structure")),
                  key=lambda u: (order.get(u["faction"], 9), u["name"]))

    n_game, n_derived = len(game_cols), len(derived_cols)
    for r, u in enumerate(rows, start=2):
        best = max(dps(u.get("atk_damage"), u.get("atk_cooldown")),
                   dps(u.get("atk2_damage"), u.get("atk2_cooldown")))
        splash = max(num(u.get("proj_splash_dmg")), num(u.get("proj2_splash_dmg")))
        cost, hp = num(u.get("cost")), num(u.get("hp"))
        vals = [u["faction"], u["name"], u.get("built_at") or "—",
                u.get("min_tier") if num(u.get("min_tier"), -1) > 0 else 0,
                int(cost), num(u.get("build_time")), int(hp),
                int(max(num(u.get("atk_range")), num(u.get("atk2_range")))),
                int(num(u.get("fow_view"))),
                best, splash,
                round(best / cost * 1000, 1) if cost else 0,
                round(hp / cost * 1000, 1) if cost else 0]
        for i, v in enumerate(vals, start=1):
            c = ws.cell(row=r, column=i, value=v)
            c.fill = ALIEN if (u["faction"] == "Alien" and i <= n_game) else (
                GAME if i <= n_game else DERIVED)
            c.font = Font(size=10, bold=(i == 2))
        keep = prev.get((u["faction"], u["name"]), {})
        for j, (name, _, _) in enumerate(ASK):
            c = ws.cell(row=r, column=n_game + n_derived + 1 + j, value=keep.get(name))
            c.fill = FILLIN
            c.font = Font(size=10)
            c.alignment = Alignment(wrap_text=True, vertical="top")
    ws.auto_filter.ref = f"A1:{get_column_letter(len(headers))}{len(rows) + 1}"
    return len(rows)


def build_answers(wb, prev):
    """
    THE SHEET THE PRODUCTION RULE WILL ACTUALLY READ.

    Units answers "what is this unit", which is reference. This answers "what do
    we build when we see X", which is the decision — and it is deliberately a
    short list, because the rule that replaces "most expensive" has to be one a
    person can hold in their head.
    """
    ws = wb.create_sheet("Answers")
    headers = ["ThreatSeen", "Answer1", "Answer2", "Answer3", "Avoid", "Why", "Confidence"]
    widths = [26, 18, 18, 18, 22, 52, 12]
    style_header(ws, headers, widths)

    seeded = [
        ("Light vehicles / raiders", "", "", "", "", "", ""),
        ("Massed infantry", "", "", "", "", "", ""),
        ("Heavy tanks", "", "", "", "", "", ""),
        ("Air", "", "", "", "", "", ""),
        ("Static defence / turrets", "", "", "", "", "", ""),
        ("Harvesters (their economy)", "", "", "", "", "", ""),
        ("Undefended expansion", "", "", "", "", "", ""),
        ("Their main base", "", "", "", "", "", ""),
        ("Scouts probing our expansions", "", "", "", "", "", ""),
        ("Nothing seen yet (default build)", "", "", "", "", "", ""),
    ]
    old = {r[0]: r for r in prev.get("__answers__", [])}
    for r, row in enumerate(seeded, start=2):
        prior = old.get(row[0])
        for i, v in enumerate(prior if prior else row, start=1):
            c = ws.cell(row=r, column=i, value=v)
            c.fill = GAME if i == 1 else FILLIN
            c.font = Font(size=10, bold=(i == 1))
            c.alignment = Alignment(wrap_text=True, vertical="top")
        ws.row_dimensions[r].height = 30

    dv = DataValidation(type="list", formula1='"high,medium,guess"', allow_blank=True)
    ws.add_data_validation(dv)
    dv.add(f"G2:G{len(seeded) + 1}")


def build_projectiles(wb):
    if not os.path.exists(PROJ):
        return 0
    ws = wb.create_sheet("Projectiles")
    with open(PROJ, newline="", encoding="utf-8-sig") as fh:
        rows = list(csv.reader(fh))
    if not rows:
        return 0
    style_header(ws, rows[0], [max(10, min(26, len(h) + 4)) for h in rows[0]])
    for r, row in enumerate(rows[1:], start=2):
        for i, v in enumerate(row, start=1):
            try:
                v = float(v) if v not in ("", "True", "False") else v
            except ValueError:
                pass
            c = ws.cell(row=r, column=i, value=v)
            c.fill = GAME
            c.font = Font(size=9)
    ws.auto_filter.ref = f"A1:{get_column_letter(len(rows[0]))}{len(rows)}"
    return len(rows) - 1


def build_readme(wb, counts, version):
    ws = wb.create_sheet("README", 0)
    ws.column_dimensions["A"].width = 118
    lines = [
        ("Si_RTS_AI — alien unit roles and counters", True),
        ("", False),
        (f"Generated by tools/build_unit_sheet.py from Si_UnitBalance's live dump. Game {version}.", False),
        (f"{counts['units']} units, {counts['structures']} structures, {counts['projectiles']} projectiles.", False),
        ("", False),
        ("WHY THIS EXISTS", True),
        ("The AI currently picks 'the most expensive unit the producer offers'. In the first played round", False),
        ("that bought 26 Dragonflies and 16 Firebugs against a Centauri ground army. DrMuck: 'It needs", False),
        ("rather to ask what units are needed to counter that attack.' Nothing in the game's data says", False),
        ("what a unit is FOR, so that part has to be written down by someone who plays.", False),
        ("", False),
        ("HOW TO USE IT", True),
        ("Blue/purple columns are read from the game and are OVERWRITTEN on every regeneration.", False),
        ("Green columns are computed from those. Do not edit either — edit the balance mod instead.", False),
        ("YELLOW columns are yours. They are preserved across regenerations, matched by faction+name,", False),
        ("so re-running after a balance change never costs you your notes.", False),
        ("", False),
        ("WHERE TO START", True),
        ("The 'Answers' sheet is the one that matters, and it is ten rows. It asks the only question the", False),
        ("production rule needs answered: when we see X, what do we build? Fill that first — the Units", False),
        ("sheet is reference for filling it, not homework in itself. Fifteen alien units matter; the rest", False),
        ("of the rows are there so you can look up what the enemy brought.", False),
        ("", False),
        ("Confidence is not decoration. 'guess' tells the feedback loop which rows to test first, and", False),
        ("tells me not to build a rule that depends on a row you were unsure about.", False),
        ("", False),
        ("WHAT HAPPENS TO IT", True),
        ("Once Answers has rows, the sheet gets exported to UserData/rtsai_units.json and the production", False),
        ("rule reads it instead of sorting by cost. combat.jsonl already records losses per composition", False),
        ("and, since the military merge, tags each engagement with the mission kind that owned the ground.", False),
        ("Join those and the table stops being a prior and starts being measured — which is the whole", False),
        ("point of writing the guesses down where they can be proved wrong.", False),
        ("", False),
        ("READ THE CAP COLUMNS", True),
        ("DPS/Cap and HP/Cap matter more than the per-cash columns. Silica has two unit caps and every", False),
        ("combat unit draws on one: Behemoth is Primary/2, Colossus Primary/15, Crab Secondary/1.", False),
        ("A Shrimp is Secondary/1 — workers DO consume cap under this balance config, which contradicts", False),
        ("what MILITARY_DESIGN §2 concluded from a runtime read that returned zeros for everything.", False),
        ("At the cap, the question stops being what a unit costs and becomes what a SLOT is worth.", False),
    ]
    for r, (text, bold) in enumerate(lines, start=1):
        c = ws.cell(row=r, column=1, value=text)
        c.font = Font(bold=bold, size=12 if (bold and r == 1) else 10)
        c.alignment = Alignment(wrap_text=False, vertical="top")


def main():
    if not os.path.exists(DUMP):
        sys.exit(f"no dump at {DUMP} — start the server once with Si_UnitBalance loaded")

    with open(DUMP, encoding="utf-8-sig") as fh:
        data = json.load(fh)
    units = data["units"]

    version = "?"
    try:
        with open(os.path.join(CFG, "game_version.txt"), encoding="utf-8-sig") as fh:
            version = fh.read().strip()
    except OSError:
        pass

    print(f"reading {len(units)} entries from Si_UnitBalance_Dump.json")
    prev = load_previous()

    wb = Workbook()
    wb.remove(wb.active)
    counts = {
        "units": build_units(wb, units, prev),
        "structures": build_structures(wb, units, prev),
        "projectiles": build_projectiles(wb),
    }
    build_answers(wb, prev)
    build_readme(wb, counts, version)
    wb.save(OUT)
    print(f"wrote {OUT}")
    print(f"  Units {counts['units']}  Structures {counts['structures']}  "
          f"Projectiles {counts['projectiles']}")


if __name__ == "__main__":
    main()
