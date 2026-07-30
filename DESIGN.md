# SilicaRTSA — Design & Investigation

## 1. What Silica's built-in AI looks like

The good news: Silica ships with a **modular commander AI** we can plug into piecewise instead of writing from zero. Everything is in the `Silica.AI` namespace of `SilicaCore.dll`.

### Class map

```
AIManager (static)
├─ Commanders: Dictionary<Team, AICommander>
├─ TickAI()               ← called from the main game tick loop
├─ AddCommander / RemoveCommander / EnableCommander(team, bool)
└─ GetCommander(team)

AICommander (one per team)
├─ Team, Settings (AICommanderSettings), IsEnabled
├─ Requests: List<AIRequest>         ← work queue the handlers write to
├─ Groups:   List<AIGroup>           ← squad-level grouping
├─ Tick(dt) → Think()  ← main entry (public, NOT virtual)
├─ AIResourcesHandler        (harvesting)
├─ AIExplorationHandler      (scouting)
├─ AIDefenseHandler          (defense)  ← weakest, biggest complaint target
├─ AIOffenseHandler          (attacks)  ← weakest, biggest complaint target
├─ AIUnitHandler             (unit production)
└─ AIConstructionHandler     (building placement)

AIHandlerBase (abstract)
└─ 6 concrete handlers above; at least 2 have `virtual Think()`

AIRequest  (Construct | Unit | None)
  + EAITaskType: None / Protect / Guard / SeekAndDestroy / ...

AICommanderSettings (ScriptableObject)
  + ThinkInterval (default 3s)
  + EnableExplore, EnableHarvest
  + Attack_DefenseToOffenseRatingMultiplier (default 3.0)
  + GroupPresetsConfig (AIGroupsConfig) — canned unit compositions
```

Alien-specific agents (`AIAlienAgent`, `AIAlienHarvesterAgent`, `AIAlienQueenAgent`) are handled via `AIBaseAgent` — same commander runs for aliens and humans.

There's a **global kill switch**: `AIBaseAgent.AIDisabled` (static bool) — if true, every commander's `Think()` early-returns.

There's a **network sync path**: `AIManager.UpdateSyncAI()` and `RPCSetAI` (a per-team enable RPC). Non-hosts see the AI state.

### Existing AI-touching mods (reference)
- **Si_EarlyEncounters** (databomb) — only patches `StrategyTeamSetup.SpawnAIUnits` to spawn crates. Doesn't touch the commander itself.
- Nothing in the DrMuck fleet or databomb's fleet has attempted to replace `AICommander.Think()`. **Fresh ground.**

## 2. How we can override — three strategies

### A. Coexist and inject (lightest touch)
Harmony **postfix** `AICommander.Think()` — after stock AI adds its `AIRequest`s, we append our own strategic requests to the same list. Stock `AIConstructionHandler` / `AIUnitHandler` still execute them.

- Pros: no risk of breaking stock, small surface, easy to disable per-team.
- Cons: stock AI's dumb decisions still fire alongside ours — muddy strategy.

### B. Selective replace (recommended MVP)
Harmony **prefix** with `return false` on specific handler `Think()` methods (starting with `AIOffenseHandler.Think()` and `AIDefenseHandler.Think()` — the ones players actually complain about). Run our replacement logic in a postfix.

Keep stock handlers for the boring-but-solid subsystems (Resources, Exploration, Unit production, Construction).

- Pros: focused effort where stock AI is worst; base economy stays working.
- Cons: has to synthesize its own `AIRequest`s so `AIUnitHandler`/`AIConstructionHandler` still act on our decisions.

### C. Full replacement (endgame)
`AIManager.EnableCommander(team, false)` for the target team, then run our own MelonMod tick that queries units + resources directly and issues orders bypassing `AIManager` entirely.

- Pros: maximum control; strategy layer is not constrained by stock request types.
- Cons: have to reimplement construction placement, harvest routing, unit production, group formation, pathing hints. Big project.

**Recommendation**: start with **B** (selective replace), scoped to offense + defense. Fall back to A for a hotfix if replacement misbehaves. Reach for C only if the request-queue abstraction proves too limiting.

