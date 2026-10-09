# Experiment — producer density, re-measured on the post-stall-fix build

**Runs overnight 2026-08-07 → 08. Read this before touching the results.**

---

## 1. Why it is being re-run

The 2026-08-06 four-arm run found nothing: every arm inside ±6%, only ratio3 vs
ratio2 clearing t=2 and not surviving correction for six comparisons. Its
conclusion was that producer density is not the lever and the working front is —
2 to 8 Bio Caches delivering out of 30 to 65 built.

That run predates a day of work aimed at exactly the working front, so the
question is open again on a bot that expands differently:

- shrimps no longer converge on one patch (census of standing + walking, quota 10)
- ground with shrimps walking to it raises demand and jumps the build order
- an out-of-reach planned hop is placed closer rather than skipped
- ground proved unbuildable is remembered and chains route around it
- a Bio Cache up a cliff no longer counts as serving its patch
- planner and executor share one number for merge radius, tap reach and detours

If density still does not separate on this build, the answer is settled and the
effort belongs elsewhere.

## 2. Arms

`WorkerCapPerBioCache = 10` in every arm, so one variable moves.

| configId | ProducerPerSites |
|---|---|
| `adaptive` | 0 — strategy sweep plus worker trajectory decide |
| `ratio2` | one Lesser Cyst per 2 planned sites |
| `ratio3` | per 3 |
| `ratio4` | per 4 |

Rotating one arm per round.

## 3. Setup

Mod **v0.41.1-no-sqrt**, md5 `08EA4B1233EB8716F3FB3F12514E99B4`.
NarakaCity, one spawn layout, nest at (2520,1275). `UserData/rtsai.json`:

```json
"configCycle": "adaptive,ratio2,ratio3,ratio4",
"endRoundAfterMinutes": 25, "roundsPerMap": 24,
"autoResourceDrain": false, "storageBufferCaches": 100,
"bridgeMode": "loop", "pileUpMaxPerPatch": 10,
"chainExtendPerTick": 2, "chainExtendDemandPerTick": 3,
"maxNodesAheadPerSite": 4, "maxBcPatchGradient": 0.5,
"tapReachMode": "measured", "timeScale": 1
```

**THE ARM TAGS REPEAT LAST NIGHT'S.** benchmarks.jsonl now holds two runs under
the same four names on different builds. Filter by time — this run starts
2026-08-07 evening — or the two pool silently and neither means anything.

## 4. Reference numbers

From 2026-08-06 (v0.28.1), 6 rounds per arm, same map and settings:

| arm | 10m | 15m | 20m | final | sd |
|---|---|---|---|---|---|
| adaptive | 68k | 161k | 278k | 378k | 31k |
| ratio2 | 70k | 167k | 273k | 358k | 21k |
| ratio3 | 69k | 172k | 293k | 404k | 29k |
| ratio4 | 67k | 156k | 281k | 390k | 29k |

Two v0.38.2 rounds on 2026-08-07 gave 372k and 413k, both inside the band, but
with a much wider mid-round spread (115k against 154k at fifteen minutes). **That
spread is the thing to watch.** The day's fixes were aimed at stalls, and a stall
costs a round its middle rather than its end.

## 5. Validity checks — before reading anything

1. `Si_RTS_AI v0.41.1-no-sqrt` in the log. Any other build and the round is not
   part of this run.
2. `[RTSA/HT] A/B arm N: '<name>'` every round.
3. Nest at (2520,1275) every round.
4. `[RTSA/CONFIG] AutoResourceDrain OFF`.
5. Fewer than 3 usable rounds in an arm ⇒ anecdote, not a rank.

## 6. What to read, in order

1. **10 / 15 / 20 minute checkpoints per arm** — mean and spread. The spread
   matters as much as the mean this time.
2. **Sites delivering** (`[BC/INCOME] … earning=`, panel 3 of `plot_arms_curves.py`).
   2-8 of 30-65 was the finding that made density irrelevant; if that has moved,
   the whole picture changes.
3. **Stall evidence per round**: count `long haul:`, `spill:`, `marked
   unbuildable`, and sites sitting at `+0n` without a Bio Cache. These should be
   rarer than today; if they are not, the fixes did not take.
4. Final income last, against 404k ± 29k.

```
python eco_sim/analyze_experiment.py 2026-08-07T17
python eco_sim/plot_arms_curves.py   2026-08-07T17
```
