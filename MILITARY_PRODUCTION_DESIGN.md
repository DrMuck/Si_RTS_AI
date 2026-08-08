# Military production and manoeuvre — the next architecture

**DrMuck's remarks after watching the 2026-08-08 replays, and what each one costs
to answer. Written before building, because three of these are blueprint-shaped
and one of them is the same mistake the economy already made once.**

Companion to `MILITARY_DESIGN.md` (the plan), `MILITARY_TACTICS.md` (missions and
the four tactics) and `MILITARY_IMPLEMENTATION.md` (what exists).

---

## What the replays actually showed

Three 40-minute rounds on NarakaCity, v0.44.0, against vanilla Sol and Centauri.
The composition prior worked — 255 Shockers and 41 Behemoths, zero Dragonflies,
zero Firebugs — and the aliens traded at **1.83:1 in cash** across 73 two-sided
engagements. The Nest survived every round.

The remarks are all about what happened *around* that: production shape, and what
the units did once they existed.

---

## 1. Production is a rate problem being solved as a placement problem

> "In the best case all free resources should be use for military production.
> This includes how many producers (cysts) are required to not sit on money too
> long."

The layer had one rule — build one of each higher-tier producer, ever. So a
single Greater Spawning Cyst was the ceiling on the entire army: **41 Behemoths
in forty minutes while 255 Shockers came off the Lesser Cysts**. That reads as a
composition choice and is not one. It is a throughput limit.

**Shipped in v0.45.0:** `maxProducersPerType` (default 3), with the count
following unspent cash — roughly one producer per 6,000 of idle budget. Crude on
purpose; it is a rate comparison, not a plan.

**What it should become.** The economy already solved this exact problem and the
answer was `WorkerPlan`: a *trajectory* to hit, from which producer count is
derived, rather than a count someone picked. The military equivalent is an army
value curve — how much army do we intend to have by minute N — and producers
follow from the gap the same way Cysts follow from the worker gap. That also
gives the push trigger something it currently lacks: a notion of being *ahead of
or behind* plan, rather than only "growth has flattened".

The honest reason not to build that today is that nobody knows what the target
curve should be. One number would settle it: army value at minute 20 in rounds
the aliens actually won. The archive can answer that and has not been asked.

## 2. Placement — the current fix is a stopgap and should be named as one

> "Military cant apply pressure from the distance. In many cases it could be more
> efficient to place producers more to the front lines or FOBs to save delay in
> walking distance."

Correct, and the old rule was the worst possible case: every producer at the
Nest, so every unit walked the full radius of the base before it mattered. On
NarakaCity that is most of a minute per unit, paid on every unit, forever.

**Shipped:** producers anchor on the highest-scoring `DefencePlanner` task,
pulled 35% back toward the Nest so they sit *behind* the line they cover, falling
back to the Nest when nothing is threatened.

**Why that is not the answer.** It sites against *current* threat, which means
the producer arrives where the last attack was. A real FOB is sited against where
the fighting will be, which is a function of the frontier `Blueprint` is already
planning and the approach the enemy has been using. The inputs exist; the pass
does not.

The rule that would actually earn it: **a producer belongs where its units'
expected walk to the next fight is shortest, discounted by how defensible the
ground is.** Both terms are computable — `ControlMap.ControlGain` for the second,
the mission portfolio for the first.

## 3. Producers as cover, and the army that grows out of them

> "producers should also be able to protect ongoing expansion and later using
> that units for a push."

This is the economic-push posture from `MILITARY_TACTICS` §5, arriving from the
production side instead of the mission side, and it is the strongest idea in the
remarks. A producer at an expansion is a defence that costs nothing to garrison
— units appear where they are needed and accumulate into the push force rather
than being drawn from it.

It needs §1 and §2 first: you cannot site producers along the frontier until
siting is frontier-aware, and you cannot afford several until the count scales.

---

## 4. Formation — now unblocked, and the docs said when

> "units were produced around the nest, but they had no decent formation"
> "Units were sent at some time straight to the enemy HQ in a stream. No
> formation no consideration of different unit speed."

`MILITARY_TACTICS` §7 deferred formations with a specific unblock condition:
*"its payoff cannot be seen until engagements are measured"*. Engagements are now
measured — 73 of them, at 1.83:1. The condition is met.

The same section says what to build first, and it is not geometry:

