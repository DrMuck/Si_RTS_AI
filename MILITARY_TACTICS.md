# Military — mission model and the four tactics

Specified by DrMuck (2026-08-07), defined against the code that exists. Companion to
`MILITARY_DESIGN.md` (the plan) and `MILITARY_IMPLEMENTATION.md` (what is built).
**Design only; none of this is implemented.**

The doctrine in one line: *hold cheaply while out-scaling, commit once, commit
everything.* The architecture in one line: *the military plans a portfolio of missions
and refreshes it, exactly as the economy plans a blueprint.*

---

## 1. The four tactics are not at the same level

This determines where each one lives.

| Tactic | Level | Home |
|---|---|---|
| **Hold and push** | Global posture — biases mission scoring | `StrategyPlanner` (does not exist) |
| **Economic push** | Global posture — mutually exclusive with the above | same planner, other arm |
| **Guerrilla raid** | A *mission kind* — runs under either posture | `MissionPlanner` |
| **Formations** | Execution layer — below missions entirely | `BattalionManager` order issuing |

Only **two** are strategy choices. Raids are one kind of mission competing for cash
like any other; formations are how any assigned force moves. Putting all four in one
selector would make raids compete for the posture slot instead of for money.

---

## 2. Missions and projects, not per-tick decisions

**This is the Blueprint pattern applied to combat.** Rule 6.5 — *"Phase 2 is a
blueprint, not a series of choices"* — was learned the expensive way: greedy per-tick
branching worked fine and had no global picture, so around 18 minutes every frontier
point claimed its own nearest patch forever. The fix was **scan → plan → build to the
plan → refresh**.

The military layer has the identical failure mode queued up. `DefencePlanner` today
ranks threatened assets every tick and `BattalionManager` raises a response per
threatened asset. That is the greedy shape. It will work, and then at scale it will
produce the combat equivalent of noding madness — a response battalion per threat,
forever, with nothing able to say that one raid is worth more than three garrison
top-ups.

So: the military plans a **portfolio**, refreshed on an interval. Battalions are the
resource it spends, not the thing that holds intent.

### Two kinds, one test

| | Completion | Priced by | Examples |
|---|---|---|---|
| **Project** | none — stands while justified | per second of coverage | home garrison, frontier screen, hold the economy |
| **Mission** | yes — reaches Done | per expected outcome | raid that HQ, break that force, deny that patch |

The test is simply **can it finish?** If not, it is a project. They differ in scoring
and in abort semantics, and that is the only reason to distinguish them.

### The object

```
Mission {
  id, kind          // garrison | screen | hold | push | raid | deny
  objective         // position or target entity
  requiredValue     // force needed, in CASH — existing convention
  assigned          // battalion(s)
  state             // Proposed -> Funded -> Forming -> Executing -> Done | Abandoned
  score             // value / cost, comparable ACROSS kinds
  precondition      // must hold to enter Funded
  abortWhen         // declarative condition, never a timer
  committedAt       // dwell, for hysteresis
}
```

### Why this shape

**A mission is the cash claimant — not "the military."** `MoneyBroker` already has
`IActionSource`, and `MILITARY_DESIGN.md` §2 concluded that cash, not population, is
the genuinely shared resource (shrimps have zero cap weight under the balance mod). If
the *mission* carries the price, a raid competes with a Bio Cache on value directly,
and rule 6.19's idle cash gets spent against a justification rather than a budget
line. A flat "military budget %" would be a tuned constant standing in for a decision
the planner can actually make.

**Battalions stop owning intent.** Today `Battalion.Role` is `"garrison" | "response"`
— intent is baked into the force. In a mission model the battalion is a force pool and
the mission holds the intent, so units can be re-tasked without being re-typed. This
is a real refactor of `BattalionManager`, not a wrapper around it.

**`missionType` stops being `"unknown"`.** `CombatLog` writes that field deliberately
empty, waiting for this. With missions it becomes the join key, and exchange ratios
can be read **per mission kind** — which is the only way to find out whether raids pay
for themselves, whether holding really does trade better than fighting, and whether a
composition is good only on defence.

**Abort conditions are already the house rule.** *"Released by the condition that
raised it, not on a timer"* is implemented in `BattalionManager` today. The mission
model generalises that rather than inventing it, and the existing 30s assignment dwell
becomes the mission commitment dwell.

### Refresh, not re-decide

Blueprint replans at intervals against what already stands. Missions do the same: a
refresh re-scores the portfolio, but an `Executing` mission is not cancelled because
something scored higher this tick — only `abortWhen` or completion ends it. Without
that, the portfolio thrashes and "never trickle in" is silently violated.

---

## 3. The trigger both postures share

Income is not army. Three separate ceilings can stop the army growing:

