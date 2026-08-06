# Si_RTS_AI â€” Commander Rules & Insights

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
| 1.3 | BC build range is larger than a Node's â€” and the BC's own radius counts (`MaximumDistanceUseRadius`). Measured: BC 200m + 37m radius; Node 150m. | implemented |
| 1.4 | The **anchor must hold** â€” always keep a margin so a placement cannot land outside chain reach. | implemented |

**Mechanism found (2026-07-29):** BC-to-patch distance â‰ˆ `anchorDist âˆ’ BC_REACH`.
If the nearest anchor is further from the patch than the BC's reach, positions
next to the patch are illegal and the game walks the BC *back* toward the anchor.
Predicted to the metre on Badlands (anchor 272m â†’ BC landed 86m out; anchor 325m
â†’ 125m out). So placing tight is about having an anchor *near the patch*, not
about the aim point.

**Reference:** the human commander built at **median 18m** from the patch (mean
19m, range 13â€“35m), and the first two BCs were the *loosest* of the round at 27m
and 29m â€” speed was worth more than precision. The AI now sits at median 11m,
i.e. slightly over-optimised for tightness.

---

## 2. Nodes

| # | Rule | State |
|---|---|---|
| 2.1 | Node spacing = **modded node reach âˆ’ 15m** (135m at the current 150m reach). Expressed against the live reach so it follows mod changes. | implemented |
| 2.2 | Node chains should run **straight to the closest useful biotics**, not fan out. "On Erg there was a node spam." **Phase 1 only â€” branching out BECOMES important approaching and during Phase 2.** | implemented |
| 2.3 | **Only Nodes** can anchor while still under construction. A Bio Cache or Cyst must FINISH before it anchors anything. | implemented |
| 2.4 | Wait for the Node to finish, then place the BC close to the biotics â€” rather than placing the BC early off a distant anchor. | implemented |
| 2.5 | Don't build many Nodes simultaneously at the start â€” **one at a time**. Saves cash; the time cost is minimal (plan tick 8s vs 20s Node build). | implemented |
| 2.6 | A Bio Cache can serve as the next chain anchor â€” it has dual use (income + anchor) and reaches further than a Node. Prefer BC hops where a patch lies on the route. | implemented |
| 2.7 | Don't place Nodes *past* a BC in the same tick â€” wait for the BC and hop off it instead. **Phase 1 rule. In Phase 2 this would block faster branching, so relax it there.** | implemented |

---

## 3. Cyst timing and order

