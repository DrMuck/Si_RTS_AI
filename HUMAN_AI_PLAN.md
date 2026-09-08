# Sol / Centauri RTS AI — plan (2026-09-07)

Spec from DrMuck (see memory `project_si_rts_ai_human_factions`). Goal: a human-faction
commander built on the same layers as the alien one, good enough to be the headless opponent
that makes the learning loop's measurements meaningful, and later to hold a public seat.

## What already exists

- `Faction/FactionControl` — `RTSAI_Sol` / `RTSAI_Centauri` prefs (off by default).
- `Faction/HumanConstruction` (2026-07, v0.7.x) — refinery-per-patch with 90° ramp snapping,
  HQ expansion via `HumanEcoLayers.HqExpansionValue`, barracks/research/silo. Contains TEST
  CHEATS (`ConstructFree` sets cash to 999,999; `SpawnAtLocation(Silo)`). Only the placement
  geometry is reused; the cheats are removed.
- `Faction/HumanHarvesterController` — harvester routing (assignment, anti-clumping, diverts,
  stuck detection). Kept, optional.
- `Faction/HumanTechResearcher` — Mark I→V research at Research Facilities. Kept.
- `Perception/MapLayers/HumanEcoLayers` — BalteriumDiscovered / HqMask / RefineryPressure.
- Shared and faction-agnostic already: `Mil/Intel`, `Mil/Objectives`, `Mil/Forces`, `Mil/Kernel`,
  `Mil/Doctrine`, `Perception/ThreatMap`, `Perception/Reach`, `Planning/MoneyBroker`.

## Architecture

```
Human/HumanControl      seat + gates (mirror of the alien takeover; vanilla commander suppressed
                         only for enabled human teams; stands down for a player commander)
Human/HumanEco          money broker + income prediction (harvester trips, refinery count),
                         refinery siting (ramp toward patch, shortest harvester path on the graph),
                         HQ expansion (edge of HQ→HQ radius, patch coverage, no overlap)
Human/HumanOpener       three starts: early tech / early eco / mix — a plan, not a rule set
Human/HumanProduction   ProductionV3 generalised: producers = structures whose menu holds combat
                         units (Barracks, Light/Heavy/Ultra fabs, air); fab EXIT side kept clear
Human/HumanDefence      SpirePlanner generalised: Turret / Heavy Turret / AA Turret
Faction/Construction    dispatcher: TryBuild(team, cd, pos, rot?) → AlienConstruction or
                         HumanConstruction (search + rotation + prerequisite + reach checks)
```

All stats (build radius, HQ→HQ range, refinery reach, costs, tiers) are read from the running
game's ConstructionData/ObjectInfo, never assumed — modded servers change them.

## Steps

1. **H1 skeleton (this week).** Dispatcher; ProductionV3 producer discovery by menu instead
   of name; SpirePlanner takes its list per faction; the military tick runs for enabled human
   teams. Cheats stripped from HumanConstruction. Verified in a headless HvA round with Sol
   under our AI and the alien under ours (both switches on).
2. **H2 economy.** Refinery scoring by ramp/harvester path on the A* graph (4 rotations), one
   per patch; HQ expansion siting with coverage and overlap rules; income prediction from
   harvester round trips feeding the broker; opener selection with cash held for tech.
3. **H3 military fit.** Human unit pools (infantry / light / heavy / air), composition target
   from the archive's top Sol commanders, fab exit placement rule, turret siting at exposed
   refineries and the front.
4. **H4 Centauri.** Same code, Centauri rosters and stats; differences handled by data.
5. **Public seat.** Stand down for a player commander exactly like the alien side.

## Open questions

- Sol first, Centauri after? (assumed yes)
- Headless HvH: both human teams under this AI, or one vanilla?
- Public servers: human AI stands down for a player commander like the alien one? (assumed yes)

## One military state per team (v0.92.67)

The military layer (Intel, Objectives, Forces, ProductionV3, SpirePlanner, QueenKeeper, Shadow,
ThreatMap, QueenStatus, Utilisation, Reach, BcIncome, ScoutPlanner, HarvesterManager) is static
state written for one seat. `Mil/MilContext.cs` makes it per team without rewriting it: `Use(team)`
saves the installed team's field values and installs the requested team's. A team's first `Use`
gets a fresh allocation (every collection a new instance of the same type, arrays of the same
length, everything else its pristine value) followed by each class's `ResetForNewRound()`.

- `Use(team)` runs at the head of the team loop in `Si_RTS_AI.cs` and at the top of
  `HumanConstruction.HandleTick` (the game's Think runs outside the loop).
- Harmony hooks ask `MilContext.AnyHeld(u)` / `AnyScout(u)`: unions over all teams, rebuilt on
  each swap, so an order event for a Sol unit is answered while the alien's state is installed.
- `static readonly` was removed from the swapped fields: CoreCLR refuses `SetValue` on initonly
  statics. Constants and `[ThreadStatic]` fields are left alone.
- Shared on purpose: `Fields` (terrain), the CompositionTarget tables (its per-team tallies live
  in swapped fields), Doctrine/MilConfig knobs.
- Rules for code in the swapped classes: never hand a deferred callback that touches these
  statics to another system (placement callbacks, coroutines); do the work in the tick.
  Telemetry and console commands outside the loop see the last ticked team.
- Diagnostics: `[MIL/CTX] per-team context over N static fields`, `fresh military state for
  <team>`, and the status summary's "military context: k teams, n swaps, readonly-ok".
