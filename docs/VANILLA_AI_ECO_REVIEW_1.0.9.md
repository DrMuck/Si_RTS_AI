# Silica vanilla commander AI (Release 1.0.9): Sol/Centauri economy review

Feedback for the Silica developers, from a static read of the decompiled `SilicaCore.dll` (Release 1.0.9, namespace `Silica.AI`) plus prefab/asset data read from the dedicated server install of the same build. Nothing below was measured in a live match; every item is marked **confirmed** (visible in code or asset data) or **plausible** (needs a runtime check). Line numbers refer to an ILSpy decompile and will be approximate against the source.

Observed in play: the human AI builds far too many refineries, places them badly (ramp not toward the field, stacked on one patch or on the edge of the HQ build range), and rarely expands with a second HQ. The code explains all three.

---

## 1. Summary: three root causes

1. **The AI builds refineries as if they were harvesters.** `AIResourcesHandler.GetConstructibleHarvester()` returns the cheapest *structure that spawns a harvester on completion*. For humans that is the Refinery (3000) until an Ultra Heavy Factory exists (the Harvester unit is only built there) or while the vehicle cap is full. Each refinery delivers 1 harvester but raises the required harvester capacity by 2 harvesters, so the shortfall never closes and a new refinery is requested every think as long as 3000 credits are free. These refineries are targeted at **the resource area nearest world origin `Vector3.zero`**, not the AI's base, with no "already served" check. This is the main cause of "way too many".
2. **No one-refinery-per-field rule and no orientation logic.** "Served" means a deposit point within 220 m x MapScale of the field's `SignalCenter`, which is the richest remaining cell and moves as the field is mined. Placement takes the first valid cell on a square spiral and the first valid yaw in the order 0/90/180/270 degrees (`useInputRotation: false`). The ramp/lane side never enters the decision.
3. **HQ expansion is gated and mis-scored.** A new HQ is only attempted at tech tier 5 (asset `MinimumTeamTier = 5` on both human HQs; the AI never plans tech toward it), only at team **start points**, and scored by `sum of resources within 600 m / distance to home`, which rewards the nearest site and counts fields already served by own refineries or held by the enemy. Out-of-reach patches never become an expansion trigger for humans; instead the refinery request is clamped onto the 600 m ring around the HQ and built there, serving nothing.

---

## 2. Refinery count and placement

### How it works today
Three independent paths request a refinery each think (3 s):

- **Path A, deposit points** (`AIResourcesHandler.Think`, ~lines 258-294). Target = `max(min(ceil(harvesters/3), W), min(W, 4 * #HQs))` where `W` = discovered non-empty areas within **2000 m x MapScale of the first HQ**. `PickDepositArea` returns the single best unserved area (score = -dist(home) + 0.25 * enemyDist - 0.5 * forward penalty). Out-of-reach areas are only deferred to the expansion code `if (... && Expansion.GetHasRelayAnchor())` (~line 283), which is never true for humans (only the HQ is a `StructureConstructionAnchor`).
- **Path B, harvester shortfall** (~lines 265-308). Needed capacity = `max(capEach * num3, 1.5 * LastMissingResources, min(W, #deposits) * num3 * capEach)` with `num3 = clamp(round(4000 / capEach), 2, 10)`. With the real harvester capacity of 7500 this is `num3 = 2`, i.e. 2 harvesters (15 000 capacity) per refinery. Then:

```csharp
ConstructionData cd = GetConstructibleHarvester() ?? GetConstructibleHarvester(onlyBuildable: false);
...
ResourceArea.GetNearestResourceArea(Team, Team.UsableResource, Vector3.zero, out var closestArea);  // nearest to WORLD ORIGIN
Vector3 nearPoint = closestArea ? closestArea.SignalCenter : Vector3.zero;
AIRequest.Construct(this, CapEconomyPriority(...), cd, nearPoint);
```

  `GetConstructibleHarvester` accepts any structure with `ObjectInfo.ContainsInitSpawnUnitType(UnitType.Harvester)`. The Refinery qualifies through its `SpawnOnInit` harvester spawn point.