| # | Rule | State |
|---|---|---|
| 3.1 | **The first Bio Cache must precede any Cyst** â€” the BC is what unlocks Cyst construction. Leading with BCs also opens ground faster (237m vs 150m reach). | implemented |
| 3.2 | From the second site on, **Cyst before BC** is better when cash/timing is the bottleneck: a BC costs money but earns nothing until shrimps exist, so deferring it leaves cash to keep Cysts queueing shrimps. Most critical for the **3rd+ Cyst at the end of Phase 1**. | **superseded in practice by 3.2b â€” see below** |
| 3.2b | 3.2 is right about that site in isolation, but it was weighed without two other mechanisms, so in practice **every site leads with its Bio Cache**. (a) A Lesser Spawning Cyst costs 1,500 against the BC's 500, so ordering it first at the 3rd/4th site takes cash away from **shrimp production at the Cysts already running** â€” Phase 1 stalls. NarakaCity 2026-07-30: cash fell 3,800 â†’ 1,700 â†’ 200 and stayed there. (b) Placement needs a *finished* anchor, and at a new site the only finished thing is the node chain, which stops short of the patch â€” the southern Cyst landed at (2375,820) against a patch at (2307,714). The BC stands beside the patch, so anchoring the Cyst off it puts the Cyst **nearer the biotics**. BC-first is therefore both cheaper and better placed; the cost is the Cyst arriving ~30s later. | implemented |
| 3.3 | **Bio Caches and Nodes anchor a NODE while still under construction** â€” they do not have to finish first, and this applies to node placement ONLY; a Bio Cache or Cyst still needs a finished anchor (user, 2026-07-30). Significant for Phase 1: the chain hops the instant the previous piece is placed. Previously **A Bio Cache anchors the build chain while it is still under construction** â€” it does not have to finish first (user, 2026-07-30). Significant for Phase 1: the chain can hop off a Bio Cache the instant it is placed instead of waiting ~30s for completion. Everything currently requires a FINISHED anchor â€” `IsChainReachable` skips `!finished`, `NextNodeTowards` and `OpenerPlanner.NearestFinished` consider only completed structures, and `team.Structures` lists a structure only once construction ENDS. So every chain hop today pays a full Bio Cache build it did not need to. | implemented v0.9.3 |
| 3.4 | **Aliens cannot build at all while the Queen is out of the Nest** (user, 2026-07-30). A player un-nested the Queen at match start and disconnected; every placement then failed for the rest of the round while cash climbed to 81,000 behind 3 shrimps. The planner has no direct signal for this, so it is inferred: repeated placement-search failures mean stop asking, not ask harder. | implemented v0.9.6 â€” direct read of the Nest's AlienQueenCompartment.DockedQueen; backoff from v0.9.5 kept as a second line |
| 3.5 | **Eco assist must account for what the human commander is doing** â€” the user was repairing destroyed Nodes while the AI planned around them (2026-07-30). Co-op mode currently assumes the planner owns eco placement. | open |
| 3.6 | **Let a Bio Cache anchor the chain rather than noding past it.** A site near the Nest needs no nodes â€” the patch is already in build range â€” and once its Bio Cache exists it anchors the chain onward. Building nodes straight toward the 3rd or 4th site before placing it spends nodes on ground the Bio Cache would have covered for free (user, 2026-07-30). Only pays when the Bio Cache is **predominantly in the direction of the next expansion** â€” required to sit at least one full hop closer to the goal â€” and only when it is placeable right now, so an unreachable site cannot stall the chain. | implemented v0.9.6 |
| 5.6 | **Shrimps per biotics is phase-dependent: ~18 early, ~10 from mid-game.** Early there are only two or three patches and everywhere else is a long walk, so packing them is right â€” ramping fast beats efficiency. From Phase 2 there is somewhere to spread to, and packing costs three ways at once: the game's crowding curve drops per-shrimp output from 0.85 at ten to 0.70 at eighteen, the patch drains faster, and the eventual depletion becomes a bigger migration (user, 2026-08-02). Switched on the same Phase 2 threshold as the rest of the planner, so it lands when expansion has actually provided somewhere to go. | implemented v0.13.3 |
| 6.1 | **A loop is worth what it protects.** Scoring bridges by hops-per-node treats a three-structure spur the same as a branch carrying half the map, so the cheap cosmetic loop near the base always won. NarakaCity 2026-08-03: one gigantic branch through the middle with a single connection to the Nest, never bridged. Value is now the number of structures hanging off both ends, and a well-populated branch earns a proportionally longer reach to find help â€” "an expansion line that has only one connection to nest within a long build distance would need to find another branch they can connect without wasting too many nodes" (user). | implemented v0.13.13 |
| 6.2 | **Loop bridges should prefer routes that pass untapped biotics** â€” the same nodes then buy redundancy AND a future expansion site (user, 2026-08-03). | open |
| 3.3 | The gap between a BC finishing and its Cyst starting must be **a few seconds**, not tens. | implemented |
| 3.4 | Build the Cyst, produce a shrimp from it, and have the BC complete at about the same time â€” so harvesting starts the moment the BC opens. | implemented |
| 3.4a | **Gap build order for the 3rd+ biotics** (2026-07-29): place the Cyst; when the Cyst *finishes*, place the BC **and** start a shrimp together. The shrimp builds while the BC goes up, so it harvests and deposits into a freshly finished BC. Timing: Cyst done t=35 â†’ BC placed + shrimp queued â†’ shrimp t=50 â†’ BC t=65 â†’ first deposit ~t=95. The BC's 500 stays in the bank for 35s funding shrimps instead of sitting in a structure that cannot earn yet. | implemented |
| 3.5 | A Lesser Cyst must **always have funds to queue shrimps** â€” more important than spending on more Nodes when there is a resource conflict. | implemented |
| 3.6 | Lesser Cyst is high value on biotic **clusters** â€” one Cyst can serve a whole cluster. Don't blindly pair 1:1 with every BC. | partial |
| 3.7 | Skip the Cyst at a new expansion BC when nearby shrimps are about to be freed by a depleting patch â€” they will migrate in. | open |

