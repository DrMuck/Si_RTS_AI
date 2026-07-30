# Si_RTS_AI archive — Async beam + round-active gate

## Milestones since last archive
- Async beam search on worker thread — 0ms plan cost on main thread (was 1400-3700ms freezes)
- ThreadStatic phase bug fix — main-thread escape hatch now sees real candidates
- Phase-aware ShrimpRelocator (P1 tolerates piles, P2 aggressive spread, depletion timers)
- Sim shrimp cap matches reality (200) — beam correctly values expansion at cap
- Phase 2 bonuses cranked: Node +6000, Cyst +6000, BC +5500
- Round-active gate — planner + relocator stop firing on MissionState ≠ STARTED
- TestHarness auto-start re-arm on ENDED→INIT same-scene
- LayerReplay round timer reset in re-arm (telemetry roundTime accurate)
- FPS chart added to layer viewer (0-240 scale, yellow line, right axis)

## Overnight round results (7 rounds)
- Every round Alien 300k+ cumulative income
- Best real 15-min single round: GreatErg 479k, 533/sec sustained (beats human 361k benchmark by 32%)
- Zero crashes, zero exceptions, planner runs invisibly on worker thread

## Known observations still to address
- NorthPolarCap: late-game expansion from 10min slower than TheMaw/GreatErg — beam
  seems to plateau once local biotics are saturated. Investigate scout Node reach
  or add "territorial control" score term.
- TheMaw was excellent expansion example — beam kept pushing out with 5-Node/tick sequences
- Map rotation is handled externally by user's other mod (not needed here)

## Follow-ups filed (see memory/project_si_rts_ai_planner_followups.md)
- 0. Phase 2 detection + expansion boost (partially done)
- 6.7. Shrimp clustering + crowd model reality check (done: smooth CrowdFactor, no zero at 18)
- 6.8. Depletion-resilience valuation in sim (still pending)
- 6.9. Phase-aware ShrimpRelocator (DONE this session)
- 6.10. Read chain reach + shrimp constants from game data (Si_UnitBalance compat)
- Territorial control scoring layer (proper fix for map-domination beyond bonus hacks)
- Money broker + TechPlanner + Cortex integration (yesterday's roadmap)
