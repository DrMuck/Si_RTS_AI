using HarmonyLib;
using MelonLoader;
using Silica;
using Silica.AI;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Si_RTS_AI.Planning
{
    /// <summary>
    /// BATTALIONS — the layer between a mission and a unit.
    ///
    /// DrMuck, 2026-08-05: "we need a military unit battalion manager that goes
    /// into strategy execution." Without one, every rule about how an army should
    /// behave has to be expressed per unit, and the two rules the spec cares most
    /// about cannot be expressed at all:
    ///
    ///   "Never trickle units in one by one." That is a statement about a GROUP
    ///   having enough strength before it moves. A per-unit planner has nothing
    ///   to hold the units back in.
    ///
    ///   "Re-plan when the situation changes, but with hysteresis." That is a
    ///   statement about a group KEEPING its assignment for a while. Per-unit
    ///   ordering re-decides continuously by construction — the exact failure the
    ///   eco planner spent 2026-08-03/04 unlearning.
    ///
    /// INTENT MOVED OUT, 2026-08-07. A battalion used to carry a Role of
    /// "garrison" or "response" and raise itself per threatened asset, which made
    /// the force and the reason for it the same object: units could not be
    /// re-tasked without being re-typed, and nothing could weigh one reason
    /// against another. Now MissionPlanner holds the intent and this holds units.
    /// A battalion is a force pool bound to a mission id, and when the mission
    /// goes the pool is released rather than disbanded.
    ///
    /// STRENGTH IS MEASURED IN CASH, not unit count. Fifteen Crabs and fifteen
    /// Behemoths are not the same army, and combat.jsonl records losses in value —
    /// so a critical-mass rule expressed in value can be calibrated against real
    /// exchanges later, and one expressed in bodies never can.
    ///
    /// SHADOW UNLESS TOLD OTHERWISE. It forms battalions, judges readiness and
    /// logs what it would do; it issues no orders until military.execute is on.
    /// </summary>
    internal static class BattalionManager
    {
        internal enum State { Forming, Ready, Committed, Returning }

        internal class Battalion
        {
            public string  Name;
            public int     MissionId;
            public MissionPlanner.Kind Kind;
            public State   Phase;
            public Vector3 Rally;
            public Vector3 Objective;
            public readonly List<Unit> Units = new List<Unit>();
            public int   Value;             // cash value of the units in it
            public int   RequiredValue;     // what the mission is judged to need
            public float AssignedAt;
            public float LastOrderAt;
            public float CommittedAt;
            /// <summary>True while the group is gathering rather than advancing.
            /// Kept on the battalion so the transition can be logged once
            /// instead of every pass.</summary>
            public bool  ClosingUp;
            public float ClosingSince;   // when the close-up began, 0 = not closing
            public bool  PatienceLogged; // one line per stall, not one per tick
        }

        internal static readonly List<Battalion> Battalions = new List<Battalion>(6);

        const float TICK_S = 5f;

        /// <summary>How long a battalion keeps an assignment before it may be
        /// re-tasked. The commitment cost the spec asks for, expressed as time —
        /// a group that changes its mind every five seconds arrives nowhere.</summary>
        const float ASSIGNMENT_DWELL_S = 30f;

        static float _lastTickAt, _lastLogAt;

        internal static void ResetForNewRound()
        {
            Battalions.Clear();
            _lastTickAt = _lastLogAt = 0f;
            _releasedToScouts = 0;
            StrippedFromVanillaAttack = 0;
            OrdersIssued = 0;
            _ordered.Clear();
        }

        internal static void Tick(Team team)
        {
            if (!MilitaryConfig.Enabled || team == null) return;
            float now = Time.time;
            if (now - _lastTickAt < TICK_S) return;
            _lastTickAt = now;

            try
            {
                RetireOrphans(now);
                Prune();
                var free = FreeCombatUnits(team);
                Fill(team, free, now);
                Judge();
                if (MilitaryConfig.Execute && CanCommand(team)) IssueOrders(team, now);
                MaybeLog(now);
            }
            catch (Exception ex) { MelonLogger.Warning("[BATTALION] tick threw: " + ex.Message); }
        }

        /// <summary>A human commanding the alien team is commanding it. We keep
        /// planning and logging, and we touch nothing.</summary>
        static bool CanCommand(Team team)
        {
            try { return AIManager.IsCommanderEnabled(team); }
            catch { return true; }      // API threw — behave as before
        }

        // ---- Rosters ---------------------------------------------------------

        /// <summary>
        /// Combat units nobody else owns. Scouts are excluded by asking
        /// ScoutPlanner rather than by guessing from unit type — the two would
        /// otherwise fight over the same Crabs, and the scout would lose every
        /// time a battalion re-formed.
        /// </summary>
        static List<Unit> FreeCombatUnits(Team team)
        {
            var free = new List<Unit>(32);
            var taken = new HashSet<Unit>();
            for (int b = 0; b < Battalions.Count; b++)
                for (int i = 0; i < Battalions[b].Units.Count; i++)
                    taken.Add(Battalions[b].Units[i]);

            try
            {
                var units = team.Units;
                if (units == null) return free;
                for (int i = 0; i < units.Count; i++)
                {
                    var u = units[i];
                    if (u == null || u.ObjectInfo == null || u.IsDestroyed) continue;
                    string n = u.ObjectInfo.DisplayName ?? "";
                    if (n == "Shrimp" || n == "Queen") continue;      // workers and the win condition
                    if (taken.Contains(u)) continue;
                    bool isScout = false;
                    try { isScout = ScoutPlanner.IsScout(u); } catch { }
                    if (isScout) continue;
                    free.Add(u);
                }
            }
            catch { }
            return free;
        }

        static void Prune()
        {
            int lostToScouts = 0;
            for (int b = Battalions.Count - 1; b >= 0; b--)
            {
                var bat = Battalions[b];
                for (int i = bat.Units.Count - 1; i >= 0; i--)
                {
                    var u = bat.Units[i];
                    if (u == null || u.IsDestroyed)
                    {
                        if (u != null) _ordered.Remove(u);
                        bat.Units.RemoveAt(i);
                        continue;
                    }
                    // Conscripted away since we took it. Hand it over rather than
                    // hold a unit we are not allowed to order and still count its
                    // value toward the army.
                    bool isScout = false;
                    try { isScout = ScoutPlanner.IsScout(u); } catch { }
                    if (isScout) { bat.Units.RemoveAt(i); lostToScouts++; }
                }
            }
            if (lostToScouts > 0) _releasedToScouts += lostToScouts;
        }

        /// <summary>Units handed back to scouting this round. If this climbs all
        /// round the two layers are fighting over the same Crabs and the tick
        /// order is wrong, which is worth knowing rather than guessing.</summary>
        static int _releasedToScouts;

        /// <summary>
        /// A battalion whose mission is no longer in the portfolio releases its
        /// units back to the pool. It is not disbanded on the tick the mission
        /// leaves — the dwell applies here too, so a defence task that flickers
        /// in and out of the ranking does not repeatedly tear a force apart.
        /// </summary>
        static void RetireOrphans(float now)
        {
            var live = new HashSet<int>();
            for (int i = 0; i < MissionPlanner.Missions.Count; i++)
                live.Add(MissionPlanner.Missions[i].Id);

            for (int b = Battalions.Count - 1; b >= 0; b--)
            {
                var bat = Battalions[b];
                if (live.Contains(bat.MissionId)) continue;
                // AssignedAt is refreshed every time Fill re-adopts this force,
                // so this reads "nothing has wanted it for a while" rather than
                // "the list was rebuilt", which is the whole difference between
                // hysteresis and an army that dissolves every refresh.
                if (now - bat.AssignedAt < ASSIGNMENT_DWELL_S) continue;
                if (bat.Units.Count > 0)
                    MelonLogger.Msg($"[BATTALION] {bat.Name} stood down — " +
                                    $"nothing has asked for it in {ASSIGNMENT_DWELL_S:F0}s, " +
                                    $"{bat.Units.Count} units released");
                bat.Units.Clear();
                Battalions.RemoveAt(b);
            }
        }

        // ---- Filling ---------------------------------------------------------

        /// <summary>
        /// Missions are filled IN ORDER, and the order is the portfolio's. The
        /// garrison comes first and takes what it needs before anything else
        /// exists — that is what makes it a floor rather than a competitor, and
        /// it is why the Queen cannot be outbid when the front is hungry.
        ///
        /// WHATEVER IS LEFT OVER goes to the push if there is one, and home if
        /// there is not. That is "hold cheaply while out-scaling, commit once,
        /// commit everything" expressed as an allocation rule rather than as a
        /// separate decision somewhere else — a surplus that reinforced whichever
        /// mission happened to be last in the list would send the reserve to the
        /// WEAKEST defence task, which is the opposite of a reserve.
        /// </summary>
        /// <summary>
        /// THE SURPLUS IS RE-DECIDED EVERY TICK, NOT PARKED FOREVER.
        ///
        /// Units past a mission's requirement used to stay where they were put,
        /// because FreeCombatUnits only ever offers units nobody holds and
        /// nothing took any back. Under Hold the reserve is the garrison, so the
        /// garrison accumulated everything and never let go: measured on the
        /// 2026-08-08 soak, 181 units worth 153,300 sitting at home against a
        /// requirement of 39,721, while the push that wanted 65,787 was Forming
        /// with ZERO units and two defence tasks sat at zero as well.
        ///
        /// Every symptom DrMuck reported from that night — only the Nest
        /// defended, expansions uncovered, no engagement — is this one rule.
        ///
        /// So over-strength battalions hand the excess back before anything is
        /// allocated, furthest-from-objective first, and the surplus then flows
        /// to whichever mission is the reserve THIS tick. A Committed force is
        /// left alone: pulling units out of a fight to rebalance a spreadsheet
        /// is how a push dies halfway.
        /// </summary>
        /// <summary>
        /// KINDS THAT STAND ON GROUND RATHER THAN ATTACK IT.
        ///
        /// Garrison and Forward share their whole mechanic — units are KEPT at a
        /// rally point, not formed up and sent — and differ only in which ground
        /// that is. Home holds the loss condition; the FOB holds the front, and
        /// DrMuck wants the Behemoths at the second rather than the first.
        ///
        /// Without this a Forward battalion sits in Forming forever, because its
        /// requirement is deliberately the size of the whole army so it never
        /// stops accepting units, and a requirement that large is never met.
        /// </summary>
        static bool Standing(MissionPlanner.Kind k) =>
            k == MissionPlanner.Kind.Garrison || k == MissionPlanner.Kind.Forward;

        static void ReleaseSurplus(List<Unit> free)
        {
            for (int b = 0; b < Battalions.Count; b++)
            {
                var bat = Battalions[b];
                if (bat.Phase == State.Committed && !Standing(bat.Kind))
                    continue;
                while (bat.Units.Count > 0 && ValueOf(bat.Units) > bat.RequiredValue)
                {
                    var u = Furthest(bat.Units, bat.Objective);
                    // Releasing the last unit of a mission that still wants
                    // something would just re-take it on the next line.
                    if (ValueOf(bat.Units) - Perception.UnitValues.CostOf(SafeName(u))
                        < bat.RequiredValue && bat.RequiredValue > 0) break;
                    bat.Units.Remove(u);
                    free.Add(u);
                }
                bat.Value = ValueOf(bat.Units);
            }
        }

        static string SafeName(Unit u)
        {
            try { return u?.ObjectInfo?.DisplayName ?? ""; } catch { return ""; }
        }

        static Unit Furthest(List<Unit> pool, Vector3 from)
        {
            Unit best = pool[0]; float bd = -1f;
            for (int i = 0; i < pool.Count; i++)
            {
                var u = pool[i];
                Vector3 p;
                try { p = u.transform.position; } catch { continue; }
                float dx = p.x - from.x, dz = p.z - from.z;
                float d = dx * dx + dz * dz;
                if (d > bd) { bd = d; best = u; }
            }
            return best;
        }

        static void Fill(Team team, List<Unit> free, float now)
        {
            var missions = MissionPlanner.Missions;
            Battalion garrison = null, push = null;

            // Requirements first, so ReleaseSurplus trims against THIS tick's
            // numbers rather than last tick's.
            for (int m = 0; m < missions.Count; m++)
            {
                var bat = Adopt(missions[m], now);
                bat.RequiredValue = missions[m].RequiredValue;
            }
            ReleaseSurplus(free);

            for (int m = 0; m < missions.Count; m++)
            {
                var mission = missions[m];
                var bat = Adopt(mission, now);

                bat.RequiredValue = mission.RequiredValue;
                while (ValueOf(bat.Units) < bat.RequiredValue && free.Count > 0)
                    Take(free, bat, bat.Objective);
                bat.Value = ValueOf(bat.Units);

                if (mission.Kind == MissionPlanner.Kind.Garrison) garrison = bat;
                if (mission.Kind == MissionPlanner.Kind.Push)     push     = bat;
            }

            // A COMMITTED FORCE DOES NOT ABSORB REINFORCEMENTS.
            //
            // The surplus used to go to the push whatever state it was in, and
            // combined with the cohesion rule below that is a deadlock rather
            // than a policy. NarakaCity 2026-08-13: the push reached 186 units
            // and a spread of 3,665m, so `closingUp` was permanently true and
            // every unit was ordered to the CENTROID OF ITS OWN BLOB instead of
            // the objective — for sixty minutes. It never left. DrMuck watched
            // combat units sit at four separate spawners and reported no
            // pressure on the enemy, which is exactly what that produces.
            //
            // The army cannot close because it is fed from Cysts scattered
            // across the map: fresh spawns re-inflate the spread as fast as the
            // tail closes it, and 186 units cannot physically stand inside 220m
            // anyway. Reinforcing a moving army is what the NEXT wave is for.
            //
            // So the surplus only tops up a force that has not left yet. Once it
            // commits, later units accumulate as free — the next mission refresh
            // raises a battalion for them, which is the "build up critical
            // armies first" rule the spec asks for rather than a trickle.
            var reserve = push ?? garrison;
            if (reserve != null && (reserve.Phase == State.Forming
                                 || reserve.Phase == State.Ready))
            {
                while (free.Count > 0) Take(free, reserve, reserve.Objective);
                reserve.Value = ValueOf(reserve.Units);
            }
        }

        /// <summary>
        /// The portfolio is rebuilt from scratch every refresh and mission ids do
        /// not survive it, so matching on id alone would dissolve and re-raise
        /// the whole army every five seconds. A battalion of the same KIND
        /// standing on the same ground is the same force under a new id.
        ///
        /// Push is matched on kind alone, because there is only ever one push and
        /// its objective moves when a better target is discovered — matching it
        /// on position would abandon the committed force at the old objective
        /// every time the ranking changed.
        /// </summary>
        static Battalion Adopt(MissionPlanner.Mission mission, float now)
        {
            var bat = Battalions.Find(b => b.MissionId == mission.Id);
            if (bat == null)
                bat = mission.Kind == MissionPlanner.Kind.Push
                    ? Battalions.Find(b => b.Kind == MissionPlanner.Kind.Push)
                    : Battalions.Find(b => b.Kind == mission.Kind &&
                                           (b.Objective - mission.Objective).sqrMagnitude < 200f * 200f);
            if (bat == null)
            {
                bat = new Battalion
                {
                    Name  = mission.Kind.ToString().ToLowerInvariant() + "-" + mission.Id,
                    Kind  = mission.Kind,
                    Phase = State.Forming,
                };
                Battalions.Add(bat);
            }
            bat.MissionId = mission.Id;
            bat.Kind      = mission.Kind;
            bat.Objective = mission.Objective;
            // AssignedAt is the "still wanted" stamp RetireOrphans reads. It has
            // to be refreshed on every re-adoption or every battalion looks
            // abandoned thirty seconds into the round.
            bat.AssignedAt = now;
            if (bat.Phase != State.Returning) bat.Rally = mission.Objective;
            return bat;
        }

        static void Take(List<Unit> free, Battalion bat, Vector3 to)
        {
            var u = Nearest(free, to);
            free.Remove(u);
            bat.Units.Add(u);
            bat.Value = ValueOf(bat.Units);
        }

        /// <summary>
        /// Readiness, which is the whole "never trickle in" rule in one place: a
        /// battalion below its required strength stays Forming and does not move.
        /// Reinforcement of a Committed battalion is allowed — that is the
        /// spec's exception, and it is not trickling, it is feeding a fight that
        /// is already happening.
        /// </summary>
        static void Judge()
        {
            for (int b = 0; b < Battalions.Count; b++)
            {
                var bat = Battalions[b];
                if (Standing(bat.Kind)) { bat.Phase = State.Ready; continue; }
                if (bat.Phase == State.Committed || bat.Phase == State.Returning) continue;
                bat.Phase = bat.Value >= bat.RequiredValue && bat.Units.Count > 0
                          ? State.Ready : State.Forming;
            }
        }

        // ---- Orders ----------------------------------------------------------
        //
        // Only READY battalions move, which is where "never trickle in" stops
        // being a comment and becomes behaviour: a Forming battalion is simply
        // never given a destination.
        //
        // Orders are RESTATED on a cadence rather than issued once. Vanilla
        // re-tasks units continuously — the scouts learned this the hard way, and
        // a single order is overwritten within seconds while the unit wanders
        // back to whatever the game AI wanted.

        [ThreadStatic] internal static bool PlannerOverride;

        /// <summary>
        /// ORDERS ARE ISSUED ON CHANGE, NOT ON A CLOCK.
        ///
        /// This used to restate every unit's destination every five seconds, and
        /// DrMuck put it plainly: "doing them military planning and execution on
        /// every tick is a bit insane." It is worse than wasteful. Measured on
        /// the 2026-08-08 round: 25,926 move orders for 506 units built, 54.7
        /// orders per unit.
        ///
        /// AND IT WAS BREAKING THE FIGHTS. A unit that stops to engage is by
        /// definition still far from its objective, so the timer handed it a
        /// fresh move order every five seconds and walked it out of the
        /// engagement. The army had a good composition and traded badly, and
        /// this is a large part of why: it was never allowed to finish anything.
        ///
        /// The restating existed because vanilla re-tasks units continuously.
        /// That hole is now closed at both doors — move orders by the prefix
        /// below, attack orders by the group patch — so the timer is no longer
        /// paying for anything.
        ///
        /// A unit is now ordered when its destination MOVES, when it has drifted
        /// off course, or when it has gone idle short of the objective. Never
        /// while it has a target.
        /// </summary>
        const float ARRIVED_M    = 120f;
        const float HOME_LEASH_M = 250f;

        /// <summary>How far a destination must move before it is a new order
        /// rather than the same one. Below this the unit is already walking
        /// somewhere close enough.</summary>
        const float ORDER_MOVED_M = 90f;

        /// <summary>Backstop for an order the game silently dropped. Long,
        /// because its only job is to catch a unit standing still for no
        /// reason — the change tests above do the real work.</summary>
        const float ORDER_BACKSTOP_S = 30f;

        struct LastOrder { public Vector3 Dest; public float At; }
        static readonly Dictionary<Unit, LastOrder> _ordered = new Dictionary<Unit, LastOrder>();

        /// <summary>
        /// THE GROUP IS THE UNIT OF COMMAND, NOT THE UNIT.
        ///
        /// DrMuck: "we think in army groups not in single units." The executor
        /// did not — it held a List&lt;Unit&gt; and gave each member the same
        /// destination independently, which is not a group, it is a crowd with a
        /// shared appointment. Wasps move at 35 and Behemoths at 9, so what
        /// arrived was a queue, and a queue arrives in the order that gets it
        /// killed: fastest and flimsiest first.
        ///
        /// A group needs three things the list did not have — a POSITION, a
        /// SPREAD, and the discipline to wait. All three come from the centroid.
        /// </summary>
        static void Cohesion(Battalion bat, out Vector3 centre, out float spread)
        {
            centre = Vector3.zero;
            spread = 0f;
            int n = 0;
            for (int i = 0; i < bat.Units.Count; i++)
            {
                Vector3 p;
                try { p = bat.Units[i].transform.position; } catch { continue; }
                centre += p; n++;
            }
            if (n == 0) return;
            centre /= n;

            // Worst straggler rather than the average: the group is only as
            // together as the member furthest from it, and averaging hides one
            // Behemoth half a map behind four Wasps.
            for (int i = 0; i < bat.Units.Count; i++)
            {
                Vector3 p;
                try { p = bat.Units[i].transform.position; } catch { continue; }
                float dx = p.x - centre.x, dz = p.z - centre.z;
                float d = dx * dx + dz * dz;
                if (d > spread) spread = d;
            }
            spread = Mathf.Sqrt(spread);
        }

        /// <summary>How strung out a group may get before it stops advancing and
        /// closes up. Generous enough that ordinary pathing noise does not stall
        /// it, tight enough that the tail is in the same fight as the head.
        ///
        /// A FLAT RADIUS IS WRONG FOR A LARGE ARMY. Two hundred and twenty metres
        /// is right for a dozen units and physically impossible for two hundred —
        /// they do not fit. Held flat it stalled a 186-unit push for a whole
        /// round. CohesionRadius() scales it with the headcount.</summary>
        const float COHESION_M = 220f;

        /// <summary>Ground one unit needs. A blob of N units occupies about
        /// sqrt(N) x this across, so the allowance grows with the square root of
        /// the headcount rather than linearly.</summary>
        const float UNIT_FOOTPRINT_M = 26f;

        /// <summary>Longest a battalion may spend closing up before it advances
        /// regardless. The escape hatch: cohesion is a preference, arriving is
        /// the point, and no amount of tidiness is worth a force that never
        /// leaves. Reached only when something is preventing the close — a
        /// straggler stuck on terrain, or reinforcements arriving faster than
        /// the tail can walk.</summary>
        const float CLOSING_PATIENCE_S = 45f;

        /// <summary>How tight this particular battalion must be. Scales with
        /// headcount, so a large army is allowed to be large.</summary>
        static float CohesionRadius(Battalion bat)
        {
            int n = bat.Units.Count;
            if (n <= 1) return COHESION_M;
            return Mathf.Max(COHESION_M, Mathf.Sqrt(n) * UNIT_FOOTPRINT_M);
        }

        /// <summary>Is this unit in a fight? Nothing we want is worth
        /// interrupting one — the order can wait until it resolves.</summary>
        static bool IsFighting(Unit u)
        {
            try { return u.Target != null; } catch { return false; }
        }

        /// <summary>Orders issued this round, so the cost of the executor is
        /// visible rather than inferred from a round summary.</summary>
        internal static int OrdersIssued;

        static void IssueOrders(Team team, float now)
        {
            for (int b = 0; b < Battalions.Count; b++)
            {
                var bat = Battalions[b];

                if (Standing(bat.Kind))
                {
                    // The garrison is not sent anywhere, it is KEPT. Only units
                    // that have drifted off the leash are recalled, so a standing
                    // guard is not re-ordered into a huddle every five seconds.
                    if (bat.Rally == Vector3.zero) continue;
                    for (int i = 0; i < bat.Units.Count; i++)
                    {
                        var u = bat.Units[i];
                        if (u == null || u.IsDestroyed) continue;
                        Vector3 p;
                        try { p = u.transform.position; } catch { continue; }
                        float dx = p.x - bat.Rally.x, dz = p.z - bat.Rally.z;
                        // Off the leash and not busy: come home. A garrison unit
                        // that is fighting is doing its job where it stands.
                        if (dx * dx + dz * dz > HOME_LEASH_M * HOME_LEASH_M
                            && NeedsOrder(u, bat.Rally, now))
                            IssueMove(u, bat.Rally);
                    }
                    continue;
                }

                if (bat.Phase == State.Forming) continue;     // holds, by design

                if (bat.Phase == State.Ready)
                {
                    bat.Phase = State.Committed;
                    bat.CommittedAt = now;
                    MelonLogger.Msg($"[BATTALION] {bat.Name} committing {bat.Units.Count} units " +
                                    $"(value {bat.Value}/{bat.RequiredValue}) to " +
                                    $"({bat.Objective.x:F0},{bat.Objective.z:F0})");
                }

                if (bat.Phase == State.Committed && ShouldRelease(bat, now))
                {
                    bat.Phase = State.Returning;
                    bat.Rally = HomeOf(team);
                    MelonLogger.Msg($"[BATTALION] {bat.Name} released at " +
                                    $"({bat.Objective.x:F0},{bat.Objective.z:F0})");
                }

                Vector3 dest = bat.Phase == State.Returning ? bat.Rally : bat.Objective;
                if (dest == Vector3.zero) continue;

                // MOVE AS A BODY. If the group is strung out, the destination for
                // everyone becomes its own centre: the head stops, the tail
                // closes, and the whole thing resumes together on the next pass.
                // One rule, and it produces staging, pace-matching and regroup
                // after a fight without any of them being written separately.
                Cohesion(bat, out Vector3 centre, out float spread);
                float allow = CohesionRadius(bat);
                bool closingUp = spread > allow && bat.Units.Count > 1;
                // The escape hatch. Without it a group that cannot close never
                // advances, and "cannot close" is the normal case for an army
                // being reinforced from across the map.
                if (closingUp)
                {
                    if (bat.ClosingSince <= 0f) bat.ClosingSince = now;
                    else if (now - bat.ClosingSince > CLOSING_PATIENCE_S)
                    {
                        closingUp = false;
                        if (!bat.PatienceLogged)
                        {
                            bat.PatienceLogged = true;
                            MelonLogger.Msg($"[BATTALION] {bat.Name} spread {spread:F0}m over " +
                                            $"allowance {allow:F0}m for {CLOSING_PATIENCE_S:F0}s — " +
                                            $"advancing anyway; arriving beats tidiness");
                        }
                    }
                }
                else { bat.ClosingSince = 0f; bat.PatienceLogged = false; }
                Vector3 target = closingUp ? centre : dest;
                if (closingUp != bat.ClosingUp)
                {
                    bat.ClosingUp = closingUp;
                    MelonLogger.Msg($"[BATTALION] {bat.Name} " +
                                    (closingUp
                                        ? $"strung out over {spread:F0}m — closing up before advancing"
                                        : $"together again ({spread:F0}m) — advancing"));
                }

                int arrived = 0;
                for (int i = 0; i < bat.Units.Count; i++)
                {
                    var u = bat.Units[i];
                    if (u == null || u.IsDestroyed) continue;
                    Vector3 p;
                    try { p = u.transform.position; } catch { continue; }
                    float dx = p.x - dest.x, dz = p.z - dest.z;
                    // Arrived units are left alone rather than re-ordered onto
                    // the same spot — a unit standing on its objective is a unit
                    // free to shoot at whatever is in front of it.
                    if (dx * dx + dz * dz < ARRIVED_M * ARRIVED_M) { arrived++; continue; }

                    // While closing up, whoever is already near the centre has
                    // nowhere to be. Ordering them at it would jostle the group
                    // in place and interrupt anyone shooting.
                    if (closingUp)
                    {
                        float cx = p.x - centre.x, cz = p.z - centre.z;
                        if (cx * cx + cz * cz < ARRIVED_M * ARRIVED_M) continue;
                    }
                    if (NeedsOrder(u, target, now)) IssueMove(u, target);
                }

                // Home and dissolved: units return to the free pool and the next
                // tick may put them somewhere more useful.
                if (bat.Phase == State.Returning && arrived >= bat.Units.Count)
                {
                    bat.Units.Clear();
                    bat.Phase = State.Forming;
                }
            }
        }

        /// <summary>
        /// Released by the condition that raised it, never by a timer.
        ///
        /// A defence ends when the threat that created it is gone. A push ends
        /// when the posture that created it does — MissionPlanner owns that
        /// judgement and it is not second-guessed here, which is the whole point
        /// of moving intent out.
        /// </summary>
        static bool ShouldRelease(Battalion bat, float now)
        {
            if (now - bat.CommittedAt < ASSIGNMENT_DWELL_S) return false;
            if (bat.Kind == MissionPlanner.Kind.Push)
                return MissionPlanner.Current != MissionPlanner.Posture.Push;

            float threat = 0f;
            try { threat = Perception.ThreatMap.ThreatNear(bat.Objective, 300f); } catch { }
            return threat <= 0f;
        }

        /// <summary>Does this unit need telling again?</summary>
        static bool NeedsOrder(Unit u, Vector3 dest, float now)
        {
            if (IsFighting(u)) return false;

            if (!_ordered.TryGetValue(u, out var last)) return true;

            float dx = last.Dest.x - dest.x, dz = last.Dest.z - dest.z;
            if (dx * dx + dz * dz > ORDER_MOVED_M * ORDER_MOVED_M) return true;

            // Standing still well short of where it was sent: the order was
            // lost, or the unit finished a fight and never resumed.
            if (now - last.At >= ORDER_BACKSTOP_S)
            {
                bool moving = true;
                try { moving = u.IsMoving; } catch { }
                if (!moving) return true;
            }
            return false;
        }

        /// <summary>
        /// HOW MUCH ORDER TRAFFIC WE ACTUALLY GENERATE.
        ///
        /// DrMuck: "serverfps tanks. How many re-orders are done to the alien
        /// military units?" A fair suspicion — an earlier round issued 25,926
        /// move orders across 506 units, and re-ordering a unit that has stopped
        /// to fight is both a performance cost and a tactical one. There was no
        /// counter, so the question could only be answered by inference.
        ///
        /// Counted per minute and against the size of the army, because the raw
        /// total says nothing: two hundred orders a minute is nothing for four
        /// hundred units and a great deal for twenty.
        /// </summary>
        static int _ordersIssued, _ordersTotal;
        static float _orderWindowAt;

        internal static string OrderRateReport(int armyUnits)
        {
            float now = Time.time;
            float span = Mathf.Max(1f, now - _orderWindowAt);
            float perMin = _ordersIssued * 60f / span;
            float perUnitMin = armyUnits > 0 ? perMin / armyUnits : 0f;
            _ordersIssued = 0;
            _orderWindowAt = now;
            return $"orders {perMin:F0}/min ({perUnitMin:F2} per unit per min, {_ordersTotal} total)";
        }

        internal static void ResetOrderStats() { _ordersIssued = _ordersTotal = 0; _orderWindowAt = 0f; }

        static void IssueMove(Unit u, Vector3 pos)
        {
            _ordered[u] = new LastOrder { Dest = pos, At = Time.time };
            OrdersIssued++;
            try
            {
                PlannerOverride = true;
                u.OnMoveOrder(pos, AgentMoveSpeed.Fast);
                _ordersIssued++; _ordersTotal++;
            }
            catch (Exception ex) { MelonLogger.Warning("[BATTALION] OnMoveOrder threw: " + ex.Message); }
            finally { PlannerOverride = false; }
        }

        /// <summary>
        /// Is this unit in a battalion that is currently ordering it?
        ///
        /// A SCOUT IS NEVER OURS, whatever the roster says. FreeCombatUnits asks
        /// ScoutPlanner before taking anything, but scouts are recruited
        /// CONTINUOUSLY and from the same pool — so a Crab the garrison absorbed
        /// at minute four gets conscripted at minute nine and is then held by
        /// both. The battalion never orders it anywhere, and the move-order
        /// prefix below refuses every order ScoutPlanner gives it, because that
        /// prefix only recognises the battalion manager's own override flag.
        ///
        /// The unit stops moving and times out at its waypoint, forever.
        /// Measured on the 2026-08-08 soak: reached=3, timedOut=769, 17% of the
        /// map explored at twenty-eight minutes, nothing discovered, therefore no
        /// defence tasks and no push — the whole military layer starved of
        /// information by an ownership collision.
        ///
        /// Checked here rather than only in Prune because this is what the
        /// Harmony prefix calls, and it has to be right on the tick a unit
        /// changes hands, not five seconds later.
        /// </summary>
        internal static bool Owns(Unit u)
        {
            if (u == null || !MilitaryConfig.Enabled || !MilitaryConfig.Execute) return false;
            try { if (ScoutPlanner.IsScout(u)) return false; } catch { }
            for (int b = 0; b < Battalions.Count; b++)
                if (Battalions[b].Units.Contains(u)) return true;
            return false;
        }

        /// <summary>
        /// Vanilla re-tasks units continuously, so a battalion that only issued
        /// orders would watch them wander off between reissues. Same guard the
        /// scouts use, and it stacks: Harmony skips the original if EITHER prefix
        /// returns false. Inert while military.execute is off, because Owns is.
        /// </summary>
        [HarmonyPatch(typeof(Unit), nameof(Unit.OnMoveOrder))]
        static class Patch_Unit_OnMoveOrder_Battalion
        {
            static bool Prefix(Unit __instance)
            {
                try
                {
                    if (__instance == null || !Owns(__instance)) return true;
                    if (PlannerOverride) return true;
                    try
                    {
                        var t = __instance.Team;
                        if (t != null && !AIManager.IsCommanderEnabled(t)) return true;
                    }
                    catch { }
                    return false;
                }
                catch { return true; }
            }
        }

        /// <summary>
        /// VANILLA WAS STILL COMMANDING OUR ARMY, THROUGH THE ONE DOOR WE LEFT OPEN.
        ///
        /// The move-order prefix above stops the game AI walking a committed unit
        /// away. It says nothing about ATTACK orders, and the game's own commander
        /// keeps running for the alien team — so it was picking units out of our
        /// battalions and sending them at whatever it fancied, one group at a
        /// time. Measured on the 2026-08-08 round: 25,926 move orders and
        /// **1,311 attack orders**, all attributed to the AI commander, on a team
        /// where THIS MOD ISSUES NO ATTACK ORDERS AT ALL. Every one of those 1,311
        /// was vanilla, and DrMuck watching the replay saw the result — units
        /// trickling toward the enemy HQ one at a time.
        ///
        /// That is the stream. It was never our push arriving in speed order; it
        /// was the game AI pulling units out of formation and sending them
        /// individually, which is also why they died piecemeal.
        ///
        /// Units are STRIPPED from the group rather than the call being blocked,
        /// the same way the shrimps are protected: a group that also holds units
        /// we do not own should still get its order. Ours simply stop being in it.
        /// </summary>
        [HarmonyPatch(typeof(AIGroup), nameof(AIGroup.OnAttackOrder))]
        static class Patch_AIGroup_OnAttackOrder_Battalion
        {
            static readonly List<Unit> _scratch = new List<Unit>(16);

            static void Prefix(AIGroup __instance)
            {
                try
                {
                    if (__instance == null) return;
                    if (!MilitaryConfig.Enabled || !MilitaryConfig.Execute) return;
                    if (!MilitaryConfig.BlockVanillaAttackOrders) return;
                    var units = __instance.Units;
                    if (units == null || units.Count == 0) return;

                    _scratch.Clear();
                    for (int i = 0; i < units.Count; i++)
                    {
                        var u = units[i];
                        if (u != null && Owns(u)) _scratch.Add(u);
                    }
                    if (_scratch.Count == 0) return;

                    for (int i = 0; i < _scratch.Count; i++)
                    {
                        try { if (__instance.RemoveUnit(_scratch[i])) StrippedFromVanillaAttack++; }
                        catch { }
                    }
                    _scratch.Clear();
                }
                catch (Exception ex)
                { MelonLogger.Warning("[BATTALION] attack-order prefix threw: " + ex.Message); }
            }
        }

        /// <summary>How many times the game AI tried to take a unit we own into an
        /// attack order. If this is large the two layers are fighting, and the
        /// army's behaviour is not ours to explain.</summary>
        internal static int StrippedFromVanillaAttack;

        // ---- Helpers ---------------------------------------------------------

        static int ValueOf(List<Unit> units)
        {
            int v = 0;
            for (int i = 0; i < units.Count; i++)
            {
                var u = units[i];
                if (u?.ObjectInfo == null) continue;
                v += Perception.UnitValues.CostOf(u.ObjectInfo.DisplayName);
            }
            return v;
        }

        static Unit Nearest(List<Unit> pool, Vector3 to)
        {
            Unit best = pool[0]; float bd = float.MaxValue;
            for (int i = 0; i < pool.Count; i++)
            {
                var u = pool[i];
                Vector3 p;
                try { p = u.transform.position; } catch { continue; }
                float dx = p.x - to.x, dz = p.z - to.z;
                float d = dx * dx + dz * dz;
                if (d < bd) { bd = d; best = u; }
            }
            return best;
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

        /// <summary>Cash value of the force actually committed to the push, or 0
        /// if there is none. The retreat rule needs THIS and not team-wide army
        /// value: a push can lose every unit it took while production at home
        /// keeps the team total climbing, and the abort would never fire.</summary>
        internal static int PushForceValue()
        {
            for (int b = 0; b < Battalions.Count; b++)
                if (Battalions[b].Kind == MissionPlanner.Kind.Push)
                    return Battalions[b].Value;
            return 0;
        }

        /// <summary>Units under our command right now — for the status command
        /// and the round summary.</summary>
        internal static int UnitsCommanded()
        {
            int n = 0;
            for (int b = 0; b < Battalions.Count; b++) n += Battalions[b].Units.Count;
            return n;
        }

        static void MaybeLog(float now)
        {
            if (now - _lastLogAt < 30f) return;
            _lastLogAt = now;
            if (Battalions.Count == 0) return;

            var sb = new System.Text.StringBuilder("[BATTALION] ");
            for (int i = 0; i < Battalions.Count; i++)
            {
                var b = Battalions[i];
                sb.Append(b.Name).Append(' ')
                  .Append(b.Phase).Append(' ').Append(b.Units.Count).Append("u val ")
                  .Append(b.Value);
                if (b.RequiredValue > 0) sb.Append('/').Append(b.RequiredValue);
                if (b.Kind != MissionPlanner.Kind.Garrison)
                {
                    sb.Append(" -> (").Append(b.Objective.x.ToString("F0")).Append(',')
                      .Append(b.Objective.z.ToString("F0")).Append(')');
                    if (b.Units.Count > 1)
                    {
                        Cohesion(b, out _, out float sp);
                        sb.Append(" spread ").Append(sp.ToString("F0")).Append('m');
                        if (b.ClosingUp) sb.Append(" closing");
                    }
                }
                sb.Append(" | ");
            }
            if (_releasedToScouts > 0) sb.Append("releasedToScouts=").Append(_releasedToScouts).Append(' ');
            if (StrippedFromVanillaAttack > 0)
                sb.Append("vanillaAttacksBlocked=").Append(StrippedFromVanillaAttack).Append(' ');
            sb.Append("ordersIssued=").Append(OrdersIssued).Append(' ');
            if (!MilitaryConfig.Execute) sb.Append("[shadow — no orders issued]");
            MelonLogger.Msg(sb.ToString());
        }
    }
}