---

## 4. Phase 1 (opening)

| # | Rule | State |
|---|---|---|
| 4.1 | Tap **3â€“4 biotics minimum** before settling. | implemented |
| 4.2 | A **4â€“5 Cyst opener** is viable off the starting bank (9000). | partial |
| 4.3 | Transition to Phase 2 around **3â€“4 minutes** on a good start. Time alone is not the trigger â€” we need MEASURED entry requirements (what did the transition look like before, and now?). | open |
| 4.4 | The planner startup delay is correct â€” a human commander also waits for the spawn-in (starter units land at t=2s / 18s / 22â€“24s). | implemented |
| 4.5 | Phase 1 should be decided **up front** as a build order, not re-derived every tick â†’ `OpenerPlanner`. | implemented |
| 4.6 | **Time is the scarce resource in Phase 1 â€” the opening must be built FAST.** First Cysts placed almost immediately after the first Bio Caches finish; 3+ biotics tapped quickly where the starting cash allows. | partial |

**Opener design agreed 2026-07-29:** derived per-map defaults with optional
per-map JSON override; score candidate openings by full `EcoSimulator` rollout;
hand off when the opening queue completes. Map layout counts as *pre-knowledge*
(an experienced commander knows the map), but **unexplored** biotics are
discounted because placement is FoW-gated â€” which makes scouting convert
discounted value into full value.

---

## 5. Shrimp management

| # | Rule | State |
|---|---|---|
| 5.1 | Up to **18 shrimps per biotics** is fine. (Capacity had been conflated with the 8-shrimp *spread* target.) | implemented |
| 5.2 | *(superseded â€” the real observation was that Phase 1 shrimps skipped their own closest biotics and ran to a farther one. See 5.5a.)* | removed |
| 5.3 | Early relocation is expensive â€” **walking eats precious early harvesting time**. Prefer building local production over walking shrimps in. | implemented |
| 5.4 | Don't over-produce at a BC that will receive migrating shrimps from a depleting neighbour. **Phase 2 concern.** | partial |
| 5.5 | Shrimp auto-relocation on depletion is a *game feature* â€” but it is **not optimal**: vanilla always sends them to the NEXT CLOSEST biotics, regardless of who else is going there. | implemented |
| 5.5a | **Relocation target rule** (shrimp allocation planner; possibly beam-search worthy): do **not** send shrimps to the closest biotics if it already has shrimps migrating to it, or a Cyst next to it producing shrimps for that spot. Prefer the 2nd/3rd/nth closest patch that has **no shrimps assigned**. Such a patch can be expanded to â€” or noded toward â€” *while* the shrimps are already walking there. | open |
| 5.6 | Avoid the mass-migration swarm when a patch depletes â€” bleed shrimps off gradually beforehand. | implemented |
| 5.7 | Team-wide shrimp ceiling must scale with the map (biotics vary 5.5Ã— across maps). | implemented |

---

## 6. Expansion / Phase 2â€“3

