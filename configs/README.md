# Server configurations

Two working setups, saved as complete file pairs rather than as instructions,
because every ad-hoc `.bak` on the server is named after what it was taken
*before* (`bak-before-coop2`) and none of them says what it *is*.

Each folder holds both files the server reads:

| file | where it goes | live? |
|---|---|---|
| `MelonPreferences.cfg` | `<server>\UserData\` | **no — server must be STOPPED** |
| `rtsai.json` | `<server>\UserData\` | yes, re-read while running |

**MelonLoader rewrites `MelonPreferences.cfg` from memory on shutdown.** Editing
it while the server is up loses the edit. Always stop first.

---

## `coop-player-commander/`

A played round. **You hold the alien commander seat and run the military; the AI
runs the economy underneath you.**

- `lockAlienCommander: false` — you keep the seat instead of being ejected
- `EcoAssistWithHumanCommander = true` — without this the eco planner stands
  down the moment a human commands, and the AI does nothing at all. The two
  settings only work as a pair.
- `military.execute` / `produce` false — the AI never touches your combat units
- `coopBlockVanillaUnitOrders: true` — stops Silica's own commander re-tasking
  units, by ORIGIN (orders raised inside `AICommander.Think`), so your orders
  are never caught by it
- Sol and Centauri play normally: `EnemyBroke = false`

Note `ScoutEnabled = true` here: `ScoutPlanner` will recruit up to 20 Squids and
order them around. Set it false if you want manual control of them — it is a
MelonPreferences value, so it needs a stop.

## `alien-military-vs-vanilla/`

**Alien military test against Silica's own AI.** The first unattended round
with the military layer actually running.

Runs **60 minutes**, not the soak's 25. The eco benchmarks only needed the
1500s checkpoint; a military round needs to reach the part of the game where
military decisions exist. Real matches run 40-60 minutes, the build trajectory
this is measured against extends to minute 40, and in the 2026-08-13 co-op round
the army did not pass 100k until minute 21 — a 25-minute cut would end the test
four minutes after the interesting part started. Same
harness as `testmode-headless/` — fake client, auto-start, force-end at 25
minutes — but the alien fights a real opponent instead of a purged one.

Differs from `testmode-headless/` in five lines, and each one is load-bearing:

```
EnemyBroke                     true  ->  false   Sol/Centauri play normally
HeadlessTest_AutoOverrideRtsai true  ->  false   ...with THEIR OWN unit choices
military.execute               false ->  true    orders are issued
military.produce               false ->  true    combat units are queued
military.offence               false ->  true    pushes are allowed
```

**`AutoOverrideRtsai` is the one that would have quietly ruined the test.** It
flips the Phase 3.1 production override on for *every* team in
`AIManager.Commanders`, with no faction filter — so our own CompositionPlanner
would have been picking Sol's and Centauri's units too, and "vs vanilla AI"
would have been us against a copy of our own composition logic. Off here.

`HeadlessTest_SuppressHumanAI` needs no change: it is the LEGACY NAME for
`EnemyBroke`, not a second switch. TestHarness reads `EnemyBroke` and falls back
to the old key only when it is unset.

plus `mil.spires.execute: true`, so static defence is built rather than only
planned. A soak is the right place to let that off the leash for the first
time: nothing is lost if it spends badly.

**Why the two enemy flags travel together.** `EnemyBroke = false` on its own
buys nothing, because `SuppressHumanAI` suicides every non-HQ human unit once a
second and zeroes their resources anyway — the alien would still be shadow-
boxing. Both, or neither.

`testmode-headless/` turns them off for the opposite reason, and its README note
explains it: with `military.produce: false` the alien has no army and dies
around minute 20, long before the benchmark checkpoints. That reasoning expires
the moment production is on, which is what this pair is for.

`RTSAI_Sol` and `RTSAI_Centauri` stay false, so the human factions are run by
Silica's own AI — the opponent is vanilla, not a second copy of us.

---

## `eco-only/`

Unattended eco soak, and the baseline every economy benchmark was measured on.
**No military at all** — `military.enabled: false`, so nothing is produced,
ordered or defended.

Was `testmode-headless/`. Renamed because "headless" described how the round is
driven and every other config here is headless too; what makes this one
different is that the military layer is off.

Two things changed when it was renamed, and both are corrections rather than
preferences:

- `HeadlessTest_AutoOverrideRtsai` **true -> false**. It flipped the Phase 3.1
  production override on for *every* team in `AIManager.Commanders` with no
  faction filter, so our own CompositionPlanner was picking Sol's and
  Centauri's units. Wrong in every config, not just this one.
- A `mil` block was added: shadow **on** (it is inert and the build-trajectory
  line is worth having in an eco round too), spires enabled but
  `execute: false` so they plan and never build.

**Round length stays 25 minutes.** The eco benchmark corpus was measured against
the 1500s checkpoint and quietly changing it would break comparability with
every earlier run — the same class of mistake as pooling build data across
balance eras. Raise `HeadlessTest_EndRoundAfterMinutes` deliberately if a longer
observation is wanted, and know that the numbers stop being comparable.

`EnemyBroke = true` here, unlike `alien-military-vs-vanilla/`: with no army the
alien dies around minute 20 against a live opponent, long before the checkpoint.

Original description follows.

Unattended soak. Fake client joins, round auto-starts, force-ends at 25 minutes,
humans are suppressed so the round survives to the benchmark checkpoints.

Differs from co-op in exactly five lines:

```
VersusAutoSelectMode          "NONE"  ->  "HUMANS_VS_HUMANS_VS_ALIENS"
HeadlessTest_AutoStartRound   false   ->  true
HeadlessTest_FakeTeamJoin     false   ->  true
TestMode                      false   ->  true
EnemyBroke                    false   ->  true
```

plus `testMode: true` in `rtsai.json`. Everything else is identical, and that is
deliberate — the two modes should differ only in how the round is driven, so a
behaviour difference between them means something real.

`EnemyBroke = true` zeroes Sol/Centauri cash and purges their units. Without it
the alien has no army (`military.produce: false`) and dies around minute 20,
long before the 1500s checkpoint.

---

## Both modes must produce the same map

Test mode used to run **vanilla 22,000 biotics per patch** while co-op ran the
configured **42,000**, because Si_MapBalance applied the layout's spawn points
but not its resource amounts — the game calls `DistributeAllResources` on a
normal round start and not on the headless path. Fixed in Si_MapBalance on
2026-08-11 (branch `beta`, commit `7e2359a`) by applying the amounts at
`SpawnBaseStructures` time instead.

Sanity check after any config change — these must match across modes:

```
[Map_Balance] Resource amounts applied (spawn-time fallback)
[Si_RTS_AI] [MAPPROF] NarakaCity: patches=107 biotics=4486000 nearest=214m
```

If `biotics` differs between a co-op and a test round, they are not the same
economy and nothing measured in one transfers to the other.

Also keep **one layout file** per map in `UserData\Spawns\<Map>\`. Two eligible
layouts made MapBalance pick a different spawn point and tonnage per round,
which silently invalidated round-to-round comparison for an evening.

---

## Tuning carried in `rtsai.json` (both modes)

Values that came out of measurement on 2026-08-11 and are not defaults:

| key | value | why |
|---|---|---|
| `remoteSupplyEnabled` / `remoteSupplyMaxWalkS` | true / 60 | A Cyst may produce for free capacity at another Bio Cache within 60s of walking. Production was collapsing 93% -> 30% at blueprint start without it; ~85% with. |
| `maxNodeFronts` | 5 | Was 2, giving 6 nodes in flight, with 324k cash idle and 8-12 node candidates queued. Fronts, not depth — a chain builds one node at a time because each hop waits for its anchor. |
| `cystQueueMax` | 1 | `ProductionQueue` excludes the unit being built, so 2 meant three shrimps in a Cyst. 1 = one building, one queued. |
| `openerDoubleCyst` | true | One opener site may take a second Cyst. |
| `openerCrowdSoften` | 1 | Crowding penalty off for opener scoring only. Stopgap — remove when the surplus-release source lands (`ECO_OPEN_ITEMS` 2b). |
| `openerTailSeconds` | 240 | Set 120 to make the opener prefer the doubled-Cyst opening. See `ECO_OPEN_ITEMS` item 5. |

`autoResourceDrain` stays **false**: it kept measurement alive by destroying
money and crediting it back, so `cumulIncome` reported cash the AI never had.
Use `storageBufferCaches` instead if the bank clips (100 caches is about +400k
capacity, placed at minute 10). Those sit at the Nest with no patches, so they
add no worker capacity and do not raise the shrimp ceiling.
