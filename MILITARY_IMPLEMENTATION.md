# Military — what is actually built

Companion to `MILITARY_DESIGN.md` (the plan) and `MILITARY_TACTICS.md` (the
mission model and the four tactics). This file is the state of the CODE, so a
later session does not have to reconstruct it from commits.

**Merged and deployable as of 2026-08-07. Off by default** — `military.enabled`
in `rtsai.json` is false, and a DLL swap must never start an army.

---

## The scaffolds are reconciled

`MilitaryManager` is **deleted**. It held its own copy of decisions
`DefencePlanner` and `BattalionManager` were making differently, and that overlap
is the whole reason `MilitaryEnabled` stayed false for a month. Its production
half became `MilitaryProduction`; its target-picking and army-coordination halves
were stubs and are gone.

One owner per decision, which was the merge plan's first instruction:

| Decision | Owner |
|---|---|
| What ground is worth defending, and what home costs | `DefencePlanner` |
| What is worth DOING, priced in cash | `MissionPlanner` |
| Which units do it, and the orders | `BattalionManager` |
| What to build and what it may cost | `MilitaryProduction` |

## Files, in the order they run

Ticked at **1 Hz from `Si_RTS_AI.PeriodicTelemetryTick`**, per alien team, each
with its own internal cadence. They used to hang off `EcoPlanner.MaybePlan`,
which returns early on its own replan gate and on a pending async plan — so an
army's reaction time was the beam's replan interval. That is fixed.

| File | Does |
|---|---|
| `Planning/MilitaryConfig.cs` | Every knob, read from `rtsai.json` at map load. |
| `Perception/UnitCaps.cs` | Cap type and weight per unit, read live. |
| `Perception/UnitValues.cs` | Cash cost per unit name, from ConstructionData **or a live unit**. |
| `Perception/ThreatMap.cs` | Threat field, `Total`, and **remembered enemy structures**. |
| `Perception/CombatLog.cs` | One row per engagement, now tagged with mission kind and side count. |
| `Planning/DefencePlanner.cs` | Ranks threatened assets by recent income × threat; sizes the home floor in cash. |
| `Planning/MissionPlanner.cs` | Posture, the portfolio, the push trigger, target selection. |
| `Planning/BattalionManager.cs` | Force pool bound to missions; readiness, dwell, orders, release. |
| `Faction/MilitaryProduction.cs` | Queues combat units, places higher-tier producers, owns the money claim. |

## Switches — all in `rtsai.json`, none in MelonPreferences

`MilitaryEnabled`, `MilitaryCriticalMassSize`, `MilitaryCystCrabFraction`,
`DefenceEnabled` and `DefenceExecute` are **removed as preferences**.
MelonPreferences rewrites its file from memory on shutdown, so an edit made
during a played evening is silently reverted — and the military knobs are exactly
the ones that want to move between one game and the next.

| Key | Default | Effect |
|---|---|---|
| `military.enabled` | **false** | Master. False = the layer computes nothing. |
| `military.execute` | true | Issue orders. False = plan and log, touch no unit. |
| `military.produce` | true | Queue combat units, place higher-tier producers. |
| `military.offence` | true | Allow push missions. |
| `military.cashPerThreat` | 8.0 | **PLACEHOLDER.** Cash a defender is worth per point of threat. |
| `military.strengthMargin` | 1.5 | **PLACEHOLDER.** How much more than the threat to send. |
| `military.homeShare` | 0.25 | Least of the army held at home, as a fraction of army VALUE. |
| `military.maxDefendMissions` | 3 | Simultaneous defences. |
| `military.ecoReserve` | 15000 | Cash the economy keeps while it can still convert it. |
| `military.lesserCystShare` | 0.25 | Lesser Cysts making combat units instead of shrimps. |
| `military.pushGrowthFloor` | 20.0 | Army growth, cash/s, below which holding is a loss. |
| `military.pushMargin` | 1.5 | Ours over their estimate before committing. |
| `military.pushRetreatFraction` | 0.4 | Fraction of committed peak at which the push is called off. |

`rtsai.playtest.json` is a ready-to-drop file for playing against the AI: soak
scaffolds off, military on at bring-up step 2.

## What the log looks like

```
[MIL/CONFIG] enabled=True execute=True produce=True offence=False | cashPerThreat=8.0 ...
[THREAT]    total=412 peak=88 known=11 at=(1652,905) distToNest=1430m | HVT Refinery(...) cost=3200
[DEFENCE]   garrisonValue=1440 (peak home threat 120) threatened=2 | site (1652,905) earned 4200 threat 88
[MISSION]   hold army=3840 growth=31.2/s theirs~1056 known=11 | Garrison#1 need 1440 [home ...] Defend#4 need 1056 @(1652,905) [earned 4200 under threat 88]
[BATTALION] garrison-1 Ready 9u val 1480/1440 | defend-4 Forming 4u val 640/1056 -> (1652,905)
[MIL/PROD]  queued=23 spent=4180 cash=87340 budget=87340 ecoFirst=no
[COMBAT]    engagement at (1620,-430) over 18s — Team_Alien lost 6 (960 value) Team_Human_Sol lost 2 (700 value)
```