| # | Rule | State |
|---|---|---|
| 6.1 | Phase 2/3 expansion is **too weak**, especially on spread maps like NarakaCity. Likely cause: **simultaneous branching in several directions at once** is what's missing, not raw spend. | open |
| 6.2 | Expansion must be **multi-directional** â€” Naraka went north and south but barely west, where 64% of the map's biotics are. | partial |
| 6.3 | Map-dependent optimisation parameters are needed â€” derived automatically where possible, per-map override where not. | implemented |
| 6.4 | Unspent cash mid/late game â€” to be addressed with the defence/offence layers. | deferred |
| 6.5 | **Phase 2 is a blueprint, not a series of choices** (DrMuck, 2026-08-04). Greedy per-tick branching works but has no global picture, so around 18min it produces "noding madness" â€” every frontier point claiming its own nearest patch forever. Instead: **scan** the discovered biotics, **plan** the whole branched network as the cheapest way to cover them, **build to that plan**, and **refresh** it at intervals against what already stands. | implemented v0.15.0 â€” `Planning/Blueprint.cs` |
| 6.6 | The plan is judged on **cost and speed**. Cost is what it actually pays (hops Ã— Node + Bio Cache), so a second patch behind the first pays only for the hops past it and branches merge instead of running side by side. Speed is the seconds before a site earns â€” chain build time plus the walk out along the network â€” which is what stops the plan running one line to 1,700m before touching the other side of the base. Both come from live ConstructionData. | implemented v0.15.0 |
| 6.7 | **Cyst placement is optimised separately** from the network. Producers are a shrimp question: is anything already covering this ground, is there unit-cap room to produce at all, and would migration staff the site without paying 1,500. The observed failure was a row of Lessers through the middle of the map unable to produce while the shrimps were all east. The eagerness is the strategy dial â€” `[Si_RTS_AI_Blueprint]` MaxCystsPerPlan / CystStaffedEnough / CystRelocationSpacings. | implemented v0.15.0 |
| 6.14 | **Income per worker is the signal that decides which lever to pull** (DrMuck, 2026-08-05, on IndustrialQuarter: "steep income rate from 10min to 20min, but then the slope was lower to the end â€” I wonder why"). Measured that round: yield peaked at 3.48/worker/s at minute 20 and fell to 2.24 by minute 33 while workers GREW 129 to 168 and income fell 449/s to 377/s. Patches drain, so the same shrimps walk further per load; and workers past a patch's useful density crowd it. Both show up in yield long before they show up in the worker count. Rising or steady yield means more workers pay, so producers are the answer; falling yield means the answer is more GROUND, and producers are suspended so the trajectory cannot pour shrimps onto patches that cannot feed them. | implemented v0.20.1 |
| 6.12 | **The economy aims at a worker trajectory: ~100 shrimps by 10 minutes** (DrMuck, 2026-08-05, from the overnight soak). Measured across 14 rounds: workers-at-10min correlates 0.80 with income-at-25min, Cysts-at-10min 0.79. The runaway round (TheMaw, 1.05M by 25min) had 20 Cysts and 106 workers at ten minutes; the stalled ones had 3-5 Cysts and 37-58 workers, one of them frozen at 37 workers from 5min to 10min. Being behind the curve now raises producer count, suspends the sustainable-capacity ceiling, and widens expansion â€” because a worker needs somewhere to work as much as it needs a producer to be born in. A reference, not a quota: it gets out of the way once the curve is met, and after that producers go to the outer sites. | implemented v0.20.0 â€” `Planning/WorkerPlan.cs`, `WorkersByTenMinutes` |
| 6.13 | **Production stalls are the expensive failure, not overproduction.** Every worker ceiling was added against a mid-game pile-up and each is right in the case that produced it, but together they had no idea what they were aiming at â€” so an economy could sit at 37 workers for five minutes with tens of thousands unspent and nothing in the code considered that wrong. The same-map evidence: two Citadel rounds with identical opener plans and producer counts, one pinned at 10-270 cash for four minutes (19 workers at 5min, 47k at 10min) and one that never was (52 workers at 5min, 74k). | implemented v0.20.0 |
| 6.10 | **Benchmark: ~100,000 earned by 10 minutes** (DrMuck, 2026-08-05). Reference point for the whole eco stack. Measured on NarakaCity the same day: 94,895 cumulative at 13m14s â€” so roughly 30% short of the target pace, and the gap is production rather than ground (70 shrimps at that point, 22 Bio Caches, 8 Cysts, 53,000 cash unspent). | open |
| 6.11 | **When cash is surplus, node into EVERY branch at once** (DrMuck, 2026-08-05). The per-tick node-fire ceiling was a flat 6 while the strategy sweep was choosing twelve fronts, so the plan committed to breadth the fire budget then refused to fund. The ceiling now follows the chosen breadth; cash is checked per fire and the shrimp reserve is held back separately, so it never needed to be the thing rationing expansion. | implemented v0.18.2 |
| 6.15 | **Unconverted cash is the biggest single waste in the economy** (DrMuck, 2026-08-06). Measured on a 35-minute NarakaCity round: cash tight only for the first five minutes (2,300 at 5min), then climbing monotonically to **138k idle at the end** against 1,000k earned â€” 13.7% of income never turned into anything, with shrimps pinned at 177 and Cysts at 13 from minute 20. Two separable parts: money NEVER SPENT (~138k, needs a claimant â€” military, tech, or uncapped production) and money SUNK IN IDLE STRUCTURES (~20-40k in Bio Caches that never earn plus their chains). | open |
| 6.17 | **The bridge rule is a switch, not a replacement** (DrMuck, 2026-08-06: "keep the previous bridging solution in mind... a json config... that allows the bridging implementation between this and the previous version"). Two objectives, both live: `loop` prices a link by the DETOUR REMOVED between its ends over the built network plus the single points of failure it bypasses (v0.26, up to 8 nodes), `shortcut` by how much the far side's road HOME shortens (v0.25, up to 6 nodes), `off` plans and draws them but builds none. Selected by `bridgeMode` in `UserData/rtsai.json` or by the A/B arms `bridgeloop` / `bridgeshortcut` / `bridgeoff`, which hold producer density at ratio3 so the two experiments never share a round. Shortcut is not merely the older attempt â€” when the network is one long line rather than a fan, the road home IS the thing worth shortening. | implemented v0.27.0 |
| 6.18 | **Configuration that can be changed while the server runs.** MelonPreferences is rewritten from memory on shutdown, so a mid-session edit is silently reverted and every change costs a restart â€” the failure that wasted 25 rounds on 2026-08-05. `UserData/rtsai.json` is READ and never written, re-read at every map load, so an edit takes effect on the next round with the server up. Every key optional; absent keys fall back to the preference, no file means no change. While the A/B rig is cycling arms it owns `workerCapPerBioCache` and `producerPerSites` â€” those keys are ignored with one warning, so a live edit cannot quietly rewrite one arm of a running comparison. Template: `rtsai.example.json`. | implemented v0.27.0 â€” `Planning/RtsaiConfig.cs` |
| 6.19 | **The auto-drain was destroying a third of the economy, and `cumulIncome` was never spendable money.** Ledger of the ratio3 round of 2026-08-06 (NarakaCity, 35min): 987,722 "earned", of which **315,403 was deleted by our own AutoResourceDrain** (cut to 70% of team capacity whenever it passed 75%), ~130,600 actually bought things â€” 258 Nodes 51,600 + 70 Bio Caches 35,000 + 15 Cysts 22,500 + 178 shrimps 28,480 = 137,580 standing â€” and 196,517 was still banked at the whistle. So the conversion rate is ~137k of ~630k spendable, about a fifth, and rule 6.15's "138k idle against 1,000k earned" understated it by treating drained income as if it had been available. Team resource capacity is **~4,000 per Bio Cache**, so the cap grows with the base and holding cash is punished twice. Drain now defaults off, is settable from `rtsai.json` without a restart, and announces its state at every round start. | measured 2026-08-06, drain off from v0.27.1 |
| 6.16 | **Recycling idle structures â€” deferred, and possibly impossible.** DrMuck: "selling off unused nodelines and empty biocaches while spreading base could be something that gives a bit money back... something for very much later, once military is in." Checked the constants dump for Sell / Refund / Recycle / Salvage / Demolish / Dismantle: **no such members exist**, so Silica may have no cash-back mechanic at all (absence of evidence â€” the dump does not cover every type). The cheaper route to most of the same money needs no API: stop committing to sites the economy cannot staff, so the cash never leaves the bank. Recycling would still be the only way to recover ground that WAS worth taking and later drained. | deferred |
| 6.9 | **A Bio Cache without a Cyst is not unstaffed** â€” it is staffed by whoever finishes a patch next, and that is predictable: patch remaining / the summed drain rate of the Bio Caches working it = depletion time, the shrimps standing there = the group that comes free, distance / shrimp speed = the walk (DrMuck, 2026-08-05). The producer decision is then a race with no tuned constant: a Lesser Cyst delivers a group after its build plus one shrimp per build interval; if migration arrives first the 1,500 buys a queue against the shrimp cap. Predictions carry error and players interfere, so the forecast is trusted only as far as the next replan. | implemented v0.16.1 â€” `Planning/SupplyForecast.cs` |
| 6.8 | **Every plan revision is persisted and served** â€” `UserData/RTSA/blueprint/round-*/rev-NNNN.json` for post-match review, and `http://localhost:<port>/blueprint` for the layers viewer to draw planned-versus-built with branch identity. A plan you cannot look at can only be judged by what got built. | implemented v0.15.0 â€” viewer layer still to be drawn |

