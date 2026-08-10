# Eco — open items

Compiled 2026-08-10, after the opener timing work (v0.73.0 → v0.77.1).
Everything here is measured, not suspected. Evidence is quoted so none of it
has to be rediscovered.

Nothing in this file is fixed. For what *was* fixed, read the commits between
`9a878b8` and `38538df`.

---

## 1. The post-Cortex candidate drought — biggest item

For roughly four minutes after the Cortex lands, the eco proposes **nothing**,
while cash accumulates and the blueprint holds a 90-site plan.

```
12:08:05  cyst 3 placed
12:09:22  Cortex        -2000  → cash 560
12:10:04  Alpha I       -2000
12:10:33  Beta II       -2000
12:11:02  Gamma III     -2000
12:08–12:12  [PLAN/DIAG] nothing fired ... candidates: BC=0 Cyst=0 Node=0
             every tick, cash 3080 → 1360
12:11:57  first new PlaceNode / PlaceBc since 12:08
```

`[UTIL] eco cash-blocked ~20% of the round`, `cash 1.9–3.2min idle`.

This is also where DrMuck's own observation points: *"our classic opener back
then was a bit flawed but had a better eco at 10min, because it was building
the fourth biocache and/or lesser cyst briefly after the cortex."* The old
opener filled this window by accident. The current one empties the bank into
tech and then proposes nothing.

Not proven to be tech's fault — tech and the drought merely occupy the same
window. The question to answer first is why enumeration returns zero
candidates with a 90-site plan available.

---

## 2. Blueprint Cysts lose their slot before they are built

`Blueprint`'s docstring claims *"It is ONE decision, held between refreshes.
Nothing here re-decides a multi-step commitment every tick"*. True of the tree.
**Not true of the Cyst pass**, which is re-decided from scratch every revision.

```
11:43:54  rev=4  Cyst planned at (2236,1895)
11:44:26  rev=5  Cyst planned at (2236,1895)
11:44:50  Bio Cache built at (2225,1885)
          ... never appears in rev 6+ ...
          no Cyst ever built there
```

`replanIntervalS: 30`, but execution is far slower — the first Phase 2 Cyst
landed four revisions after it was planned. So a Cyst can be planned and
dropped repeatedly without ever being built, while its Bio Cache goes up and
stands unstaffed. Cysts went to 739m, 1024m and 1090m instead; the 682m site
kept its Bio Cache and got nothing.

**Fix shape:** once a Cyst is planned at a site whose Bio Cache is going up, it
holds its slot until built or explicitly invalidated.

---

## 3. Producer budget is pinned at 8 by a literal

`maxCystsPerPlan: 4` in `rtsai.json` has **no effect** — `cystStrategyAuto: true`
routes the budget through `ExpansionStrategy.Producers` and never reads it.
Three limiters then stack, and the last **overwrites** rather than caps:

```csharp
budget = CystStrategyAuto ? sweep : maxCystsPerPlan
if (BehindSchedule && ProducersNeeded > budget) budget = ProducersNeeded
if (perSites > 0) budget = ceil(sites / perSites)      // overwrite
```

With `producerPerSites: 3` and 44→94 sites the ratio asks for 15–32. Every
revision produced exactly 8, because `BuildSiteInfo()` ends with:

```csharp
if (list.Count >= 24) break;      // undocumented hard cap
```

`ceil(24/3) = 8`, permanently. Two consequences:

- the budget cannot respond to cash, map size or economy
- Bio Caches ranked **25+** in the plan are structurally ineligible for a Cyst
  (65 of 89 sites, in the round measured)

**DrMuck wants this derived from starting cash.** Deliberately not attempted
yet: at rev 1 the bank was 560, less than one Cyst, so a naive `cash / 1500`
divisor plans zero producers for a planner whose job is to run ahead of the
money. `v0.75.1` added `cysts=8/budget8(ratio(1 per 3 of 24))` to the rev line
so the real binding limiter is visible first.

---

## 4. Whispering Plains — colliding Cyst offsets between distinct sites

Two *genuinely different* sites lose a producer to the 90m duplicate guard,
because each Cyst is offset from its own Bio Cache and the offsets point at
each other:

```
Cyst step 2 target (407,-1297)  → built
Cyst step 4 target (458,-1318)  → 55m away → refused ×10 → gave up
```

Not the doubling problem (fixed in v0.76.3 via the `deliberatePair` flag) —
these are separate patches. WhisperingPlains is dense (`nearest=64m`,
`within1500m=43`), so it hits hard there and barely on NarakaCity.

**Fix shape:** choose the offset direction away from existing Cysts, and
retarget on refusal instead of retrying the same point ten times.

---

## 5. The terminal tail's `min()` regime flip

```csharp
plan.TerminalValue = Mathf.Min(rateAtHandoff * TERMINAL_TAIL_S, remainingAtHandoff);
```