| Ceiling | Symptom | Signal |
|---|---|---|
| **Unit cap** | army cannot grow at all | `UnitCaps` — per-team limit **still unknown**, see §8 |
| **Production throughput** | army grows at a fixed rate regardless of cash | rising unspent cash while producers saturated |
| **Economy** | income falling as patches drain | `EcoRateSampler` yield/worker, patch reserves |

Rather than detect which one binds, measure what they all cause:

> **Push when army growth rate approaches zero.**

One signal — `d(armyValue)/dt` in cash over a rolling window — covering all three. If
we cannot get stronger and the enemy still can, every further second of holding is a
strict loss. Unit cap is the sharpest case: at cap with a full army, waiting is decay.

**Corollary, and it is the important one:** the hold phase's job is *not* to bank
resources. It is to build the production and supply capacity so the army curve is
steep when needed. Cash cannot be converted to army on demand — the steamroll army
must be produced *during* the hold, not after the decision to push.

That gives a diagnostic with no tuned constant, against an already-measured defect:
**rules 6.15 / 6.19 — unspent cash is the biggest single waste in the economy** (138k
idle against 1,000k earned; ~137k of ~630k spendable actually converted). Military is
the missing claimant. Rising float during a hold means we are under-built on
production and wasting the very lead the hold is buying.

### What does *not* trigger the push

Enemy inactivity. A quiet enemy is neutral-to-good: static defence holds ground more
cheaply per unit area than mobile army does, so quiet minutes convert a larger share
of income into army. An unattacked hold with equal patch share compounds *ahead*.
Kills-at-our-defences is the wrong metric and should not be built.

### What *does* force an early push

The Fabian expiry. Holding works only while our build-up rate exceeds theirs, which is
a question of ground, not aggression:

> **Abort the hold when enemy controlled patch share pulls ahead, or when our reserves
> project to run dry before theirs** — even if the army is not ready.

Needs enemy expansion discovery, which we do not have (§8).

---

## 4. Hold and push

**Posture.** Biases the portfolio; two states with hysteresis.

### Hold
- Garrison **project** funded to its floor first — Queen safety is a constraint and a
  veto, never a ranked priority.
- A `hold` project accumulates the main force at a rally point and **does not seek
  engagement**. It is a reserve, not a wall; a static perimeter gets probed until it
  finds an undefended seam.
- Screen projects run at reduced weight — a hold is not a single base, or the economy
  dies to patch depletion.

### Push trigger
All of:
1. `d(armyValue)/dt` below threshold, **or** at unit cap (§3)
2. `ourArmyValue / theirEstimatedArmyValue` ≥ margin
3. Garrison floor still satisfiable without the pushing force

**Or**, overriding all three: the Fabian expiry.

### Push execution
- **One mass, one point.** Defeat-in-detail target ordering — hit their split fragments
  in sequence, not their concentration. A dense arrow, not a broad front.
- **Do not strip production to buy the last units.** A push that stalls against depth
  needs reinforcement flow, and that is exactly when their production is intact and
  ours has been cashed out.
- Return to `Hold` only when army value falls below a fraction of its committed peak —
  not on first contact, not on a timer.

### Measurable
Exchange ratio at our defences vs in the open, per composition, from `combat.jsonl`
joined on `missionType`. The whole doctrine rests on this number and it is currently
unmeasured.

---

## 5. Economic push

**Posture.** The army advances *with* the expansion frontier instead of sitting home.
Ink-spot made concrete: a site is not complete until covered, and the next is not
taken until the last holds. Expressed as one `screen` project per funded branch.

Strategically the most interesting of the four, because it targets a known open
defect: **rules 6.1 / 6.2 — Phase 2/3 expansion is too weak on spread maps, and it is
multi-directional breadth that is missing.** Part of why breadth fails may be that
uncovered breadth simply dies. If so this is an eco fix wearing a military hat, and it
shows up in existing eco metrics rather than needing new ones — which makes it cheap
to falsify.

### Mechanics
- `Blueprint`'s planned branches already give direction and ordering, so army position
  becomes a function of the frontier rather than an independent decision.
- Each planned site above a value threshold proposes a screen project sized from
  `ThreatMap` along that branch.
- `ControlMap.ControlGain(pos, r)` exists and is already consumed by expansion, so
  military scoring off the same term makes both layers agree on what ground is worth
  by construction.
- **Denying their expansion is the Silica form of a blockade** — there are no supply
  lines to cut, so contesting patches is the equivalent. It is the aggressive half of
  a defensive strategy, and it is what *makes* time be on our side rather than
  assuming it is.

### Choose this over hold-and-push when
`ControlMap.HeldFraction` is low and contested, or the map is spread per `MapProfile`.
On dense maps with a defensible footprint, hold-and-push is cheaper.

---

## 6. Guerrilla raid

**A mission kind, not a posture.** Small, fast, high value-per-cost force against
weakly-defended critical infrastructure — HQ, economy, production. Flyer swarms,
firebugs, anything with the speed to reach and leave.

