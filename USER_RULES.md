# Si_RTS_AI — Commander Rules & Insights

Everything DrMuck has specified or observed about how the alien AI *should* play,
collected from live-round observation. This is the spec the planners are built
against; where a rule and the code disagree, the rule wins.

Each entry notes whether it is **implemented**, **partial**, or **open**.

Last updated 2026-07-30 (v0.8.25). Clarifications from DrMuck merged.

---

## 1. Bio Cache placement

| # | Rule | State |
|---|---|---|
| 1.1 | **Bio Cache as close as possible to its biotics**, especially in Phase 1. Placement was often far out even when the anchor permitted a close spot. | implemented |
| 1.2 | A Node is **not** required if the biotics is within the BC's build range of the Nest or any *finished* alien structure. **Matters most in Phase 1.** | implemented |
| 1.3 | BC build range is larger than a Node's — and the BC's own radius counts (`MaximumDistanceUseRadius`). Measured: BC 200m + 37m radius; Node 150m. | implemented |
| 1.4 | The **anchor must hold** — always keep a margin so a placement cannot land outside chain reach. | implemented |

**Mechanism found (2026-07-29):** BC-to-patch distance ≈ `anchorDist − BC_REACH`.
If the nearest anchor is further from the patch than the BC's reach, positions
next to the patch are illegal and the game walks the BC *back* toward the anchor.
Predicted to the metre on Badlands (anchor 272m → BC landed 86m out; anchor 325m
→ 125m out). So placing tight is about having an anchor *near the patch*, not
about the aim point.

**Reference:** the human commander built at **median 18m** from the patch (mean
19m, range 13–35m), and the first two BCs were the *loosest* of the round at 27m
and 29m — speed was worth more than precision. The AI now sits at median 11m,
i.e. slightly over-optimised for tightness.

---

## 2. Nodes

| # | Rule | State |
|---|---|---|
| 2.1 | Node spacing = **modded node reach − 15m** (135m at the current 150m reach). Expressed against the live reach so it follows mod changes. | implemented |
| 2.2 | Node chains should run **straight to the closest useful biotics**, not fan out. "On Erg there was a node spam." **Phase 1 only — branching out BECOMES important approaching and during Phase 2.** | implemented |
| 2.3 | **Only Nodes** can anchor while still under construction. A Bio Cache or Cyst must FINISH before it anchors anything. | implemented |
| 2.4 | Wait for the Node to finish, then place the BC close to the biotics — rather than placing the BC early off a distant anchor. | implemented |
| 2.5 | Don't build many Nodes simultaneously at the start — **one at a time**. Saves cash; the time cost is minimal (plan tick 8s vs 20s Node build). | implemented |
| 2.6 | A Bio Cache can serve as the next chain anchor — it has dual use (income + anchor) and reaches further than a Node. Prefer BC hops where a patch lies on the route. | implemented |
| 2.7 | Don't place Nodes *past* a BC in the same tick — wait for the BC and hop off it instead. **Phase 1 rule. In Phase 2 this would block faster branching, so relax it there.** | implemented |

---

## 3. Cyst timing and order

| # | Rule | State |
|---|---|---|
| 3.1 | **The first Bio Cache must precede any Cyst** — the BC is what unlocks Cyst construction. Leading with BCs also opens ground faster (237m vs 150m reach). | implemented |
| 3.2 | From the second site on, **Cyst before BC** is better when cash/timing is the bottleneck: a BC costs money but earns nothing until shrimps exist, so deferring it leaves cash to keep Cysts queueing shrimps. Most critical for the **3rd+ Cyst at the end of Phase 1**. | **superseded in practice by 3.2b — see below** |
| 3.2b | 3.2 is right about that site in isolation, but it was weighed without two other mechanisms, so in practice **every site leads with its Bio Cache**. (a) A Lesser Spawning Cyst costs 1,500 against the BC's 500, so ordering it first at the 3rd/4th site takes cash away from **shrimp production at the Cysts already running** — Phase 1 stalls. NarakaCity 2026-07-30: cash fell 3,800 → 1,700 → 200 and stayed there. (b) Placement needs a *finished* anchor, and at a new site the only finished thing is the node chain, which stops short of the patch — the southern Cyst landed at (2375,820) against a patch at (2307,714). The BC stands beside the patch, so anchoring the Cyst off it puts the Cyst **nearer the biotics**. BC-first is therefore both cheaper and better placed; the cost is the Cyst arriving ~30s later. | implemented |
| 3.3 | **A Bio Cache anchors the build chain while it is still under construction** — it does not have to finish first (user, 2026-07-30). Significant for Phase 1: the chain can hop off a Bio Cache the instant it is placed instead of waiting ~30s for completion. Everything currently requires a FINISHED anchor — `IsChainReachable` skips `!finished`, `NextNodeTowards` and `OpenerPlanner.NearestFinished` consider only completed structures, and `team.Structures` lists a structure only once construction ENDS. So every chain hop today pays a full Bio Cache build it did not need to. | **not implemented — verify then apply** |
| 3.3 | The gap between a BC finishing and its Cyst starting must be **a few seconds**, not tens. | implemented |
| 3.4 | Build the Cyst, produce a shrimp from it, and have the BC complete at about the same time — so harvesting starts the moment the BC opens. | implemented |
| 3.4a | **Gap build order for the 3rd+ biotics** (2026-07-29): place the Cyst; when the Cyst *finishes*, place the BC **and** start a shrimp together. The shrimp builds while the BC goes up, so it harvests and deposits into a freshly finished BC. Timing: Cyst done t=35 → BC placed + shrimp queued → shrimp t=50 → BC t=65 → first deposit ~t=95. The BC's 500 stays in the bank for 35s funding shrimps instead of sitting in a structure that cannot earn yet. | implemented |
| 3.5 | A Lesser Cyst must **always have funds to queue shrimps** — more important than spending on more Nodes when there is a resource conflict. | implemented |
| 3.6 | Lesser Cyst is high value on biotic **clusters** — one Cyst can serve a whole cluster. Don't blindly pair 1:1 with every BC. | partial |
| 3.7 | Skip the Cyst at a new expansion BC when nearby shrimps are about to be freed by a depleting patch — they will migrate in. | open |

