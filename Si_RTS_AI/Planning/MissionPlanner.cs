using MelonLoader;
using Silica;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Si_RTS_AI.Planning
{
    /// <summary>
    /// THE MILITARY PLANS A PORTFOLIO AND REFRESHES IT, EXACTLY AS THE ECONOMY
    /// PLANS A BLUEPRINT.
    ///
    /// Rule 6.5 — "Phase 2 is a blueprint, not a series of choices" — was learned
    /// expensively: greedy per-tick branching worked fine and had no global
    /// picture, so around eighteen minutes every frontier point claimed its own
    /// nearest patch forever. The fix was scan, plan, build to the plan, refresh.
    ///
    /// The military had the identical failure queued up. DefencePlanner ranked
    /// threatened assets every tick and BattalionManager raised a response per
    /// threatened asset — the greedy shape, which works and then at scale
    /// produces a response battalion per threat forever, with nothing able to say
    /// that one push is worth more than three garrison top-ups.
    ///
    /// So intent lives HERE and nowhere else. DefencePlanner says what ground is
    /// worth defending. BattalionManager owns units and orders. This decides what
    /// is worth doing, prices it in cash, and hands it down in one ordered list.
    ///
    /// THREE KINDS, DELIBERATELY. Raids and formations and the economic-push
    /// posture are designed in MILITARY_TACTICS and are not built, because raids
    /// need enemy-structure discovery to be trustworthy rather than merely
    /// present, and formations cannot be judged before exchange ratios exist.
    /// Fewer solid rules.
    ///
    /// WHAT ORDERS WHAT, AND WHY IT IS A PRIORITY AND NOT A SCORE. Garrison is a
    /// floor and a veto, never a ranked entry — the Queen is a loss condition,
    /// not a high-priority asset. Defence outranks offence whenever a defend
    /// mission exists at all. The honest reason is that scoring a raid against a
    /// garrison top-up needs exchange ratios per mission kind, and there are
    /// none; a priority ordering cannot produce the failure a miscalibrated
    /// score can, which is marching off while the economy is eaten.
    /// </summary>
    internal static class MissionPlanner
    {
        internal enum Kind { Garrison, Defend, Forward, Push }

        internal class Mission
        {
            public int     Id;
            public Kind    Kind;
            public Vector3 Objective;
            public int     RequiredValue;   // cash
            public float   Score;           // comparable within a kind, not across
            public string  Note;            // what it is about, for the log
            public float   CreatedAt;
        }

        /// <summary>The portfolio, in funding order. Read by BattalionManager.</summary>
        internal static readonly List<Mission> Missions = new List<Mission>(8);

        internal enum Posture { Hold, Push }
        internal static Posture Current { get; private set; }

        // ---- What the trigger is made of, all measured ------------------------

        /// <summary>Cash value of every combat unit we own.</summary>
        internal static int ArmyValue { get; private set; }

        /// <summary>Cash per second the army has been growing over the window.
        /// One signal covering all three ceilings — unit cap, production
        /// throughput, economy — because which one binds does not change what to
        /// do about it. If we cannot get stronger and they still can, every
        /// further second of holding is a strict loss.</summary>
        internal static float ArmyGrowthPerS { get; private set; }

        /// <summary>Their army, as far as we can see it. Threat is in the game's
        /// attack-rating units; this is the same conversion defence uses.</summary>
        internal static int EnemyEstimate { get; private set; }

        internal static Vector3 PushObjective { get; private set; }

        /// <summary>The portfolio is rebuilt from scratch each pass, so this is the
        /// most expensive tier and the one whose inputs change slowest. A mission
        /// that needs re-deciding twice in ten seconds was not a plan.</summary>
        const float TICK_S     = 10f;
        const float LOG_S      = 20f;

        /// <summary>Window the growth rate is measured over. Long enough that a
        /// single unit finishing does not read as growth, short enough that a
        /// stalled army is noticed inside a minute or two.</summary>
        const float GROWTH_WINDOW_S = 120f;

        /// <summary>Threat radius around a push objective, for judging how
        /// defended it is. Same radius defence uses on its own assets.</summary>
        const float TARGET_THREAT_RADIUS = 300f;

        /// <summary>Radius the enemy force at a threatened asset is counted over.
        /// Matches DefencePlanner's own asset radius, so the thing that raised
        /// the task and the thing that sizes the answer look at the same ground.</summary>
        const float ASSET_DEFENCE_RADIUS = 300f;

        /// <summary>How long after coming home the army will not leave again.
        /// Posture-level hysteresis: without it a push that retreats on a bad
        /// minute re-commits on the next tick into the force that beat it.</summary>
        const float POSTURE_DWELL_S = 60f;

        static readonly List<(float t, int value)> _armyTrace = new List<(float, int)>(64);
        static readonly Dictionary<string, int> _idByThing = new Dictionary<string, int>(16);
        static float _lastTickAt, _lastLogAt;
        static int   _nextId;
        static float _postureChangedAt;
        static int   _pushPeakValue;

        internal static void ResetForNewRound()
        {
            Missions.Clear();
            _armyTrace.Clear();
            _idByThing.Clear();
            Current = Posture.Hold;
            ArmyValue = 0; ArmyGrowthPerS = 0f; EnemyEstimate = 0;
            PushObjective = Vector3.zero;
            PushRequirement = 0;
            TargetIsHq = false;
            _lastTickAt = _lastLogAt = 0f;
            _nextId = 0;
            _postureChangedAt = 0f;
            _pushPeakValue = 0;
        }

        internal static void Tick(Team team)
        {
            if (!MilitaryConfig.Enabled || team == null) return;
            float now = Time.time;
            if (now - _lastTickAt < TICK_S) return;
            _lastTickAt = now;

            try
            {
                MeasureArmy(team, now);
                MeasureEnemy();
                UpdatePosture(team, now);
                BuildPortfolio(team, now);
                MaybeLog(now);
            }
            catch (Exception ex) { MelonLogger.Warning("[MISSION] tick threw: " + ex.Message); }
        }

        // ---- Measurement ------------------------------------------------------

        static void MeasureArmy(Team team, float now)
        {
            int value = 0;
            try
            {
                var units = team.Units;
                if (units != null)
                    for (int i = 0; i < units.Count; i++)
                    {
                        var u = units[i];
                        if (u?.ObjectInfo == null || u.IsDestroyed) continue;
                        string n = u.ObjectInfo.DisplayName ?? "";
                        if (n == "Shrimp" || n == "Queen") continue;
                        value += Perception.UnitValues.CostOf(n);
                    }
            }
            catch { }
            ArmyValue = value;

            // The plan is fed here because this is where the army is counted;
            // two places measuring the same thing is how the eco layer's worst
            // day happened.
            float roundS = 0f;
            try { roundS = Perception.MapLayers.LayerReplay.CurrentRoundTime; } catch { }
            ArmyPlan.Update(roundS, value);

            _armyTrace.Add((now, value));
            while (_armyTrace.Count > 1 && now - _armyTrace[0].t > GROWTH_WINDOW_S)
                _armyTrace.RemoveAt(0);

            // Only a FULL window is a growth rate. A short one at round start
            // reads as "not growing" for the honest reason that there has not
            // been time to grow, and that must not be allowed to look like the
            // ceiling the push trigger is watching for.
            var first = _armyTrace[0];
            float span = now - first.t;
            ArmyGrowthPerS = span >= GROWTH_WINDOW_S * 0.9f
                           ? (value - first.value) / Mathf.Max(1f, span)
                           : float.MaxValue;
        }

        static void MeasureEnemy()
        {
            // Their cash, not their danger. See ThreatMap's note on why the
            // threat field cannot be an amount.
            try { EnemyEstimate = Perception.ThreatMap.TotalValue; } catch { EnemyEstimate = 0; }
        }

        // ---- Posture ----------------------------------------------------------

        /// <summary>
        /// PUSH WHEN ARMY GROWTH APPROACHES ZERO WHILE MONEY IS NOT THE REASON.
        ///
        /// Growth alone is ambiguous — a flat army might be flat because we are
        /// broke, and pushing broke is how a good economy loses to a worse one.
        /// Paired with "and we are sitting on spendable cash", it is unambiguous:
        /// the money is there, it is not becoming army, so the ceiling is
        /// production or population and waiting cannot lift either. Rounds that
        /// sit on 100-200k unspent from minute twelve are exactly this case, and
        /// rules 6.15/6.19 already name idle cash as the economy's largest single
        /// waste. Military is the missing claimant.
        ///
        /// Nothing here triggers on enemy inactivity. A quiet enemy is
        /// neutral-to-good: static defence holds ground more cheaply per unit
        /// area than mobile army does, so quiet minutes convert a larger share of
        /// income into army and an unattacked hold compounds ahead.
        /// </summary>
        static void UpdatePosture(Team team, float now)
        {
            if (!MilitaryConfig.Offence)
            {
                if (Current != Posture.Hold) SetPosture(Posture.Hold, now, "offence disabled");
                return;
            }

            if (Current == Posture.Push)
            {
                // Released by the condition that raised it, not by a timer. A
                // push that has lost most of what it committed is a push that
                // has failed, whatever the clock says.
                //
                // MEASURED ON THE FORCE, NOT ON THE TEAM. This used to watch
                // team-wide ArmyValue, which the home producers keep climbing
                // while the push is being wiped out — so the abort fired on the
                // round ending rather than on the push failing. The 2026-08-08
                // soak logged "army fell to 0 of a committed peak 109688" four
                // times, which is not a retreat, it is a post-mortem.
                //
                // Falls back to team value until the force actually exists, or a
                // push would abort in the second before its battalion forms.
                int force = 0;
                try { force = BattalionManager.PushForceValue(); } catch { }
                int watched = force > 0 ? force : ArmyValue;
                if (watched > _pushPeakValue) _pushPeakValue = watched;
                if (force > 0 && _pushPeakValue > 0 &&
                    watched < _pushPeakValue * MilitaryConfig.PushRetreatFraction)
                {
                    SetPosture(Posture.Hold, now,
                               $"push force fell to {watched} of its peak {_pushPeakValue}");
                    return;
                }
                if (!TryPickTarget(out var still, out _))
                {
                    SetPosture(Posture.Hold, now, "no discovered objective left");
                    return;
                }
                PushObjective = still;
                return;
            }

            // Having just come home is a reason not to leave again immediately.
            // The commitment cost the spec asks for, at the posture level.
            if (_postureChangedAt > 0f && now - _postureChangedAt < POSTURE_DWELL_S) return;

            if (ArmyValue <= 0) return;
            if (ArmyGrowthPerS > MilitaryConfig.PushGrowthFloor) return;   // still building

            int spendable = SpendableCash(team);
            if (spendable < MilitaryConfig.EcoReserve) return;             // broke, not capped

            // BEING UNABLE TO SEE THEM IS NOT THE SAME AS THEM BEING WEAK, and
            // the arithmetic cannot tell the two apart: threat only counts units
            // inside our fog, so an unscouted enemy estimates at zero and every
            // army on earth clears a margin against zero. Without this the AI
            // walks five Crabs into an unseen base and calls it a push.
            //
            // The cost of the guard is passivity against an opponent we never
            // manage to look at, which is the safer of the two failures and is
            // visible in the log as knownStructures and theirs~0.
            if (EnemyEstimate <= 0) return;

            // Pick the target FIRST, because the bar to clear is the price of
            // killing THAT base — not a generic multiple of their army. A push
            // that leaves before it can finish the job is the one-by-one trickle
            // in a larger denomination.
            if (!TryPickTarget(out var objective, out string what)) return;
            if (ArmyValue < PushRequirement) return;

            PushObjective  = objective;
            _pushPeakValue = 0;      // the force has not formed yet
            SetPosture(Posture.Push, now,
                       $"army {ArmyValue} flat at {ArmyGrowthPerS:F1}/s with {spendable} spendable, " +
                       $"and {ArmyValue} >= {PushRequirement} needed → {what}");
        }

        static void SetPosture(Posture p, float now, string why)
        {
            if (Current == p) return;
            Current = p;
            _postureChangedAt = now;
            if (p == Posture.Hold) { PushObjective = Vector3.zero; _pushPeakValue = 0; }
            MelonLogger.Msg($"[MISSION] posture → {p.ToString().ToUpperInvariant()}: {why}");
        }

        /// <summary>
        /// Cash the military may consider its own. The economy gets everything
        /// while it is BEHIND its worker trajectory and still converting — that
        /// is the measured condition under which another shrimp earns. Once it is
        /// on track, or once yield is falling and the answer is ground rather
        /// than workers, the bank is not the economy's constraint and holding
        /// cash back from the army buys nothing.
        ///
        /// Time-shaped off WorkerPlan rather than a fixed budget share: before
        /// minute eight the economy very much needs the money, after minute
        /// twelve it demonstrably does not.
        /// </summary>
        internal static int SpendableCash(Team team)
        {
            // THE OPENING IS NOT NEGOTIABLE. DrMuck, 2026-08-07: "Military should
            // never rob the opener."
            //
            // Not a reserve, not a share, not a trajectory test that can read the
            // wrong way for thirty seconds — nothing at all until the opening
            // queue is empty. The measured version of this rule cost 9,000 cash
            // against an opener whose whole plan was 9,000, because at t+79s the
            // worker curve had not started and "on track" was technically true.
            // A categorical rule cannot produce that failure; a trajectory test
            // did, on its first round.
            try { if (OpenerPlanner.QueueActive) return 0; }
            catch { }

            // THE ECONOMY GETS RIGHT OF WAY WHEN IT IS ACTUALLY BLOCKED.
            //
            // Not a budget share — the right share changes every minute and a
            // fixed one is wrong at both ends of a round. The rule is simply
            // that the military never takes cash on a tick where a PLACEMENT was
            // refused for want of money. When the economy is not blocked, the
            // money is by definition free, and DrMuck's own rule applies: money
            // sitting is potential sitting dead.
            try { if (EcoPlanner.EcoStarvedOfCash) return 0; }
            catch { }

            int cash = 0;
            try { cash = team.TotalResources; } catch { }
            // ONE definition, in WorkerPlan. This had its own copy for a day and
            // the copies disagreed exactly where it mattered — see the comment on
            // WorkerPlan.CanStillConvertCash.
            int reserve = WorkerPlan.CanStillConvertCash ? MilitaryConfig.EcoReserve : 0;
            try { reserve += MoneyBroker.GetReservedCash(team); } catch { }
            return Mathf.Max(0, cash - reserve);
        }

        /// <summary>
        /// WHICH BASE WE ARE TRYING TO KILL, and what finishing it would cost.
        ///
        /// DrMuck: "maybe we go first for an objective to kill the enemy base(s).
        /// Build a strong army where we are sure it could destroy the base."
        ///
        /// The old rule picked the most expensive single STRUCTURE weighted by
        /// how undefended it was, which is a building rather than a base — so
        /// the army walked at a Refinery, killed it, and stood there. Nothing
        /// ever asked what finishing the job would take, so "sure it could
        /// destroy the base" was not a thought the layer could have.
        ///
        /// Now the target is a base cluster and the price has two parts:
        ///
        ///   BEAT WHAT DEFENDS IT — and not merely what is standing there. Their
        ///   whole army can answer a push on their base, so the requirement uses
        ///   the larger of the local force and their total estimated force. A
        ///   push sized against the garrison arrives and then meets the rest.
        ///
        ///   THEN KILL THE BUILDINGS — structures take time and damage to remove
        ///   and the force has to still exist afterwards. Priced as a share of
        ///   the base's cash value. That share is a PLACEHOLDER; it is the one
        ///   invented number here and it is in config for that reason.
        ///
        /// Choosing between bases: prefer one we can afford over a richer one we
        /// cannot, and prefer an HQ when the prices are close, because HQs are
        /// the actual win condition.
        /// </summary>
        static bool TryPickTarget(out Vector3 pos, out string what)
        {
            pos = Vector3.zero; what = null;
            // THE WIN CONDITION IS THE OBJECTIVE. DrMuck: "as you know destruct
            // all enemy HQs is the objective."
            //
            // That is not a preference to weight, it is the definition of
            // winning, and the four drivers in the spec — deny expansion, weaken
            // eco, weaken production, weaken military — are INSTRUMENTAL. They
            // exist to make an HQ killable. Scoring an HQ base against a
            // Refinery on value-per-cash would let a rich enough Refinery
            // outrank the thing that ends the game, which is the wrong shape of
            // decision no matter what the constants are.
            //
            // So: an HQ base we can afford always wins. Only when none is
            // affordable do we look at what would make one affordable later.
            Perception.ThreatMap.Base best = null;
            int bestPrice = 0;
            float bestScore = 0f;
            bool bestIsHq = false;

            var bases = Perception.ThreatMap.Bases;
            for (int i = 0; i < bases.Count; i++)
            {
                var b = bases[i];
                if (b.Cost <= 0) continue;
                int price = PriceOfKilling(b);
                if (price <= 0) continue;

                bool affordable = ArmyValue >= price;
                bool isHq = b.HasHq;

                // An affordable HQ ends the argument. Among several, the
                // cheapest to finish — we can come back for the others.
                if (isHq && affordable)
                {
                    if (!bestIsHq || price < bestPrice)
                    { best = b; bestPrice = price; bestIsHq = true; }
                    continue;
                }
                if (bestIsHq) continue;      // nothing outranks an affordable HQ

                // Nothing decisive is in reach, so pick what best weakens them
                // per cash committed. Value carries the spec's ordering: their
                // eco and production are worth more than their army standing on
                // open ground, and a base is worth what it cost them.
                float score = b.Cost / (float)price;
                if (affordable) score *= 2f;   // able to finish beats able to start
                if (score <= bestScore) continue;
                bestScore = score; best = b; bestPrice = price;
            }

            // NOTHING DISCOVERED IS NOT A REASON TO STAND STILL.
            //
            // The round that prompted this held 217 units worth 179,900 at the
            // nest for 29 minutes with posture=hold and "no discovered
            // objective" — an army that could have ended the game several times
            // over, waiting on a scouting report that a lone Crab was never
            // going to deliver. Meanwhile 35 Crabs died telling us exactly where
            // to look.
            //
            // A Behemoth battalion survives ground that kills a Crab, so the
            // army does its own scouting. This is armed reconnaissance, not a
            // push: the objective is the ground itself, and its value is that
            // arriving there lifts the fog and turns a guess into the real HQ
            // target that the branch above then prefers on the next cycle.
            if (best == null)
            {
                if (!ScoutPlanner.DeathGround(out var deathGround, out int votes)) return false;

                // No raze term — there is nothing known to raze. Beat what we
                // believe is there and keep the ordinary margin, so this cannot
                // become the five-Crabs-into-an-unseen-base failure in a larger
                // denomination.
                int need = Mathf.CeilToInt(Mathf.Max(EnemyEstimate, 1) * MilitaryConfig.PushMargin);
                pos = deathGround;
                PushRequirement = need;
                TargetIsHq = false;
                what = $"armed reconnaissance to ({deathGround.x:F0},{deathGround.z:F0}) — " +
                       $"no base discovered, but {votes} scouts died there; " +
                       $"need {need} against theirs~{EnemyEstimate}";
                return true;
            }

            pos = best.Centre;
            PushRequirement = bestPrice;
            TargetIsHq = best.HasHq;
            what = (best.HasHq
                        ? $"THEIR HQ — {best.Count} buildings worth {best.Cost}"
                        : $"a base of {best.Count} buildings worth {best.Cost} (no HQ; " +
                          "weakening them until one is reachable)") +
                   $" at ({best.Centre.x:F0},{best.Centre.z:F0}), " +
                   $"costing about {bestPrice} to finish";
            return true;
        }

        /// <summary>True when the current push objective would remove an enemy
        /// HQ, which is the only thing that actually wins.</summary>
        internal static bool TargetIsHq { get; private set; }

        /// <summary>Enemy HQs we have discovered and still believe stand. The
        /// win condition made countable, so a round can be read as progress
        /// toward it rather than as a list of skirmishes.</summary>
        internal static int KnownEnemyHqs()
        {
            int n = 0;
            try
            {
                var bs = Perception.ThreatMap.Bases;
                for (int i = 0; i < bs.Count; i++) if (bs[i].HasHq) n++;
            }
            catch { }
            return n;
        }

        /// <summary>What it would take to take this base down and still be
        /// standing. See TryPickTarget for why it is these two terms.</summary>
        static int PriceOfKilling(Perception.ThreatMap.Base b)
        {
            int local = 0;
            try { local = Perception.ThreatMap.ValueNear(b.Centre, BASE_DEFENCE_RADIUS); } catch { }
            int defenders = Mathf.Max(local, EnemyEstimate);
            int beatThem = Mathf.CeilToInt(defenders * MilitaryConfig.PushMargin);
            int razeIt   = Mathf.CeilToInt(b.Cost * MilitaryConfig.StructureRazeShare);
            return beatThem + razeIt;
        }

        /// <summary>How wide to look when asking what is standing in a base.
        /// Matches the clustering distance, so "defending it" and "part of it"
        /// mean the same ground.</summary>
        const float BASE_DEFENCE_RADIUS = 700f;

        /// <summary>Cash the push must be worth to finish the CURRENT target.
        /// Set by TryPickTarget so the portfolio and the log agree on one
        /// number rather than each computing their own.</summary>
        internal static int PushRequirement { get; private set; }

        // ---- The portfolio ----------------------------------------------------

        /// <summary>
        /// A mission id names A THING WORTH DOING, not a refresh of the list. The
        /// portfolio is rebuilt every five seconds; if the ids came off a counter
        /// then every battalion would be orphaned on every refresh and the log
        /// would number the same garrison in the hundreds by minute ten.
        ///
        /// Ground is rounded, so a defence task that jitters a few metres between
        /// samples is still the same task. The push is keyed on its kind alone,
        /// because there is only ever one and re-aiming it does not make it a
        /// different intention.
        /// </summary>
        static int IdFor(Kind kind, Vector3 objective)
        {
            string key = kind == Kind.Push
                ? "push"
                : kind + "|" + Mathf.RoundToInt(objective.x / 100f) + "," + Mathf.RoundToInt(objective.z / 100f);
            if (_idByThing.TryGetValue(key, out int id)) return id;
            id = ++_nextId;
            _idByThing[key] = id;
            return id;
        }

        static void BuildPortfolio(Team team, float now)
        {
            Missions.Clear();

            // 1) Garrison. A project, not a mission — it never completes, and it
            //    is subtracted before anything else is allocated.
            Vector3 nest = HomeOf(team);

            // A FLOOR THAT CANNOT BECOME A CEILING ON EVERYTHING ELSE.
            //
            // The garrison is filled first, so whatever it asks for is taken
            // before any defence exists. On 2026-08-07 it asked for more than the
            // army was ever worth, took all of it, and every defend battalion sat
            // at zero units for the whole round while the base was dismantled
            // around them — the army in a blob at the Nest.
            //
            // So it is bounded by a share of what we actually have. If the enemy
            // at home genuinely outvalues the entire army, sending everything
            // home does not save the Nest; it only guarantees the expansions die
            // too. The floor is homeShare, the ceiling homeCapShare, and between
            // them it is what the enemy has actually brought.
            // PRESSURE AT THE RIGHT PLACE IS DEFENCE, AND IT IS CHEAPER.
            //
            // DrMuck: "only a small army is needed to defend the nest when the
            // enemy is pressured by our army at the forward operating base and
            // comes not through." The old floor could not express that — it was
            // a flat share of the army held at home whatever else we controlled,
            // so the bot paid full price for base defence while also standing on
            // the ground the attack would have come through. That is how it
            // ended up a sitting duck at spawn.
            //
            // Holding forward ground halves the share. Not zero: the Queen is
            // still a loss condition and a forward hold can break.
            bool forward = false;
            try { forward = MilitaryBlueprint.HoldingForward; } catch { }

            // The floor is a FIXED amount of defence, scaled down when we are
            // holding forward ground, and never a share of our own army. See
            // MilitaryConfig.HomeFloorCash for why the share was wrong: it grew
            // with the army, so the home requirement rose every time we built
            // anything and the army could never be released.
            int floorCash = Mathf.CeilToInt(MilitaryConfig.HomeFloorCash * (forward ? 0.5f : 1f));
            int wanted = Mathf.Max(DefencePlanner.GarrisonValue, floorCash);
            // The cap still scales with the army, because a huge army CAN
            // spare more for home — but it is a ceiling, never a demand.
            int homeCap = Mathf.Max(Mathf.CeilToInt(ArmyValue * MilitaryConfig.HomeCapShare),
                                    floorCash);
            int homeFloor = Mathf.Min(wanted, homeCap);

            Missions.Add(new Mission
            {
                Id = IdFor(Kind.Garrison, nest), Kind = Kind.Garrison, Objective = nest,
                RequiredValue = homeFloor, Score = float.MaxValue,
                Note = (forward ? "forward hold, half share; " : "") +
                       (wanted > homeFloor
                     ? $"home, wanted {wanted} capped to {homeFloor} of army {ArmyValue}"
                     : $"home (enemy near nest {DefencePlanner.GarrisonValue}, floor {floorCash})"),
                CreatedAt = now,
            });

            // 1b) THE ARMY WAITS AT THE FRONT, NOT AT HOME.
            //
            // DrMuck: "Fob towards enemy base needs more producers and behes
            // there." Two reasons this is not merely tidier. A FOB holding
            // producers and no defenders is a gift to the first raid that finds
            // it. And an army that masses at the Nest has to walk the whole map
            // when the push finally triggers, arriving strung out and late —
            // which is the "sent in one by one" failure wearing a different hat.
            //
            // So the staging point is the forward site. Everything above the
            // home floor and the defence tasks gathers there and waits, which
            // means the push begins from the front rather than from spawn.
            if (MilitaryBlueprint.TryForwardPos(out var fob))
            {
                // Whatever is left after home and defence. Deliberately larger
                // than the army so it never stops accepting units — it is a
                // staging area, not a quota.
                Missions.Add(new Mission
                {
                    Id = IdFor(Kind.Forward, fob), Kind = Kind.Forward, Objective = fob,
                    RequiredValue = Mathf.Max(1, ArmyValue),
                    Score = 1f,
                    Note = $"stage at the FOB ({fob.x:F0},{fob.z:F0}) — " +
                           "hold the front and start the push from there",
                    CreatedAt = now,
                });
            }

            // 2) Defence, in DefencePlanner's order — recent income times threat.
            //    A Bio Cache on a drained patch scores zero however much it cost.
            var tasks = DefencePlanner.Tasks;
            int made = 0;
            for (int i = 0; i < tasks.Count && made < MilitaryConfig.MaxDefendMissions; i++)
            {
                if (tasks[i].Kind == "home") continue;      // the garrison has it
                made++;
                Missions.Add(new Mission
                {
                    Id = IdFor(Kind.Defend, tasks[i].Pos), Kind = Kind.Defend, Objective = tasks[i].Pos,
                    RequiredValue = DefencePlanner.ValueFor(tasks[i].Pos, ASSET_DEFENCE_RADIUS),
                    Score = tasks[i].Score,
                    Note = $"earned {tasks[i].RecentIncome} under threat {tasks[i].Threat:F0}",
                    CreatedAt = now,
                });
            }

            // 3) Offence, last and only while pushing. One mass, one point — the
            //    portfolio never holds two pushes, because a push divided is two
            //    forces that lose separately.
            if (Current == Posture.Push && PushObjective != Vector3.zero)
            {
                Missions.Add(new Mission
                {
                    Id = IdFor(Kind.Push, PushObjective), Kind = Kind.Push, Objective = PushObjective,
                    RequiredValue = Mathf.Max(1, PushRequirement),
                    Score = 0f,
                    Note = $"kill base — need {PushRequirement}, have {ArmyValue}, theirs ~{EnemyEstimate}",
                    CreatedAt = now,
                });
            }
        }

        /// <summary>Which mission kind owns the ground at a position — the join
        /// key combat.jsonl has been writing as "unknown" while waiting for
        /// this. Exchange ratios per mission kind are the only way to find out
        /// whether holding really does trade better than fighting.</summary>
        internal static string KindAt(Vector3 pos, float radiusM)
        {
            float r2 = radiusM * radiusM;
            string best = null;
            for (int i = 0; i < Missions.Count; i++)
            {
                var m = Missions[i];
                float dx = m.Objective.x - pos.x, dz = m.Objective.z - pos.z;
                if (dx * dx + dz * dz > r2) continue;
                // Later kinds win: a fight at a push objective is a push even if
                // it happens to be near something we also defend.
                best = m.Kind.ToString().ToLowerInvariant();
            }
            return best ?? "none";
        }

        static Vector3 HomeOf(Team team)
        {
            try
            {
                var structs = team.Structures;
                if (structs != null)
                    for (int i = 0; i < structs.Count; i++)
                    {
                        var s = structs[i];
                        if (s?.ObjectInfo == null || s.IsDestroyed) continue;
                        if (s.ObjectInfo.DisplayName == "Nest") return s.transform.position;
                    }
            }
            catch { }
            return Vector3.zero;
        }

        static void MaybeLog(float now)
        {
            if (now - _lastLogAt < LOG_S) return;
            _lastLogAt = now;

            var sb = new System.Text.StringBuilder("[MISSION] ");
            sb.Append(Current.ToString().ToLowerInvariant())
              .Append(" army=").Append(ArmyValue)
              .Append(" growth=").Append(ArmyGrowthPerS == float.MaxValue
                                         ? "warmup" : ArmyGrowthPerS.ToString("F1") + "/s")
              .Append(" theirs~").Append(EnemyEstimate)
              .Append(" known=").Append(Perception.ThreatMap.KnownCount)
              .Append(" enemyHQs=").Append(KnownEnemyHqs())
              .Append(" [").Append(Perception.ThreatMap.SightReport()).Append("] | ");
            // Home comes off the Garrison mission rather than a team lookup —
            // it is the same Nest position, and this scope has no team handle.
            Vector3 home = Vector3.zero;
            for (int i = 0; i < Missions.Count; i++)
                if (Missions[i].Kind == Kind.Garrison) { home = Missions[i].Objective; break; }
            try { Perception.ThreatMap.ReportDiscoveries(home); } catch { }
            for (int i = 0; i < Missions.Count; i++)
            {
                var m = Missions[i];
                sb.Append(m.Kind).Append('#').Append(m.Id).Append(" need ").Append(m.RequiredValue);
                if (m.Kind != Kind.Garrison)
                    sb.Append(" @(").Append(m.Objective.x.ToString("F0")).Append(',')
                      .Append(m.Objective.z.ToString("F0")).Append(')');
                sb.Append(" [").Append(m.Note).Append("] ");
            }
            if (!MilitaryConfig.Execute) sb.Append(" [shadow — no orders issued]");
            MelonLogger.Msg(sb.ToString());
        }
    }
}
