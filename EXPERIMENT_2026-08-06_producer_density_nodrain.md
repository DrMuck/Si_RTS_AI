# Experiment — producer density, re-measured without the drain

**Runs overnight 2026-08-06 → 07. Read this before touching the results.**

The military merge happens tomorrow on top of whatever this says, so the one
thing this run has to deliver is a producer density that is not an artefact.

---

## 1. Why every earlier number is void

`AutoResourceDrain` was on. It cut team cash to 70% of capacity whenever it
passed 75%, and `cumulIncome` credited what it destroyed — so the headline
number measured **earning power past the cap, not money the AI ever had**. The
ledger of the ratio3 round of 2026-08-06:

| | |
|---|---|
| cumulIncome (as reported) | 987,722 |
| deleted by the drain | ~315,400 |
| actually spent | ~130,600 (258 Nodes, 70 Bio Caches, 15 Cysts, 178 shrimps = 137,580 standing) |
| still banked at the whistle | 196,517 |

Drain is off from v0.27.1, so `cumulIncome` now means money that existed.
**Rounds either side of that switch must not be pooled.** Expect the headline to
fall by roughly a third on a long round; that is the metric changing, not the
economy.

## 2. The arms

Producer density again, from scratch, plus the adaptive logic as the thing to
beat. `WorkerCapPerBioCache = 10` in every arm (H1 held on), so one variable moves.

| configId | ProducerPerSites | Meaning |
|---|---|---|
| `adaptive` | 0 | strategy sweep + worker trajectory decide |
| `ratio2` | 2 | one Lesser Cyst per 2 planned sites |
| `ratio3` | 3 | per 3 |
| `ratio4` | 4 | per 4 |

Arms rotate **per round**, so drift over the night spreads evenly instead of
landing on whichever ran last.

## 3. Settings — all in `UserData/rtsai.json`, no restart needed to change them

```json
"autoResourceDrain":   false,
"configCycle":         "adaptive,ratio2,ratio3,ratio4",
"endRoundAfterMinutes": 25,
"roundsPerMap":         24,
"bridgeMode":          "loop",
"storageBufferCaches":  100,
"storageBufferAtMinute": 10
```

Requires **v0.27.2** or later (earlier builds do not read this file). Map
`NarakaCity`, the only map whose alien spawn does not move between rounds — it
must be the loaded map when the server starts, because `roundsPerMap` holds
whatever is running rather than choosing it.

### Why 25 minutes and not 35

With the drain off the bank fills and the game clamps it, so income flattens
once cash reaches capacity (~4,000 per Bio Cache — about 280k at 70 caches).
Past that point every arm reads the same, which would bury the difference rather
than measure it. 25 minutes also buys ~18 rounds instead of ~13, so 4-5
repetitions per arm against a 15% noise floor.

`bridgeMode` stays `loop` all night. Two experiments in one soak means neither
result is attributable — the bridge arms wait for their own run.

## 4. Read the checkpoints, not the finish

Primary comparison at **10 / 15 / 20 minutes**, which is pre-cap on every arm.
Final `cumulIncome` is secondary and is only meaningful if the round never
pinned at capacity — check the cash trace before quoting it.

Secondary, and now more interesting than before: **how long an arm sits at the
cap**. A cap-bound economy is one that should have been buying something, which
is exactly the argument the money broker will need tomorrow.

## 5. Validity checks — before reading anything

1. `[RTSA/CONFIG] AutoResourceDrain OFF` in the log. If it says ON, the round
   belongs to the old regime and is not part of this run.
2. `[RTSA/HT] A/B arm N: '<name>'` every round. A round without it ran
   unconfigured and groups with nothing.
3. Nest at `(2520,1275)` every round. Any other spawn is discarded, not analysed.
4. Fewer than 3 usable rounds in an arm ⇒ report it as an anecdote, not a rank.

## 6. Analysis

```
python eco_sim/analyze_experiment.py 2026-08-06T23
python eco_sim/plot_arms_curves.py   2026-08-06T23
```

`plot_arms_curves.py` has `ARM_COLOUR` / `ARMS` set for the older four-arm
sweep — swap `ratio5` for `ratio4` before running it on this data.

