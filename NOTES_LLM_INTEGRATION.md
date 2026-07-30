# Notes: LLM Integration (Future Work)

Parking these ideas here so we can pick them up once the rule-based foundation is solid.
**Not to be implemented yet** — we have significant rule-based work first (see §"Prerequisites").

## Why LLM at all

Eco / expansion / military balance is a **multi-objective resource allocation
optimization problem** with a huge state space:

- What structure to build next (BioCache, Cyst, Node, tech-up, defense, production)
- Where to place it (which biotics patch, which chokepoint, main base vs expansion)
- When to shift from eco → military (before we get rushed, but not too early)
- What unit composition (counter enemy comp, phase-appropriate, faction-specific)
- Multi-step planning (build order chains with prereqs and dependencies)

Score-based heuristics can solve much of this — but the coefficients need tuning,
and truly novel situations (unusual opponent, weird map) break rules. LLM handles
the "reason about the whole state" case naturally.

## What LLMs are good at here

| Decision | Cadence | Why LLM fits |
|--|--|--|
| **Weighting Engine** — eco%/expa%/mil% split | 30-60s | Judgment call over multi-dimensional state |
| **Sub-Strategy Selector** — steamroll/guerilla/turtle | 30-60s | Context-heavy, no clean scoring function |
| **Composition adaptation** — "enemy is air-heavy → build AA" | 30-60s | Adaptive counter-picking to observed enemy |
| **Novel-situation handler** — unusual maps, unusual play | 30-60s | Generalization beyond rule library |
| **Tuning coefficients offline** — set scoring weights | Once, dev-time | Analyze past round logs, output weight tables |

## What LLMs are bad at here

| Decision | Why LLM is wrong tool |
|--|--|
| **Exact coordinate placement** | Spatial reasoning at meter-level. LLM hallucinates positions. |
| **Per-tick execution loops** (every 500ms) | API roundtrip 300-800ms even for smallest models. Can't fit. |
| **Deterministic guarantees** | Non-deterministic output. Very hard to regression-test. |
| **Offline gameplay** | No internet → mod breaks unless rule-based fallback exists. |

## Proposed hybrid architecture (when we get there)

```
┌──────────────────────────────────────────────────────────────┐
│  STRATEGIC LAYER  (called every 30-60s, LLM-augmented)        │
│  Input:  PerceptionSnapshot + last 30s of events              │
│  Output: DirectiveJSON                                         │
│    { weight: {eco:0.5, expa:0.2, mil:0.3},                    │
│      sub_strategy: "eco_first",                                │
│      composition_target: [Heavy:6, Sniper:3, ...],             │
│      priorities: {biocaches_wanted:5, next_biotics_id:2},      │
│      unit_role_hints: {Heavy:"defense-line", Sniper:"harass"} }│
│  Provider: Claude Haiku (~$0.001/call) with 2s budget         │
│  Fallback: phase-based hard-coded defaults if LLM times out    │
└──────────────────────────────────────────────────────────────┘
                              ↓ directive (cached until next update)
┌──────────────────────────────────────────────────────────────┐
│  TACTICAL SCORING (called every Think tick, pure rules)       │
│  Enumerate all candidate actions (build X, produce Y, order Z │
│  at pos P). Score each by:                                     │
│    directive.weight[dimension] × immediate_effect_on_dimension │
│    - cost                                                      │
│    - expected_income_delta                                     │
│    - threat_reduction                                          │
│    - army_value_added                                          │
│    - time_to_complete                                          │
│  Pick highest-scoring action affordable + buildable + valid.   │
└──────────────────────────────────────────────────────────────┘
                              ↓ action
┌──────────────────────────────────────────────────────────────┐
│  EXECUTION (millisecond, pure rules)                          │
│  Placement math + game API calls.                              │
└──────────────────────────────────────────────────────────────┘
```

