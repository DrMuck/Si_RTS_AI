# Si_RTS_AI — Architecture

Sorting the ideas from `new 4.txt` into a coherent block diagram, so we build the AI in the right order.

## 1. Three-layer block diagram

```
                    ┌─────────────────────────────────────────────────────────────┐
                    │                  PERCEPTION LAYER                            │
                    │        (fed by GameEvents + tick observability)             │
                    ├─────────────────────────────────────────────────────────────┤
                    │                                                              │
                    │   ┌─────────────┐  ┌──────────────┐  ┌──────────────────┐  │
                    │   │ WorldModel  │  │ MapKnowledge │  │ EnemyIntel        │  │
                    │   │ HQs, units, │  │ visibility   │  │ last-seen units,  │  │
                    │   │ structures, │  │ mask,        │  │ estimated comp,   │  │
                    │   │ income,     │  │ resource     │  │ base positions,   │  │
                    │   │ tech, cash  │  │ patches, ter.│  │ threat direction  │  │
                    │   └──────┬──────┘  └──────┬───────┘  └────────┬─────────┘  │
                    └──────────┼─────────────────┼──────────────────┼────────────┘
                               │                 │                   │
                               ▼                 ▼                   ▼
        ┌────────────────────────────────────────────────────────────────────────┐
        │                        STRATEGIC LAYER                                  │
        │             (per-team, re-evaluated every ThinkInterval)                │
        ├────────────────────────────────────────────────────────────────────────┤
        │                                                                         │
        │   ┌──────────────────────────────────────────────────────────────┐    │
        │   │                    Weighting Engine                            │    │
        │   │       eco% / expansion% / military% split every tick          │    │
        │   │   (phase-aware + threat-aware + income-aware; see §4)          │    │
        │   └────────────────────────────┬─────────────────────────────────┘    │
        │                                │                                        │
        │        ┌───────────────────────┼───────────────────────────┐            │
        │        ▼                       ▼                            ▼            │
        │  ┌───────────────┐  ┌────────────────────┐  ┌────────────────────────┐│
        │  │  Build Order  │  │  Sub-Strategy      │  │  Composition Planner    ││
        │  │  Planner      │  │  Selector          │  │  Early / Mid / Late     ││
        │  │               │  │                     │  │  + counter picks       ││
        │  │  Faction-     │  │  1-3 concurrent:    │  │  based on enemy comp   ││
        │  │  aware:       │  │  - eco harass       │  │                         ││
        │  │  Sol/Cent →   │  │  - expa suppress    │  │  Outputs: unit-type    ││
        │  │  HQ→Refin→    │  │  - guerilla         │  │  quotas per tick       ││
        │  │  factories    │  │  - defense          │  │                         ││
        │  │  Alien →      │  │  - HQ / Queen rush  │  │                         ││
        │  │  Nest→Cyst→   │  │  - combined ops     │  │                         ││
        │  │  node chain   │  │                     │  │                         ││
        │  └───────┬───────┘  └──────────┬─────────┘  └───────────┬─────────────┘│
        │          │                     │                          │              │
        │          │                     ▼                          │              │
        │          │            ┌────────────────────┐              │              │
        │          │            │  Scout Planner     │              │              │
        │          │            │  which units go    │              │              │
        │          │            │  where to reveal   │              │              │
        │          │            │  resources / enemy │              │              │
        │          │            │  expansion         │              │              │
        │          │            └──────────┬─────────┘              │              │
        └──────────┼────────────────────────┼──────────────────────┼──────────────┘
                   │                        │                       │
                   ▼                        ▼                       ▼
        ┌────────────────────────────────────────────────────────────────────────┐
        │                        EXECUTION LAYER                                  │
        │      (converts strategy decisions into concrete Silica API calls)       │
        ├────────────────────────────────────────────────────────────────────────┤
        │                                                                         │
        │   ┌──────────────────┐   ┌───────────────────┐   ┌──────────────────┐│
        │   │  Construction    │   │  Unit Production  │   │  Order Emitter   ││
        │   │  Emitter         │   │  Emitter          │   │                  ││
        │   │                  │   │                    │   │  Groups units    ││
        │   │  Respects:       │   │  Enqueues into     │   │  into battalions ││
        │   │  - build radius  │   │  factory generators│   │  by role         ││
        │   │  - visibility    │   │  to match comp     │   │  Rate-limits:    ││
        │   │    (humans)      │   │  quotas            │   │  1 order per     ││
        │   │  - node chain    │   │                    │   │  unit per 3-5s   ││
        │   │    (aliens)      │   │  Never over-queues:│   │  unless in combat││
        │   │  - terrain flat  │   │  max 1 unit of     │   │                  ││
        │   │  - LOS to        │   │  each type queued  │   │  Battalion-      ││
        │   │    resource      │   │                    │   │  aware position: ││
        │   │                  │   │                    │   │  cliffs, valleys ││
        │   └────────┬─────────┘   └─────────┬─────────┘   └────────┬─────────┘│
        └────────────┼───────────────────────┼──────────────────────┼───────────┘
                     │                       │                       │
                     ▼                       ▼                       ▼
             RPC_ConstructRequest   RPCModifyGenerator      Unit.OnAttackOrder
             (place a structure)   (add to production Q)   Unit.OnMoveOrder
                                                            Unit.OnStopOrder
```

