# Co-op eco server — the settings that are NOT in rtsai.json

> **Superseded 2026-10-09 (v0.94.0).** Every setting below now lives in one json
> under `UserData/RTSAI/configs/` — pick `coop-eco`, `coop-eco-scout` or
> `coop-ai-commander` with `/rtsai`. Only `VersusAutoSelectMode` is still a
> MelonPreferences value. Kept for the reasoning.

Written 2026-08-09. DrMuck: *"we should have some old coop settings file archived
maybe."* `rtsai.playtest.json` covered the mod's own config for a played round;
nothing ever recorded the **MelonPreferences** half, which is the half that can
restart the map underneath your players.

Two files, and they behave differently:

| | `UserData/rtsai.json` | `UserData/MelonPreferences.cfg` |
|---|---|---|
| Owner | this mod | vanilla + the test harness |
| Edit while running? | yes, re-read | **NO** — MelonLoader rewrites it from memory on shutdown and silently reverts anything you changed while it ran |

**Always stop the server before touching MelonPreferences.cfg.**

---

## The mod config

Copy `rtsai.coop-eco.json` over `UserData/rtsai.json`. It sets military to
observe-only (`enabled: true`, `execute: false`, `produce: false`) so the planners
still log intel while nothing is built and no order is ever issued.

The one line that matters most is `"testMode": false`. It overrides the
MelonPreferences `TestMode`, and left true it auto-joins a fake client, force-ends
the round and restarts the map.

---

## The MelonPreferences a played co-op round needs

```ini
VersusAutoSelectMode = "NONE"      # players vote for the mode themselves
TestMode             = false       # master gate for the whole harness
EnemyBroke           = false       # true zeroes human cash every second

HeadlessTest_Enable          = false
HeadlessTest_AutoStartRound  = false   # no forced round start — players start it
HeadlessTest_FakeTeamJoin    = false   # no bot joins to make the round begin
HeadlessTest_SuppressCombat  = false   # players must be able to shoot
HeadlessTest_AutoRotateMap   = false
```

`VersusAutoSelectMode` legal values, read from the enum rather than guessed —
the mod prints them at startup:

```
NONE, HUMANS_VS_HUMANS, HUMANS_VS_ALIENS, HUMANS_VS_HUMANS_VS_ALIENS, HUMANS2_VS_ALIENS
```

`NONE` is the one that leaves the choice to the lobby. A value that does not parse
is **not** an error anyone sees — it falls back and the round starts with the
wrong teams.

### Left ON deliberately

- `lockAlienCommander: true` (in rtsai.json) — this is what makes it co-op. The
  alien commander seat stays with the AI so it can run the economy for the
  players, and anyone who takes it is returned to the field. Turn it off and the
  eco layer does nothing at all, because it only acts while it holds the seat.
- `HeadlessTest_TelemetryPort = 8765` — the layers viewer. Decoupled from
  TestMode on purpose; harmless in a played round and it is how you watch the
  economy live.
- `HeadlessTest_ForceAssignAICommanders`, `HeadlessTest_SuppressHumanAI`,
  `HeadlessTest_PreventEmptyEndround`, `HeadlessTest_AutoOverrideRtsai` — all
  gated on `TestMode`, so they are inert. Left as they are rather than churned,
  since changing settings that do nothing only makes the next diff harder to read.

---

## Why observe-only is safe with players in the game

Not an assumption — the gates were read before the config was written.

- `produce: false` → `MilitaryProduction` returns before spending a credit.
- `execute: false` → `BattalionManager.IssueOrders` is never called, **and**
  `Owns()` returns false for every unit.

That second clause is the one that matters. Two Harmony prefixes hang off
`Unit.OnMoveOrder` and `AIGroup.OnAttackOrder` to stop the vanilla commander
pulling units out of our battalions. With `Owns()` false they pass every order
straight through, so **player and vanilla orders are untouched**. A layer that is
merely quiet is not the same as one that is out of the way.

`SuppressCombat` is a separate mechanism entirely (`Faction/SuppressCombat.cs`),
defaults off, and is gated on the harness — but it silences *every* attack order
in the game when on, so it is worth confirming rather than trusting.

---

## Before starting, in order

1. Server stopped.
2. `rtsai.json` is the co-op one — check `"testMode": false` and
   `"military": { "execute": false, "produce": false }`.
3. `MelonPreferences.cfg` matches the block above.
4. Start. In the log, confirm:
   - `[RTSA] [Silica]/VersusAutoSelectMode accepts: ... (currently 'NONE')`
   - no `[MIL/PROD] placed` lines at all
   - `[MISSION]` and `[MIL/BP]` lines still appearing — that is the intel still
     flowing with nothing acting on it