- **Path C, first-of-each-type** (`AIConstructionHandler.Think`, ~lines 358-362): priority 50, no position, placed around the HQ. Only fires with zero refineries.

Turning a request into a site (`AIConstructionHandler.TryBuild`, ~lines 517-588):
- Duplicate guard only looks at **construction sites** within 600 m of the request point; finished refineries are ignored.
- Target clamped to 600 m from the nearest HQ; search radius 600 m, 15 m grid, 30 s, square spiral from the snapped `SignalCenter`, yaw 0/90/180/270 in world axes, first passing candidate wins (`ConstructionPlacement.QueueFirstValidPlacementAroundPoint`, `useInputRotation: false`).
- Validity: no overlap with any non-empty `ResourceArea.Bounds`, both 60 m Entry boxes free and on the Harvester navmesh, both AIEntryPoints connected to some discovered patch (`ConstructionAccessRequirement`). None of these prefers a ramp facing the field.
- On failure: 45 s cooldown for the whole structure type; no per-area memory.

### Confirmed defects
| # | Defect | Where |
|---|---|---|
| R1 | Path B treats the Refinery as a harvester source; no refinery cap, no per-patch rule; stops only near `#refineries ~ 2W` or when the Harvester unit becomes the cheaper option. `1.5 * LastMissingResources` (sum of shortfalls of all unaffordable requests, e.g. saving for an HQ or tech tier) inflates the demand further. | `AIResourcesHandler.GetConstructibleHarvester` (~463-482), `Think` (~265-308), `AIConstructionHandler` (~389-406) |
| R2 | Path B target = area nearest `Vector3.zero`. Same point for both teams; successive refineries ring that patch or stack on the 600 m HQ ring in the direction of the map origin. | `AIResourcesHandler.cs` ~304 |
| R3 | Path A counts patches up to 2000 m x MapScale away but can only build within 600 m of an HQ. Out-of-reach patch -> request clamped to the 600 m ring -> refinery > 220 m from the patch -> patch still "unserved" -> next think picks it again -> refineries stack until the target count is met. | ~152-169, ~283, `TryBuild` ~538-541 |
| R4 | Yaw/ramp orientation never chosen; first passing world-axis yaw wins (biased to 0 degrees). Side of the field decided by spiral order, not HQ direction. | `AIConstructionHandler.cs` ~559, `ConstructionPlacement.cs` ~421, ~528-551 |
| R5 | "Served" measured to the moving `SignalCenter` (richest remaining cell, recomputed after each extraction) rather than the field's bounds; a large field can lose "served" status and get a second refinery. | `ResourceArea.cs` ~668-688 |
| R6 | Split fields (one visual field = several `ResourceArea` objects > 220 m apart) get one refinery per object. | `PickDepositArea` |
| R7 | No per-area failure memory: an unplaceable area is retried every 45 s and the second-best is never tried; each failed search holds the single placement slot for up to 30 s, blocking factories and barracks. | `AIConstructionHandler` ~431, ~1116-1129 |
| R8 | Constants tuned for a 2000-capacity harvester (`HARVESTER_REFERENCE_CAPACITY`, "4000 per area"), real capacity is 7500; with the clamp minimum of 2 this yields 2 harvesters per refinery. | `AIResourcesHandler.cs` ~28-30, ~265 |
| R9 | `DEPOSIT_POINTS_PER_AREA_MAX = 4` is applied per **HQ**, not per area (target up to 12 refineries with `Expansion_BasesMax = 3`). | ~264 |

Plausible: the entry-reachability check disables itself entirely if **any** discovered patch samples off the Harvester navmesh (`ConstructionAccessRequirement.GetSatisfied` ~164: `if (m_TargetAreas.Count == 0 || !flag) return true;`), and `ConstructionBoundsEntryNum == ConstructionBoundsEntryNum` (`ConstructionPreview.cs` ~470/543/552) is always true and looks like a typo.

