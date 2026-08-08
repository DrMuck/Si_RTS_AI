# The military model — DrMuck's spec, mapped onto the code

**DrMuck's design, 2026-08-08 (`Documents/new 9.txt`), consolidated against what
exists. Design first, code after — this file is for arguing with.**

Supersedes the scattered planning in `MILITARY_DESIGN.md`,
`MILITARY_TACTICS.md` and `MILITARY_PRODUCTION_DESIGN.md` as the statement of
INTENT. Those remain accurate about mechanism and history.

---

## 0. The thing this document exists to fix

The layer has missions, battalions, producers and a composition prior, and it
still reads as a bot that does not know what it is trying to do. The reason is
now obvious with the spec in hand: **nothing in the code encodes an objective.**
`MissionPlanner` has three kinds — Garrison, Defend, Push — and "Push" is not a
goal, it is a direction. The spec has eight objectives in two ordered lists, and
the ordering is most of the content.

So the model is: **objectives are first-class, ranked, and each one carries an
expectation that says whether it is working.**

---

## 1. Offensive objectives, in DrMuck's order

> To win, all enemy HQs need to be destroyed. Main drivers to weaken enemy:

| # | Objective | Why it is first | What we can see today |
|---|---|---|---|
| 1 | **Deny enemy expansion** — especially fresh, poorly defended. Map control. | An expansion killed young costs them the investment and the ground | Enemy structures are remembered with `LastSeenAt`. **"Freshly built" is not tracked.** |
| 2 | **Weaken enemy eco** — harvesters, Refineries | Their income is their army | Refineries are in `ThreatMap` known set. Harvesters are units — visible, not tracked as targets |
| 3 | **Weaken military production** | Slows replacement | Factory names are classified in `rtsai_units.json` |
| 4 | **Weaken enemy military** | Last, because it is the most expensive way to hurt them | This is the ONLY thing the current push does |

**The current code inverts this list.** `MissionPlanner.TryPickTarget` ranks
discovered structures by `cost / (1 + localThreat)`, which picks the most
expensive weakly-defended building — usually an HQ or Refinery. Nothing prefers a
*fresh* expansion, and nothing distinguishes eco from production from military.
The bot is doing #2 by accident and #1 never.

**What #1 needs that is genuinely missing:** a `FirstSeenAt` on remembered enemy
structures, and a notion of "expansion" (a structure far from their main). Both
are small. `ThreatMap` already stamps every sighting; it just overwrites.

## 2. Defensive objectives, in DrMuck's order

| Objective | Note from the spec | State |
|---|---|---|
| **Queen** | loss condition. Predict guerilla and AIR. Mid-game the war is away from the nest — detect **slip-throughs**. Keep units AND defence towers | Garrison exists. No air-specific logic. **No slip-through detection.** No towers. |
| **Valuable eco** | tapped or *about to be* tapped — never depleted | `DefencePlanner` does exactly this (recent income × threat). The "about to be tapped" half is missing |
| **Valuable military production** | *especially areas that are a real imminent threat to the enemy* | Not defended at all. This is the FOB, and it is the one the spec says matters |
| **FPS player guerilla** | a single good player is a different threat class | Nothing. `ThreatMap` cannot tell a piloted unit from an AI one |

The FOB point deserves emphasis: the spec says a forward production site should
be defended *because it threatens the enemy*, which is the mirror of "pressure is
defence". Our `MilitaryBlueprint` will now place one; nothing protects it.

## 3. Threat analysis — the perception gap

The spec asks for four things and we have one and a half.

- **Enemy base expansion / map control** — we remember structures; we do not
  measure their footprint or its growth.
- **Army build-up and PREDICTED movement** — we have current value by class.
  No history, so no vector, so no prediction.
- **A point system on strength AND capability** (light / air / heavy / soldier),
  clustered, possibly a heatmap — we have exactly this by class, cash-weighted,
  in `ThreatMap.EnemyMix`. **This part is done.**
- **Single dangerous FPS players, and where they will move** — nothing, and the
  server data says this matters: 81% of kills in real games are piloted units.

The through-line: **we have snapshots and no history.** Every prediction item in
the spec needs the same missing thing — a short trail per enemy group, giving
direction and speed. One structure unblocks four requirements.

## 4. Military production

Spec: plan placement including FOBs; force the blueprint toward the enemy;
**"a sweetspot of getting enough units there in time traded off against risk
exposure"**; align with the money broker; align with income prediction so
buildings run at high utilisation.

Status after v0.49–v0.50: placement has purposes (Home / Cover / Forward), the
FOB condition is DrMuck's own ("time to build, or units already there"), count
is gated on measured income rate projected to when the building lands, and
saturation is per producer type.

**Still wrong or missing:**
- The money broker is not consulted. `MissionPlanner.SpendableCash` subtracts
  `MoneyBroker.GetReservedCash` and otherwise ignores it — military is not a
  registered `IActionSource`, so eco and tech cannot see the claim coming.
