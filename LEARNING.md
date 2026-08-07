# Learning from matches — what is tractable and what is not

DrMuck, 2026-08-07: *"we need to learn what unit compositions are good against enemy
attacks... not sure if reinforcement learning is right here, but we need to learn from
many matches. Plan to run the server so human commanders can fight against my alien
AI."*

Your instinct is right — RL is the wrong tool, and not marginally. But the goal is
achievable, because the thing worth learning is much smaller than a policy. This file
is the arithmetic, the blocking defect in the current log, and the server plan.

---

## 1. Why RL does not fit, in numbers

RL needs episodes measured in the hundreds of thousands. AlphaStar-class RTS agents
consumed on the order of 10⁵–10⁶ games, run in massively parallel fast-forwarded
simulators.

Our budget: a Silica match is 15–35 minutes of **real time**, on a real dedicated
server, against real humans who have to show up. Call it 20–50 matches a week from an
active community server. That is **three to four orders of magnitude short**, and the
gap cannot be closed by patience — it is a missing simulator, not a missing GPU.

Three further blockers, any one of which would be sufficient on its own:

- **No fast-forward.** There is no headless combat sim to roll out against. The
  environment runs at wall-clock speed.
- **Sparse credit assignment.** Match outcome is one bit at the end. Attributing a
  loss to a composition chosen twenty minutes earlier is precisely the hard part of
  RL, and it is what the missing 10⁵ episodes are *for*.
- **Non-stationarity.** The AI is under active development — every eco change shifts
  the state distribution and invalidates prior experience. RL assumes a fixed
  environment; ours changes weekly, sometimes daily.

---

## 2. The unit of learning is the engagement, not the match

This is what makes the problem tractable, and it changes the arithmetic by two orders
of magnitude.

We do not need to learn a policy. We need to estimate **how compositions trade against
each other** — and every match already contains dozens of independent observations of
exactly that. `CombatLog` detects engagements by roster diffing and writes one row
each. A 30-minute round yields tens of them, not one.

That converts an intractable RL problem into a **parameter estimation** problem:

| | RL over matches | Estimation over engagements |
|---|---|---|
| Samples needed | 10⁵–10⁶ | 10³ |
| Signal | one bit at the end | exchange ratio per engagement, immediately |
| Credit assignment | the hard part | none — the observation *is* the outcome |
| Sensitive to eco changes | yes, fatally | only via composition availability |

**Rough sizing.** Pool units into ~6 classes per side → a 6×6 counter-matrix, 36
cells. At ~30 observations per cell for a usable estimate that is ~1,000 engagements;
at 30–50 engagements per match, **20–35 matches**. That is weeks of a modest server,
not years. Measure the real engagement rate from existing `combat.jsonl` rows before
trusting these numbers — the multiplier is the whole argument and it is checkable
today.

---

## 3. What we are actually estimating

`MILITARY_DESIGN.md` §2.6 already called this correctly: *"Ship the hand-authored
prior; the learning half needs a match-history pipeline that does not exist.
Half-built learning is worse than an honest table."* That is still the right shape.

- **Hand-authored prior.** A table of expected trade rates by class. Ships immediately,
  works with zero data, and is the fallback for every unobserved cell.
- **Update toward measurement.** Each engagement updates the relevant cell. Standard
  shrinkage: a cell with 3 observations barely moves off the prior, a cell with 200
  effectively replaces it.
- **Partial pooling by class.** When a specific unit pairing is sparse, borrow from its
  class pairing. This is the single trick that makes 30 matches useful instead of 300.
- **Carry uncertainty, and use it.** A cell must expose its sample count, so the
  planner can refuse to act on a number backed by four fights.

No neural network, no reward shaping, no training loop. A counter-matrix with sample
counts, computed offline from a `.jsonl`, is the whole deliverable.

---

## 4. Blocking defect: the log records who died, not who fought

`CombatLog.cs` writes per engagement:

```
ts, map, roundT, durationS, x, z, lost{ team: { units, value, comp{} } }, missionType
```

`lost` is **losses only**. There is no record of the forces *present*. That makes the
exchange ratio uncomputable in the direction we care about: a composition that wins
cleanly without losses is indistinguishable from one that was never in the fight, and
every ratio is silently conditioned on having died.

**Nothing else in this document matters until the row carries `engaged` alongside
`lost`.** Running the server before that fix produces matches that cannot answer the
question they were collected for.

---

## 5. Confounders to stamp before opening the server

Data collected without these is not merely noisier — it is misattributed, and there is
no way to repair it afterwards.