### Suggested fixes
1. In `GetConstructibleHarvester`, skip structures that have a `ResourceDepositPoint` (or return null and leave refineries to Path A). Replace `Vector3.zero` with the home position or the area Path A picked.
2. Derive harvester demand from harvesters per refinery (1-2) instead of `round(4000/capEach)` clamped >= 2, and stop adding `LastMissingResources` to harvester demand.
3. Compute `W` and `PickDepositArea` only over areas whose **edge** is within `MaximumBaseStructureDistance` of an HQ; skip out-of-reach areas instead of clamping; merge `ResourceArea`s whose bounds are within a deposit radius of each other.
4. Make "served" a real one-per-field claim: distance from deposit point to the nearest non-empty cell / `Bounds`, and count refinery construction sites as serving.
5. Choose position and yaw on purpose: start the search at the field edge nearest the HQ; for each valid cell score all 4 yaws by distance (or navmesh path) from the AIEntryPoints/deposit point to the nearest resource cell; or compute the yaw that turns the lane side (Sol: local +X, Centauri: local -Z) toward the field and pass it via the existing `useInputRotation`/`inputRotation` parameters. Reject candidates whose deposit point ends up farther than the served radius from the field.
6. Blacklist an area for ~120 s after a failed placement.

**Note on reach constants vs modded values.** `WORKED_AREA_REACH` (2000 m x MapScale, measured from the FIRST HQ only) and `DEPOSIT_AREA_SERVED_RADIUS` (220 m x MapScale) are hard-coded constants. The refinery and HQ build reach (`ConstructionData.MaximumBaseStructureDistance`) are read at runtime, so modded values flow into the placement clamp in `TryBuild`, the out-of-reach test at ~282 and the expansion's 0.9 x reach logic, but never into `W`. The clean formulation is: `W_refinery` = patches whose edge is within refinery reach of ANY own HQ (this is what refinery requests should count), and `W_expansion` = patches within HQ reach + refinery reach of an own HQ but outside `W_refinery` (this is the HQ-expansion demand signal). On a server where refinery reach is 1000 m and HQ reach 2500 m, the vanilla 2000 m constant still counts patches a refinery cannot reach, and the 220 m served radius does not scale with the larger reach either.

---

## 3. HQ expansion

### How it works today (`AIExpansion.TryBegin` / `TryPickSite`, ~559-681)
A human AI attempts a new HQ only when all of these hold at once:
- `Team.GetHasPrerequisitesForConstruction(HQ)` -> tech tier >= `MinimumTeamTier` = **5** (asset, both human HQs); the expansion code never raises tech priority to get there.
- Standing defence: >= 4 combat groups (+2 per extra base) and no undefended base cluster, rechecked every tick; failure aborts the attempt.
- Trigger: all ever-worked areas down to 40 % of peak, **or** 21 000 free credits (HQ 7000 x (1 + `Expansion_AffordableMultiple` 2)).
- Site: candidates are **team start points only** (own and enemy); filter: no own structure within 300 m, no detected enemy within 800 m x MapScale; score = `CountResourcesNear(site, 600) / distance(home)`; highest wins.
- Then: escort the nearest defence group to ~90 m of the site, request the HQ at priority 100; `TryBuild` clamps to 1500 m from the nearest own HQ, needs active vision at the site. Attempt timeout `TravelTime(2d, 240)`, which is a flat 240 s for MapScale <= 1 and includes saving time.

