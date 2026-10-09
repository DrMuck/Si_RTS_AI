# Military production as a planning problem, not a scoring function

Written 2026-08-09, after DrMuck: *"I am loosing myself how to implement my ideas
and rules properly. The noding optimization and blueprint planner felt nice here
and shows also mid to longterm planning. A vision to follow."*

That instinct is correct and this document says why, in a form that can be built.

---

## 1. Why eco feels right and military does not

| | Eco layer | Military layer (today) |
|---|---|---|
| Decision shape | plan a **sequence** over a horizon | score **one site** per tick |
| Scoring | `EcoSimulator` — a forward model | hand-set constants |
| Constants | few; the search does the work | `Home 1`, `Cover 10 + earn/1000`, `Forward 50`, `QUIET_THREAT 40`, `COVER_RADIUS 400`, `FORWARD_FRACTION 0.35`, … |
| When wrong | re-plan finds a different sequence | add another constant or gate |

Rule 6.5 — *"Phase 2 is a blueprint, not a series of choices"* — was learned when
greedy per-tick placement had no global picture and every frontier point claimed
its nearest patch forever. **Producer placement has that same shape right now.**
This session's history is the proof: three separate paths defaulting to the Nest,
a gate that failed open, four Cyst factories in the quiet north — each a real bug,
each fixed with a rule, and the next round produced a new one.

The fix is not a better constant. It is giving the military layer a forward model
so it can be *searched* the way the economy is.

---

## 2. The actual question, stated so it can be answered

DrMuck's two bullets:

> an earlier producer can help to get out units out early enough to go with the
> forward (eco expansion)

> it might be more important to have more forward producers so units are stacked
> together at the expansion frontline quicker

These are not two heuristics to trade off by feel. They are **the same quantity at
two different times** — how much army is standing at the front at minute *M*.

For a producer built at position `p` at time `t`:

```
delivered(p, t, M) = Σ over units it can finish before M
                     of  value(unit) x atFront(unit, M)

atFront(unit, M)   = 1 if the unit reached the front before M, else 0
                     -> depends on walkTime(p -> front) = dist / speed

cost(p, t)         = cash + eco displaced   (income lost by not expanding)
risk(p, t)         = P(producer dies before it repays)  -- from ThreatMap
```

Maximise `Σ delivered − Σ cost` over the set of producers and their build times.

That single objective already contains both bullets:

- **Build early, near home** — low risk, buildable immediately, but `walkTime` is
  large so `atFront` is low for a long time. *Good when M is far away.*
- **Build later, far forward** — `walkTime ≈ 0`, so `atFront ≈ 1` from the moment
  it finishes; but it cannot be built until the network reaches, and `risk` is
  higher. *Good when M is near and the front is held.*

The "sweet spot" is not a tuning constant. It is where those two curves cross, and
**it moves during the round** — which is exactly why no fixed constant has worked.

---

## 3. The reframing that makes it measurable

**Stop using distance. Use walk-time budget.**

A producer is well placed when a unit it makes reaches the front within `T`
seconds. `T` is the reinforcement cadence the front needs — roughly how long the
front can hold without new units.

That converts both questions into one rule with two parameters that mean something
physical:

- **Where** — the most forward ground satisfying `walkTime(p → front) ≤ T`
  and `survivable(p)`.
- **How many** — enough that `productionRate x T ≥ lossRate at the front`.
  Fewer and the front starves; more and they idle (which is what
  `producers 88% busy` alongside idle Behemoths was telling us).

`T` is measurable from the game, not guessed: time between the front taking
damage and needing reinforcement.

### The front line is computable from layers we already have

`ControlMap` (ours) and `ThreatMap` (theirs) already exist. The front is the
contour where **own influence == enemy influence**. That is a far better anchor
than "35% of the way to their base" (`FORWARD_FRACTION`), because it moves as the
battle moves and it is defined even when no enemy base has been scouted.

This also fixes the crawl properly: the FOB follows the *front*, not a fraction of
a line to a target we may not have found.

---

## 4. What the RTS AI literature actually does here

Four ideas map directly onto this problem.

**BOSS — Build Order Search System** (Churchill & Buro, UAlbertaBot).
Depth-first branch-and-bound over build orders against a concurrency-aware forward
simulator. Answers: *given a goal army at time T, what is the cheapest build order
that reaches it?* This is literally DrMuck's "good army up quick that can take out
the enemy", and it is the closest published analogue to what `EcoSimulator` does
for the economy. **The single highest-value thing to copy.**

**BWEB — Brood War Easy Builder.** Placement as precomputed structured layout —
Blocks, Stations, Walls — rather than per-tick scoring. Candidate producer sites
should be *enumerated once* along the expansion axis and then selected from, not
re-derived every 20s by a scoring function. Cheaper and far more stable.

**Influence / tension maps** (Forbus et al.; standard in commercial RTS AI).
Own-minus-enemy influence gives the front line; the gradient gives the safe
approach. We have both layers already and use neither this way.

**TorchCraftAI — UPCTuple + Blackboard.** Modules post *intents* with confidence;
a placement module and a production module consume them. Relevant because it keeps
"what to build" and "where to build it" as separate concerns that communicate
through data — the drift between those two decisions is a failure this project has
already made three times (the FOB aiming at one base while the army walked to
another).

**Goal-Driven Autonomy** (Molineaux, Klenk, Aha). Expectation + discrepancy
detection: state what you expect a decision to achieve, then notice when it does
not. Already in `MILITARY_MODEL.md` as a build-order item; still not built. It is
what would have caught "producer built, army never arrived" without DrMuck having
to watch the game.

---

## 5. Build order (small, each one testable)

1. **Front line from existing layers.** `ControlMap − ThreatMap` contour. Log it.
   No decisions change yet. Cheap, and immediately shows whether the estimate is
   sane.
2. **Walk-time budget replaces `FORWARD_FRACTION`.** Site selection becomes
   "most forward ground within `T` seconds of the front". One parameter with a
   physical meaning replaces the fraction, `COVER_RADIUS_M`, and the forwardness
   score.
3. **Enumerate candidate sites once** (BWEB-style) along the expansion axis
   instead of re-scoring every tick. Stability, and it makes step 4 cheap.
4. **`MilSimulator`** — the forward model. Given cash split, producer set, and
   walk times: what army value is standing at the front at minute *M*? Same role
   `EcoSimulator` plays, same shape.
5. **Search the split.** With a forward model on both sides, "spend on eco or on
   army" stops being a hand-tuned reserve (`ecoReserve`, `homeShare`,
   `EcoStarvedOfCash`, the 45s window…) and becomes one comparison: whichever
   sequence yields more army-at-the-front at *M* without dropping income below
   the eco trajectory.
6. **Expectations.** Every producer records what it expected to deliver. The
   discrepancy is the signal for the next round of work.

Steps 1–2 are small and would already have prevented most of this session's bugs.
Step 4 is the one that turns the military layer into the thing DrMuck said he
wants to follow.

---

## 6. What this replaces

If steps 1–5 land, these all go away — not because they were wrong, but because
they were each a constant standing in for a model that did not exist:

`FORWARD_FRACTION`, `COVER_RADIUS_M`, `QUIET_THREAT`, `HOME_SATISFIED_M`,
`SAME_SPOT_M`, `FORWARD_PRODUCERS`, `homeShare`, `homeCapShare`, `homeFloorCash`,
`ecoReserve`, `strengthMargin`, `pushMargin`, `BANK_COVERS_S`, `EcoStarvedOfCash`.

Fourteen constants, most of them added in the last two days in response to a
specific observed failure. That count *is* the argument.
