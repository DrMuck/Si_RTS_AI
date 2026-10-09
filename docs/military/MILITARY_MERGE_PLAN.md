# Military merge — the handover

**Written 2026-08-07 evening, at the end of the eco session. Read this first in
the military session; it exists so that session does not begin by reconstructing
today from commits.**

Design lives in `MILITARY_DESIGN.md` (what to build) and `MILITARY_TACTICS.md`
(missions and the four tactics). `MILITARY_IMPLEMENTATION.md` is the state of the
code. This file is only the MERGE: what order to touch things in, and what will
bite.

---

## 0. Where the eco stands, because it constrains the merge

Known-good build `v0.41.1-no-sqrt`, tag `known-good-eco-20260807`, archived DLL
`_archive/Si_RTS_AI_KNOWN-GOOD-ECO_v0.41.1.dll` (md5 08EA4B12). Base expansion
confirmed good after closing the gap between plan and execution.

Two eco facts the military layer inherits:

- **The bank is not the constraint; conversion is.** Rounds sit on 100-200k
  unspent from minute 12 while expansion is limited by placement, not cash. An
  army is the obvious claimant, and it does not have to fight expansion for money
  the way it would in a tighter economy.
- **Workers plateau around 175 by minute 20** and income tracks worker count, not
  ground. Whatever the unit cap turns out to mean for combat units, the eco is
  already at its own ceiling by then — so military spending after minute 20 costs
  almost nothing in eco terms.

## 1. Do this first: reconcile the two scaffolds

`MilitaryManager` (older: production + HVT picking) and `DefencePlanner` +
`BattalionManager` (2026-08-05) BOTH decide things about combat units. That
overlap is why `MilitaryEnabled` has stayed false all along. Nothing else can
proceed safely until one of them owns unit orders.

Recommendation, on the evidence of today: **one owner per decision.** The whole
of 2026-08-07 was spent unpicking cases where the planner and the executor each
held their own copy of one decision and the copies drifted — merge radius, tap
reach, detour geometry, destination readiness. The military layer is about to
have the same shape (a planner deciding missions, an executor issuing orders), so
settle ownership before writing anything new:

- `DefencePlanner` — WHAT is worth defending and how much force it needs
- `BattalionManager` — WHICH units, formed into what, and the orders
- `MilitaryManager` — production only, or deleted and its production folded in

## 2. Then bring up defence, in this order

1. Deploy with `DefenceEnabled=true`, `DefenceExecute=false`. Confirm `[UNITCAP]`
   reads sensible weights, `[DEFENCE]` picks assets a human would defend,
   `[BATTALION]` groups sanely. Nothing moves.
2. Turn on `DefenceExecute`. Watch three things: the garrison stays home,
   responses arrive together rather than trickling, and units are not tugged
   between vanilla and us.
3. Only then calibrate. The three placeholders — `THREAT_PER_DEFENDER=25`,
   `STRENGTH_MARGIN=1.5`, `MilitaryCriticalMassSize=15` — all want exchange
   ratios from `combat.jsonl`, and inventing precision before the data exists is
   how the beam's scoring rules happened.

## 3. The blocker, and the unlock

`combat.jsonl` is written by roster diffing and needs no switch, but it stays
EMPTY until something actually fights. Soak rounds run with combat suppressed,
and the alien AI has no offence, so no amount of AI-vs-AI produces the data.

**The unlock is DrMuck playing Sol or Centauri against the alien AI.** For that
game:

```json
"testMode": false,           // no forced round end, no map cycling, aliens fight
"storageBufferCaches": 0     // the storage scaffold is a measurement tool
```

Expect an economy that expands well and defends badly — that is the honest
current state, not a bug to chase mid-game. One game of real raiding is worth
more than any amount of soak for calibration.

## 4. What will bite

- **Deploying replaces the DLL and invalidates any soak in progress.** Check
  whether an experiment is running before touching `Mods/`.
- **The unit cap is shared.** Shrimps have `UnitCapValue=0` and consume none, but
  combat units do. Any military production competes with nothing on the eco side
  directly — but see `UnitCaps.cs` before assuming.
- **`suppressCombat` only applies while `testMode` is true.** Leaving it true in a
  played game is harmless.
- **Money broker**: the military claim should be time-shaped off `WorkerPlan`
  trajectory and yield, not a fixed share. The eco does not need the money after
  minute 12; before minute 8 it very much does.

## 5. Still open on the eco side, so it is not forgotten

- Vanilla-driven relocation is invisible until the shrimps arrive — our census
  only knows destinations we ordered. The clean fix is reading the unit's actual
  move destination by reflection, which closes the whole class.
- `roundTime` reads from process start when a round begins without a scene
  reload, so per-round telemetry can show absurd ages (59.9 min on an 8-minute
  round). It misled a diagnosis today.
- No clean measurement of the day's work yet — the overnight four-arm re-run is
  the first, protocol in `EXPERIMENT_2026-08-07_producer_density_rerun.md`.