## 2. Where each of your ideas lives

Reading straight off `new 4.txt`:

| Your idea (§ from `new 4.txt`)                 | Block it lives in                          | Notes |
|--|--|--|
| **A. Build radius max constraint**              | Construction Emitter (guard)               | Also applies to Alien node chain connectivity |
| **A. Human visibility-restricted placement**    | Construction Emitter (per-faction rule)    | Sol/Cent only: query current fog-of-war mask before placing |
| **A#1. Human eco: HQ → Refinery near balterium** | Build Order Planner (Sol/Cent branch)     | Constraint: refinery ramp face resource — geometry check in Construction Emitter |
| **A#1. Expansion HQs at max HQ2HQ radius**       | Build Order Planner + Construction Emitter | Radius as constraint, near-balterium as scoring heuristic |
| **A#1. Multi-HQ expansion for exponential eco**  | Build Order Planner (long-horizon plan)    | 2+ expansion HQs when income supports it |
| **A#2. Alien eco: BioCache + Shrimp + Cyst**     | Build Order Planner (Alien branch)         | Target: 3 biotics per base setup, 10 shrimps/biotics |
| **A#2. Alien node chain die-off if cut**         | Construction Emitter (Alien branch)        | Constraint: enforce continuous chain-to-Nest connectivity when placing new nodes |
| **A#3. Tempo — eco → tech → prod → scout/def**  | Build Order Planner (phase machine)        | Standard early-game arc; per faction different structure list |
| **A#4. Eco/expansion/military weighting**        | **Weighting Engine** (top-level dispatch)  | The most important block for strategic feel |
| **A#5. Strategic building placement**            | Construction Emitter (scoring)             | Score candidate positions by: distance-to-enemy, resource proximity, chokepoint value |
| **C. Scout for resources / enemy expansions**    | Scout Planner + EnemyIntel feedback        | Assigns cheap units to spiral-out or head to plausible enemy expansion points |
| **D#1. Eco harassment**                          | Sub-Strategy Selector                      | Target: enemy harvesters, refineries, biotic caches, node chains |
| **D#2. Base expansion suppression**              | Sub-Strategy Selector                      | Target: enemy in-construction structures + weakly defended new HQs |
| **D#3. Guerilla warfare**                        | Sub-Strategy Selector                      | Small squad routing to high-value low-defense spots |
| **D#4. Base defense**                            | Sub-Strategy Selector + Order Emitter      | Perimeter defense line, later becomes offense reserve |
| **D#5. HQ / Queen destruction**                  | Sub-Strategy Selector (endgame trigger)    | Fired when eco + army lead pass a threshold |
| **D#6. Combined operations**                     | Sub-Strategy Selector (multi-mode)         | Run 2-3 strategies concurrently with different units |
| **D#7. Battalion / terrain placement**           | Order Emitter                              | Think in squads, prefer high-ground / chokepoints |
| **E#1. Early / mid / late compositions**         | Composition Planner (phase logic)          | Phase determined by tick count + income + tech tier |
| **E#2. Counter compositions**                    | Composition Planner + EnemyIntel           | If enemy = air-heavy, request AA-heavy comp |
| **F. Enemy unit detection = only visible/scouted** | EnemyIntel (visibility gate on all data) | Every intel entry has an age + certainty; stale entries decay |

## 3. Cross-cutting constraints (must be enforced in every layer)