- "Sweetspot" is currently a boolean (quiet OR covered), not a trade-off. The
  real form is *expected units delivered before the site is contested*.
- Nothing rebuilds a producer that dies.

## 5. Node cutting — the FOB's actual weakness

Spec: **anchor nest so forward bases survive node cut-off**, plus branch
bridging. This is not decoration. A forward producer is at the end of a node
chain, and the 2026-08-07 round lost 57 nodes; an FOB on a cut chain is a
building that stops working and cannot be rebuilt.

`NodeManager` has repair and loop-closing live. Anchor nest is listed as not
built. **This gates the FOB being worth anything**, and it is the dependency I
would most easily have missed.

## 6. Unit composition

Spec: links to threat analysis; learn from matches; DrMuck can seed it.

Done, and measured rather than seeded: `rtsai_units.json` carries effectiveness
under AI control and cash-priced counters per enemy class, from 2,619 replays.
`UnitPrior.Score` weights by the live `EnemyMix`.

**The remaining defect is structural**, and the research notes name the fix: a
score picks a *winner*, so production is a monoculture — 255 Shockers to 41
Behemoths. A composition needs a QUEUE with proportions, the way
UAlbertaBot/CommandCenter does it.

## 7. Army attacks

> Dont send in units one by one: build up critical armies first... Only
> exception if you want to support the frontline army, or if you blast e.g. fast
> building units from multiple lesser cysts (e.g. crabs as meatshield)

Critical mass is implemented as cash-valued readiness, and v0.51 added group
cohesion so a committed force also *arrives* together rather than merely leaving
together.

**Both exceptions are missing**, and the second is interesting: a Crab is the
worst unit in the archive by kill/death (0.06) and the spec wants it anyway, as a
*meatshield*. That is a role the effectiveness table cannot express — it measures
what a unit kills, not what it absorbs. Worth saying out loud, because the prior
will actively fight this rule unless meatshield is modelled as its own purpose.

## 8. Strategy planner

Spec: strategies and sub-missions; change when needed; **no strategy hopping.**

We have posture (Hold/Push) with a 60s dwell — the anti-hopping half, without
the strategies. The spec's examples ("push enemy early expansion", "defend here",
"make a guerilla attack") are exactly §1 and §2's objectives instantiated at a
place, which is what a mission should be.

## 9. Formations

Spec: "a unit blob is seldom effective." Agreed and deferred, for the reason
`MILITARY_TACTICS` §7 gave and v0.51 partially anticipates: concentration before
geometry. Cohesion is in; shape is not.

---

## The model, stated once

**An objective is a ranked intent with a place, a price and an expectation.**

    Objective {
      kind          DenyExpansion | RaidEco | RaidProduction | BreakArmy
                  | DefendQueen | DefendEco | DefendProduction | AntiGuerilla
      rank          from the spec's two ordered lists — NOT a learned score
      where         position, from perception
      price         cash of force needed, from measured enemy value nearby
      expectation   what must become true, and by when
      dwell         how long before it may be reconsidered
    }

Three deliberate choices in that shape:

**Rank comes from the spec, not from a score.** DrMuck ordered these from
experience, and a learned weight would need outcome data we do not have.
Scoring happens WITHIN a rank — which fresh expansion, which Refinery — never
across ranks.

**Expectation is what we have never had.** Without it a failed objective is
discovered by watching a replay. With it, the bot reports its own failures:
*deny* expects their structure count at that site to stop rising; *defend eco*
expects the site to keep earning; *raid eco* expects their harvester count near
the target to fall. All three are observable today.

**Price stays in cash**, because everything else already is.

## Build order

Each step is a round of evidence, one change at a time — the discipline I broke
over the last two days and should not break again.

0. **`FirstSeenAt` on remembered enemy structures.** Unblocks objective #1, the
   highest-ranked thing in the whole spec, and it is a one-line change to a
   dictionary we already maintain.
1. **Objective kinds replacing "Push"**, ranked per the spec, with targets chosen
   within rank. This is where the bot stops attacking whatever is expensive.
2. **Expectations + discrepancy logging.** No behaviour change; it starts
   labelling success and failure, which everything later needs.
3. **Enemy group trails** — position history per cluster. One structure, four
   spec requirements: expansion growth, army prediction, slip-through detection,
   FPS tracking.
4. **Production queue with proportions**, killing the monoculture.
5. **Defend the FOB**, and **anchor nest** so it survives a node cut.
6. Meatshield as an explicit role; formations last.

---

## 10. Where this spec CONTRADICTS documents already in the repo

Checked rather than assumed. Four conflicts, and in three of them the older
document is the one that is wrong.

