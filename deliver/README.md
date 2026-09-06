# Si_RTS_AI for the main branch (0.9.42) — standard gameplay

Built from the same source as the beta mod (v0.92.27) with the `GAME_MAIN` switch, so it
uses the main branch's unit order verbs. Load-tested on a 0.9.42 dedicated server.

## What it does on a public server

- Commands the **alien** team only while **no player holds the alien commander seat**,
  exactly like the vanilla AI commander would. A player who takes the seat gets the team;
  the mod stands down (the economy planner does not assist a human commander).
- Sol and Centauri are untouched.
- No test harness, no forced round end, no fake client, no map restart.
- Performance patches included: construction-site anchor check throttled to 0.5 s,
  seated-player animator guarded, fps cap 144. Do not run `Si_ServerHotfix` alongside
  this mod: it carries the same two patches.

## Install (server stopped)

1. Copy `Si_RTS_AI.dll` into `Mods\`.
2. Copy `rtsai.json` into `UserData\`.
3. Merge the sections in `MelonPreferences.sections.cfg` into `UserData\MelonPreferences.cfg`
   (they are the only sections the mod reads; MelonLoader rewrites the file on shutdown,
   so edit it only while the server is stopped).
4. Start the server. The log should show, in this order:

```
Si_RTS_AI v0.92.27
[RTSA/SRV] site anchor-check throttled to every 0.5s per site
[RTSA/SRV] compartment animator guarded (skips when the Animator or DefaultAnimator is null)
[RTSA/SRV] Soldier.OnOrderedLateUpdate exceptions are contained
[RTSA] Registered /rtsai chat command.
```

## Chat commands (admin, Power.Generic)

| command | effect |
|---|---|
| `/rtsai off` | Hands the aliens to the vanilla commander mid-game: every force is released, all order gates yield. |
| `/rtsai on` | Takes the aliens back (only while no player holds the seat). |
| `/rtsai status` | Faction switches and tallies. Open to everyone. |
| `/rtsai mil` | The military layer's objectives and forces. Open to everyone. |

## Logs

Per round: `UserData\RTSA\round-<stamp>-<map>.log` (planner, forces, orders, a
per-minute performance budget line) and the mod's own lines in `MelonLoader\Latest.log`.

## Rebuild

Project: `C:\Users\schwe\Projects\Si_RTS_AI_main`, branch `main-game`. Sync from the beta
repo with `git pull ../Si_RTS_AI beta`, references for 0.9.42 in `include\netstandard2.1\`,
then `dotnet build -c Release` in `Si_RTS_AI\`.
