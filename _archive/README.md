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
| `v0.7.40_2026-07-03` | Older milestone. |
| `v_2026-07-06_*` | Older milestones (map-control expansion, async beam round gate). |