Proposed under either posture. Raid missions are **capped as a fraction of total army
value** so the portfolio can never starve the main force or the garrison floor — a
budget ceiling on a mission kind, which is different from scoring and needs to be,
because a high-scoring raid must still not eat the reserve.

### Target ranking
`targetValue × (1 / localDefence)` over **discovered** enemy structures. The
weakly-defended half is what makes it a raid rather than a donation.

### The blocker, stated plainly
**We cannot see the targets.** `MILITARY_DESIGN.md` §5 already flags it: scouting is
tuned for *biotics* discovery, so acting on enemy structures gives scouting a second
customer with different priorities, which likely changes the scout star's targeting.
Rules 7.6–7.8 tuned that star hard for economic exploration and a second customer will
pull against it.

This makes raids the **highest-risk of the four** — not because the combat logic is
hard, but because targeting depends on information the AI does not collect. Do not
build it before that scouting customer exists.

### Measurable
Enemy income denied is not observable. Proxy: structure kills × their cost from
`combat.jsonl`, against raid force value lost. A raid that does not clear 1:1 in cash
is a donation.

---

## 7. Formations

**Execution layer, below the mission.** Weighted toward a flank to break a weak point
rather than spreading evenly across a front.

`MILITARY_DESIGN.md` §4 defers this and that judgement holds: *its payoff cannot be
seen until engagements are measured.* Formations before `combat.jsonl` yields exchange
ratios is tuning against a number we cannot read.

Two things to settle first:

1. **Does the game honour it?** A formation is per-unit offset targets on a move
   order; whether Silica's movement keeps that shape under contact is unverified. The
   stacking Harmony prefix on `OnMoveOrder` — already written so vanilla cannot
   re-task a committed unit — is the hook, but holding a *shape* is a stronger claim
   than holding an assignment.
2. **Concentration before geometry.** The cheap version is *which sub-group goes
   where*. Most of the value of "weight the strong flank" is available by splitting an
   assigned force into two unequal sub-groups with different objectives — no new
   movement machinery, and measurable with the same exchange ratio.

---

## 8. Dependencies, honestly

| Needed by | Depends on | State |
|---|---|---|
| Push trigger (§3) | `d(armyValue)/dt` in cash | `UnitValues` exists; the derivative does not |
| Push trigger — cap arm | per-team unit cap limit | **unknown.** `UnitCaps` probes `Team`, has not found it |
| Fabian expiry (§3) | enemy patch/expansion discovery | **missing** — scouting is biotics-tuned |
| Enemy army estimate (§4) | threat capability classes, staleness decay | **missing** — `ThreatMap` has neither |
| Raid targeting (§6) | same enemy-structure discovery | **missing**, same blocker |
| Mission scoring across kinds | exchange ratios per composition | `combat.jsonl` writes rows; **never read** |

Three of the four are gated behind one missing capability: **scouting that looks for
enemy structures rather than resource patches.** Highest-value unblock, and it is not
a military feature.

Per open item 7: no hardcoded game parameters. Costs, speeds, cap weights and ranges
come from live `ConstructionData`. Any constant introduced here is labelled a
placeholder in source, as `THREAT_PER_DEFENDER` and `STRENGTH_MARGIN` already are.

---

## 9. Build order

Deliberately not "all of it", and it does not start with the fun part.

0. **Finish the defence bring-up already queued.** `DefenceExecute` is still false and
   no round of `[DEFENCE]` output has been reviewed. Nothing below is worth starting
   while the order side is unproven.
1. **Read `combat.jsonl`.** Rows are being written and have never been analysed.
   Replace the three placeholders with measured numbers. Data work, no code.
2. **Enemy-structure scouting.** Unblocks three of the four — a second scout customer,
   deliberately traded against rules 7.6–7.8's economic tuning.
3. **`MissionPlanner`, shadow only.** The portfolio, the two kinds, mission-as-cash-
   claimant. Log the proposed portfolio and its scores every refresh, order nothing.
   Cheap, and it can run during an eco soak.
4. **Refactor `BattalionManager`** so intent moves from `Role` to the mission and
   battalions become a force pool. Do this once the portfolio exists to assign from,
   not before.
5. **Hold-and-push.** Before economic-push because it needs less: no frontier
   coupling, no per-branch screens.
6. **Economic push.** Judge it on the *eco* metrics — if 6.1/6.2 improve, §5's
   hypothesis was right.
7. **Raids.** After 2, budget-capped against the main force.
8. **Formations** — sub-group concentration first, geometry only if 1 says shape pays.

---

## 10. What this does not answer

Whether alien compositions trade well at all, which units are fast enough to raid
with, and whether a Crab swarm is a real army or a speed bump. Those come from rounds,
not from this document — the same rule the eco stack learned the hard way.
