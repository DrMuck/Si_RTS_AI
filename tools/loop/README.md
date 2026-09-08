# Self-improvement loop (item 4)

Data-driven development loop for Si_RTS_AI, StarCraft-AI style: many headless rounds → metrics
→ cross-game statistics → proposals for compositions and strategies → human review → code.
Never a per-round JSON knob tuner. Deterministic stages first; a model only at batch level.

## Stages

| stage | file | model | what it does |
|---|---|---|---|
| extract | `extract.py` | none | one JSON per round: outcome, duration, version, per-team built/lost, alien timeline (army/enemy/cash per minute), objectives done/failed, forces (withdrawals, first KillHQ staging, HQ gone), siege time, fps |
| stats | `stats.py` | none | `analysis/loop/report.md`: win rate by version and map, non-wins classified by rules (research-hold, early-loss, army-idle, withdrawal-churn, path-refusals), composition of wins vs non-wins, timing |
| propose | `propose.py` (next) | **Opus** | reads the report + doctrine values + composition target + the match notes, returns structured proposals (composition targets, opener/strategy changes, doctrine ratios) with evidence; written to `analysis/loop/proposals-<date>.md` for review |
| run | `run_batch.sh` (next) | none | N rounds of a config through the headless harness, then extract → stats → propose |

Fable 5.1 is reserved for consulting (design, review, hard debugging); the loop's model step
runs on Opus by default (`--model` switch in propose.py).

## Usage

```
python tools/loop/extract.py --since 20260907     # RTSA round logs -> analysis/loop/rounds.jsonl
python tools/loop/stats.py                        # -> analysis/loop/report.md
```

## Notes

- Outcome comes from the round's own evidence (enemy HQ gone / Headquarters in the enemy's
  lost list / our Nest lost / force-end). The game log's Victory line is not reliable enough
  (the file is rewritten) and is only a cross-check.
- The per-round `.melon.log` copies under RTSA are cumulative copies of `Latest.log`; the mod
  version is the last `Si_RTS_AI v0.9x.y` banner in it.
- Statistics over many rounds, not round-to-round: a change is judged against the baseline
  version's distribution on the same maps.
