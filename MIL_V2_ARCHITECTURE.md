# Military v2 — the framework

**Branch `military-v2`, started 2026-08-12. This is the working spec for the
rebuild. It supersedes `MILITARY_DESIGN`, `MILITARY_TACTICS`,
`MILITARY_PRODUCTION_DESIGN` and `MILITARY_IMPLEMENTATION` as the statement of
what we are building. `MILITARY_MODEL.md` (the objectives) and
`MILITARY_OPTIMIZATION.md` (why a forward model) are the two it builds ON, not
replaces.**

---

## 0. Why a rebuild rather than another fix

`MILITARY_OPTIMIZATION` §6 listed fourteen constants added over two days, each
one in response to a specific observed failure, and said the count *is* the
argument. That diagnosis was right and the layer has not moved since. The
military code today — `MissionPlanner`, `BattalionManager`, `MilitaryProduction`,
`MilitaryBlueprint`, `DefencePlanner` — is roughly 200 KB that has never been
turned on for a full round, because every time it runs it produces a new failure
that needs a new constant.

The eco layer does not have this problem, and the difference is not care or
effort. **Eco has `EcoSimulator`.** It can ask "what does this sequence earn"
and search. Military can only ask "what does this constant say".

So the rebuild is not a rewrite of the same decisions in tidier code. It is the
addition of the one thing that was missing, and then decisions expressed as
searches over it.

## 1. The shape

Four layers, and the top two are the new ones.

```
  L0  MEASUREMENT     offline, Python, mil_sim/
      replay archive -> engagements -> fitted parameters -> mil_model.json

  L1  FORWARD MODEL   the combat kernel, twice
      mil_sim/kernel.py   (offline: search, validation, experiments)
      Si_RTS_AI/Mil/CombatKernel.cs  (in-game: the same maths, same JSON)

  L2  DECISION        objectives, ranked; each priced by asking L1
      what is worth doing, where, with how much, and what must become true

  L3  EXECUTION       battalions, orders, formations
```

L0 and L1 are the investment. L2 and L3 get *smaller* than today's code, because
most of what they currently do by hand becomes a query against L1.

## 2. The rule that makes L0 survive a rebalance

**Fit dimensionless behaviour. Read absolute physics.**

We mod this game. Si_UnitBalance rewrites cost, health, damage, projectile speed
and lifetime, and pushes changes between sessions — so a stat table baked into
this repo is stale on the next push, and the replay archive spans several
different balances already. A model fitted on absolute damage numbers would be
fitted to whatever the balance was in June.

So every parameter L0 learns must be a ratio, and every absolute must be read
live:

| Read from the live dump (absolute) | Fitted from replays (dimensionless) |
|---|---|
| hp, cost, cap, build time | engagement distance / weapon reach |
| damage, cooldown, reach | share of a present force that trades |
| move speed, fow view | cash destroyed / cash lost, per matchup |
| | losses / (force ratio) — the exchange exponent |

Rebalance the game and the model recalibrates itself, because the physics half
is re-read and the behaviour half was never about the numbers that changed.
Whether that holds is *testable*, not assumed: `measure_range.py --by-epoch`
splits the archive by month, and a ratio that drifts across epochs is a ratio
that was secretly absolute.

## 3. What reach actually is — the first thing v1 got wrong

`atk_range` is **not** a weapon's reach. It is
`CreatureAttack.AttackProjectileAimDistMax`: the distance at which the AI starts
aiming and firing. How far a shot travels is set by the projectile.

```
instant-hit   base_speed IS the raycast distance; lifetime is the visual beam
ballistic     integrate with drag and gravity until lifetime, speed floor, or
              the round arcs back to launch height
melee         contact

effective_reach = min(aim_cap, ballistic_reach)
```

`mil_sim/unit_stats.py` derives it, and the archive confirms it — derived reach
against measured 99th-percentile kill distance, 40 replays, 15,723 kills:

