using MelonLoader;
using Silica;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Si_RTS_AI.Planning
{
    /// <summary>
    /// WHERE PRODUCERS GO, AND WHAT EACH ONE IS FOR.
    ///
    /// DrMuck, after watching the army sit at spawn: "It is about a good unit
    /// composition. Overproducing and sitting ducks at one location is not quite
    /// helpful... Where, when and how many of producer to place. and then where
    /// to build." And the strategic point underneath it:
    ///
    ///   "only a small army is needed to defend the nest when the enemy is
    ///    pressured by our army at the forward operating base and comes not
    ///    through"
    ///
    /// That inverts the allocation the layer had. Home was funded first and
    /// everything else got the remainder, so the bot spent its army standing
    /// still — which is exactly what a defence-first rule buys you. Pressure at
    /// the right place IS defence, and it is cheaper.
    ///
    /// THIS IS THE BLUEPRINT PATTERN, AGAIN. Rule 6.5 — "Phase 2 is a blueprint,
    /// not a series of choices" — was learned when greedy per-tick placement had
    /// no global picture and every frontier point claimed its nearest patch
    /// forever. Producer placement had the same shape: one rule, "put it at the
    /// Nest", then "put it near the loudest threat". Neither could express what
    /// a producer is FOR, so neither could tell a base defence from a forward
    /// operating base.
    ///
    /// So sites are PLANNED, with a purpose attached, and refreshed:
    ///
    ///   Home      one, near the Nest. The fallback and the floor.
    ///   Cover     behind an earning expansion that nothing is protecting.
    ///             Units appear where the economy already is, so the walk is
    ///             zero and the site defends itself as a side effect.
    ///   Forward   between our frontier and their discovered base. The FOB —
    ///             pressure that keeps them off our ground rather than a wall
    ///             on it.
    ///
    /// WHEN, which is the part that stops this being reckless. DrMuck: "only if
    /// there is enough time to build before harassment, or if there are already
    /// own units for defense." A Forward site is planned only where one of those
    /// two holds, and both are things we can actually check: the threat field
    /// says whether the ground is quiet, and the battalion roster says whether
    /// anything of ours is standing near it.
    ///
    /// HOW MANY is not decided here — MilitaryProduction owns that, gated on
    /// income rate and on producers being saturated. This decides where the next
    /// one goes when the answer is yes.
    /// </summary>
    internal static class MilitaryBlueprint
    {
        internal enum Purpose { Home, Cover, Forward }

        internal class Site
        {
            public Vector3 Pos;
            public Purpose Purpose;
            public float   Score;
            public string  Why = "";
        }

        /// <summary>Planned producer sites, best first. Read by MilitaryProduction.</summary>
        internal static readonly List<Site> Sites = new List<Site>(8);

        const float TICK_S = 10f;
        const float LOG_S  = 60f;

        /// <summary>How far from a Bio Cache a producer still counts as covering
        /// it. Roughly the distance a defender can cross before an attacker
        /// finishes the job.</summary>
        const float COVER_RADIUS_M = 400f;

        /// <summary>Ground quiet enough to build on unattended. Threat is the
        /// accumulating danger field, so this is deliberately near zero — "we
        /// have not seen anything here lately" rather than "it is safe".</summary>
        const float QUIET_THREAT = 40f;

        /// <summary>A Forward site sits this far along the line from our frontier
        /// toward their base. Not at their door: a producer inside their defence
        /// envelope is a donation, and MILITARY_TACTICS §6 already says the
        /// weakly-defended half is what makes a raid something other than one.</summary>
        const float FORWARD_FRACTION = 0.35f;

        static float _lastTickAt, _lastLogAt;

        internal static void ResetForNewRound()
        {
            Sites.Clear();
            _lastTickAt = _lastLogAt = 0f;
        }

        internal static void Tick(Team team)
        {
            if (!MilitaryConfig.Enabled || team == null) return;
            float now = Time.time;
            if (now - _lastTickAt < TICK_S) return;
            _lastTickAt = now;

            try
            {
                Vector3 nest = HomeOf(team);
                if (nest == Vector3.zero) return;
                Plan(team, nest);
                MaybeLog(now);
            }
            catch (Exception ex) { MelonLogger.Warning("[MIL/BP] tick threw: " + ex.Message); }
        }

        static void Plan(Team team, Vector3 nest)
        {
            Sites.Clear();
            var existing = ProducerPositions(team);

            // ---- Home. Always planned, always last in value ------------------
            //
            // It is the floor rather than the goal. A producer at the Nest has
            // the longest walk to anywhere that matters, and it exists so the
            // layer is never without one.
            Sites.Add(new Site
            {
                Pos = nest, Purpose = Purpose.Home, Score = 1f,
                Why = "fallback at the Nest",
            });

            // ---- Cover. Behind an earning expansion nothing is protecting ----
            //
            // Ranked by what the site EARNS, not by what is attacking it. A
            // producer is a standing investment; putting it where the last raid
            // happened chases noise, putting it where the money is does not.
            Perception.BcIncome.ForEach((pos, lifetime, recent) =>
            {
                if (recent <= 0) return;
                if (NearAny(existing, pos, COVER_RADIUS_M)) return;   // already covered
                float dHome = Vector3.Distance(pos, nest);
                if (dHome < COVER_RADIUS_M) return;                   // the Nest covers it

                // Behind the site relative to home, so it is not the first thing
                // an attacker meets.
                Vector3 at = Vector3.Lerp(pos, nest, 0.25f);
                Sites.Add(new Site
                {
                    Pos = at, Purpose = Purpose.Cover, Score = 10f + recent / 1000f,
                    Why = $"covers ground earning {recent} at " +
                          $"({pos.x:F0},{pos.z:F0}), {dHome:F0}m out",
                });
            });

            // ---- Forward. Toward their base, if the ground allows it ---------
            if (TryForward(team, nest, existing, out var fwd)) Sites.Add(fwd);

            Sites.Sort((a, b) => b.Score.CompareTo(a.Score));
        }

        /// <summary>
        /// The FOB. Planned only against a base we have actually SEEN, on ground
        /// that is either quiet or already held by us.
        ///
        /// Scored above Cover on purpose. Pressure at their door keeps them off
        /// our ground, which is the cheaper way to hold it — but only when it can
        /// be established, which is why the two conditions come first and the
        /// score comes second.
        /// </summary>
        static bool TryForward(Team team, Vector3 nest, List<Vector3> existing, out Site site)
        {
            site = null;
            Vector3 target = Vector3.zero;
            int bestCost = 0;
            try
            {
                Perception.ThreatMap.ForEachKnown(k =>
                {
                    if (k.Cost <= bestCost) return;
                    bestCost = k.Cost; target = k.Pos;
                });
            }
            catch { }
            if (bestCost <= 0 || target == Vector3.zero) return false;

            Vector3 at = Vector3.Lerp(nest, target, FORWARD_FRACTION);
            if (NearAny(existing, at, COVER_RADIUS_M)) return false;

            float threat = 0f;
            try { threat = Perception.ThreatMap.ThreatNear(at, COVER_RADIUS_M); } catch { }
            bool quiet = threat <= QUIET_THREAT;
            bool covered = OwnForceNear(at, COVER_RADIUS_M);

            // DrMuck's condition, literally: time to build, OR units already
            // there to hold it. Neither means a building handed to the enemy.
            if (!quiet && !covered) return false;

            site = new Site
            {
                Pos = at, Purpose = Purpose.Forward, Score = 50f,
                Why = quiet
                    ? $"forward toward ({target.x:F0},{target.z:F0}), ground quiet (threat {threat:F0})"
                    : $"forward toward ({target.x:F0},{target.z:F0}), covered by our own force",
            };
            return true;
        }

        /// <summary>Is anything of ours standing near this ground? A battalion
        /// is asked rather than the unit list, because a unit passing through is
        /// not cover and a battalion assigned there is.</summary>
        internal static bool OwnForceNear(Vector3 pos, float radiusM)
        {
            float r2 = radiusM * radiusM;
            try
            {
                foreach (var b in BattalionManager.Battalions)
                {
                    if (b.Units.Count == 0) continue;
                    for (int i = 0; i < b.Units.Count; i++)
                    {
                        Vector3 p;
                        try { p = b.Units[i].transform.position; } catch { continue; }
                        float dx = p.x - pos.x, dz = p.z - pos.z;
                        if (dx * dx + dz * dz <= r2) return true;
                    }
                }
            }
            catch { }
            return false;
        }

        /// <summary>True while we hold ground forward of the Nest — which is
        /// what lets the home garrison be smaller. MissionPlanner reads it.</summary>
        internal static bool HoldingForward
        {
            get
            {
                for (int i = 0; i < Sites.Count; i++)
                    if (Sites[i].Purpose == Purpose.Forward &&
                        OwnForceNear(Sites[i].Pos, COVER_RADIUS_M)) return true;
                return false;
            }
        }

        /// <summary>Where the next producer should go, or the Nest if nothing is
        /// planned. Purpose comes back with it so the log can say why.</summary>
        internal static Site NextSite(Team team)
        {
            if (Sites.Count > 0) return Sites[0];
            return new Site { Pos = HomeOf(team), Purpose = Purpose.Home, Why = "no plan yet" };
        }

        static List<Vector3> ProducerPositions(Team team)
        {
            var list = new List<Vector3>(8);
            try
            {
                var structs = team.Structures;
                if (structs == null) return list;
                for (int i = 0; i < structs.Count; i++)
                {
                    var s = structs[i];
                    if (s?.ObjectInfo == null || s.IsDestroyed) continue;
                    string n = s.ObjectInfo.DisplayName ?? "";
                    if (n.IndexOf("Spawning Cyst", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        n.IndexOf("Grand Spawner", StringComparison.OrdinalIgnoreCase) >= 0)
                        list.Add(s.transform.position);
                }
            }
            catch { }
            return list;
        }

        static bool NearAny(List<Vector3> pts, Vector3 p, float radiusM)
        {
            float r2 = radiusM * radiusM;
            for (int i = 0; i < pts.Count; i++)
            {
                float dx = pts[i].x - p.x, dz = pts[i].z - p.z;
                if (dx * dx + dz * dz <= r2) return true;
            }
            return false;
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
            if (Sites.Count == 0) return;
            var sb = new System.Text.StringBuilder("[MIL/BP] ");
            for (int i = 0; i < Sites.Count && i < 4; i++)
                sb.Append(Sites[i].Purpose).Append(" (")
                  .Append(Sites[i].Pos.x.ToString("F0")).Append(',')
                  .Append(Sites[i].Pos.z.ToString("F0")).Append(") ")
                  .Append(Sites[i].Why).Append(" | ");
            sb.Append(HoldingForward ? "holding forward" : "no forward hold");
            MelonLogger.Msg(sb.ToString());
        }
    }
}