Only one argument scales with map tonnage, so the term silently switches
between two regimes:

- **resource-capped** — flat across all plans, drops out of the comparison
- **rate-proportional** — a 240× multiplier on handoff rate, dominates everything

Both switches observed:

- 22k → 42k biotics (Si_MapBalance applying `UserData\Spawns`) released the cap
  and produced the north-west opener
- correcting build times in v0.74.0 dropped handoff rate 257 → 223, which pushed
  `rate × 240` *below* `remaining` and released the cap again — **at 22k**, where
  it had been flat all along

Every opener surprise this session arrived through this term. It is quiet now
and still one constant change away from flipping. Also note `TERMINAL_TAIL_S =
240` on top of `SCORE_HORIZON_S = 240` scores an opener over an effective 480s,
for a phase whose own docstring says it hands off at 3–4 minutes.

**Options:** cap per-site rather than per-plan; or replace the flat 240s
projection with something that decays.

---

## 6. TechPlanner spends through `OpenerPlanner.OutstandingCost`

Untouched all session. The **reserve** honours `openerOwes`
([TechPlanner.cs:143](Si_RTS_AI/Planning/TechPlanner.cs#L143)); the **spend
gate** ([:296](Si_RTS_AI/Planning/TechPlanner.cs#L296)) never looks at it.

```
22:27:04  Cortex fires. cash=2460  openerOwes=2000  →  cash 460
22:27:11+ [OPENER] step 12/13 Bc refused: reserve1500  ×4
```

Also: the payback gate was removed 2026-07-07 and the header still documents
`PAYBACK_FIRST_S = 60s` as if it existed. What remains is a headcount proxy,
`CountShrimps >= 15`, whose comment assumes 15 shrimps ≈ 75/s. Measured:
`perShrimp` falls 6.00 → 3.01 as the count goes 3 → 15, so it is ~45/s — 40%
below the assumption. `EcoRateSampler` can supply the real rate.

---

## 7. Income-aware shrimp reserve

`shrimpReserve = min(realCysts, 10) × SHRIMP_COST`
([EcoPlanner.cs:1781](Si_RTS_AI/Planning/EcoPlanner.cs#L1781)). Flat per Cyst,
blind to whether income covers production. Drain and income scale differently:

| | shrimps | perShrimp | income | cysts | drain | net |
|---|---|---|---|---|---|---|
| t≈101s | 3 | 6.00/s | 18/s | 1 | 10.7/s | +7.3/s |
| t≈266s | 15 | 3.01/s | 45/s | 4 | 42.7/s | **+2.3/s** |

Drain is **linear** in Cysts; income is **sub-linear** in shrimps. The
break-even is visible in the trace — cash pinned at exactly 1200 from 22:26:14
to 22:26:30 — while the constant reserve held back 640 regardless.

**Fix shape:** `reserve = max(0, drainPerSec − incomePerSec) × BRIDGE_S`. Both
quantities already exist in `EcoState`. Self-cancels once income covers
production.

**Blast radius:** `shrimpReserve` also funds `maxNodeFires` through the
`surplus` term at [:1803](Si_RTS_AI/Planning/EcoPlanner.cs#L1803), so this
loosens Phase 2 expansion width too. Needs measuring on both.

Open sub-question: is the sub-linear `perShrimp` crowding, longer hauls, or
shrimps counted before they are productive? That decides whether the lookahead
is one spawn cycle or one harvest cycle.

---

## 8. Bio Cache worker cap is the mid-game limiter

`workerCapPerBioCache: 10`. Shrimp production runs at **27–55%** of nominal
with `blocked: bcCap=12` consistently from ~t=400s. Extra Cysts cannot help
until more Bio Caches land — so producer count and Bio Cache count have to be
planned together, and currently are not.

```
[SHRIMP/PROD] 5.4/min against 20.0/min from 5 producers (27%)
              live=63 queued=2 | blocked: teamCap=0 bcCap=12 alreadyBusy=2
```

---

## 9. No ownership of node placement between opener and beam

The eco beam enumerates and fires `PlaceNode` on its 8s tick against the same
ground the opener's `TickChain` is working. Opener Node steps are `MarkDone`'d
without placement ([EcoPlanner.cs:2153](Si_RTS_AI/Planning/EcoPlanner.cs#L2153),
*"NODE STEPS ARE BUDGET, NOT PLACEMENT"*), so the beam has no idea the opener
owns a chain.

Produced three nodes within 18m on 2026-08-10 11:32. Much rarer since the
opener stopped chaining north-east, but unfixed. There is already a
*"WHOEVER OWNS PHASE 2 EXPANSION, OWNS IT ALONE"* rule at
[EcoPlanner.cs:2201](Si_RTS_AI/Planning/EcoPlanner.cs#L2201) — the opener needs
the same over nodes while its queue is active.

---

## 10. Placement slides are silent, and chains do not route

A request can land ~90m from where it was asked with nothing in the log:
asked (2442,1903), got (2395,1855). `TickChain` hops in a straight line and
accepts whatever comes back.

`Blueprint.SteppedAsideFromObstruction` already exists for exactly this —
steps sideways, keeps distance from the anchor intact — plus
`RecentlyAsked`/`NoteAsked` to remember bad ground. The opener's chain uses
none of it.

Worth logging any placement landing >40m from its request, regardless.

---

## 11. Blueprint node pacing — low priority

Blueprint nodes fire **2 per 8s tick = 15/min**; the opener's `TickChain` runs
at **1 Hz = 60/min**. Same work, four times slower, because Phase 2 nodes only
fire inside `ApplyPlanResult`.

Limiters: `PLAN_CADENCE_S 8s` × `maxNodeFronts 2`, with
`maxUnbuiltNodesPerFront × fronts = 6` in flight (`nodesInFlight6/6` hit 8
times in one round).

DrMuck: *"this is a minor thing, maybe not worth optimizing"* — agreed. Chains
are also gated by 20s serial build per hop, so the request cadence mostly hides
behind it. Only pays off where several independent fronts advance at once.

---

## 12. Opener Bio Caches fire on the 8s tick

Cysts and chain nodes run at 1 Hz; Bio Caches only fire in the 8s eco loop.
Measured cost: node finished 10:50:38.733, Bio Cache requested 10:50:50.851 —
**12.1s**, spanning a tick it should have caught. Up to ~8s per site, ~16s
observed.

---

## 13. Constants still hardcoded, and one wrong comment

Derived correctly now: costs, build times (`TotalConstructionTime`),
`SHRIMP_SPEED`, research duration, `min_tier`.

Still hardcoded, cannot track Si_UnitBalance:

| constant | source it should have |
|---|---|
| `CARRY_CAPACITY = 400` | `ResourceHolder.MaxAmount` |
| `MAX_PER_PATCH = 18` | observed only, no config source |
| `HARVEST_RATE = 9.5` / `DEPOSIT_RATE = 50` | empirically fitted, not game fields |

`BC_RADIUS_M = 37f`'s comment documents a value the game does **not** return —
it resolved to **9** every round (`BC=500/200m(+r9=209m)`). Used in every Bio
Cache reach test.

**Also unbuilt:** the round-start audit cross-checking `_base × mult` from
`Si_UnitBalance_Config.json` against the live read, and warning when a read
falls back to a default instead of succeeding. Currently a failed read is
completely silent — which is how the `BuildUpTime` error survived so long.

---

## 14. Test mode does not apply `UserData\Spawns`

Headless `TestMode` never calls `DistributeAllResources`, so Si_MapBalance's
resource config is ignored and the map runs vanilla uniform **22,000**/patch.
Live runs get the configured **42,000** (`biotics_amount: 42000` plus four
`patch_overrides` at 40,000 — reconciles exactly to the 4,486,000 in
`MAPPROF`).

Consequences:

- every soak and every `benchmarks.jsonl` entry measures an economy the server
  never runs
- `known-good-eco-20260807` was validated in test mode, so the opener has only
  ever been tuned against 22k
- the two regimes score differently, because of item 5

Until this is closed, benchmark comparisons across modes mean little. Note also
that `EnemyBroke = true` removes enemy pressure entirely, so rounds run with it
are not comparable to the 68,399 @600s reference either.

---

## 15. The doubling bonus has never been validated

The opener prefers a doubled-Cyst opening by ~1277 over its 3-Cyst variant, and
until v0.76.3 **the doubled Cyst had never once been built**. The bonus has
therefore never been checked against a measured outcome.

Reasons to expect it is optimistic: the second Cyst lands ~35s after its
sibling (35s build, waits on a finished Bio Cache), and it shares the same
patch's worker cap — which item 8 says is already the binding constraint.

Needs a couple of clean 10-minute runs before the number is trusted.

---

## 16. Blueprint attach-cost primitive (deferred architecture)

`Blueprint` is a Takahashi-Matsuyama Steiner tree and knows things the opener's
chain synthesis does not: fog filtering, shared-network costing, obstruction
and elevation routing, node merging. The opener hops in a straight line at
`NODE_REACH_M − 15` while `TickChain` uses `− 40`, so it under-counts hops by
~20% (planned 5, laid 8).

`Replan` cannot be called 728 times — it is stateful and scans all terminals.
The integration is to extract the *attach-cost primitive* — "cheapest way to
reach terminal T from the network as it stands, in hops and cash" — as a pure
function both callers use.

Deliberately deferred: it moves the routing model underneath a scoring model
only just made honest, and the two changes should not land together.