---

## 4. Phase 1 (opening)

| # | Rule | State |
|---|---|---|
| 4.1 | Tap **3–4 biotics minimum** before settling. | implemented |
| 4.2 | A **4–5 Cyst opener** is viable off the starting bank (9000). | partial |
| 4.3 | Transition to Phase 2 around **3–4 minutes** on a good start. Time alone is not the trigger — we need MEASURED entry requirements (what did the transition look like before, and now?). | open |
| 4.4 | The planner startup delay is correct — a human commander also waits for the spawn-in (starter units land at t=2s / 18s / 22–24s). | implemented |
| 4.5 | Phase 1 should be decided **up front** as a build order, not re-derived every tick → `OpenerPlanner`. | implemented |
| 4.6 | **Time is the scarce resource in Phase 1 — the opening must be built FAST.** First Cysts placed almost immediately after the first Bio Caches finish; 3+ biotics tapped quickly where the starting cash allows. | partial |

**Opener design agreed 2026-07-29:** derived per-map defaults with optional
per-map JSON override; score candidate openings by full `EcoSimulator` rollout;
hand off when the opening queue completes. Map layout counts as *pre-knowledge*
(an experienced commander knows the map), but **unexplored** biotics are
discounted because placement is FoW-gated — which makes scouting convert
discounted value into full value.

---

## 5. Shrimp management

| # | Rule | State |
|---|---|---|
| 5.1 | Up to **18 shrimps per biotics** is fine. (Capacity had been conflated with the 8-shrimp *spread* target.) | implemented |
| 5.2 | *(superseded — the real observation was that Phase 1 shrimps skipped their own closest biotics and ran to a farther one. See 5.5a.)* | removed |
| 5.3 | Early relocation is expensive — **walking eats precious early harvesting time**. Prefer building local production over walking shrimps in. | implemented |
| 5.4 | Don't over-produce at a BC that will receive migrating shrimps from a depleting neighbour. **Phase 2 concern.** | partial |
| 5.5 | Shrimp auto-relocation on depletion is a *game feature* — but it is **not optimal**: vanilla always sends them to the NEXT CLOSEST biotics, regardless of who else is going there. | implemented |
| 5.5a | **Relocation target rule** (shrimp allocation planner; possibly beam-search worthy): do **not** send shrimps to the closest biotics if it already has shrimps migrating to it, or a Cyst next to it producing shrimps for that spot. Prefer the 2nd/3rd/nth closest patch that has **no shrimps assigned**. Such a patch can be expanded to — or noded toward — *while* the shrimps are already walking there. | open |
| 5.6 | Avoid the mass-migration swarm when a patch depletes — bleed shrimps off gradually beforehand. | implemented |
| 5.7 | Team-wide shrimp ceiling must scale with the map (biotics vary 5.5× across maps). | implemented |

---

## 6. Expansion / Phase 2–3

| # | Rule | State |
|---|---|---|
| 6.1 | Phase 2/3 expansion is **too weak**, especially on spread maps like NarakaCity. Likely cause: **simultaneous branching in several directions at once** is what's missing, not raw spend. | open |
| 6.2 | Expansion must be **multi-directional** — Naraka went north and south but barely west, where 64% of the map's biotics are. | partial |
| 6.3 | Map-dependent optimisation parameters are needed — derived automatically where possible, per-map override where not. | implemented |
| 6.4 | Unspent cash mid/late game — to be addressed with the defence/offence layers. | deferred |

