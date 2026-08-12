# Archived builds

One folder per kept version: the built DLL plus the source tree at that commit.
Every folder here has a matching annotated git tag, so `git show <tag>` gives the
commit message and `git diff <tag>..HEAD` gives everything since.

To roll the server back, copy the DLL over
`E:\Steam\steamapps\common\Silica Dedicated Server\Mods\Si_RTS_AI.dll`
and restart — no rebuild needed.

| Version | Why it is kept |
|---|---|
| `v0.20.1_2026-08-05` | Current. Worker trajectory (100 by 10min) plus yield-driven lever choice: producers while income per worker holds, ground when it falls. |
| `v0.19.5_2026-08-05` | The build all 16 overnight soak rounds ran on — the baseline every eco benchmark in USER_RULES 6.10/6.12 was measured against. Roll back here to reproduce those numbers. |
| `Si_RTS_AI_KNOWN-GOOD-ECO_v0.41.1.dll` | **Protected backup.** The last eco build DrMuck validated by hand. Kept deliberately even though it carries known bugs — it predates the node cap, the opener timing rewrite and the game-fog work, so it is the fallback if a later build regresses the economy beyond repair. Source is tag `known-good-eco-20260807` (commit `1993146`). |
| `v0.7.40_2026-07-03` | Older milestone. |
| `v_2026-07-06_*` | Older milestones (map-control expansion, async beam round gate). |

## Integrity

Verify a protected build before trusting it:

```
md5sum _archive/Si_RTS_AI_KNOWN-GOOD-ECO_v0.41.1.dll
```

| File | MD5 |
|---|---|
| `Si_RTS_AI_KNOWN-GOOD-ECO_v0.41.1.dll` | `08EA4B1233EB8716F3FB3F12514E99B4` |

Confirmed 2026-08-11. This DLL is tracked in git, so the blob is recoverable from
any commit that contains it even if the working copy is lost:

```
git cat-file -p HEAD:_archive/Si_RTS_AI_KNOWN-GOOD-ECO_v0.41.1.dll > restored.dll
```

Note what rolling back to it costs, beyond the eco behaviour: it predates the
node budget, the sequenced opener layout, TotalConstructionTime build times, the
remote-supply producer fix, and the game-fog rewrite. On the fog specifically it
reconstructs visibility from guessed 700m/500m disks, which measured 0 of 380
enemy structures visible.
