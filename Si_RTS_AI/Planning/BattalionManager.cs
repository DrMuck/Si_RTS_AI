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
    /// A battalion is a named set of units with one assignment, a strength, and a
    /// state. Strategy hands it a task; it decides whether it is ready, and holds
    /// what it has been given.
    ///
    /// STRENGTH IS MEASURED IN CASH, not unit count. Fifteen Crabs and fifteen
    /// Behemoths are not the same army, and combat.jsonl records losses in value —
    /// so a critical-mass rule expressed in value can be calibrated against real
    /// exchanges later, and one expressed in bodies never can.
    ///
    /// SHADOW BY DEFAULT. It forms battalions, judges readiness and logs what it
    /// would do. It issues no orders until DefenceExecute is on, so the grouping
    /// can be watched for a round before anything acts on it.
    /// </summary>
    internal static class BattalionManager
    {
        internal enum State { Forming, Ready, Committed, Returning }

        internal class Battalion
        {
            public string  Name;
            public string  Role;            // "garrison" | "response"
            public State   Phase;
            public Vector3 Rally;
            public Vector3 Objective;
            public readonly List<Unit> Units = new List<Unit>();
            public int   Value;             // cash value of the units in it
            public int   RequiredValue;     // what the task is judged to need
            public float AssignedAt;
            public float LastOrderAt;
            public float CommittedAt;
        }

        internal static readonly List<Battalion> Battalions = new List<Battalion>(4);

        const float TICK_S = 5f;

        /// <summary>How long a battalion keeps an assignment before it may be
        /// re-tasked. The commitment cost the spec asks for, expressed as time —
        /// a group that changes its mind every five seconds arrives nowhere.</summary>
        const float ASSIGNMENT_DWELL_S = 30f;

        /// <summary>Strength a response needs against the threat it is sent at.
        /// Above 1 because the defender should not be sent to trade evenly.
        /// Calibratable from combat.jsonl exchange ratios; a placeholder until
        /// there are any.</summary>
        const float STRENGTH_MARGIN = 1.5f;

        static float _lastTickAt, _lastLogAt;

        internal static void ResetForNewRound()
        {
            Battalions.Clear();
            _lastTickAt = _lastLogAt = 0f;
        }

        internal static void Tick(Team team)
        {
            if (!DefencePlanner.Enabled || team == null) return;
            float now = Time.time;
            if (now - _lastTickAt < TICK_S) return;
            _lastTickAt = now;

            try
            {
                Prune();
                var free = FreeCombatUnits(team);
                MaintainGarrison(team, free, now);
                MaintainResponses(free, now);
                Judge();
                if (DefencePlanner.Execute) IssueOrders(team, now);
                MaybeLog(now);
            }
            catch (Exception ex) { MelonLogger.Warning("[BATTALION] tick threw: " + ex.Message); }
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
            for (int b = Battalions.Count - 1; b >= 0; b--)
            {
                var bat = Battalions[b];
                for (int i = bat.Units.Count - 1; i >= 0; i--)
                {
                    var u = bat.Units[i];
                    if (u == null || u.IsDestroyed) bat.Units.RemoveAt(i);
                }
                if (bat.Units.Count == 0 && bat.Role != "garrison") Battalions.RemoveAt(b);
            }
        }

        // ---- Composition -----------------------------------------------------

        /// <summary>
        /// The garrison exists whether or not there is a threat, and is filled to
        /// DefencePlanner's floor before anything else is formed. It is not a
        /// battalion that competes for units; it is the reason the others have
        /// fewer.
        /// </summary>
        static void MaintainGarrison(Team team, List<Unit> free, float now)
        {
            var g = Battalions.Find(b => b.Role == "garrison");
            if (g == null)
            {
                g = new Battalion { Name = "garrison", Role = "garrison",
                                    Phase = State.Ready, AssignedAt = now };
                Battalions.Add(g);
            }
            g.Rally = g.Objective = HomeOf(team);

            int want = DefencePlanner.GarrisonFloor;
            while (g.Units.Count < want && free.Count > 0)
            {
                var u = Nearest(free, g.Rally);
                free.Remove(u);
                g.Units.Add(u);
            }
            g.Value = ValueOf(g.Units);
            g.RequiredValue = 0;
        }

        /// <summary>
        /// One battalion per threatened asset DefencePlanner ranked, in its
        /// order, taking whatever units are left. A response already assigned
        /// keeps its objective for ASSIGNMENT_DWELL_S even if the ranking moves —
        /// that is the hysteresis, and without it the army oscillates between two
        /// equally threatened sites and defends neither.
        /// </summary>
        static void MaintainResponses(List<Unit> free, float now)
        {
            var tasks = DefencePlanner.Tasks;
            int made = 0;
            for (int t = 0; t < tasks.Count && made < 3; t++)
            {
                if (tasks[t].Kind == "home") continue;      // the garrison has it
                made++;

                var bat = Battalions.Find(b => b.Role == "response" &&
                                               (b.Objective - tasks[t].Pos).sqrMagnitude < 300f * 300f);
                if (bat == null)
                {
                    // Anything already out and not yet dwelling can be re-aimed.
                    bat = Battalions.Find(b => b.Role == "response" &&
                                               now - b.AssignedAt > ASSIGNMENT_DWELL_S &&
                                               b.Phase != State.Committed);
                    if (bat == null)
                    {
                        bat = new Battalion { Name = "resp" + (Battalions.Count + 1),
                                              Role = "response", Phase = State.Forming };
                        Battalions.Add(bat);
                    }
                    bat.Objective = tasks[t].Pos;
                    bat.Rally     = tasks[t].Pos;
                    bat.AssignedAt = now;
                }

                bat.RequiredValue = Mathf.CeilToInt(tasks[t].Threat * STRENGTH_MARGIN);
                while (ValueOf(bat.Units) < bat.RequiredValue && free.Count > 0)
                {
                    var u = Nearest(free, bat.Objective);
                    free.Remove(u);
                    bat.Units.Add(u);
                }
                bat.Value = ValueOf(bat.Units);
            }
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
                if (bat.Role == "garrison") { bat.Phase = State.Ready; continue; }
                if (bat.Phase == State.Committed) continue;
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

        const float REISSUE_S    = 5f;
        const float ARRIVED_M    = 120f;
        const float HOME_LEASH_M = 250f;

        static void IssueOrders(Team team, float now)
        {
            for (int b = 0; b < Battalions.Count; b++)
            {
                var bat = Battalions[b];

                if (bat.Role == "garrison")
                {
                    // The garrison is not sent anywhere, it is KEPT. Only units
                    // that have drifted off the leash are recalled, so a standing
                    // guard is not re-ordered into a huddle every five seconds.
                    if (now - bat.LastOrderAt < REISSUE_S) continue;
                    bat.LastOrderAt = now;
                    for (int i = 0; i < bat.Units.Count; i++)
                    {
                        var u = bat.Units[i];
                        if (u == null || u.IsDestroyed) continue;
                        Vector3 p;
                        try { p = u.transform.position; } catch { continue; }
                        float dx = p.x - bat.Rally.x, dz = p.z - bat.Rally.z;
                        if (dx * dx + dz * dz > HOME_LEASH_M * HOME_LEASH_M)
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

                if (bat.Phase == State.Committed)
                {
                    // Released by the condition that raised it, not by a timer:
                    // the same threat reading that created the defence task.
                    float threat = 0f;
                    try { threat = Perception.ThreatMap.ThreatNear(bat.Objective, 300f); } catch { }
                    if (threat <= 0f && now - bat.CommittedAt > ASSIGNMENT_DWELL_S)
                    {
                        bat.Phase = State.Returning;
                        bat.Rally = HomeOf(team);
                        MelonLogger.Msg($"[BATTALION] {bat.Name} released — no threat left at " +
                                        $"({bat.Objective.x:F0},{bat.Objective.z:F0})");
                    }
                }

                if (now - bat.LastOrderAt < REISSUE_S) continue;
                bat.LastOrderAt = now;

                Vector3 dest = bat.Phase == State.Returning ? bat.Rally : bat.Objective;
                int arrived = 0;
                for (int i = 0; i < bat.Units.Count; i++)
                {
                    var u = bat.Units[i];
                    if (u == null || u.IsDestroyed) continue;
                    Vector3 p;
                    try { p = u.transform.position; } catch { continue; }
                    float dx = p.x - dest.x, dz = p.z - dest.z;
                    if (dx * dx + dz * dz < ARRIVED_M * ARRIVED_M) { arrived++; continue; }
                    IssueMove(u, dest);
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

        static void IssueMove(Unit u, Vector3 pos)
        {
            try
            {
                PlannerOverride = true;
                u.OnMoveOrder(pos, AgentMoveSpeed.Fast);
            }
            catch (Exception ex) { MelonLogger.Warning("[BATTALION] OnMoveOrder threw: " + ex.Message); }
            finally { PlannerOverride = false; }
        }

        /// <summary>Is this unit in a battalion that is currently ordering it?</summary>
        internal static bool Owns(Unit u)
        {
            if (u == null || !DefencePlanner.Execute) return false;
            for (int b = 0; b < Battalions.Count; b++)
                if (Battalions[b].Units.Contains(u)) return true;
            return false;
        }

        /// <summary>
        /// Vanilla re-tasks units continuously, so a battalion that only issued
        /// orders would watch them wander off between reissues. Same guard the
        /// scouts use, and it stacks: Harmony skips the original if EITHER prefix
        /// returns false. Inert while DefenceExecute is off, because Owns is.
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

        static void MaybeLog(float now)
        {
            if (now - _lastLogAt < 30f) return;
            _lastLogAt = now;
            if (Battalions.Count == 0) return;

            var sb = new System.Text.StringBuilder("[BATTALION] ");
            for (int i = 0; i < Battalions.Count; i++)
            {
                var b = Battalions[i];
                sb.Append(b.Name).Append('(').Append(b.Role).Append(") ")
                  .Append(b.Phase).Append(' ').Append(b.Units.Count).Append("u val ")
                  .Append(b.Value);
                if (b.RequiredValue > 0) sb.Append('/').Append(b.RequiredValue);
                if (b.Role == "response")
                    sb.Append(" -> (").Append(b.Objective.x.ToString("F0")).Append(',')
                      .Append(b.Objective.z.ToString("F0")).Append(')');
                sb.Append(" | ");
            }
            if (!DefencePlanner.Execute) sb.Append("[shadow — no orders issued]");
            MelonLogger.Msg(sb.ToString());
        }
    }
}
