# Experiment — producer density and worker under-saturation

**Started 2026-08-05 evening. Read this first tomorrow.**

Running unattended on the dedicated server. Nothing here needs the server touched
while it runs — deploying a new DLL mid-soak invalidates every round after it.

---

## 1. The question

On large maps the bot pays up-front for distant biotics and the return arrives
late, because the shrimp cap is already consumed by inner, saturated nodes. The
outer sites then sit idle. The question is not *expand or not* but **how to reach
the highest income rate per invested credit over a realistic match**.

Two levers, one held fixed and one swept:

- **H1 — deliberate under-saturation (HELD ON in every arm).** Per-Bio-Cache
  worker cap 18 → 10, so freed unit cap goes outward. The crowd curve pays 1.00
  for the first six shrimps on a fresh patch and 0.70 for the thirteenth on a
  full one, so under-saturating should pay whenever fresh ground is reachable.
- **Producer density (SWEPT).** DrMuck: *"skip only 1-4 biotics and then the next
  one becomes a lesser and a biocache. This might be very map dependent and is
  actually an optimization task instead a hard coded rule task."* Implemented as
  a density — one Lesser Cyst per N planned sites — with placement still going to
  the worst-supplied sites first, so only the density moves.

H2 (relocation-instead-of-production) was dropped by DrMuck. H4 (payback-horizon
gate) is deliberately out of scope for this run.

## 2. Arms

| configId | WorkerCapPerBioCache | ProducerPerSites | Meaning |
|---|---|---|---|
| `adaptive` | 10 | 0 | Current logic decides producers (strategy sweep + worker trajectory) |
| `ratio2` | 10 | 2 | One producer per 2 sites |
| `ratio3` | 10 | 3 | One producer per 3 sites |
| `ratio5` | 10 | 5 | One producer per 5 sites |
| `cap18` | 18 | 0 | *Not in tonight's cycle.* Control that restores the stock worker cap |

Arms **rotate per round**, not in blocks — any drift over eight hours then spreads
evenly across arms instead of landing on whichever ran last.

## 3. Setup

Mod **v0.23.1-maprotation** — the map-command fix is required for the run to
work at all, so the first attempt (v0.21.1) has to be restarted on this build.
It also carries the military shadow code; set `DefenceEnabled=false` so its log
lines do not clutter an economy run. Fallback if anything looks wrong: **v0.20.1**, archived
as a runnable DLL at `_archive/v0.20.1_2026-08-05/` (copy over
`E:\Steam\...\Silica Dedicated Server\Mods\Si_RTS_AI.dll`, restart, no rebuild).

```
HeadlessTest_EndRoundAfterMinutes = 40
HeadlessTest_ConfigCycle          = "adaptive,ratio2,ratio3,ratio5"
HeadlessTest_RoundsPerMap         = 8
HeadlessTest_MapRotation          = "NarakaCity,..."
HeadlessTest_AutoRotateMap        = false
HeadlessTest_ShrimpStateSampler   = false
DefenceEnabled                    = false
```

**The first attempt did not restart the map.** Ending a round does not reliably
reload it, so the harness now issues `map <name> mp_strategy` after every
force-end and reloads the SAME map until `RoundsPerMap` is reached (v0.23.1).
That is the point rather than an accident — the arms need identical ground, and
most maps randomise the alien spawn between rounds.

Map **NarakaCity**. 12 rounds ≈ 8 h ⇒ 3 repetitions per arm.

### Why NarakaCity, and the spawn trap

`Si_MapBalance` picks a **random layout per round** when a map has several files in
`UserData/Spawns/<Map>/`. That is why the alien spawn moved between rounds and why
several "same map" comparisons from earlier on 2026-08-05 were **not** controlled.

```
NarakaCity      1 layout file  → 13 rounds, all at (2520,1275)
GreatErg       25 layout files → 3 rounds, 2 distinct spawns
NorthPolarCap                  → 3 rounds, 3 distinct spawns
```