`/rtsai mil` prints the same state on demand, which matters because those lines
are 20-30 seconds apart and a player wants to know NOW why nothing came north.

## The rules that are encoded

- **Defend what earns, not what cost.** Asset score is `RecentDeposited × ThreatNear`.
  A Bio Cache on a drained patch scores zero however much it cost.
- **The Queen is a constraint, not a priority.** The garrison is a floor
  subtracted before anything else is allocated: the larger of what the worst
  incursion actually cost and `homeShare` of army value. It is never a ranked
  entry, so it cannot be outbid.
- **Defence outranks offence**, as a priority and not a score. Scoring across
  mission kinds needs exchange ratios per kind and there are none; a priority
  cannot produce the failure a miscalibrated score can, which is marching off
  while the economy is eaten.
- **Never trickle in** = only `Ready` battalions get a destination, and readiness
  is `value >= requiredValue` in cash.
- **The surplus is a reserve.** Units past every mission's requirement go to the
  push if there is one and home if there is not.
- **Released by the condition that raised it** — a defence ends when threat at its
  objective is gone; a push ends when the posture does. Never a timer.
- **Push when the army stops growing while cash is available.** One signal for all
  three ceilings. Paired with the cash test so "flat because broke" cannot look
  like "flat because capped". Enemy inactivity is NOT a trigger.
- **Being unable to see them is not the same as them being weak** — the push
  refuses to commit while the enemy estimate is zero.
- **The economy is paid first, measured rather than budgeted.** `ecoReserve`
  applies only while `WorkerPlan` is behind trajectory AND yield is not falling;
  before the ramp starts the economy owns everything unconditionally.
- **One owner per producer.** A Lesser Cyst claimed by the military is skipped by
  `AlienShrimpProducer`. Without that the share was decorative — the shrimp
  producer holds every Cyst at queue depth two, so the military's stride would
  have found no free slot, every tick, forever, while looking configured.
- **Hysteresis** at three levels: 30s assignment dwell, 30s before an unwanted
  battalion stands down, 60s posture dwell.

## Three defects found and fixed on the way in

1. **`UnitValues` priced most units at zero.** It only asked what WE can build, so
   every enemy unit and everything above our tech read 0. Across the 661 rows
   `combat.jsonl` had by 2026-08-07 that is Militia, Rifleman, Hunter, Shocker,
   both Quads and both Raiders — the exchange ratio the layer calibrates against
   was reading zero on one side. Falls back to the unit's own `ObjectInfo.Cost`.
2. **`ThreatMap` might never have stamped anything.** `Observe` shared its
   throttle with the DECAY clock `Tick` sets, so whether the threat field got
   built depended on where the alien team sat in `MP_Strategy.TeamSetups` —
   alien first would have skipped every enemy team on every tick, silently and
   forever. The caller is already 1 Hz; the throttle is gone.
3. **The defence layer had never produced one line of output.** `DefenceEnabled`
   was false in the live cfg (correctly, for clean eco soaks), so the merge
   plan's bring-up step 1 — "confirm `[DEFENCE]` picks assets a human would
   defend" — has still never actually been done. It is step 1 of the playtest.

## Not built, deliberately

- **Raids.** Target ranking is easy; knowing the targets is not. Enemy-structure
  memory now exists but has never been checked against a real base, and a raid
  aimed at a stale memory is a donation. After one played game.
- **Formations.** Cannot be judged before exchange ratios exist. Sub-group
  concentration first, geometry only if the data says shape pays.
- **Economic-push posture.** Needs per-branch screens coupled to `Blueprint`.
- **The Fabian expiry** (abort the hold when enemy patch share pulls ahead).
  Needs enemy *expansion* discovery, which is more than seeing a structure once.
- **Anchor nest, military production siting, learned counter-matrix.**

## Still open, and honest about it

- `[UNITCAP] resolved from game:` printed an EMPTY list on 2026-08-07 while
  Si_UnitBalance logged "Applied unit cap overrides to 13 units". Every
  `UnitCapValue` we read was 0, so `workersConsumeCap=False` is **unconfirmed
  rather than measured**. It does not block anything — cash is the contention
  either way — but do not quote that boolean as a finding.
- The per-team unit cap LIMIT is still unknown. `UnitCaps.ProbeTeamTotals` logs
  candidates once per round; nobody has read the answer off a round yet.
- `cashPerThreat` and `strengthMargin` are unmeasured, and everything the layer
  does is scaled by them. One played game with two-sided `combat.jsonl` rows
  moves them more than a night of AI-vs-AI, which structurally cannot produce a
  fight at all.