> **Concentration before geometry.** The cheap version is *which sub-group goes
> where*. Most of the value of "weight the strong flank" is available by
> splitting an assigned force into two unequal sub-groups with different
> objectives — no new movement machinery.

**The stream is a separate defect and cheaper to fix.** A battalion issues every
unit the same destination and they arrive in speed order: Wasps at 35, Behemoths
at 9. That is trickling in by another route — the readiness rule holds the force
until it is strong enough to *leave*, and nothing holds it together while it
*travels*.

Two candidate rules, and the first is nearly free:

- **Stage, then commit.** A push moves to a rally point short of the objective,
  waits until its slowest members arrive, and only then advances. One extra
  state, and it converts a stream into a mass.
- **Move at the speed of the slowest.** `AgentMoveSpeed` is an enum, not a
  number, so this is coarser than it sounds and may not be expressible.

Staging first. It reuses `Rally`, which the battalion already carries and
currently only uses for the trip home.

## 5. Prediction and pre-positioning

> "Units did not counter enemy attacks well. Enemy attacks need to be predicted
> and units need to be positioned at FOB, expansion in time to counter that"

The measured shape of the failure: across three rounds, defend battalions read
`Forming 0u` **150 times** against `Committed 19u` ten times. Not because the
allocator starved them — that bug is fixed — but because a defend mission sizes
its requirement from *enemy cash currently visible near the asset*. No enemy
visible, requirement zero, no force sent. The AI is perfectly reactive and
therefore always late by exactly the travel time.

**What would fix it, in increasing order of honesty:**

1. **A standing share per earning site.** Cheap, and it is a garrison floor
   generalised outward: any Bio Cache earning above some threshold holds a small
   force regardless of threat. Cannot predict anything, but it removes the travel
   time on the most valuable ground.
2. **Threat memory per site.** `DefencePlanner` already keeps `PeakHomeThreat`
   for the Nest and forgets it everywhere else. Per-site peak, decaying over
   minutes rather than seconds, means ground that was raided once keeps a guard.
   This is prediction in the only sense the data supports: *this opponent has
   attacked here before.*
3. **Approach vectors.** Which direction incursions arrive from, accumulated over
   the round. `ThreatMap` has the field and throws the history away.

(2) is the right first build. It is one dictionary, it uses a rule the codebase
already trusts at home, and it fails safe — the worst case is a small force
parked at ground that is no longer contested.

---

## 6. Viewer

> "add heatmap that shows enemy army pressure"
> "add a map that shows military tactics planning of rts ai. E.g. defense here,
> relocate army here (also with pointing arrows maybe)"

**Both shipped in v0.45.0**, because the data already existed and was being
discarded.

- `enemy_pressure` and `enemy_value` layers, written into every replay snapshot
  alongside the eco layers. Two fields rather than one on purpose: pressure is
  where it is *dangerous to stand* and spreads with weapon range; value is where
  their army physically *is*. Reading them together is how you tell a screen from
  a doom-stack.
- `/military` endpoint: posture, army value, enemy estimate, the enemy mix by
  class, every mission with its objective and price, and every battalion with a
  `from` (centroid of its units) and a `to` (its objective). **That pair is the
  arrow.** State comes with it, because `Forming` — wants to go, cannot — and
  `Committed` — is going — must not draw the same.

The viewer front-end still has to consume it. Suggested treatment, in the spirit
of the existing layer viewer: pressure as a heat overlay, missions as pins scaled
by `requiredValue`, battalions as arrows whose thickness is `value` and whose
style is the state — dashed for Forming, solid for Committed.

---

## Build order

Not "all of it", and it does not start with the fun part.

0. **Read army value at minute 20 in won rounds, out of the archive.** Data work,
   no code. It is the target curve §1 needs and the sanity check on everything
   else.
1. **Staging before commit** (§4). Cheapest thing here, fixes the visible stream,
   and reuses machinery that already exists.
2. **Per-site threat memory** (§5.2). Makes defence anticipatory without
   pretending to predict.
3. **Army-value trajectory → producer count** (§1). Replaces the cash heuristic
   shipped today with the same shape `WorkerPlan` uses.
4. **Frontier-aware producer siting** (§2), which then makes §3's producers
   double as expansion cover (§3).
5. **Sub-group concentration** (§4), geometry only if the exchange ratios say
   shape pays.

Formations proper stay last, and for the reason `MILITARY_TACTICS` gave: it is
the item most likely to feel like progress and least likely to be measurable.
