# Military v3 — the rebuild plan

**Written 2026-09-05, on branch `beta`, against Silica beta 0.9.46.** This is
the working spec for the military rebuild DrMuck asked for: an RTS opponent
that shows intelligence about when and where to attack and defend, with what,
and that sites and runs military production at high utilisation. It builds on
`MIL_V2_ARCHITECTURE.md` (measurement, kernel, objectives) and
`MILITARY_MODEL.md` (DrMuck's ordered objectives) and replaces the code that
`MILITARY_IMPLEMENTATION.md` describes.

Read `USER_RULES.md` §11 first if you have not: those twelve rules are the
constraints and this document is how they are met.

---

## 0. Why the current layer fails, in one paragraph each

Everything below is taken from the code as it stands (v0.91.0) and the two
military rounds of 2026-08-13 (NarakaCity, 60 and 45 minutes, vanilla Sol and
Centauri), not from memory. What those rounds measured, from the round logs,
the server kill log and `combat.jsonl`:

| | round 1 (v0.90.0) | round 2 (v0.91.0) |
|---|---|---|
| orders to combat units in the round | 60 for 383 units, **0 attack orders** | ~115 for 430 units |
| cash unspent at the end | 80k, flat from minute 15 | **332k, 49 % of income** |
| alien structures lost | 48 | 100 (56 Nodes) |
| Shrimps lost | 144 of 354 | 43 of 212 |
| enemy structures killed | 0 | 12, one Centauri HQ |
| two-sided cash exchange | 1.62 in our favour | 1.84 |
| Nest attacked | never | never |

Three readings matter. The exchange ratio is fine — the units are good and the
composition prior works — so what is missing is command, not chassis. The one
HQ kill was not the military layer's doing: the eco planner placed three
Greater Spawning Cysts 700 m from the Centauri HQ and Behemoths spawned next
door; Sol, which killed 75 of our structures, was never touched. And every
`[MISSION]`, `[BATTALION]`, `[MIL/PROD]` and `[DEFENCE]` line from both rounds
is gone, because the military layer logged only to `MelonLoader/Latest.log`,
which the next server start overwrote. Nothing military can be reviewed after
the fact today; that is fixed first.

**It cannot see the enemy as a thing that moves.** `ThreatMap` is two decaying
fields and a dictionary of remembered structures. There is no notion of an
enemy *group*, no position history, therefore no heading, no speed, no arrival
time, and no way to tell a raid from a main push or a piloted unit from a bot.
Every defence is sized from *enemy cash inside 300 m of the asset right now*, so
a defence exists only once the enemy is already there, and the response arrives
one walk-time late — 150 `Forming 0u` lines against 10 `Committed` in one round.

**It has no forward model, so every decision is a constant.** Fourteen tuned
numbers (`MILITARY_OPTIMIZATION.md` §6), each added after a specific failure,
each right in the case that produced it. The eco layer escaped this because it
has `EcoSimulator` and searches over it; the military layer can only ask what a
constant says. The offline kernel (`mil_sim/`) exists and is validated, and
nothing in the game reads it beyond a shadow log.

**Objectives are not first-class.** There are three mission kinds and "Push" is
a direction, not a goal. DrMuck's two ordered lists — deny expansion, weaken
eco, weaken production, weaken army; defend Queen, eco, production, against
guerrilla — appear in no code path. The push picks the most expensive
affordable base cluster and walks at its centre with a plain move order.

**Execution is move orders and hope.** Units are given a destination and rely
on auto-acquire inside `TargetingDistance`; there was no attack order in the
mod at all. Cohesion was a centroid rule that deadlocked a 186-unit army for a
whole round. Speed differences were unmanaged, so a push arrived as a queue,
fastest and flimsiest first.

**Production is a per-tick argmax and a saturation heuristic.** A score picks
one winner per producer per tick, which is why 255 Shockers stood beside 41
Behemoths; producer count and siting are three hand-set rules that each
defaulted to the Nest in a different way.

**Nothing states what success would look like.** A failed push is discovered in
a replay the next morning. No mission carries an expectation, so no mission can
report its own failure, and nothing can be learned from a round without a human
watching it.

## 1. What 0.9.46 gives us that August did not

The beta replaced per-unit order verbs with an order-processor model. The port
commit only shimmed the move order. The full set, all server-authoritative,
all through `unit.OrderAgent.IssueOrder(definition, target, params)` with the
definitions on `GameDatabase.Database.OrderDefinitions`:

| Order | Definition | Target | What the agent does |
|---|---|---|---|
| Move | `Move` | point | walk there, ignore enemies unless shot |
| **Attack-move** | `Attack` | point, `Object == null` | walk there engaging what it meets |
| Attack | `Attack` | enemy `Target` | close and kill that object |
| Defend | `Defend` | friendly `Target` | stand with it, engage what comes |
| Stop | `Stop` | none | clear path and assigned targets |

And two things the design docs assumed were impossible:

- **`StrategyMode.PerformMoveAttack(List<BaseGameObject>, pos, target, speed, isAttack)`**
  is the server-side implementation of a player's multi-select right-click:
  it lays the group out on a grid facing the objective, scales every unit's
  speed to the slowest member, and registers a `UnitCohesionGroup` that keeps
  the shape until contact. Formation and pace-matching are one call.
- The game paths on the A* Pathfinding Project (`AstarPath`, `FloodPath`,
  `MultiTargetPath`, `GetNearest`, `IsPathPossible`). Walkability is a graph we
  can sample, and true path lengths are available on request.

Also confirmed from the decompile: `Team.UnitCapEntries[].Current/Max` is the
per-team cap the docs called unknown; `ObjectInfo` carries the game's own
taxonomy (`StructureSelectionType.HQ`, `StructureType` flags
Resource/Production/Research/Defense, `UnitType`, `UnitWeight`, `UnitCapType`,
`MaxHealth`, `UIAttackRating`); `Unit.ControlledBy` says who is piloting;
`DamageManager.Health/MaxHealth` is readable on anything. No hardcoded unit or
structure list is needed anywhere in this plan.

## 2. Principles carried forward, stated once

1. **Few solid rules beat many tuned ones.** Every number that survives is
   either read from the game, fitted from the archive, or one of a handful of
   labelled placeholders in `rtsai.json`.
2. **Scan, plan, execute to the plan, refresh.** A refresh re-scores; it never
   cancels an executing mission. Missions end on completion, on their own
   expectation failing, or on the kernel saying the fight is lost.
3. **One owner per decision**, and purpose travels with the request.
4. **The Queen is a constraint, not a priority.** Home defence is a floor
   subtracted first and a veto on orders that would strip it.
5. **Commit at 2–4× effective force, never at parity, split the surplus past
   4×** — measured on 58,080 fights, not tuned.
6. **Never trickle.** A force leaves when it is strong enough and travels as a
   body; reinforcement is the next wave, not a stream.
7. **Shadow first, one change per round, read the log before the next.**
8. **The opener is untouchable** and the economy has right of way whenever a
   placement was refused for cash.

## 3. Architecture

```
  PERCEPTION   Mil/Intel        enemy tracks, forecasts, corridors, bases, recon demand
               Mil/Fields       walkability, walk-time fields, front line, danger   (pure arrays)
  MODEL        Mil/Kernel       effective force, p_win, expected loss, raze time    (pure maths)
  DECISION     Mil/Objectives   candidates -> priced -> portfolio, with expectations
               Mil/Production   composition queue, producer count, siting, spires
  EXECUTION    Mil/Forces       force pools, staging, formation orders, retreat, reserve
  INSTRUMENT   Mil/Report       [INTEL] [OBJ] [FORCE] [MIL/PROD] lines, combat.jsonl v2, /military v2
```

Ownership:

| Decision | Owner | Reads |
|---|---|---|
| where the enemy is, is going, and will arrive | `Intel` | game units and structures inside our fog, `GameFow` |
| how long a walk takes, what ground is safe | `Fields` | A* graph (once per map), terrain heights, `Intel`, `ControlMap` |
| what a fight costs and who wins | `Kernel` | `mil_doctrine.json`, `rtsai_units.json`, live `ObjectInfo` |
| what is worth doing, ranked, and whether it worked | `Objectives` | everything above, `BcIncome`, `Blueprint` |
| which units go, formed how, and when to leave or retreat | `Forces` | `Objectives`, `Kernel`, `Fields` |
| what to build, how many producers, where | `Production` | `Objectives` demand, `Kernel`, `Fields`, `MoneyBroker` |
| static defence | `SpirePlanner` (kept) | `Intel` forecast and corridors instead of `DefencePlanner` |

`MissionPlanner`, `BattalionManager`, `DefencePlanner`, `MilitaryBlueprint` are
retired. `ThreatMap` keeps its two fields for the viewer and for `ControlMap`;
`Intel` supersedes it as the thing decisions read. `ScoutPlanner`,
`CombatLog`, `UnitValues`, `UnitCaps`, `Doctrine`, `UnitPrior`, `Shadow`,
`SpirePlanner` stay.

## 4. Modules

### 4.1 Intel — the enemy as tracks, not stamps

Every stamp pass (4 Hz, only units inside our active fog) clusters visible
enemy units into groups by 250 m single linkage and matches them to existing
**tracks** by predicted position. A track carries:

- position, velocity (EMA over the last 20 s), heading, and the time last seen
- composition by `UnitType`/class and count, effective force (kernel), raw cash
- `pilotedCount` from `Unit.ControlledBy` — the FPS-player signal
- confidence, decaying with a 90 s half-life once unseen; position extrapolated
  along the heading for at most 30 s, then held

Tracks give the four things the spec asks for from one structure:

- **Enemy army estimate** = Σ track effective force × confidence, which is
  nonzero while the enemy is out of sight and goes to zero honestly.
- **Forecast** per asset of ours (Nest, earning sites, producer clusters, FOB):
  for each track whose heading lies within 35° of the asset and whose
  path-time (`Fields`) is under the horizon (180 s), an arrival `(eta, force,
  confidence)`. Tracks already inside the asset radius count at eta 0.
- **Slip-through detection** = a forecast on the Nest while the main force is
  away. That is the mid-game case DrMuck named.
- **Guerrilla / FPS** = a small fast track, or any track with piloted units,
  inside our controlled ground.

**Corridors.** When a track first enters ground we control it records its entry
cell into a histogram with a 10-minute half-life, and the sector it came from
relative to the Nest. This is the only prediction the data supports — "this
opponent has come this way before" — and it positions the reserve and the
spires before anything is seen.

**Bases.** The existing cluster logic stays; each known structure gains
`FirstSeenAt` and a class from `ObjectInfo` (HQ / Resource / Production /
Defense / Research). A base gets `IsMain` (highest cost per team), `AgeS`,
`IsExpansion` (not main, and Resource or HQ), `Growth` (structure count delta
over 3 min), `LocalDefence` (kernel force of tracks within 500 m plus its
Defense structures). Harvesters are tracked as units of `UnitType.Harvester`
and attach to the nearest base as an eco target.

**Recon demand.** A base that is a candidate objective and has not been seen
for 120 s asks for one fast unit to overfly it. Cheap, bounded to one unit per
base, and it is what makes the price of an objective a current number rather
than a memory. The scout star is untouched.

### 4.2 Fields — walk time and safe ground, as arrays

All on the existing 40 m `GridWorld` (22,500 cells on NarakaCity), all pure
functions of arrays so they can run on a worker thread today and on a sidecar
process or GPU later (§8).

- **Walkability** sampled once per map from the A* graph (`GetNearest` per
  cell centre, walkable within half a cell) and **slope cost** from
  `Terrain.SampleHeight`. Built over the first minute of a round, off the
  critical path. NarakaCity's walls stop being invisible.
- **Walk-time field** = multi-source Dijkstra (8-neighbour, slope-weighted)
  from any set of seeds, divided by a speed class. Used for: arrival ETAs,
  producer siting ("time for its output to reach the expected fights"),
  reserve placement ("minimise the worst arrival time to what we defend"),
  and rally-point selection.
- **Danger field** = enemy pressure (kept from `ThreatMap`) plus track reach.
  A path cost term, so a rally point is chosen outside it and a retreat runs
  away from it.
- **Front line** = the contour where our control equals their pressure. Where
  the reserve and the FOB sit when there is no objective.

Precise path lengths for a handful of high-value queries (a push ETA, an FOB
candidate) can be asked of `MultiTargetPath` asynchronously; the grid answers
everything else.

### 4.3 Kernel — the forward model, in C#

One static class, same maths as `mil_sim/fit_kernel.py`, reading the same
files:

```
effective(force)  = Σ count · cost · w[unit]          w from mil_doctrine.json (AI-vs-AI fit), 1.0 when unmeasured
pWin(A, B)        = σ( k · ln(effA / effB) )           k from the same file
lossRatio(r)      = r^-β                               β = 0.60 AI-vs-AI exchange exponent
razeTime(force, S)= Σ MaxHealth(S) / Σ dps(force)      dps from the balance dump, live-modded
priceToBeat(D)    = the smallest effective force with ratio ≥ CommitAt against D
```

Inputs the kernel needs and where they come from: unit cost and `MaxHealth`
from live `ObjectInfo`; damage and cooldown from
`UserData/UnitBalance_cfg/Si_UnitBalance_Dump.json`, which the balance mod
rewrites every run, with a fallback of one DPS per ten cost when a unit is
absent; static defence structures counted at cost × `defenceWeight`
(placeholder 1.0, labelled, measured from `combat.jsonl` v2 once rows carry the
static-defence flag).

The kernel is allowed to decide only where it was validated: fights above
`RefuseBelow` and below `WastefulAbove`. At parity it says "coin flip" and the
planner declines. Every prediction it makes for a real engagement is logged
beside the outcome so the calibration can be read off a round.

### 4.4 Objectives — DrMuck's lists, priced, with expectations

Every 15 s the planner generates candidates, prices each with the kernel, and
assembles a portfolio. Executing missions are re-scored, not cancelled.

**Constraint (always first):**
- `DefendQueen` — required = max(price to beat the Nest forecast, `homeFloorCash`).
  Never outbid. Vetoes any order that would take the reserve below it while a
  forecast on the Nest is live.

**Deadline-driven defence, ordered by arrival time:**
- `DefendEco(site)` — sites earning now (`BcIncome`) or being tapped (Blueprint
  items in flight), with a forecast arrival. Required = price to beat the
  arriving force; deadline = eta. Only funded if a force can arrive before the
  deadline; otherwise the answer is a spire or a producer there, and the
  planner says so.
- `DefendProduction(cluster)` — the same for producer clusters away from home.
- `Screen(site)` — a standing force at the top earning exposed sites when idle
  army exists, sized from per-site threat memory (5-minute half-life). The
  cheap fix for "always one walk-time late".
- `Intercept(track)` — a small fast or piloted track on our ground: the fast
  pool at ≥ 2×.

**Offence, in the spec's order, scored within rank by gain per expected loss:**
1. `DenyExpansion(base)` — not their main, age < 6 min or still growing.
   Gain = its cost + the ground; price = beat local defence and whatever can
   reinforce inside the raze time.
2. `RaidEco(target)` — Resource structures and harvester clusters with low local
   defence. Fast pool; capped at `raidShare` (0.25) of army value so raids can
   never starve the main force. Gain = cash destroyed.
3. `RaidProduction(target)` — Production structures that are not HQ, same shape.
4. `BreakArmy(track)` — only when the line pool can reach it at ≥ CommitAt and
   it threatens something; idle enemy armies on open ground are left alone.
5. `KillHQ(base)` — the win condition. Price = beat max(local defence, total
   enemy estimate) at CommitAt plus the raze time under reinforcement. When it
   is affordable it outranks everything below the constraint.

**Portfolio assembly** is greedy in that order over the free force by pool
class, with the surplus rule: past `WastefulAbove` the excess is released to
the next objective rather than stacked. Whatever is left is the **reserve**,
which stands at the FOB front if one exists, else at a point chosen by the
walk-time field to minimise the worst arrival time to the defended assets,
weighted by the corridor histogram. That is "hold and push" without a posture
machine: the reserve grows until an offensive objective becomes affordable,
then it leaves.

**Expectations** (Goal-Driven Autonomy, finally):

| Kind | Expectation | Checked |
|---|---|---|
| DefendQueen / DefendEco / DefendProduction | asset alive and forecast cleared by eta + 60 s | every tick |
| Intercept | track effective force falls 50 % within 90 s of contact | every tick |
| DenyExpansion | structure count at the base stops rising within 120 s, then falls | 30 s |
| RaidEco / RaidProduction | target destroyed, or ≥ 1:1 cash exchange, within raze time + walk | 30 s |
| BreakArmy | track force falls 50 % before ours does | every tick |
| KillHQ | HQ health falls monotonically once engaged; base count falls | 30 s |

A violated expectation ends the mission with `[OBJ] FAILED <kind> <why>`, the
force withdraws to its rally, and the same objective key is held for 120 s.
Every outcome is written to `objectives.jsonl` with the kernel's prediction,
which is the dataset for learning which kinds pay (`LEARNING.md` §2).

### 4.5 Forces — the executor

Pools by class, derived from the game: **Fast** (speed ≥ 20 or flyer), **Swarm**
(Secondary cap), **Line** (Primary cap, cost < 3000), **Heavy** (Primary, cost
≥ 3000). Scouts stay `ScoutPlanner`'s.

A force is a group of units bound to one objective:

```
Forming -> Staging -> Advancing -> Engaged -> Done | Withdrawing -> Forming
```

- **Forming**: fill from the free pool nearest the objective; leave when
  effective force ≥ required. Never before.
- **Staging**: one `PerformMoveAttack` (isAttack=false) to a rally point chosen
  by `Fields` — short of the objective, outside the danger field, at most 60 s
  from it. The game paces the group to its slowest member and holds the shape.
  Advance when the slowest member has arrived or after `stagingPatience`
  (45 s), whichever is first.
- **Advancing**: `PerformMoveAttack` with isAttack=true against the objective's
  `Target` (structure or track leader), or an attack-move to the point. Orders
  are re-issued only when the destination moves more than 90 m, when a unit
  stands idle short of it, or when the target dies; never while a unit has a
  target.
- **Engaged**: the kernel is asked every 5 s with the force as it now stands
  against the tracks and defence within 500 m. Below `RefuseBelow` it
  **withdraws** to the rally; that is the retreat rule, and it is the first one
  this project has ever had.
- **Withdrawing**: `Stop` then move, then back to Forming as a pool.

The reserve is a standing force with `Defend` orders on the structure it
guards, so it engages what comes without walking off.

Order traffic is counted as before; the target is under 0.5 orders per unit per
minute outside contact.

### 4.6 Production — composition, count, siting, spires

**Composition as a queue with proportions.** Demand comes from the portfolio:
each unfunded or forming objective contributes its shortfall by pool class,
and the reserve contributes a target equal to the price of the cheapest
offensive objective on the board (or, with none known, the price to beat the
enemy estimate). Per producer, the next unit maximises
`demandGap[class] × valuePerCapSlot × counterFit`, where value is the doctrine
weight over cost per cap point and counterFit is the `rtsai_units.json` counter
against the observed enemy mix. Because the gap shrinks as units are queued,
the result is a mix, not a winner. Crabs are queued only for a `Swarm` demand,
which exists when an objective asks for meat in front of a line — DrMuck's
exception, modelled as a role.

**How many producers.** Two evidence rules and nothing else: (a) every
producer of a type busy and cash idle for two minutes of income → one more;
(b) portfolio demand of a class cannot be delivered before its deadline by the
current throughput → one more of the type that makes it. Both capped by cap
room read from `Team.UnitCapEntries`. Lost producers are rebuilt at the best
current site.

**Where.** Candidate sites are enumerated once per Blueprint revision: every
functional structure of ours, offset one build reach toward the front. Each is
scored by the walk-time field from the site to the expected fight cells (the
portfolio's objectives and the corridor histogram, weighted), minus the danger
at the site, with the network-reach constraint enforced by the same
`AlienConstruction` anchor test the economy uses. The **FOB** is the best
cluster toward the current main offensive objective and takes up to
`fobProducers` producers once it is either covered by a force or has a spire.
Everything is logged as `[MIL/SITE]` with the score terms so a bad site can be
read, not guessed.

**Spires.** `SpirePlanner` keeps its rules and switches its site source from
`DefencePlanner.Tasks` to the forecast list and the corridor histogram, plus
the FOB. Execute goes on in the same round production v3 does.

**Money.** `SpendableCash` keeps its two categorical rules (opener untouchable,
economy right of way when starved). Within the budget the order is: spires
whose site has an arrival under 90 s, then units for funded objectives, then
producers, then units for the reserve.

### 4.7 Report — what a round can be read from

Every military line goes to the round log as well as the console (the
`AppendToRound` path the observability layer already has), and `Latest.log` is
copied beside the round log at round end. The August rounds cannot be
re-read; the next ones will be.

The scout layer re-issues every waypoint order every 5 s — 44 % of all order
lines in round 1 were thirty Crabs being told the same thing — and that
changes to on-change-only with the same rule the forces use.

- `[INTEL]` every 30 s: tracks (count, value, piloted), forecasts with eta,
  bases with age and growth, recon requests.
- `[OBJ]` every 15 s: the portfolio with price, assigned force, expectation and
  its current reading; `[OBJ] DONE|FAILED` on completion.
- `[FORCE]` state transitions, staging waits, retreats with the kernel ratio.
- `[MIL/PROD]` composition demand by class, what each producer picked and why,
  producer count decisions; `[MIL/SITE]` the ranked candidates.
- `combat.jsonl` v2: `engaged` census per side within 400 m at the first
  loss, `piloted` counts, `staticDefence` flag, `objective` kind, kernel
  `pWin` at open — the four confounders `LEARNING.md` §5 says cannot be
  reconstructed later.
- `objectives.jsonl`: one row per finished objective.
- `/military` v2: tracks, forecasts, objectives, forces with from/to and state,
  candidate sites, corridor histogram. The layers viewer gets tracks as arrows
  with a velocity tail, objectives as pins, forces as arrows styled by state.

## 5. The rules, numbered, so they can be counted

1. Home defence is a floor: `homeFloorCash`, scaled by the Nest forecast.
2. A force commits at ≥ `CommitAt` effective ratio and refuses below
   `RefuseBelow`; surplus past `WastefulAbove` goes elsewhere.
3. A force leaves only when full and travels as one formation.
4. A force retreats when the kernel reads its fight below `RefuseBelow`.
5. Defence is funded by forecast arrival, not by presence.
6. Offence is ranked by the spec's order, scored within rank by gain per loss.
7. Raids may never exceed `raidShare` of army value.
8. Producers grow on two evidences: saturation with idle cash, or a deadline
   the throughput cannot meet.
9. Producers stand where their output reaches the expected fights soonest,
   discounted by danger, within network reach.
10. A mission ends on completion, expectation failure, or rule 4. Never a timer.

Numbers in `rtsai.json` (`mil.*`), all labelled placeholders except the three
bands, which are fitted: `commit.{refuseBelow,commitAt,wastefulAbove}` (from
`mil_doctrine.json`), `homeFloorCash` 12000, `forecastHorizonS` 180,
`raidShare` 0.25, `stagingPatienceS` 45, `fobProducers` 3, `defenceWeight` 1.0,
`screenMemoryHalfLifeS` 300. Everything else is derived.

## 6. Build order and round protocol

One change per round. Each step ships logging-only or default-off. The test
rig is `configs/alien-military-vs-vanilla/` (60 min, vanilla Sol and Centauri,
combat on), and the baseline is the two rounds of 2026-08-13.

| Step | Ships | Switch | Read in the log |
|---|---|---|---|
| 0 | the 0.9.46 port deployed, eco only | as today | blocked-order counters match August; eco alive at 25 min |
| 1 | `Intel`, `Fields`, `Kernel` | `mil.v3.shadow` | do tracks follow real groups; do forecasts precede the losses `combat.jsonl` records; kernel `pWin` vs outcomes |
| 2 | `Objectives`, `Report`, `combat.jsonl` v2 | shadow | does the portfolio read like a plan a person would make; do expectations resolve |
| 3 | `Forces`, defence only | `military.execute`, `offence:false` | responses arrive before eta; Nest never attacked unopposed; structures lost vs baseline |
| 4 | offence | `offence:true` | expectation pass rate per kind; exchange ratio per kind; enemy HQs killed |
| 5 | `Production` v3, siting, spires | `produce`, `mil.spires.execute` | producers ≥ 85 % busy, cash idle < 2 min, army engaged > 60 %, FOB stands |
| 6+ | overnight soak, one config | — | the `[UTIL]` line, `objectives.jsonl`, Nest survival, HQ kills |

Metrics that decide whether a step stays: the four utilisation meters,
structures lost, enemy structures killed, expectation pass rate, cash exchange
per objective kind, whether the Nest was ever attacked without a force present.

Speed-ups for smoke tests only: `timeScale` 2 with the achieved-rate line
checked; measurement rounds run at 1.

## 7. What is deliberately not in v3.0

Learned counter matrix (the prior is measured and shipped; the update loop
needs the `objectives.jsonl` corpus first). Formations beyond the game's own
grid. Anchor Nest for a forward base (queued behind FOB survival data). Air
micro. Sol and Centauri (the doctrine data says one human build order covers
both, and the objectives generalise, but the alien is the customer).

## 8. CPU and GPU, later

`Fields` and the portfolio search are written as pure functions over arrays
with a snapshot in and arrays out, no Unity calls inside. That is the boundary
for moving them: first to the worker thread `ExpansionStrategy` already uses,
then to a sidecar process on this box's GPU (the WSL2/CUDA environment exists)
talking over the telemetry port — the mod posts a snapshot, reads back fields
and a scored portfolio, and falls back to the in-process version when the
sidecar is absent. The kernel is small enough that the GPU's value is in
running many portfolio rollouts, not in one prediction. Nothing in v3.0 needs
it; everything in v3.0 is shaped so it can use it.

## 9. Risks, named

- **Pathing on city maps.** The 40 m grid may still misjudge narrow gaps; the
  A* multi-target query is the fallback for the few decisions that matter.
- **Fog for structures.** Bases are only ever as current as the last overflight;
  recon demand exists for this and its cost is one fast unit at a time.
- **Human factions' behaviour under 0.9.46** is unmeasured; the archive is
  0.9.42. The bands are dimensionless and should survive; the unit weights get
  re-fitted from the first beta rounds.
- **Order gates.** An attack-move is an `Attack` definition with no object and
  is not caught by the move-order gates; the co-op gate needs the same filter
  extended before a played co-op round.

## 10. Round log

### Round 1 — smoke, 2026-09-05 15:17, NarakaCity, 20 min at 2x, v0.92.0 build 1

Everything switched on at once (execute, produce, offence, spires), because a
smoke round on this box can afford to look silly and a played one cannot.

| | value |
|---|---|
| military cash spent by minute 20 | 229,640 (135 units, 13 producers) |
| cash idle at the end | 0.1 minutes of income (August: 332k unspent) |
| producers busy, round average | 84 % |
| army engaged | 69 % |
| objectives activated / done / failed | 32 / 17 / 3 |
| two-sided cash exchange | 1.46 in our favour; 6,350 : 672 where a defence force plus a spire met a Sol push |
| economy cash-blocked | 22 % of samples — the military was taking the eco's cash |

What the round found, and what changed for build 2 and 3:

- The reserve held its units forever and every objective got one unit —
  the trickle in a different coat. Now the reserve releases each tick and a
  force is raised only when the free pool can pay the whole price (rule 3).
- `ControlMap` was rebuilt for whichever team ticked first, so "our ground"
  was Sol's; intercepts fired on Sol scouts at Sol's own base. Alien only now.
- A visible structure unstamped for 1.5 s was forgotten and rediscovered a
  second later, which read as "target destroyed" to a raid twelve seconds old.
  Forget grace is now six seconds, and bases carry a persistent identity.
- Demand summed every unaffordable raid's full price and asked for 200,000 eff
  of production; only active objectives and the one reserve target count now.
- A base priced against two scouts turned out to answer with an army. Three
  rules, not a constant: the enemy estimate per team is floored on its recent
  peak (five-minute half-life); a base that beats a raid is remembered as
  having answered with that force (ten-minute half-life); a force that has not
  fought yet turns back when the refreshed price would make the kernel refuse.
- Defence outranked offence on paper only: a KillHQ force marched west while a
  33,000-eff Sol army walked into the forward base. A defence objective may now
  pre-empt any offensive force that has not yet fought.
- The military spent the economy's cash. It now leaves three of the dearest eco
  action in the bank at all times (`mil.ecoFloorCash`), plus the worker reserve
  while the economy is still converting.
- Staging patience was a flat 45 s and every long march "advanced on patience"
  with stragglers kilometres behind. Patience is now the slowest member's walk
  plus the margin.

Fog is gated (19 % of the map active, 86 % explored at minute 13); the early
knowledge of both enemy HQs came from the scout star, not omniscience.