**`MILITARY_DESIGN` §2 — "there is no population tug-of-war to arbitrate".**
Wrong, and DrMuck's own eco remark is the proof: *"new shrimps aren't produced at
the outer layers because of shrimp unit cap."* The doc concluded workers are free
from a runtime read that returned `UnitCapValue = 0` for everything — and we
later watched `[UNITCAP] resolved from game:` print an EMPTY list, which is the
same read failing rather than succeeding. Si_UnitBalance's dump says
`Shrimp = Secondary/1`. So the cap binds, workers and army do compete, and the
eco/military population question that section retired is open again. This also
makes the per-cap scoring already in `UnitPrior` more important, not less.

**`MILITARY_TACTICS` §2 — mission score "comparable ACROSS kinds".** The spec
gives two ORDERED lists instead, and ordering is the right encoding while we have
no exchange data per mission kind. `MissionPlanner` already implements priority
rather than cross-kind scoring; the tactics doc is the outlier and the spec
settles it.

**`USER_RULES` 11.6 — raids ranked by `value × 1/localDefence`.** This is
implemented, and it actively fights the spec's #1 objective. A freshly built
expansion is CHEAP, so a value-weighted ranking sorts it near the bottom —
exactly the target the spec says matters most. The rule is not wrong for raids;
it is wrong as the only offensive ranking.

**`USER_RULES` 11.2 — "push when army growth rate approaches zero".** Not
contradicted by the spec, but not supported by the rounds either: growth never
flattens while production runs, so across four rounds it fired three times and
once in the last. The spec never mentions growth rate — it names targets. Keep
11.2 as one trigger among several; it cannot be the only door to offence.

**Agreements worth noting**, because they are load-bearing. `LEARNING.md` already
argues the whole learning position — engagement as the unit of learning, a
hand-authored prior updated toward measurement, uncertainty carried per cell —
and `rtsai_units.json` is that prior, now measured rather than hand-authored.
Nothing in the spec disturbs it.

## 11. Working mode

Synthesised from the RTS-AI research and from how this project has actually
failed. Two templates, both cheap, both aimed at the same defect: we keep
learning a round too late.

**Before code, every objective fills in five lines.** The shape comes from
Goal-Driven Autonomy — the expectation is the part we have never had.

    Objective:     deny a fresh enemy expansion
    Perception:    remembered enemy structures with FirstSeenAt   [MISSING today]
    Decision:      youngest structure beyond their main, weakest local defence
    Expectation:   structure count at that site stops rising within 120s
    Log line:      [OBJ] deny (x,z) age 40s, expect count<=2 by t+120
    Failure mode:  we walk at a defended expansion and lose the force

If Perception says MISSING, that is the work — not the decision rule. Three of
the spec's four threat-analysis items need one structure (a position trail per
enemy group), and writing them in this form is what made that obvious.

**Per change, one loop, one round each.**

1. Ship it logging-only or behind a default-off switch.
2. Read the log: does the DECISION look sane? (This is TorchCraftAI's shadow
   habit and this project's own rule; I skipped it repeatedly this week.)
3. Turn it on for one round.
4. Read the EXPECTATION violations — did it do what it claimed?
5. One change per round. Five changes in a build is how we got three days of
   unattributable behaviour.

**Ownership, from CommandCenter's boring hierarchy.** One module owns each
decision and nothing else touches it:

| Decision | Owner |
|---|---|
| what is worth doing, ranked | `MissionPlanner` |
| what ground is worth defending | `DefencePlanner` |
| where producers go and what for | `MilitaryBlueprint` |
| how many producers, and paying for them | `MilitaryProduction` |
| which units, formed how, ordered where | `BattalionManager` |
| what a unit is worth | `UnitPrior` |

**And purpose travels with the request** (TorchCraftAI's UPC). A producer request
carries its purpose to placement; a battalion carries its objective's kind, so
the executor knows whether it is screening or committing.

---

## Appendix — the eco question, which is separate and testable tonight

> On big maps a lot of forward expansion occurs that leads to front-investing
> money without a near-term return... new shrimps aren't produced at the outer
> layers because of the shrimp unit cap.

This is a real defect and it is measurable, unlike most of the above. The
hypothesis worth testing is DrMuck's second one: **tap more patches
simultaneously rather than pushing the frontier further**, because a shrimp at a
near patch earns immediately and a Bio Cache at range earns when the walk
finishes.

A clean A/B on NarakaCity, identical spawn, arms differing only in where shrimp
production is allowed to sit:

- `outerFirst`  — current behaviour
- `innerCap`    — cap shrimps per inner Bio Cache lower, freeing cap for outer
- `breadth`     — prefer a NEW tapped patch over deepening an existing one

Metric is already in place: `cumulIncome` at 25 and 40 minutes from
`benchmarks.jsonl`. The rig cycles arms per round and tags rows with `configId`,
so this needs configuration rather than code — which is why it can run tonight
while the military work stays on the bench.