## 3. Strategy model — what "smarter AI" needs to decide

Each tick (~every 3s under stock `ThinkInterval`) the strategist for a team should output:

### Economic policy
- Target harvester count (per resource type, per known patch)
- Resource-storage allocation (build refineries near which patches next?)
- Tech-tier target (rush T3 or stay wide at T2?)
- Anti-idle: are our workers actually harvesting?

### Military production
- Compositional target (e.g. "60% infantry, 30% light vehicle, 10% air")
- Faction-specific presets (Sol: Rifleman spam early, Siege Tanks late; Alien: Crab swarm early, Behemoth late)
- Reactive tuning: enemy has lots of air → build AA. Enemy has lots of infantry → build MG turrets.

### Military strategy (the fun bit)
- **Steamroll**: mass units, one big push at the enemy HQ. Trigger when we have a numeric + tech lead.
- **Guerrilla**: small squads hit outposts / harvesters / vulnerable buildings. Trigger when we're behind or the enemy has a strong front line.
- **Defense-first**: turtle around HQ + refineries, trade attrition. Trigger when a big enemy push is inbound.
- **Scout / delay**: harass the enemy's harvest while we out-econ them.

Ideally the strategist picks one of these modes per tick based on a simple scoring function (unit-count ratio, income ratio, HQ distance, known enemy composition).

## 4. Phased plan

| Phase | Deliverable | Scope |
|--|--|--|
| **0** ← where we are now | Scaffold + design doc + AI structure map | ✅ |
| **1** | Read-only observability | MelonMod that hooks `AICommander.Think()` postfix and logs what the stock AI is deciding, per team, every tick. No behavior change. Lets us characterize the stock AI's weaknesses on live matches before we replace anything. |
| **2** | Strategy state tracker | Per-team `TeamStrategyState` — tracks known enemy positions, unit counts, resource income, current attack posture. Fed by `GameEvents.OnUnitSpawned/Destroyed/StructureDestroyed`. Still no orders issued — just observation. |
| **3** | Offense-only replacement | Prefix-return-false `AIOffenseHandler.Think()`. Implement 3 modes (steamroll / guerrilla / defensive-hold) with a simple scoring rule to pick between them. Emit `AIRequest`s so `AIUnitHandler` produces the right units. |
| **4** | Defense replacement | Same treatment for `AIDefenseHandler`. Adds proper turret placement around production, HQ garrisons, chokepoint anchors. |
| **5** | Production shaping | Postfix on `AIUnitHandler.Think()` to bias unit composition toward what the current strategic mode demands. |
| **6** | Chat command control | `/rtsa mode <team> <steamroll\|guerrilla\|defensive\|auto>`, `/rtsa status`, `/rtsa on/off`. Uses SilicaAdminMod's `Power.Generic` gate like KGT. |
| **7** | Faction-specific tuning | Separate `SolStrategy` / `CentauriStrategy` / `AlienStrategy` classes so alien swarm rush and human combined-arms have distinct feel. |

Each phase is shippable + independently testable.

## 5. What we ship immediately after this doc is agreed

Phase 1 — a `SilicaRTSA.dll` that:
- Does not change any AI behavior
- Logs every tick what the stock AI is producing / attacking / defending, per team
- Writes a per-round summary to `UserData/RTSA/round-<timestamp>.log`

That gives us the empirical basis to design the replacement — "the stock AI attacks with 3 rifleman on average", "60% of its attack orders are Guard, only 8% SeekAndDestroy", etc. — instead of guessing.

## 6. Non-goals (explicitly)

- **No LLM in the loop**. Fast tick, no API key required, runs on the dedicated server without external calls.
- **No client-side changes**. Server-authoritative decisions synced via existing `AIManager.UpdateSyncAI()` / `RPCSetAI` paths.
- **Not touching AIBaseAgent / low-level unit AI** (aiming, pathing, target selection). Those are fine; the complaint is *strategic* dumbness, not unit-level dumbness.
- **No auto-difficulty scaler**. That's a separate config knob (`Attack_DefenseToOffenseRatingMultiplier` already exists and can be exposed via chat).
