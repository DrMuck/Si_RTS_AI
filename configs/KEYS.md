# Configuration keys

One json per configuration in `<server>/UserData/RTSAI/configs/`; the active
one is named in `UserData/RTSAI/state.json`. Every key is optional — the
default in the right-hand column applies when it is absent. Keys starting with
`_` are comments. Dotted names below mean nested objects:
`military.enabled` is `{ "military": { "enabled": true } }`.

**Live** = read whenever used, so a change applies at once after `/rtsai
reload` or a config switch. **Round** = read at map load (and at `/rtsai
reload`), so it applies to the next round.

## Switches

| key | default | when | meaning |
|---|---|---|---|
| `enabled` | true | live | MASTER. false = the mod does nothing: no patch body runs, no tick, no logs, no telemetry, no time scale / fps cap. `/rtsai on|off` overrides it (state.json). |
| `factions.alien` / `.sol` / `.centauri` | true / false / false | live | Which teams the mod commands (when no player holds the seat). Vanilla AI runs the others. `/rtsai enable <f> on|off` changes it for the current round. |
| `ecoAssistWithHumanCommander` | false | live | Keep the economy planner running for a HUMAN alien commander (co-op). Without it every planner stands down when a player takes the seat. |
| `scoutAssistWithHumanCommander` | false | live | Keep ScoutPlanner sweeping for a human commander (co-op). |
| `lockAlienCommander` | false | round | Eject any player who takes the alien seat within a second and hand it back to the AI. Required when playing AGAINST the bot; mutually exclusive with the two assist keys. |
| `coopBlockVanillaUnitOrders` | false | live | Refuse move/attack orders raised INSIDE `AICommander.Think` on teams the mod runs, so Silica's own commander cannot re-task units players drive. Player orders are untouched (they come from the network handler). |
| `commanderLog` | true | live | `[CMD] [CASH] [ORDER] [SELECT] [PILOT] [TEAM] [AI]` lines in the round log. `/rtsai log cmd on|off|auto`. |
| `roundLog` | true | live | The per-round log file and the `.melon.log` copy. `/rtsai log round on|off|auto`. |
| `telemetryPort` | 8765 | live | HTTP port for the layers viewer (localhost). 0 = off. |

## Planners

| key | default | when | meaning |
|---|---|---|---|
| `scout.enabled` | true | live | ScoutPlanner recruits and builds scouts (Crabs/Squids; Scouts for humans). |
| `scout.maxUnits` | 20 | live | Arms of the scout star. |
| `openerExecute` | true | live | OpenerPlanner drives the opening build order (false = shadow: logs its opening, builds nothing). |
| `ecoPlannerActive` | true (presets) / false (built-in) | live | The rolling-horizon eco planner executes (false = shadow, `[PLAN]` lines only). |
| `military.enabled` | false | round | Master for the military layer. false = it computes nothing. |
| `military.execute` | true | round | Issue orders. false = plan and log, touch no unit. |
| `military.produce` | true | round | Queue combat units and place higher-tier producers. |
| `military.offence` | true | round | Allow push missions. |
| `military.blockVanillaAttackOrders` | true | round | Take held units out of attack orders the game's commander issues. |
| `military.strengthMargin` | 1.5 | round | How much more than the enemy's cash a force must be worth before it is sent. |
| `military.homeShare` / `.homeCapShare` | 0.25 / 0.5 | round | Least / most of army value the garrison holds. |
| `military.homeFloorCash` | 12000 | round | Absolute garrison floor (about three Behemoths). |
| `military.maxDefendMissions` | 3 | round | Simultaneous defences. |
| `military.maxProducersPerType` | 3 | round | Higher-tier producers of each kind. |
| `military.ecoReserve` | 15000 | round | Cash kept for the economy while it is behind its worker trajectory. |
| `military.lesserCystShare` | 0.25 | round | Share of Lesser Cysts making combat units. |
| `military.pushGrowthFloor` / `.pushMargin` / `.pushRetreatFraction` | 20 / 1.5 / 0.4 | round | Push trigger, commitment margin, retreat point. |
| `military.structureRazeShare` | 0.5 | round | Share of a base's cash value the attacker must also be worth. |
| `mil.shadow` | true | round | Doctrine shadow: logs what `mil_doctrine.json` would say. Orders nothing. |
| `mil.spires.enabled` / `.execute` | true / false | round | Static defence: plan / build. |
| `mil.spires.cashFloor` / `.perSite` / `.heavyThreat` / `.nestByMin` | 4000 / 1 / 2000 / 8 | round | Spire sizing. |
| `mil.homeFloorCash` | = military.homeFloorCash | round | v3 garrison floor. |
| `mil.forecastHorizonS` / `.raidShare` / `.stagingPatienceS` / `.fobProducers` | 180 / 0.25 / 45 / 3 | round | v3 placeholders (MIL_V3_PLAN §5). |
| `mil.defenceWeight` / `.screenMemoryHalfLifeS` / `.recon` / `.standoffM` | 1.0 / 300 / true / 450 | round | v3 placeholders. |
| `mil.enemyStartEff` / `.noOffenceBeforeS` / `.reachEnforce` | 4000 / 600 / false | round | v3 placeholders. |
| `mil.ecoFloorCash` / `.defendRefuseBelow` / `.preemptWithinS` | 4500 / 0.5 / 90 | round | v3 placeholders. |

