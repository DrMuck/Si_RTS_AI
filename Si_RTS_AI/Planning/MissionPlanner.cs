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
        internal enum Kind { Garrison, Defend, Push }

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

        const float TICK_S     = 5f;
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
                if (ArmyValue > _pushPeakValue) _pushPeakValue = ArmyValue;
                if (_pushPeakValue > 0 &&
                    ArmyValue < _pushPeakValue * MilitaryConfig.PushRetreatFraction)
                {
                    SetPosture(Posture.Hold, now,
                               $"army fell to {ArmyValue} of a committed peak {_pushPeakValue}");
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
            if (ArmyValue < EnemyEstimate * MilitaryConfig.PushMargin) return;

            if (!TryPickTarget(out var objective, out string what)) return;

            PushObjective  = objective;
            _pushPeakValue = ArmyValue;
            SetPosture(Posture.Push, now,
                       $"army {ArmyValue} flat at {ArmyGrowthPerS:F1}/s with {spendable} spendable, " +
                       $"theirs ~{EnemyEstimate} → {what}");
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
        /// The best of what we have SEEN, ranked by what it is worth over how
        /// defended it is. Weakly-defended value first, which on any real map
        /// means their expansions before their main base — the Silica form of a
        /// blockade, since there are no supply lines to cut and contesting
        /// patches is the equivalent.
        ///
        /// Only discovered structures are eligible. The AI could read every
        /// enemy position off the server and that would be both unfair and
        /// misleading — a planner trained on omniscience makes decisions it
        /// could never justify from what it can see.
        /// </summary>
        static bool TryPickTarget(out Vector3 pos, out string what)
        {
            pos = Vector3.zero; what = null;
            float best = 0f;
            Vector3 bestPos = Vector3.zero;
            string bestName = null;

            Perception.ThreatMap.ForEachKnown(k =>
            {
                if (k.Cost <= 0) return;
                float local = 0f;
                try { local = Perception.ThreatMap.ThreatNear(k.Pos, TARGET_THREAT_RADIUS); } catch { }
                float score = k.Cost / (1f + local);
                if (score <= best) return;
                best = score; bestPos = k.Pos; bestName = k.Name;
            });

            if (bestName == null) return false;
            pos = bestPos;
            what = $"{bestName} at ({bestPos.x:F0},{bestPos.z:F0})";
            return true;
        }

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
            int wanted = Mathf.Max(DefencePlanner.GarrisonValue,
                                   Mathf.CeilToInt(ArmyValue * MilitaryConfig.HomeShare));
            int homeCap = Mathf.Max(Mathf.CeilToInt(ArmyValue * MilitaryConfig.HomeCapShare),
                                    Mathf.CeilToInt(ArmyValue * MilitaryConfig.HomeShare));
            int homeFloor = Mathf.Min(wanted, homeCap);

            Missions.Add(new Mission
            {
                Id = IdFor(Kind.Garrison, nest), Kind = Kind.Garrison, Objective = nest,
                RequiredValue = homeFloor, Score = float.MaxValue,
                Note = wanted > homeFloor
                     ? $"home, wanted {wanted} capped to {homeFloor} of army {ArmyValue}"
                     : $"home (enemy near nest {DefencePlanner.GarrisonValue})",
                CreatedAt = now,
            });

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
                    RequiredValue = Mathf.Max(1, Mathf.CeilToInt(EnemyEstimate * MilitaryConfig.PushMargin)),
                    Score = 0f,
                    Note = $"theirs ~{EnemyEstimate}, ours {ArmyValue}",
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
              .Append(" | ");
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