---

## 7. Scouting

| # | Rule | State |
|---|---|---|
| 7.1 | Use the **starter Crabs** for scouting map resources. Squids too (fewer of them). | implemented |
| 7.2 | Use **all** starter Crabs â€” don't cap conscription during the spawn-in window. | implemented |
| 7.3 | Spread scouts **uniformly in all directions** from the Nest. Don't group them. | implemented |
| 7.4 | Send them toward the **map borders** â€” rings should be relative to the map edge, not fixed radii. | implemented |
| 7.5 | Up to ~20 scouts is fine, but **don't rush to 20** â€” grow the roster gradually. | implemented |
| 7.6 | **A scout keeps its heading.** Scouts were seen turning around before reaching the west border, leaving areas uncovered (DrMuck, 2026-08-05). They were not turning around: the star sized itself by the LIVE SCOUT COUNT, so two scouts meant a two-armed star, and every Crab built or lost re-divided the circle and re-aimed everyone still walking. The star is fixed at MaxScouts arms; a scout claims the arm furthest from those already taken and holds it for life. | implemented v0.16.1 |
| 7.8 | **Don't feed an arm that kills scouts.** DrMuck, 2026-08-05: "some of the scout crabs were fed into one scouting arm, because they were cannon fodder for a HQ." A fixed star makes this worse on its own, because the arm a scout just died on is empty and therefore the furthest from every other scout â€” the most attractive one to claim next. A death now records a grave: that ground is skipped as a waypoint for 240s (it was revealed on the way in, so nothing is lost by not going back), and the arm it sits on costs the next recruit three arm-spacings of angular gap, which sends them to a different part of the map instead of one arm along. Graves expire so a fallen base reopens the ground; if the probe dies again the grave simply refreshes. | implemented v0.16.2 |
| 7.7 | **An exhausted arm is not an explored map.** Releasing a scout when its own arm held nothing new burned 58 Crabs in one round to keep two scouts, at 58% explored with 38 patches dark â€” each new Crab was recruited onto a short, already-revealed arm and discharged the same second. A scout with nothing on its arm now walks to the nearest dark ground (undiscovered biotics first, then the fog layer) and is released only when there is none. | implemented v0.16.1 |