### Confirmed defects
| # | Defect |
|---|---|
| H1 | Tier-5 gate with no tech planning toward it: the second HQ comes as late as the generic tech logic (priority 25 early, needs 2-4x surplus) allows. |
| H2 | Only start points are candidates; the "as far out as HQ reach allows, over unclaimed fields" spot is never evaluated. |
| H3 | Score rewards proximity (divide by distance) and counts **all** fields within 600 m, including ones already served by own refineries, enemy-held, or undiscovered. Tooltips for `Expansion_SiteResourcesMin` / `Expansion_OpportunityResourcesMin` say "unworked"; the code does not implement that. This is the reward-function issue. |
| H4 | No reach check on the human path (the alien chain path has one). A start point > ~1.5-1.7 km from every own HQ: request clamped to exactly 1500 m (invalid under the radial far-edge rule), escort stands at the wrong point, placement fails, 45 s cooldown, retry until timeout. |
| H5 | No failure memory and no cooldown after `Abandon()`: the same site is re-picked next tick; one unbuildable site blocks expansion for the rest of the match while the 7000 reservation stays held. |
| H6 | `Abandon()` releases the 7000 reservation inside the same `Think` before `AIConstructionHandler.Think` runs, so the saved bank can be spent on every timeout. |
| H7 | `Expansion_OpportunityResourcesMin` (8000) is never referenced; both triggers use `Expansion_SiteResourcesMin` (5000). |
| H8 | "Patch out of reach" never becomes an expansion signal for humans (`RequestResourceReachTarget` only via relay anchor); instead clamped refineries fill the deposit quota (see R3). |
| H9 | Inconsistent MapScale use: 600 m site radius and 300 m own-structure exclusion unscaled, enemy-avoid distance scaled (up to 2 km); detected enemy structures are never forgotten, so one seen enemy refinery can exclude every start point on a large map. |

Plausible: the 21 000-credit trigger is practically never reached (surplus goes to units, storage only built near full); the depletion trigger fires after income has already fallen; the defence requirement cancels attempts mid-way when the escort was the only home guard; an unscouted enemy spawn can be chosen as the site; a 3rd HQ is scored by distance from the first HQ.

### Suggested fixes
1. Score sites by **newly covered, unclaimed** fields (exclude fields within own/allied/enemy build reach or with an enemy refinery) and drop or invert the divide-by-distance term (prefer the far edge of HQ reach).
2. Generate candidates around field clusters or on a ring at ~0.9 x HQ reach around own HQs, with a reach check, instead of start points only.
3. Add a "reach-starved" trigger for humans: if the best field is out of refinery reach and no relay anchor exists, start an expansion instead of building a clamped refinery.
4. When expansion is wanted but the tier gate blocks it, raise tech-tier priority (or post the HQ construct request so `GetNextConstructibleTowards` resolves the next tier).
5. Blacklist failed sites, add a cooldown after `Abandon`, exclude saving time from the attempt timeout, keep the reservation through the timeout tick.
6. Wire up or delete `Expansion_OpportunityResourcesMin`; scale distances consistently; send the escort to the clamped point.

---

## 4. Harvester management

### How it works today
- **Count** is set by two disagreeing mechanisms: the economy formula (priority `(1 - cap/need)^2 * 100`, capped at 55, requests rarely served because `AIConstructionHandler` breaks at the first unaffordable higher-priority request) and **random group presets** (`AIGroupPresetGenerator` ~449: `Random.Range(1,5)` members per harvester group, re-rolled for all groups whenever the buildable set changes, surplus never shed, missing members rebuilt at priority 25-100 with no economy cap).
- **Patch choice**: each harvester picks the straight-line nearest discovered area on its own at spawn, on depletion and after unloading (`AIHarvesterAgent` ~606-625, ~786-792, ~831-836); no deposit distance, enemy, play-area or path cost considered. `ResourceArea.MAX_MINERS` / `ReserveMinerSlot` exist but only `AIAlienHarvesterAgent` uses them. Commander harvest tasks only order idle units; units added to a group later get no order (`AIGroup.AddUnit`).
- **Deposit choice**: nearest unreserved pad by straight-line distance (`Team.cs` ~1835-1875); the pad is reserved for the whole drive home. If all pads are busy the harvester parks up to 200 m short and never re-checks other pads; when a pad frees, all waiters go for it without re-reserving; the DOCKING transition checks no claim and has no timeout.