| Field | Why | Available? |
|---|---|---|
| `engaged` per side | §4. The denominator. | **must build** |
| `aiVersion` + config hash | The AI changes weekly. Pooling across versions learns an average of behaviours that never coexisted. `rtsai.json` already exists — hash it. | easy |
| Commander identity | **Commander skill is a larger effect than composition.** Without it the matrix learns "we lose to this player", not "Crabs lose to tanks". | easy |
| FPS players in the fight | A player-piloted unit is worth several AI ones. `MILITARY_DESIGN.md` §2.3 lists FPS-player tracking as missing — for this purpose it is not optional. | **must build** |
| Static defence in range | Otherwise "we trade well here" conflates unit quality with turret support, and the hold-and-push premise (§ MILITARY_TACTICS 4) is exactly the thing being tested. | moderate |
| Tech / upgrade level | An upgraded unit is a different unit. | moderate |
| `missionType` | Currently hardcoded `"unknown"`. Becomes real with the mission model — the join key that lets ratios be read per mission kind. | after MissionPlanner |

The first four are cheap and they are the ones that cannot be reconstructed later.

---

## 6. The one RL-shaped idea worth keeping: exploration

A matrix learned from our own play only ever describes **compositions we already
build**. If the AI never builds a unit, its column stays at the prior forever, and the
system cannot discover that the unit is good. This is selection bias, and it is the
one place where deliberate exploration earns its cost.

The cheap version: occasionally choose a non-greedy composition and flag the row as
exploratory. That is a contextual bandit, not RL — no policy gradient, no value
network, and the exploration rate is a single number that can be turned to zero.

Worth doing **only after** the matrix has real data in its populated cells. Exploring
before you can measure is just noise.

---

## 7. Human commanders are free demonstration data

This is the strongest argument for the server plan, and it is not about RL at all.

Every human commander round is a record of *what an experienced player builds, when,
and against what*. That is demonstration data, and it is far more sample-efficient
than anything learned from outcomes — one good round tells you more about composition
than fifty self-play losses.

**This pattern is already proven in this repo.** `USER_RULES.md` §9 is exactly this: a
single human commander round on NarakaCity, turned into the reference benchmark that
the entire eco stack is measured against, down to per-Bio-Cache timings. It found the
insight that mattered (income tripled when reach crossed 2000m) from **one** round.

Do the same for army composition. Log the human commander's production choices and
army makeup over time, per map and phase, and compare against ours. The gap is the
finding — the same way the AI's −19%/−40%/−55% compounding eco deficit was the
finding.

---

## 8. Server plan

**Phase 0 — do not open yet.** Fix §4, add the four cheap fields from §5. Confirm on a
local round that rows carry `engaged`, a version stamp, and commander identity.

**Phase 1 — observation only.** Open the server with the AI playing its current game.
`DefenceExecute` may be false; that is fine, because Phase 1 is about establishing the
engagement rate, the confounder distribution, and *the human commander baseline*, none
of which need our military to be good. Target: enough rounds to measure engagements
per match and check §2's sizing.

**Phase 2 — prior in place, matrix accumulating.** Ship the hand-authored table. The
AI acts on the prior; the matrix updates in shadow. Compare shadow recommendation
against prior recommendation and log divergences. Nothing behavioural changes.

**Phase 3 — matrix drives composition** for cells above a sample threshold, prior
elsewhere. This is the first behaviour change and the first thing that can regress.

**Phase 4 — exploration** per §6, if cells remain empty.

**Housekeeping:** tell players the server logs match data for AI development, and say
what is recorded. It is cheap, it is the honest thing to do, and commander identity is
one of the fields.

---

## 9. Relationship to the LLM notes

`NOTES_LLM_INTEGRATION.md` lists *"composition adaptation — enemy is air-heavy → build
AA"* as a good LLM job at 30–60s cadence. That does not compete with this.

The counter-matrix is the **input** such a layer would consume. An LLM asked to pick a
composition with no measured trade data is guessing with better prose; given a matrix
with sample counts it is doing the thing it is actually good at — judgement over
multi-dimensional state. Build the measurement first either way. It is also the
cheaper half, and it works offline.

---

## 10. What would falsify this plan

- **Engagement rate is much lower than assumed.** If `CombatLog` yields 5 rows per
  match rather than 40, §2's sizing collapses and the whole approach needs rethinking.
  Checkable today, from rows already on disk.
- **Roster-diff engagement detection is too noisy** to attribute losses to a specific
  fight — e.g. it merges simultaneous fights across the map, or splits one long fight
  into many.
- **Commander skill dominates so heavily** that composition effects cannot be separated
  at this sample size. Detectable once identity is logged: if between-commander
  variance swamps between-composition variance, the matrix needs per-skill
  stratification and therefore far more data.

Each of these is measurable early and cheaply. Check them in Phase 1 rather than
discovering them at match 200.