**Key property**: without LLM, the Strategic layer's fallback directives still
run everything else. The mod is fully functional offline. LLM is a *quality
booster*, not a *hard dependency*.

## Prerequisites (must exist before LLM adds value)

1. **Perception layer complete** — reliable TeamState snapshot every tick
   (own units/structures/income/tech, enemy last-seen composition + positions,
   map biotics/balterium patch inventory, phase estimate)
2. **Execution layer robust** — placement + emission is deterministic, tested,
   handles the edge cases (out-of-range, terrain slide, tech-lock)
3. **Tactical scoring engine working** — score-based ranking of candidate actions.
   Without this, LLM has nothing to talk to. This is what we're building now.
4. **Baseline metrics established** — how much resource does stock AI leave
   unspent, how many BioCaches vs biotics, unit variety index, orders per unit.
   Need numbers to prove LLM lift.
5. **Round logs at Alien-vs-Alien parity** — currently Sol vs Alien AI is
   imbalanced; need clean comparisons before optimizing.
6. **Failure modes catalogued** — where does rule-based scoring plateau? Those
   are the LLM's real opportunities. Without this catalog we don't know what
   the LLM should improve.

Only after all six are green does LLM integration make sense.

## Implementation sketch (for when the time comes)

### 1. New module: `Strategic/DirectiveProvider.cs`
- Interface `IDirectiveProvider { Directive Get(PerceptionSnapshot); }`
- Impl `HardCodedDirectiveProvider` (default) — reads DESIGN.md phase table
- Impl `LlmDirectiveProvider` — calls Anthropic API async
- Config selects provider

### 2. MelonPreferences entries
```
Si_RTS_AI.LLM.Enabled = false
Si_RTS_AI.LLM.Provider = "anthropic"
Si_RTS_AI.LLM.ApiKey = "sk-ant-..."
Si_RTS_AI.LLM.Model = "claude-haiku-4-5"
Si_RTS_AI.LLM.IntervalSeconds = 45
Si_RTS_AI.LLM.TimeoutMs = 3000
Si_RTS_AI.LLM.FallbackToHardCoded = true
```

### 3. Background task loop
- Fires every N seconds (default 45)
- Builds compact state prompt (< 4K tokens)
- Sends async request
- On success: swap active Directive atomically
- On failure/timeout: keep previous directive, log warning
- Never blocks the Think loop

### 4. Prompt template (draft — refine later)
```
System: You are the strategic commander for team {faction} in Silica.
Return JSON: { weight: {eco, expa, mil}, sub_strategy: string,
composition_target: object, priorities: object }.

User: [state snapshot: own income {N}/tick, own units {inventory},
own structures {inventory}, enemy last-seen at {positions}, enemy
composition estimate {inventory}, phase {N}, map biotics remaining
{count}, current mode {steamroll|...}, last directive rationale {...}]
```

### 5. Cost / rate accounting
- Log per-call token usage
- Cap at N calls per round (safety)
- Log estimated $ per round to server console
- Warn if daily budget exceeded

### 6. Testing hooks
- Deterministic replay mode: swap `IDirectiveProvider` for a scripted one
- Golden-directive tests: feed known state, assert directive stays within bounds
- Chaos mode: randomize directive response times to stress fallback

## Alternative: fully-offline learned policy

If we don't want cloud dependency, an alternative is to train a small policy
network *offline* on match data and ship it with the mod:
- Collect thousands of round logs (we already log per round!)
- Label good vs bad directives based on match outcome
- Train a small (few-MB) model with e.g. LightGBM or a tiny neural net
- Ship the model file with the mod
- Runtime inference is <1ms, no API dependency

This is basically the "LLM tuned rule coefficients" idea from earlier, formalized.
More engineering work up-front, but no ongoing API cost and fully offline. Worth
revisiting once we have a corpus of round logs.

## Bottom line

Notes recorded. Coming back to this **after** the rule-based scoring engine +
perception layer are solid. Continuing with Phase 3.2 iteration now.
