# Military model — interfaces and build order

Response to `bot-design-spec.md` §2–§3, written against the code that exists rather
than in the abstract. **Design only; nothing here is implemented yet.**

The spec's module list is the right end state. What follows is what each module can
already stand on, one contract the spec does not name, one I proposed and then had
to withdraw, and the order I would build in — which is deliberately not "all of it".

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

## 2. Population: I had this wrong, and the correction removes work

I claimed eco and military spend the same 185-unit ceiling, and that the
PopulationBroker was the sharpest coupling between them. DrMuck: *"There is a unit
cap for smaller and larger units! And the unitbalance Mod disables shrimps counting
to the lesser unit cap. Military units count to the unit cap."*

Checked against the live constants dump, which the server wrote with the balance
mod active:

```
Shrimp      UnitCapType = Secondary   UnitCapValue = 0
Harvester   UnitCapType = Primary     UnitCapValue = 0
```

So there are **two caps**, workers are typed onto them (Shrimp secondary, Harvester
primary), and under this balance config a **Shrimp's cap weight is zero** — workers
consume no cap at all. What I called a shared 185-unit ceiling is
`AlienShrimpProducer.SHRIMP_HARD_CAP`, which is **our own** server-performance
constant, not the game's limit.

Consequences:

- **There is no population tug-of-war to arbitrate.** Producing shrimps does not
  take slots from the army under this configuration. The PopulationBroker as I
  described it is not needed, and building it would have solved a problem that does
  not exist.
- **The genuinely shared resource is cash**, which already has a broker. Military
  becomes a third `IActionSource` claimant beside eco and tech — the seam the
  planner notes already anticipated — plus build capacity and server performance.
- **The dependency is one-directional instead:** military spending slows the worker
  trajectory through cash, and `WorkerPlan.BehindSchedule` will register that as a
  deficit and try to spend *more* on producers. That feedback is worth watching, and
  it is a money question, not a population one.

**Read the caps at runtime rather than assuming either way.** The zero weight is a
balance-mod choice; in vanilla, or under a different config, shrimps count again and
the tug-of-war returns. `UnitCapType` / `UnitCapValue` are already visible in
`ConstructionData`, so the military layer should ask rather than hardcode — the same
rule the eco layer already follows for costs and ranges.

What I could NOT confirm from the dump: the cap weights of individual military
units, and where the per-team cap limits live. Every sampled entry read
`UnitCapValue = 0`, which is either the mod zeroing broadly or the dump capturing a
template rather than resolved values. That needs a live read before any military
production planner prices a unit in cap terms.

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

1. **Combat instrumentation** — `combat.jsonl` plus engagement detection. No
   behaviour change; it can run while the eco soak continues.
2. **Defence** (§2.2) — garrison floor, threat-triggered recall, defending *tapped*
   biotics only. Measurable immediately as income lost to raids, which the existing
   eco metrics already show.
3. **Threat analysis upgrade** (§2.3) — capability classes, staleness decay, FPS
   player tracking. Defence gives it a consumer first, so it is built against a use.
4. **Strategy planner + army manager** (§2.8, §2.7) — with hysteresis, once step 1
   can say whether a re-plan helped.
5. **Military production siting** (§2.4) — biasing the blueprint toward FOBs. Comes
   after 2–4 because the risk term needs a threat map that is worth trusting.
6. **Anchor nest** (§2.5) — extends `NodeManager`; naturally paired with 6.

Deferred with reasons rather than dropped:

- **Learned counter-matrix** (§2.6). Ship the hand-authored prior; the learning half
  needs a match-history pipeline that does not exist. Half-built learning is worse
  than an honest table.
- **Formations** (§2.9). Real work, and its payoff cannot be seen until engagements
  are measured. After step 1, not before.

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

## 6. Built so far (shadow, not deployed)

- `Perception/UnitCaps` — cap type and weight per unit, read live. Answers
  `WorkersConsumeCap` instead of assuming it, and probes the Team object for the
  per-team limit, which is still unknown.
- `Perception/CombatLog` — one row per engagement in `combat.jsonl`, detected by
  roster diffing rather than a death hook. Exchange ratios in cash.
- `Perception/BcIncome.RecentDeposited` — what a structure earned in the last two
  minutes, which is what makes "defend what earns" computable.
- `Planning/DefencePlanner` — ranks threatened assets by recent income x threat,
  and sizes a home garrison floor from the worst incursion actually seen. Home is
  a floor subtracted first, never a competitor in the ranking. `DefenceEnabled`
  computes and logs; `DefenceExecute` (off) is what would issue orders.

The remaining defence work is the ORDER side — pulling units to a task and
returning them — which is deliberately not written until a round of `[DEFENCE]`
lines shows the ranking picks sane assets, and `combat.jsonl` says what an
engagement costs. `THREAT_PER_DEFENDER` is a placeholder until then and is
labelled as one in the source.

## 7. What this does not answer

Combat effectiveness of specific alien units, tier timings, and whether Crab
swarms trade well at all. Those are questions for step 2's data, and they should be
answered from rounds rather than from the design doc.