- **Build-radius rules** — all structures have max distance from HQ / anchor; Construction Emitter refuses out-of-range placements.
- **Visibility rules** — human factions can only place structures in currently-visible terrain; Alien nodes can extend into remembered (scouted) terrain but only along an unbroken chain to a Nest.
- **Resource rules** — never queue construction/production if `MissingResources > threshold` (we saw last round's Sol AI sitting on 13k unspent — the opposite failure mode).
- **Rate limiting** — no unit gets more than one order per 3-5 s unless it's in active combat. Immediate 10-20× reduction in the "twitchy micro" pattern.
- **Faction dispatch** — Sol / Centauri share most planners; Alien has its own Build Order + Construction branches for node chains + BioCache economy.

## 4. Weighting Engine — the heart

Every ThinkInterval, this block outputs a triple `(eco%, expansion%, military%)` summing to 100%. Sub-strategies read it to know how many units they may claim, Build Order reads it to know whether to prioritize a Refinery vs a Factory vs a Barracks.

Signals feeding it:
- **Phase** (from tick count + tech tier + structures built): Early / Mid / Late
- **Income delta**: are we falling behind on income? Push eco.
- **Threat proximity**: is an enemy squad within N units of our HQ? Push military.
- **Enemy tech lead**: is enemy comp signaling T3+ and we're at T2? Push tech via Construction.
- **Unit surplus**: do we have idle military? Push expansion (they can garrison the new HQ).
- **Recent losses**: lost a Refinery / node chain? Push repair or replacement.

We start with a **static per-phase default** and tune it via observed round summaries — the observability we already built.

## 5. Implementation order (phased)

Order chosen so each phase is testable in isolation and gives a visible improvement:

| Phase | Deliverable | Testable improvement |
|--|--|--|
| **3.0** ← now | Skeleton of all blocks + no-op passthrough. `AICommander` still runs; we just observe. | No behavior change, but new modules loaded and health-checked. |
| **3.1** | Suppress `AIUnitHandler.Think()` + our own Composition Planner + Unit Production Emitter (Sol first) | Sol AI stops hoarding 13k credits. Sol unit output should ~2× per unit type. |
| **3.2** | Add `AIConstructionHandler.Think()` suppression + Build Order Planner (Sol first) | Sol advances to T3/T4 reliably. Structure diversity: barracks + light + heavy + air + UHF instead of only light+refinery. |
| **3.3** | Faction dispatch: repeat 3.1 + 3.2 for Alien (BioCache / Cyst / node chain rules) | Alien economy stops collapsing when a node link dies. |
| **3.4** | Weighting Engine (still phase-static rules but per-team) | Team behavior varies by state — behind on eco → refinery push; ahead → HQ expansion. |
| **3.5** | Sub-Strategy Selector + Order Emitter with battalion grouping | m/a/s per unit drops from 30-97 to 3-5 (matches human commander). |
| **3.6** | Scout Planner + EnemyIntel with visibility gating | AI reacts to what it saw (not what would exist perfectly), enables counter-compositions. |
| **3.7** | Chat commands `/rtsai mode <team> <mode>`, `/rtsai status`, `/rtsai off`. Round summary now separates our-AI stats vs stock-AI stats. | Live control. |
| **3.8** | Per-faction personality knobs (aggro-vs-turtle) exposed via JSON config | Server admin can tune "AI strength" without rebuilding. |

## 6. What NOT to include yet (deferred)

- **LLM / model-based decisions** — everything above is heuristic + score-based. Fast, deterministic, no API key. Future-work notes for hybrid LLM+rules Strategic layer: see [`NOTES_LLM_INTEGRATION.md`](NOTES_LLM_INTEGRATION.md).
- **Learning across rounds** — no online adaptation for now. Round data is written to `UserData/RTSA/round-*.log` for offline analysis and tuning.
- **Per-map policies** — same policy on every map for MVP. Per-map overrides come after the core is validated.
- **Alien queen movement** — the Alien Queen (mobile HQ) has non-trivial mechanics; treat as static for MVP.
- **Air / naval micro** — keep to ground for the first version, expand once ground is solid.

## 7. Namespace layout in code (once we start Phase 3.1)

```
Si_RTS_AI/
├── Si_RTS_AI.cs               ← MelonMod entry, GameEvents subscriber (already exists)
├── Phase2Handlers.cs          ← Existing observability (kept, feeds Perception)
├── Perception/
│   ├── WorldModel.cs
│   ├── MapKnowledge.cs
│   └── EnemyIntel.cs
├── Strategic/
│   ├── WeightingEngine.cs
│   ├── BuildOrderPlanner.cs
│   ├── SubStrategySelector.cs
│   ├── CompositionPlanner.cs
│   └── ScoutPlanner.cs
├── Execution/
│   ├── ConstructionEmitter.cs
│   ├── UnitProductionEmitter.cs
│   └── OrderEmitter.cs
├── Faction/
│   ├── HumanRules.cs         (Sol + Centauri shared)
│   └── AlienRules.cs         (node chain, biocache, cyst)
└── Suppression/
    └── StockHandlerPatches.cs (Harmony prefix-return-false patches)
```

Each planner takes `PerceptionSnapshot` + `TeamState` in, returns a plain data struct (no side effects). Emitters do the side effects. Tests can be written in isolation because each planner is a pure function of its inputs.
