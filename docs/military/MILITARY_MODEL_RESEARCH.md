# How mature RTS AIs model this — notes for our production planner

**Research for DrMuck, 2026-08-08. He asked for high-level objectives worth
tracking and for architecture references before we design further. This is what
the field actually does, and which parts are worth stealing.**

Companion to `MILITARY_PRODUCTION_DESIGN.md` (our remarks-driven plan).

---

## 1. Goal-Driven Autonomy — the missing piece, and it is not a planner

Weber, Mateas and Jhala's GDA work on StarCraft is the closest thing to a direct
answer to "high level objectives that need to be kept track of". The insight is
not that you should have goals — everyone has goals — it is that **a goal must
come with an EXPECTATION, and the agent must notice when reality disagrees.**

The loop is four steps, and we currently implement one and a half:

| GDA step | What it means | Us |
|---|---|---|
| Goal formulation | pick what to pursue | `MissionPlanner` — yes |
| Plan generation | how to pursue it | `BattalionManager` — yes |
| **Expectation** | what should be TRUE if this is working | **nothing** |
| **Discrepancy detection** | notice when it is not | **nothing** |
| Explanation → new goal | decide what that means | partly — `abortWhen` |

Their framing: *"traditional game AI techniques do not reason about
expectations, making it impossible for an agent to detect discrepancies between
expected and actual game states."* That is us exactly. A push commits and we
find out it failed by reading a log the next morning, because nothing ever said
what success would have looked like.

**What this would change for us concretely.** A mission would carry, alongside
`requiredValue` and `abortWhen`, an expectation such as:

- Cover producer: *"enemy value near this Bio Cache should fall below X within
  90s of the force arriving"*
- Forward FOB: *"our army value should stop falling"* or *"their expansion count
  should stop rising"*
- Push: *"the objective's structure count should decrease"*

A violated expectation is a *self-reported failure*, and it arrives in seconds
rather than in a replay review. It is also exactly the signal a feedback loop
needs — you cannot learn from outcomes you never labelled.

The reported result was a 73% win rate against the built-in AI, which is
context, not proof — but the mechanism is cheap and we have none of it.

## 2. TorchCraftAI — objectives as progressive refinement, not separate decisions

Their core abstraction is the **UPCTuple**: a distribution over **U**nits,
**P**ositions and **C**ommands, posted to a **Blackboard** and *progressively
refined* by modules until it is concrete enough to be a game command.

    Strategy posts a vague UPC → AutoBuild turns it into a build order →
    BuildingPlacer refines the POSITION → Builder executes → UPCToCommand fires

The idea worth stealing is the one in the middle. **"Where to build" is a
refinement of "build something for this purpose", not an independent decision.**
Our code separates them — `MilitaryBlueprint` picks a spot, `MilitaryProduction`
picks a unit — and they can therefore disagree about what the building is for.
In their model the purpose travels with the request all the way down.

Also worth noting: units and positions are *distributions*, not points. A
strategy module says "somewhere over there, roughly these units", and downstream
modules sharpen it. That is a better fit for our Forward-FOB problem than
picking one exact position at planning time and being wrong.

## 3. UAlbertaBot / CommandCenter — the boring hierarchy that works

Strategy manager → Production manager (owns a build-order QUEUE) → Building
manager (placement) → Worker manager. `CommandCenter` is explicitly the reusable
skeleton of it.

Two details we do not have:

- **The production manager owns a queue, not a per-tick decision.** It follows
  an ordered plan and produces items as prerequisites and resources allow. Our
  producer picks a unit fresh every tick, which is why the composition is a
  monoculture: whatever scores highest wins every single time. A queue can hold
  *"2 Behemoth, 4 Shocker, 1 Defiler"* and that is a composition rather than a
  winner.
- **Building placement is a separate manager**, so it can reserve space and
  refuse a request, rather than being a helper function inside production.

## 4. BWEB — placement is precomputed geography, not a per-tick score

The strongest practical idea in the search, and the one most different from what
we do. BWEB precomputes three kinds of spatial furniture per map:

- **Blocks** — packed rectangles of production real estate, sized so buildings
  do not trap units. Production goes in Blocks.
- **Stations** — one per base location, *including defence positions that cover
  the workers*.
- **Walls** — chokepoint configurations, with a reserved path so your own units
  can still get out.

We recompute placement geometry live, every time, from threat and income. BWEB
computes the map's *structure* once and then only chooses among slots. That is
both far cheaper and far less prone to the "producer ends up somewhere daft"
failure, because the candidate set was never daft to begin with.

The Station concept in particular is our Cover purpose done properly: a base is
not a point, it is a slot layout that already knows where a defender should
stand.

## 5. Influence maps — we have one and should say so

Threat/potential fields are the standard spatial primitive (Uriarte & Ontañón).
`ThreatMap` is an influence map; `ControlMap` is another. What the literature
adds is that decisions are usually taken on *combinations* — e.g. a placement
score is `own influence − enemy influence`, and a retreat threshold is a
contour. We only ever query ours at points.

---

## What I would actually take from this

In order of value per unit of work:

1. **Expectations on missions (GDA).** Cheap, and it converts "watch the replay
   and see" into "the log says the plan failed and when". Everything else we
   want to measure depends on having labels, and this generates them.
2. **A production QUEUE instead of a per-tick pick.** Directly fixes the Shocker
   monoculture, because a queue expresses proportions and a score cannot.
3. **Precomputed placement slots per base** (BWEB Stations). Turns placement
   from geometry-under-fire into a choice among known-good positions, and gives
   the Cover purpose somewhere sensible to put a producer.
4. **Purpose travelling with the request** (UPC-style), so production and
   placement cannot disagree about what a building is for.

Deliberately NOT taking: the neural building-placement model from TorchCraftAI,
and the learned macro-management work. Same reason as before — we cannot debug
what we cannot read, and we do not have the training signal.

## Sources

- Weber, Mateas, Jhala — *Applying Goal-Driven Autonomy to StarCraft*,
  https://cdn.aaai.org/ojs/12401/12401-52-15929-1-2-20201228.pdf
- Weber — *Learning from Demonstration for Goal-Driven Autonomy*,
  http://alumni.soe.ucsc.edu/~bweber/pubs/Weber-AAAI-2012.pdf
- TorchCraftAI core abstractions (UPC / Blackboard / Modules / Tasks),
  https://torchcraft.github.io/TorchCraftAI/docs/core-abstractions.html
- CommandCenter (UAlbertaBot architecture, reusable skeleton),
  https://github.com/davechurchill/commandcenter
- BWEB — Blocks, Stations, Walls, https://github.com/Cmccrave/BWEB
- Locutus (Steamhammer fork, uses BWEB for placement),
  https://github.com/bmnielsen/Locutus
- Churchill & Buro — *Build Order Optimization in StarCraft*,
  https://cdn.aaai.org/ojs/12435/12435-52-15963-1-2-20201228.pdf
- awesome-starcraftAI (curated index),
  https://github.com/SKTBrain/awesome-starcraftAI