---

## 7. Scouting

| # | Rule | State |
|---|---|---|
| 7.1 | Use the **starter Crabs** for scouting map resources. Squids too (fewer of them). | implemented |
| 7.2 | Use **all** starter Crabs — don't cap conscription during the spawn-in window. | implemented |
| 7.3 | Spread scouts **uniformly in all directions** from the Nest. Don't group them. | implemented |
| 7.4 | Send them toward the **map borders** — rings should be relative to the map edge, not fixed radii. | implemented |
| 7.5 | Up to ~20 scouts is fine, but **don't rush to 20** — grow the roster gradually. | implemented |

---

## 8. Map & mod awareness

| # | Rule | State |
|---|---|---|
| 8.1 | **Si_MapBalance** sets biotics amounts per map *and* per spawn position — never assume a fixed patch value. | implemented |
| 8.2 | `UserData/Spawns/<Map>/layout_*.json` carries spawn positions, resource amounts, per-patch overrides and chain ranges. | noted |
| 8.3 | The AI must read **modded values** (Si_UnitBalance) rather than vanilla assumptions. Reading once at round start is sufficient. | implemented |
| 8.4 | Node build range is 200m-class due to mods — read it, don't assume. Measured live: BC 200m, Cyst 150m, Node 150m; Node **cost 200** (not 100). | implemented |
| 8.5 | **IndustrialQuarter** has high-density biotic clusters: 1–2 Cysts can feed one cluster, and a Phase 1 of 2–4 *clusters* may beat 3–4 individual patches. Cluster mode for that map only. | implemented |
| 8.6 | Tech build-up looks fine as-is. | no action |
| 8.7 | Logging volume is acceptable. | no action |

---

## 9. Reference benchmark — NarakaCity, human commander

Round `20260728_150909`, same spawn (Nest 2520,1275).

| | |
|---|---|
| avg income | **258.5/s** (248,601 over 962s) |
| structures | 22 Bio Cache, 11 Cyst, **157 Node**, 1 Cortex |
| shrimps | 135 |
| BC #1 / #2 / #3 / #4 | 52s / 54s / 143s / 265s |
| Cyst #1 / #2 / #3 | 94s / 98s / 128s |
| income @300 / 420 / 600 / 800s | 21,230 / 40,740 / 63,685 / 175,686 |
| BC distance from Nest (median / max) | 1371m / **3166m** |
| Node spacing (median / max) | 135m / 146m, **0 orphaned** |

**Key insight:** the human's income *tripled* at t≈658s, exactly when reach
crossed 2000m — on Naraka only 13 of 107 patches lie within 1500m, and the
median patch is 3415m out. Reach is the dominant variable on that map.

**Also:** the AI deficit **compounds** — −19% at 180s, −40% at 300s, −55% at
800s. A late third Cyst means fewer shrimps at 3 minutes, less cash at 5, fewer
Bio Caches at 8. Fix the opening, not the late game.

---

## 10. Open items, priority order

1. **Nearest-patch skip** — a closer patch passed over for a farther one.
   Confirmed on NarakaCity (216m skipped for 601m) and Badlands (222m skipped
   for 272m/324m). The `OpenerPlanner` selects these patches correctly, so
   executing the opener may resolve it; if not, the frontier/unlock term is the
   suspect. **Phase 2 problem, not Phase 1** — and it is partly a shrimp
   management issue rather than pure placement.
2. **Phase 2 bonuses are miscalibrated.** `PHASE2_{NODE,CYST,BC}_BONUS = 15000`
   each, sized to cancel a "10s × income-rate" action penalty at **1500/s**.
   In the window that matters on Naraka income is 50–300/s, so the bonus is
   5–30× oversized — which is why Phase 2 over-builds Nodes.
3. **Income model over-predicts ~3.5×** in the opening window (claimed 42,418
   earned / 34,123 in hand at 240s against a real ~12,000 / ~2,000). Needs
   calibrating before the Opener can judge affordability from simulated income
   rather than from the starting bank.
4. **Deep chains** — 58 Nodes against the human's 157; max reach 2363m against
   3166m.
5. `BC_RADIUS_M` reflects as 9m, should be 37m — wrong `ObjectInfo` picked.
6. **BlackIsle rounds do not end** (t=1555s with no round summary while other
   maps end at 960s). Unclear whether this is ours.
7. **NO HARDCODED GAME PARAMETERS.** Build ranges, build times, costs, radii —
   all must be derived from the game at runtime. They differ from vanilla under
   mods, and future game updates will move the vanilla values too. Anything
   hardcoded is a latent bug. (This session found the constants had NEVER been
   read: Node cost was 100 when it is 200, and `TotalConstructionTime` was used
   where `BuildUpTime` was meant.)