### Confirmed defects
| # | Defect |
|---|---|
| V1 | No load balancing: harvesters cluster on the field nearest whichever refinery spawned them; on depletion all pick the same next field. |
| V2 | Harvester count is a dice roll via group preset size, and ratchets up only. |
| V3 | When the Primary vehicle cap is full, harvesters leave `BuildableUnits` and the shortfall becomes a **refinery** request (same path as R1); refinery-spawned harvesters ignore the cap. |
| V4 | Harvester demand tied to `1.5 * LastMissingResources` (construction wishlist), not income/throughput. |
| V5 | Harvester build location anchored to the area nearest world origin (same `Vector3.zero` as R2). |
| V6 | Pad reserved for the whole return trip; a full harvester often drives to a distant refinery; no re-check when a nearer pad frees. |
| V7 | Waiters race for a freed pad; docking has no claim check and no timeout -> ramp jams. |
| V8 | Group reassignment can pull a docked harvester off the pad half-unloaded (`DOCKED` branch undocks on a queued move even if `AmountStored > 0`). |
| V9 | No harvester is built while the 7000 HQ reservation is held unless bank >= 8500, precisely when depletion made replacements necessary. |

Plausible: full team storage leaves every docked harvester waiting and blocks all pads; idle harvesters park on the ramp entry; a harvester can end up DOCKED off-pad with no recovery; wasted trips to a destroyed refinery.

### Suggested fixes
1. Make commander patch assignment binding (order new members, re-target harvesters on the wrong field, suppress self-search while a task exists) and reuse the alien miner-slot cap for humans.
2. Give harvester count one owner: no random preset path for harvesters; linear priority curve or a floor.
3. Choose pads by path cost plus queue time; reserve only when close; re-check while waiting; check the claim before docking; add a docking timeout.
4. Exempt harvesters from the expansion reservation; build them near the HQ or the most under-served field.

---

## 5. Key numbers (1.0.9 assets unless noted)
| Item | Value |
|---|---|
| Refinery | 3000 credits, 1 deposit point, spawns 1 harvester, build reach 600 m from HQ, grid 15 m, no production |
| HQ | 7000 credits, reach 1500 m, `MinimumTeamTier` 5, no deposit point |
| Harvester unit | 1500 credits, capacity 7500, built only by Ultra Heavy Factory |
| Think interval | 3 s |
| `WORKED_AREA_REACH` | 2000 m x MapScale (MapScale = clamp(half-extent/1500, 0.4, 2.5)) |
| `DEPOSIT_AREA_SERVED_RADIUS` | 220 m x MapScale |
| `DEPOSIT_POINTS_PER_AREA_MAX` | 4 (applied per HQ) |
| `HARVESTER_REFERENCE_CAPACITY` | 2000 (real: 7500) |
| Economy priority cap | 55 (`Posture_EconomyPriorityCap`) |
| Max refinery sites in flight via Path B | 2 |
| Placement failure / blocked cooldown | 45 s / 12 s |
| Expansion: bases max / depletion / affordable multiple | 3 (scaled 2-8) / 40 % / 2x (21 000 free) |
| Expansion: site radius / own exclusion / enemy avoid / timeout | 600 m / 300 m / 800 m x MapScale / 240 s |
| Read at runtime (modded values honoured) | `MaximumBaseStructureDistance` of Refinery and HQ, `ResourceCost`, `MinimumTeamTier` |
| Hard-coded (modded reach ignored) | `WORKED_AREA_REACH` 2000 m, `DEPOSIT_AREA_SERVED_RADIUS` 220 m, `Expansion_SiteResourceRadius` 600 m, `ResourceReach_Distance` 450 m |

## 6. How to confirm in a live match
Log per AI think for one human team: `LastNeededDepositPoints`, `W`, refinery count, harvester capacity vs `LastNeededHarvesterCapacity`, `LastMissingResources`, the `ConstructionData` and `nearPoint` chosen in the Path B branch, and for each placed refinery its distance to the target field and its yaw vs the yaw that would have turned the lane toward the field. Many Path B refinery requests aimed at the origin-nearest field before an Ultra Heavy Factory exists would confirm R1/R2 as the dominant cause. For expansion, log `TryPickSite` candidates with their scores and the abandon reason.