## Economy

| key | default | when | meaning |
|---|---|---|---|
| `blueprintDrivesPhase2` | true | live | Phase 2 executes a planned network (false = natural branching). |
| `replanIntervalS` | 30 | live | Seconds between replans. |
| `maxCystsPerPlan` | 4 | live | Producers per plan when `cystStrategyAuto` is false. |
| `cystStrategyAuto` | true | live | The strategy sweep decides producer count. |
| `workersByTenMinutes` | 100 | live | The worker trajectory the economy aims at. |
| `workerCapPerBioCache` | 18 (presets: 10) | live | Workers one Bio Cache may hold. Owned by the A/B rig while arms cycle. |
| `producerPerSites` | 0 (presets: 3) | live | 0 = adaptive; N = one Lesser Cyst per N planned sites. Owned by the rig while arms cycle. |
| `cystQueueMax` | — | live | Shrimps QUEUED per Cyst, not counting the one building. |
| `maxUnbuiltNodesPerFront` / `maxNodeFronts` | 3 / 0 | live | Nodes in flight per front / fronts noding at once (0 = planner's own budget). |
| `maxUnbuiltNodes` | — | live | Flat global cap; overrides the per-front budget if present. Leave out. |
| `pileUpMaxPerPatch` / `pileUpMaxDetourM` | 10 / 0 | live | Anti-pile-up: shrimps walking to one patch / detour allowed (0 = unlimited). |
| `bridgeMode` | loop | live | `loop` / `shortcut` / `off` — how a cross-branch link is priced. |
| `chainExtendPerTick` / `chainExtendDemandPerTick` | 2 / 3 | live | Node-chain extension budgets per planner tick. |
| `tapReachMode` | measured | live | `measured` / `optimistic` — how close a chain must get before a Bio Cache is placeable. |
| `openerDoubleCyst` / `openerTailSeconds` / `openerCrowdSoften` / `crowdSoften` | true / 240 / 1 / 0 | live | Opener scoring (see configs/README.md). |
| `remoteSupplyEnabled` / `remoteSupplyMaxWalkS` | true / 60 | live | A Cyst may produce for free capacity elsewhere within this walk. |
| `storageBufferCaches` / `storageBufferAtMinute` / `storageBufferOffsetM` | 0 / 10 / 150 | live | Measurement scaffold: free Bio Caches spawned at the Nest to raise storage. 0 = off. |
| `autoResourceDrain` | false | live | Measurement scaffold: cut cash to 70% of capacity whenever it passes 75%. Destroys money. |
| `eco.phase2CystCoverageRadiusM` | 250 | live | Existing Cyst within this many m of a BC covers it. |
| `eco.phase2CystMinClusterPatches` | 2 (presets: 1) | live | Patches a BC needs in its cluster to get a Cyst candidate. |
| `eco.phase2CystTargetFarthestInCluster` | true (presets: false) | live | Cyst at the cluster's far patch (true) or at the BC (false). |
| `eco.phase2MaxUncystedBcQueue` | 4 | live | In-flight uncysted BCs tolerated before new-BC enumeration blocks. |
| `eco.phase1MinTappedPatches` | 2 | live | Phase 1 objective: distinct patches served before breadth expansion. |
| `blueprint.maxSitesPerPlan` | 128 | live | Bio Cache sites one plan covers. |
| `blueprint.cystStaffedEnough` | 0.8 | live | Migration coverage at which a planned Cyst is redundant. |
| `blueprint.cystRelocationSpacings` | 3.5 | live | Patch spacings a shrimp walks before a site must grow its own. |
| `blueprint.noCystsFromShrimpCount` | 180 | live | Stop planning producers at this shrimp count. |
| `blueprint.persistPlans` | true | live | Write plan revisions to `UserData/RTSA/blueprint/`. |

## Harness and measurement

| key | default | when | meaning |
|---|---|---|---|
| `testMode` | false | live | THE SOAK SWITCH. true = fake client joins, forced round end, map cycling, `suppressCombat` / `enemyBroke` apply. Never in a played game. |
| `suppressCombat` | false | live | (testMode) Drop every `AIGroup.OnAttackOrder`. |
| `enemyBroke` | false | live | (testMode) Zero Sol/Centauri cash and purge their units every second. |
| `endRoundAfterMinutes` | 10 | live | (testMode) Force-end after this many minutes (1..120). |
| `roundsPerMap` | 6 | live | (testMode) Rounds on one map before the rotation advances. 0 = no map commands. |
| `configCycle` | "" | round | A/B rig arms, comma-separated. **Empty = the default arm cycle**, `"off"` = no arms. |
| `harness.autoOverrideProduction` | false | live | Flip the Phase 3.1 production override on for EVERY AI-commanded team (no faction filter). |
| `harness.preventEmptyEndround` | true | live | (testMode) Pin `NoPlayersTime` at 0 so the empty-server end-round never fires. |
| `harness.autoStartRound` | true | live | (testMode) Call `SetTeamVersusMode` once per scene and wait for the natural start. |
| `harness.autoStartTimeoutSeconds` | 45 | live | (testMode) Force-flip fallback after this long (15..300). |
| `harness.forceAssignAICommanders` | true | live | (testMode) Ensure every active team has an AI commander. |
| `harness.fakeTeamJoin` | true | live | (testMode) Synthesize a player joining a team so the round starts. |
| `harness.autoRotateMap` | false | live | (testMode) Load the next map of `harness.mapRotation` after a force-end. |
| `harness.mapRotation` | NorthPolarCap,NarakaCity,WhisperingPlains | live | (testMode) The rotation. |
| `harness.configId` | baseline | live | Tag written to every benchmark row. Overwritten per arm by the rig. |
| `harness.shrimpStateSampler` | false | live | Log every shrimp's position once a second (4 GB per night). |
| `timeScale` | 1 | live | Simulation speed (0.1..8). The achieved rate is reported; a short box makes a different game. |
| `serverFpsCap` | 0 | live | TEST KNOB for fps-drop correlation runs: pins `Application.targetFrameRate` while > 0, even with the mod off. 0 (every preset) = the game's own `MaxFPS` from GameSettings.xml, settable in the server console with `MaxFPS <n>`. |
| `perfTimers` | true | start | Time the game's physics / ordered-update loops for the `[RTSA/PERF] budget` line. Patched at mod start. |
| `perfProbes` | "" | start | Extra empty-prefix probes, `Type:Method,...`, for the perf monitor. |
| `layerSnapshots` | true | live | Write layer replay frames to disk. |
| `siteAnchorCheckS` | — | live | (historic) anchor-check throttle; the developer fixed the underlying bug. |