To pin any other map: leave one layout in `UserData/Spawns/<Map>/` and move the
rest into `~disabled/` (NarakaCity already follows this convention). Reversible.

## 4. Where the data lands

| File | Contains |
|---|---|
| `UserData/RTSA/benchmarks.jsonl` | One row per team per round: `configId`, `map`, `elapsedS`, `cumulIncome`, checkpoints at 60/120/180/300/600/900/1200/1500/1800s |
| `UserData/RTSA/cashflow_*.csv` | Per second: cash, income/s, shrimps, producers, committed cysts |
| `UserData/RTSA/bc_metrics.jsonl` | Every 30s: per Bio Cache `workersInRange`, `patchesInRange`, `nearestPatchDist`, **`deposited`**, **`firstDepositT`**, **`builtAtT`** |
| `UserData/RTSA/blueprint/round-*/rev-*.json` | Plan per revision, and the **nest position** used for the spawn check |

Older sampler logs were moved to `UserData/RTSA/_archive_samplers/` before the run,
so everything present tomorrow belongs to this experiment.

## 5. Validity checks — do these BEFORE reading results

1. **Spawn.** Every round's nest must be `(2520,1275)`. Any round that differs is
   discarded, not analysed.
2. **Per-Bio-Cache income meter.** Look for `[BC/INCOME] ... ratio=`. Near 1.0 means
   per-BC numbers are trustworthy. **Below 0.2 means the attribution mechanism does
   not work in this game build** — the line says so in words — and `deposited` /
   `firstDepositT` must be thrown away. Everything else in the experiment survives.
3. **Arm application.** Each round should log
   `[RTSA/HT] A/B arm N: '<name>' (WorkerCapPerBioCache=.. ProducerPerSites=..)`.
   Rounds without it ran unconfigured and are not part of any arm.
4. **Round count per arm.** Fewer than 3 usable rounds in an arm means its result is
   an anecdote — say so rather than ranking it.

## 6. Success criteria

Primary, in order:

1. Highest **cumulative income** at the 20 / 30 / 40 min marks.
2. Smallest share of **claimed-but-untapped** sites (a Bio Cache standing with
   `workersInRange = 0`, or `deposited = 0` well after `builtAtT`).

Secondary, for understanding rather than ranking: worker count and saturation
distribution, income per worker over time (does the chosen density delay the yield
decline), resource float, and time-to-first-return per expansion.

## 7. Analysis

```
python eco_sim/analyze_experiment.py                  # since default cutoff
python eco_sim/analyze_experiment.py 2026-08-05T19    # explicit start
```

Prints per-arm means with spread, flags discarded rounds, and reports the meter
ratio. Then re-run the plots for the shapes:

```
python eco_sim/plot_overnight_income.py 2026-08-05T19
python eco_sim/plot_overnight_ramp.py   2026-08-05T19
```

## 8. Known limits of this run — state them with any conclusion

- **No seed control.** Two NarakaCity rounds on identical settings differed ~10%
  earlier the same day. With 3 reps per arm, differences under roughly 10% are not
  distinguishable from noise.
- **One map.** The whole point is that the optimum is expected to be map-dependent.
  A second map only needs the winner and its neighbour to check whether the optimum
  moved — about 2.7 h, not another 8.
- **Suppressed combat.** Soak rounds run with combat suppressed, so nothing here
  says how these settings behave when expansions get raided.
- **Shrimp cap is shared with the military model.** Any density chosen here will be
  re-litigated once an army competes for the same 185-unit ceiling.

## 9. What the result feeds

The spacing curve — not the winning number — is the deliverable. If the curve has a
clear interior optimum, it becomes the seed for a per-map optimiser rather than a
constant, which is what DrMuck asked for. If it is flat, producer density does not
matter as much as the correlation suggested and the effort belongs elsewhere.