| unit | derived reach | measured p99 | max | binding limit |
|---|---|---|---|---|
| Shocker | 200 | 200 | 218 | **projectile** (aim cap says 400) |
| Behemoth | 400 | 398 | 457 | projectile |
| Siege Tank | 958 | 932 | 1096 | projectile |
| Colossus | 752 | 571 | 678 | projectile |
| Crimson Tank | 1000 | 917 | 1272 | aim cap |
| Crab (melee) | — | 13 | 13 | contact |
| Hunter (melee) | — | 32 | 32 | contact |

The melee rows are the useful ones: a pipeline that reports a Crab killing at
4 m median and 13 m maximum is measuring what it thinks it is measuring.

**The behavioural finding, stated against reach rather than the aim cap:** the
median kill lands at **0.15–0.45 of the killer's real reach**. The AI closes
well inside its weapon envelope before anything dies. That number — `engage_frac`
— is the first fitted parameter, and it is the one that decides whether "range
advantage" is worth paying for in a composition. On this evidence, mostly it is
not, which is a real claim about unit value that the current cash-per-kill prior
cannot express.

**Infantry is a hole.** Soldier weapons (`hha_*`) are not in the balance dump, so
Juggernaut, Templar, Marksman and the rest have no derivable reach. Their reach
has to be measured (Juggernaut's p99 is 689 m, which makes it long-range AT) and
carried as an empirical entry with a lower confidence than a derived one.

## 4. The unit of analysis is a fight, not a kill

`tools/mine_replay_kills.py` counts who kills what and says in its own header
why that is not enough: a unit that softens a target and dies before the last hit
is credited nothing. That is unfixable at the level of a kill, because **a kill
has no denominator.** It does not know how many units were standing there.

`mil_sim/mine_engagements.py` clusters kills into fights (300 m, 30 s gap) and
takes a census from the tick stream of what stood within 400 m at the opening
tick and again at the closing tick. That gives every row a denominator:

    fight_id, map, t0, duration, x, z, median_range
    per side: n, cash, piloted_frac, struct_cash,
              end_n, end_cash, lost_units, lost_cash, killed_cash

Which finally makes the only question the planner ever asks answerable:

> if I commit 12k of these against 9k of those, do I win, what does it cost,
> and how long does it take

**The piloted split is not optional.** 81% of kills in real games are scored by
piloted units and the bot cannot pilot. A model fitted across both learns how
well *humans* fight and hands the number to something that cannot do it. Every
row carries `piloted_frac` per side; the AI-vs-AI subset is the one the planner
is entitled to believe, and the piloted subset is what it should expect to face.

**Force means things that shoot.** The first census counted every unit in the
radius, and 10% of all census cash turned out to be Shrimps and Harvesters —
landing asymmetrically on whichever side was fighting at home. `roles.py` decides
this evidence-first rather than from the dump, because infantry weapons are not
in the dump and the naive rule filed Juggernaut, Templar and Sniper as furniture.
Harvesters are excluded by role despite scoring 0.32 kills per unit built: they
kill by being driven over people, and their cash is economy.

**The archive is filtered to the public server and to skilled rounds.** The live
server folder is DrMuck's own box — soaks and bring-up games where nothing shoots
back. Rows carry per-side commander and FPS ELO so a fit can require that someone
who can play was in the round.

### What it says — 58,080 fights from 2,619 replays

| | parity | 1.25–2× | 2–4× | >4× | all |
|---|---|---|---|---|---|
| every fight | 51% | 52% | 54% | 55% | 53% |
| **decisive fights** | 55% | 70% | **83%** | 82% | 76% |
| decisive, AI vs AI | 56% | 74% | **87%** | 86% | 79% |

*P(the side with more combat cash wins), by cash ratio.*

**A fight only counts if one side broke** — 70% of its value lost while the other
keeps 70% of its own. That is 37% of the rows, and the filter is not tidying. On
a fight neither side disengaged from, "who lost less cash" is a coin flip with a
number attached, and those rows flatten every curve: they drove the exchange
exponent to 0.01 and would have been reported as *mass does not pay in Silica*.
It does. Finding this before fitting anything is what step 1 of the build order
is for.

Two results the kernel has to carry:

**Concentration pays sub-linearly.** Fitting `loss_ratio = force_ratio^-beta`
over decisive fights gives **beta = 0.52** (0.60 AI-vs-AI, r = −0.40). Lanchester's
square law says 2.0 and even the linear law says 1.0. Doubling the army does not
quarter your losses; it cuts them by about a third. That is the signature of
*partial engagement* — only the units in contact ever trade — and it is the same
quantity as `trade_frac`. It also means the ceiling on massing is real: past
about 2–4× the win rate stops climbing, so a fifth Behemoth added to a winning
force buys nothing and should have gone somewhere else on the map. That is an
argument for two fronts over one doomstack, and it is measured rather than
asserted.

**The baseline the kernel must beat is 76%** (79% AI-vs-AI) — the accuracy of
"more combat cash wins" on decisive fights. Composition has to be worth more than
that or it is not worth modelling.

## 5. The combat kernel (L1)

One function, called from both languages, reading one JSON.

```
predict(forceA, forceB, context) -> {
    p_win          probability A holds the ground
    loss_A         cash, mean and sigma
    loss_B         cash, mean and sigma
    duration       seconds, mean and sigma
}
```

`force` is a composition — `{unit: count}` — not a cash number, because the
whole point of a composition is that two forces of equal cash are not equal.
`context` carries what the map contributes: distance to each side's nearest
production, whether static defence is present, and the reinforcement rate.

Parameters, all fitted in L0, all dimensionless:

- `engage_frac[unit]` — kill distance as a fraction of reach (§3)
- `trade_frac[unit]` — share of a present force that actually trades in a fight
  of a given size; this is what turns "40 units were nearby" into "9 of them
  fought", and it is where a blob loses to a formation
- `counter[unit][class]` — cash destroyed per cash lost, AI-controlled
- `exchange_exponent` — how losses scale with the force ratio. Lanchester's
  square law says 2; real RTS fights with partial engagement come in lower, and
  fitting it rather than assuming it is the difference between "twice the army
  wins four times as hard" and the truth
- `sigma` per output — carried, never discarded

**Variance is the design, not an error bar.** This is a 3D physically simulated
game and any point estimate of a fight is a lie. The kernel returns
distributions and the planner's commit rule is `p_win >= theta`, so a matchup
the model is uncertain about produces caution automatically. A planner that
consumed means would be confidently wrong exactly where the game is most random.

**The kernel is only allowed to be believed where it has been validated.** L0
holds out a slice of the archive; `validate.py` reports calibration error per
regime (small/large fight, AI/piloted, alien/human). A decision may use the
kernel only where its measured error is smaller than the decision's margin. Where
it is not, we say so and fall back to a rank, which is what
`MILITARY_MODEL` §"rank comes from the spec" already argues for.

## 6. Objectives (L2)

Unchanged from `MILITARY_MODEL.md` — DrMuck's two ordered lists, and the
ordering is most of the content. What changes is that `price` and `expectation`
stop being guesses and become kernel queries.

    Objective {
      kind        DenyExpansion | RaidEco | RaidProduction | BreakArmy
                | DefendQueen | DefendEco | DefendProduction | AntiGuerilla
      rank        from the spec's ordering — never a learned score
      where       from perception
      price       kernel: smallest force with p_win >= theta against what is there
      expectation what must become true, and by when
      dwell       how long before it may be reconsidered
    }

Ranking stays ordinal and scoring happens *within* a rank, because we have no
exchange data across mission kinds and a miscalibrated cross-kind score produces
the one failure that cannot be recovered from — marching off while the economy
is eaten.

`price` is the change worth naming: today `strengthMargin = 1.5` is a placeholder
multiplier applied to a threat estimate. With the kernel it becomes *the smallest
force that wins with probability theta*, which is the same idea with the guess
removed.

## 7. Ownership

One module owns each decision and nothing else touches it.

| Decision | Owner |
|---|---|
| what a fight would cost | `Mil/CombatKernel` |
| what is worth doing, ranked | `Mil/Objectives` |
| what ground is worth holding | `Mil/Defence` |
| where producers go, and what for | `Mil/Siting` |
| how many producers, and paying for them | `Mil/Production` |
| which units, formed how, ordered where | `Mil/Battalions` |
| what a unit is worth *right now* | `Mil/UnitValue` (kernel-backed) |

Purpose travels with the request (TorchCraftAI's UPC): a producer request carries
why it exists all the way to placement, and a battalion carries its objective's
kind so the executor knows whether it is screening or committing. The v1 failure
this prevents is documented — the FOB aiming at one base while the army walked to
another, three times.

## 8. Build order, with the gate that was skipped last time

Each step ships logging-only or default-off, and **one change per round**.

0. **`mil_sim` engagement dataset.** *Done — 58,080 fights, §4.*
1. **Fit and validate the kernel offline.** No game code. The gate is now a
   number: beat **76%** (79% on AI-vs-AI) on held-out decisive fights, which is
   what "more combat cash wins" already achieves. If composition cannot beat
   that, the kernel is not ready and nothing downstream is either — a real
   possible outcome, and cheap to discover here.
   Before fitting, two known defects in the fight definition are worth one pass:
   the greedy clustering fragments rolling battles (median fight is 44 s and 7
   kills), and presence is a 400 m circle rather than the units that actually
   traded. Both push `trade_frac` around, and `trade_frac` is the parameter
   `beta = 0.52` is really measuring.
2. **Kernel in-game, shadow only.** It logs a prediction for every fight that
   happens; nothing consumes it. Read the log: were the predictions sane?
3. **`price` from the kernel**, replacing `strengthMargin`. One constant dies.
4. **Objectives replacing "Push"**, ranked, with expectations logged.
5. **Front line from `ControlMap − ThreatMap`**, and walk-time budget replacing
   `FORWARD_FRACTION` and `COVER_RADIUS_M`. Three constants die.
6. **Production as a queue with proportions**, killing the Shocker monoculture —
   a score picks a winner every tick and that is why 255 Shockers to 41
   Behemoths.
7. Meatshield as an explicit role; formations last, and only if `trade_frac`
   says shape pays.

Steps 1 and 2 are the discipline that was broken in August — five changes in a
build, then three days of unattributable behaviour.

## 9. What this framework cannot do, said now

- **Damage that did not kill is invisible.** A fight broken off with both sides
  at half health reads as a small exchange. Retreats are systematically
  under-weighted, so the kernel will overvalue units that survive by leaving.
- **Terrain is invisible.** Two fights at the same range, one across a ridge and
  one across open ground, are the same row.
- **Reinforcement is only partly visible** — start and end census, nothing in
  between.
- **The archive is mostly not what we want.** Most replays are soaks with one
  player; most real fights involve piloted units. The AI-vs-AI subset that the
  planner is entitled to believe is the smallest slice of the data, and its size
  is the honest limit on how much the kernel can be trusted.
- **We cannot fit what the bot has never done.** There is no data on formations,
  on flanking, or on withdrawing a losing force, because nothing in the archive
  did those things deliberately. Those stay judgement, and the kernel should
  refuse to price them rather than extrapolate.

## Appendix — the eco question, still open and still separate

`new 9.txt` also raised forward expansion on big maps front-investing cash with
no near-term return, because new shrimps are not produced at the outer layers
under the unit cap. That is captured in `MILITARY_MODEL.md`'s appendix as a
three-arm A/B on NarakaCity (`outerFirst` / `innerCap` / `breadth`) measured on
`cumulIncome` at 25 and 40 minutes. It needs configuration rather than code and
it is independent of everything above — it can run on the eco branch while the
military work happens here.
