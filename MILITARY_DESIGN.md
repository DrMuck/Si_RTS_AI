# Military model — interfaces and build order

Response to `bot-design-spec.md` §2–§3, written against the code that exists rather
than in the abstract. **Design only; nothing here is implemented yet.**

The spec's module list is the right end state. What follows is what each module can
already stand on, the two contracts the spec does not yet name, and the order I
would build in — which is deliberately not "all of it".

---

## 1. What already exists

| Spec module | Existing code | State |
|---|---|---|
| Threat analysis | `Perception/ThreatMap.cs` — `Observe`, `Tick`, `ThreatNear(pos, r)` | HVT list + threat sampling. No capability classes, no FPS-player tracking, no decay. |
| Map control | `Perception/ControlMap.cs` — `HeldFraction`, `ControlGain(pos, r)` | Live. Already consumed by expansion. |
| Node cutting | `Planning/NodeManager.cs` — `TryGetRepair`, `TryGetLoop`, `IsDecaying` | Repair and loop-closing are live. Anchor-nest is not. |
| Bridging | `Planning/Blueprint.cs` — `BridgePass`, valued in seconds-saved per cash | Live; builds the best one from surplus. |
| Money | `Planning/MoneyBroker.cs` — `IActionSource`, `GetReservedCash` | Reservation exists; military is not a claimant yet. |
| Production | `Faction/MilitaryManager.cs` (646 lines, opt-in, `MilitaryEnabled=false`) | Perception + Crab production. Army coordination stubbed. |
| Composition | `Strategic/CompositionPlanner.cs` | Exists for Sol; not wired to alien threat. |

So §2.3 and §2.5 are partly built, and §2.4 has a blueprint to bias rather than a
placement engine to write. The genuinely empty boxes are the strategy planner, army
manager, and formations.

---

## 2. The contract the spec is missing: population

Eco and military spend the **same 185-unit ceiling**, and nothing arbitrates it.
`WorkerPlan` will drive toward ~170 shrimps if left alone, which leaves fifteen
units for an army — and the failure will present as "the army never arrives" while
the cause is an eco setting.

This wants the same shape as the money broker:

```
PopulationBroker
  consumes: WorkerPlan trajectory + yield, ThreatMap pressure, active strategy
  produces: workerBudget, armyBudget   (sum <= hard cap, both floors honoured)
```

Two rules I would start with, both derived rather than tuned:

- **A worker that cannot earn is an army slot.** `WorkerPlan.YieldFalling` already
  says when marginal workers stop converting into income. That is precisely the
  moment population is worth more as army than as economy — the same signal, one
  more consumer.
- **A defended economy has a floor.** Army budget never falls below what standing
  garrison duty needs (§2.2 item 1), because the Queen is a loss condition and
  loss conditions are not traded against income.

Until this exists, any military build competes with a worker trajectory that does
not know it is in a competition.

## 3. The other missing piece: a combat measurement loop

Eco only started improving when rounds produced numbers — `benchmarks.jsonl`, the
per-second cashflow, and later per-Bio-Cache attribution. §2.7 (critical mass) and
§2.8 (strategy hysteresis) are exactly the kind of rules that look right and cannot
be checked without one.

Minimum viable, and it should land **before** the first engagement rule:

```
combat.jsonl, one row per engagement
  { t, where, ourValueLost, theirValueLost, ourComposition, theirComposition,
    missionType, outcome }
```

From that: exchange ratio per composition, per mission type, and per critical-mass
threshold. Without it, "never trickle units in" is folklore we happen to agree with.

## 4. Ordering (what I would actually build)

1. **PopulationBroker** — the contract above. Small, and it unblocks everything else
   from silently starving.
2. **Combat instrumentation** — `combat.jsonl` plus engagement detection. No
   behaviour change; it can run while the eco soak continues.
3. **Defence** (§2.2) — garrison floor, threat-triggered recall, defending *tapped*
   biotics only. Measurable immediately as income lost to raids, which the existing
   eco metrics already show.
4. **Threat analysis upgrade** (§2.3) — capability classes, staleness decay, FPS
   player tracking. Defence gives it a consumer first, so it is built against a use.
5. **Strategy planner + army manager** (§2.8, §2.7) — with hysteresis, once step 2
   can say whether a re-plan helped.
6. **Military production siting** (§2.4) — biasing the blueprint toward FOBs. Comes
   after 3–5 because the risk term needs a threat map that is worth trusting.
7. **Anchor nest** (§2.5) — extends `NodeManager`; naturally paired with 6.

Deferred with reasons rather than dropped:

- **Learned counter-matrix** (§2.6). Ship the hand-authored prior; the learning half
  needs a match-history pipeline that does not exist. Half-built learning is worse
  than an honest table.
- **Formations** (§2.9). Real work, and its payoff cannot be seen until engagements
  are measured. After step 2, not before.

## 5. Two corrections to the spec's framing

**Queen safety is a constraint, not priority #1.** A ranked list implies it can be
traded against priority #2 when the economy is desperate. It cannot — it is a loss
condition. Model it as a floor on garrison and a veto on orders that strip home
defence, not as the top row of a priority table.

**"Deny enemy expansion" (§2.1 item 1) needs a discovery precondition.** It ranks
above eco damage, but we only see discovered structures (§2.3), and scouting is
currently tuned for *biotics* discovery. Acting on it means scouting has a second
customer with different priorities — enemy expansions rather than resource patches
— and that likely changes the scout star's targeting. Worth deciding before the
strategy planner assumes the information is there.

## 6. What this does not answer

Combat effectiveness of specific alien units, tier timings, and whether Crab
swarms trade well at all. Those are questions for step 2's data, and they should be
answered from rounds rather than from the design doc.
