# Si_RTS_AI archive — Map Control Expansion milestone

Timestamp: (see directory name)

## Session progress
- Baseline start: ~78k @ 10min NorthPolarCap
- This build: 150k+ @ 10min, 253k+ @ 12min (map east half filled)
- Human benchmark: 361k @ 16min (equivalent ~210k @ 10min)
- Gap closed from ~75% to ~30% under human productivity per minute

## Key features in this build
- Cyst gate with queue depth 2 + shrimp-cap awareness (≥180 shrimps: gate opens for BC without Cyst)
- Cyst enum uses 60m radius + excludes Nest (matches BC gate — fixes min-7 stall)
- Phase 2 bonuses: BC +2000, Node +2000, Cyst +5000
- Biotics-anchored scout Nodes (one per angular sector with unreachable biotics)
- Full-marginal shrimp relocator: threshold 1.3 (was 1.15), NEAR_DEPLETION_THRESHOLD=3000 (was 2000)
- Beam 6×6×4 (was 8×10×6, eliminates 5s tick freeze at late-game structure counts)
- 20s startup delay + human-commander opt-out via IsCommanderEnabled
- FoW filter for BC candidates (Nodes unfiltered for scouting)
- Shrimp hard cap 200 (live + queued)
- Server FPS in /state endpoint + yellow line on layer viewer eco chart

## What's NOT in this build (filed as follow-ups)
- Phase 3 (military / tech / defense) — blocked until eco is solid
- Directional bias improvement for scout Nodes
- /entities pagination for high-structure-count telemetry
- Cyst-before-BC ordering optimization
- Depletion handoff (partially in via Cyst handoff scoring)
