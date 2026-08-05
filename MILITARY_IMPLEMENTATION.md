# Military — what is actually built

Companion to `MILITARY_DESIGN.md` (which is the plan). This file is the state of
the code, so a later session does not have to reconstruct it from commits.

**Nothing here is deployed or switched on.** All of it is shadow or observation.
Written 2026-08-05 while the eco producer-density soak was running.

---

## Files, in the order they run

| File | Version | Does |
|---|---|---|
| `Perception/UnitCaps.cs` | v0.21.2 | Reads `UnitCapType` / `UnitCapValue` per unit from live ConstructionData once per round. Exposes `WorkersConsumeCap`, `WeightOf(name)`, `CapTypeOf(name)`. Also probes `Team` for cap-ish members and logs them. |
| `Perception/UnitValues.cs` | v0.22.1 | One cash-cost lookup per unit name, shared by the combat log and the battalion manager. |
| `Perception/CombatLog.cs` | v0.21.3 | Detects engagements by roster diffing and writes `UserData/RTSA/combat.jsonl`. |
| `Perception/BcIncome.cs` | v0.21.1 / v0.22.0 | Per-Bio-Cache income attribution, plus `RecentDeposited(pos)` over a 120s window and `ForEach`. |
| `Planning/DefencePlanner.cs` | v0.22.0 | Ranks threatened assets by *recent income × threat*; sizes the home garrison floor. |
| `Planning/BattalionManager.cs` | v0.22.1 / v0.23.0 | Forms battalions, judges readiness in cash value, issues and restates move orders. |

Tick order per team, from `AlienConstruction.HandleTick`:
`BcIncome.Sample` → `UnitCaps.Resolve` → `DefencePlanner.Tick` → `BattalionManager.Tick`.
`CombatLog.Tick` runs per frame from `Si_RTS_AI.OnUpdate`, independent of teams.

## Switches

| Preference | Default | Effect |
|---|---|---|
| `DefenceEnabled` | **true** | DefencePlanner and BattalionManager compute and log. No orders. |
| `DefenceExecute` | **false** | Battalions actually order units. This is the only switch that changes behaviour. |
| `MilitaryEnabled` | false | The older `MilitaryManager` scaffold (production + HVT picking). Unrelated to the above and not yet reconciled with it. |

**For a clean eco experiment, set `DefenceEnabled=false`** — the compute is cheap but
the log lines are noise in a run that is about economy.

## What the log looks like

```
[UNITCAP]   resolved from game: Shrimp=Secondary/0 Crab=Secondary/1 ... | workersConsumeCap=False
[UNITCAP]   team cap-ish members: ...
[BC/INCOME] attributed=412300 teamCumulative=486120 ratio=0.85 bcs=22 earning=17 untapped=5
[DEFENCE]   garrison=6 (peak home threat 142) threatened=3 | site (1652,905) earned 4200 threat 88  [shadow]
[BATTALION] garrison(garrison) Ready 6u val 960 | resp2(response) Forming 3u val 480/1200 -> (1652,905)  [shadow]
[COMBAT]    engagement at (1620,-430) over 18s — Team_Alien lost 6 (960 value) Team_Human_Sol lost 2 (700 value)
```

## The rules that are encoded

- **Defend what earns, not what cost.** Asset score is `RecentDeposited × ThreatNear`.
  A Bio Cache on a drained patch scores zero however much it cost.
- **The Queen is a constraint, not a priority.** Home garrison is a floor subtracted
  before anything else is allocated, sized from the worst incursion seen near the
  Nest, and it does not decay within a round.
- **Never trickle in** = only `Ready` battalions are given a destination. Readiness is
  `value >= requiredValue`, in cash.
- **Hysteresis** = a 30s assignment dwell before a battalion may be re-tasked.
- **Released by the condition that raised it** — a committed battalion returns when
  threat at its objective is gone, not on a timer.
- **The garrison is kept, not sent** — only units past a 250m leash are recalled.
- **Orders are restated every 5s**, with a stacking Harmony prefix on `OnMoveOrder`
  so vanilla cannot re-task a committed unit. Inert while `DefenceExecute` is off.

## Placeholders — all three calibrate off the same data

| Constant | Where | Waiting on |
|---|---|---|
| `THREAT_PER_DEFENDER = 25` | DefencePlanner | Exchange ratios from `combat.jsonl` |
| `STRENGTH_MARGIN = 1.5` | BattalionManager | Same |
| `MilitaryCriticalMassSize = 15` | MilitaryManager (old) | Same; superseded by value-based readiness |

They are labelled as placeholders in the source. Inventing precision before the
data exists is how the beam's scoring rules happened.

## Not built

- Offence of any kind — no attack orders, no target selection beyond the old
  `MilitaryManager` HVT list.
- Strategy planner and missions. `combat.jsonl` writes `missionType:"unknown"`
  deliberately rather than inventing one.
- Threat capability classes, staleness decay, FPS-player tracking.
- Military production siting / FOBs, anchor nest.
- Formations.
- Reconciliation between `MilitaryManager` (old scaffold) and the new defence and
  battalion layers. **They currently overlap** — both can decide something about
  combat units — and `MilitaryEnabled` should stay false until that is resolved.

## Suggested bring-up order

1. Deploy with `DefenceExecute=false`. Confirm `[UNITCAP]` reads sensible weights,
   `[DEFENCE]` picks assets a human would defend, `[BATTALION]` groups sanely.
2. Turn on `DefenceExecute`. Watch that the garrison stays home, responses arrive
   together, and units are not tugged between vanilla and us.
3. Only then read `combat.jsonl` for exchange ratios and replace the three
   placeholders with measured numbers.
4. Offence after that, because "deny enemy expansion" needs both a strategy layer
   and scouting that looks for enemy structures rather than biotics.