---

## 8. Map & mod awareness

| # | Rule | State |
|---|---|---|
| 8.1 | **Si_MapBalance** sets biotics amounts per map *and* per spawn position â€” never assume a fixed patch value. | implemented |
| 8.2 | `UserData/Spawns/<Map>/layout_*.json` carries spawn positions, resource amounts, per-patch overrides and chain ranges. | noted |
| 8.3 | The AI must read **modded values** (Si_UnitBalance) rather than vanilla assumptions. Reading once at round start is sufficient. | implemented |
| 8.4 | Node build range is 200m-class due to mods â€” read it, don't assume. Measured live: BC 200m, Cyst 150m, Node 150m; Node **cost 200** (not 100). | implemented |
| 8.5 | **IndustrialQuarter** has high-density biotic clusters: 1â€“2 Cysts can feed one cluster, and a Phase 1 of 2â€“4 *clusters* may beat 3â€“4 individual patches. Cluster mode for that map only. | implemented |
| 8.6 | Tech build-up looks fine as-is. | no action |
| 8.7 | Logging volume is acceptable. | no action |

---

## 9. Reference benchmark â€” NarakaCity, human commander

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

**Key insight:** the human's income *tripled* at tâ‰ˆ658s, exactly when reach
crossed 2000m â€” on Naraka only 13 of 107 patches lie within 1500m, and the
median patch is 3415m out. Reach is the dominant variable on that map.

**Also:** the AI deficit **compounds** â€” âˆ’19% at 180s, âˆ’40% at 300s, âˆ’55% at
800s. A late third Cyst means fewer shrimps at 3 minutes, less cash at 5, fewer
Bio Caches at 8. Fix the opening, not the late game.

---

## 10. Open items, priority order

1. **Nearest-patch skip** â€” a closer patch passed over for a farther one.
   Confirmed on NarakaCity (216m skipped for 601m) and Badlands (222m skipped
   for 272m/324m). The `OpenerPlanner` selects these patches correctly, so
   executing the opener may resolve it; if not, the frontier/unlock term is the
   suspect. **Phase 2 problem, not Phase 1** â€” and it is partly a shrimp
   management issue rather than pure placement.
2. **Phase 2 bonuses are miscalibrated.** `PHASE2_{NODE,CYST,BC}_BONUS = 15000`
   each, sized to cancel a "10s Ã— income-rate" action penalty at **1500/s**.
   In the window that matters on Naraka income is 50â€“300/s, so the bonus is
   5â€“30Ã— oversized â€” which is why Phase 2 over-builds Nodes.
3. **Income model over-predicts ~3.5Ã—** in the opening window (claimed 42,418
   earned / 34,123 in hand at 240s against a real ~12,000 / ~2,000). Needs
   calibrating before the Opener can judge affordability from simulated income
   rather than from the starting bank.
4. **Deep chains** â€” 58 Nodes against the human's 157; max reach 2363m against
   3166m.
5. `BC_RADIUS_M` reflects as 9m, should be 37m â€” wrong `ObjectInfo` picked.
6. **BlackIsle rounds do not end** (t=1555s with no round summary while other
   maps end at 960s). Unclear whether this is ours.
7. **NO HARDCODED GAME PARAMETERS.** Build ranges, build times, costs, radii â€”
   all must be derived from the game at runtime. They differ from vanilla under
   mods, and future game updates will move the vanilla values too. Anything
   hardcoded is a latent bug. (This session found the constants had NEVER been
   read: Node cost was 100 when it is 200, and `TotalConstructionTime` was used
   where `BuildUpTime` was meant.)

