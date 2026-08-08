using MelonLoader;
using Silica;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Si_RTS_AI.Planning
{
    /// <summary>
    /// DEFEND WHAT EARNS, NOT WHAT COST.
    ///
    /// The obvious way to rank assets is by what they cost to build, and it is
    /// wrong in the case that matters: a Bio Cache on a drained patch cost the
    /// same 500 as one feeding twenty shrimps, and defending it spends an army
    /// on ground the economy has already abandoned. DrMuck's spec says it
    /// directly — "do not spend defence on depleted nodes" — and now that
    /// BcIncome attributes deliveries per structure, "what has this earned in
    /// the last two minutes" is a number rather than a guess.
    ///
    /// So the ranking is (recent income) x (threat present). Both terms are
    /// measured, neither is tuned, and an asset that is not earning or not
    /// threatened does not appear.
    ///
    /// THE QUEEN IS NOT ON THAT LIST. She is a loss condition, not a
    /// high-priority asset, and a ranked list implies she can be outbid when the
    /// economy is desperate. She cannot. Home garrison is a FLOOR that is
    /// subtracted before anything else is allocated, and it does not fall
    /// because the front is hungry.
    ///
    /// WHAT IT DOES NOT DO, SINCE 2026-08-07: decide anything about units. It
    /// ranks ground and sizes the home floor. MissionPlanner turns that into
    /// missions and BattalionManager turns missions into orders. One owner per
    /// decision — the whole of that day was spent unpicking cases where a
    /// planner and an executor each kept their own copy of one decision and the
    /// copies drifted.
    /// </summary>
    internal static class DefencePlanner
    {
        /// <summary>A thing worth defending, and why.</summary>
        internal struct Task
        {
            public Vector3 Pos;
            public long    RecentIncome;   // what it delivered in the last window
            public float   Threat;         // enemy presence near it
            public float   Score;
            public string  Kind;           // "site" or "home"
        }

        internal static readonly List<Task> Tasks = new List<Task>(8);

        /// <summary>Cash worth standing at home, sized from the worst incursion
        /// actually seen. In cash rather than bodies because a battalion's
        /// strength is in cash — fifteen Crabs and fifteen Behemoths are not the
        /// same garrison, and a count cannot say so.</summary>
        internal static int GarrisonValue;

        /// <summary>Enemy value seen near the Nest, the thing the floor is sized
        /// against. A garrison exists to beat what actually shows up, not a
        /// number somebody picked.</summary>
        internal static float PeakHomeThreat;

        /// <summary>Which ground is earning and threatened moves slowly; the
        /// response to it does not have to.</summary>
        const float TICK_S              = 10f;
        const float ASSET_THREAT_RADIUS = 300f;
        const float HOME_RADIUS_M       = 600f;

        static float _lastTickAt, _lastLogAt;

        internal static void ResetForNewRound()
        {
            Tasks.Clear();
            GarrisonValue = 0;
            PeakHomeThreat = 0f;
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
                Vector3 nest = FindNest(team);
                UpdateGarrison(nest);
                RankAssets(nest);
                MaybeLog(now);
            }
            catch (Exception ex) { MelonLogger.Warning("[DEFENCE] tick threw: " + ex.Message); }
        }

        /// <summary>
        /// The home floor tracks the worst incursion seen, and does not decay
        /// back down within a round: having been raided once is evidence about
        /// this opponent, and forgetting it is how the second raid succeeds too.
        ///
        /// The other half of the floor lives in MissionPlanner, as a share of
        /// army value — this half answers "what has actually come at us", that
        /// half answers "and never less than this much of what we own".
        /// </summary>
        static void UpdateGarrison(Vector3 nest)
        {
            if (nest == Vector3.zero) return;
            try { PeakHomeThreat = Mathf.Max(PeakHomeThreat,
                                             Perception.ThreatMap.ThreatNear(nest, HOME_RADIUS_M)); } catch { }
            GarrisonValue = ValueFor(nest, HOME_RADIUS_M);
        }

        /// <summary>
        /// What a force sent here should be worth, in cash.
        ///
        /// It is the enemy's OWN cash within the radius times a margin, so there
        /// is no conversion constant between the game's units and ours and
        /// nothing to calibrate — both sides of the comparison are prices the
        /// game itself set. The previous version multiplied a threat reading by
        /// an invented cashPerThreat and asked for 1.4 million cash of garrison,
        /// which the whole army could not have paid at any point in the round.
        ///
        /// PEAK IS GONE FROM THE SIZING and stays only in the log. It was there so
        /// a raid that ended did not immediately empty the garrison, and it made
        /// one bad minute pin the requirement at its maximum for the rest of the
        /// round. The reserve already sits at home whenever there is no push, so
        /// the memory bought nothing and cost everything.
        /// </summary>
        internal static int ValueFor(Vector3 pos, float radiusM)
        {
            int enemy = 0;
            try { enemy = Perception.ThreatMap.ValueNear(pos, radiusM); } catch { }
            return Mathf.CeilToInt(enemy * MilitaryConfig.StrengthMargin);
        }

        static void RankAssets(Vector3 nest)
        {
            Tasks.Clear();

            // Home first, and not as a competitor — it is listed so the log shows
            // what the floor is protecting, but its units are already reserved.
            if (nest != Vector3.zero)
            {
                float ht = 0f;
                try { ht = Perception.ThreatMap.ThreatNear(nest, HOME_RADIUS_M); } catch { }
                if (ht > 0f)
                    Tasks.Add(new Task { Pos = nest, RecentIncome = 0, Threat = ht,
                                         Score = float.MaxValue, Kind = "home" });
            }

            Perception.BcIncome.ForEach((pos, lifetime, recent) =>
            {
                if (recent <= 0) return;                    // earning nothing lately
                float threat = 0f;
                try { threat = Perception.ThreatMap.ThreatNear(pos, ASSET_THREAT_RADIUS); } catch { }
                if (threat <= 0f) return;                   // nobody is coming
                Tasks.Add(new Task
                {
                    Pos = pos, RecentIncome = recent, Threat = threat,
                    Score = recent * threat, Kind = "site",
                });
            });

            Tasks.Sort((a, b) => b.Score.CompareTo(a.Score));
        }

        static void MaybeLog(float now)
        {
            if (now - _lastLogAt < 30f) return;
            _lastLogAt = now;
            if (Tasks.Count == 0 && PeakHomeThreat <= 0f) return;

            var sb = new System.Text.StringBuilder("[DEFENCE] garrisonValue=");
            sb.Append(GarrisonValue).Append(" (peak home threat ")
              .Append(PeakHomeThreat.ToString("F0")).Append(") threatened=")
              .Append(Tasks.Count);
            for (int i = 0; i < Tasks.Count && i < 4; i++)
            {
                var t = Tasks[i];
                sb.Append(" | ").Append(t.Kind).Append(" (")
                  .Append(t.Pos.x.ToString("F0")).Append(',').Append(t.Pos.z.ToString("F0"))
                  .Append(") earned ").Append(t.RecentIncome)
                  .Append(" threat ").Append(t.Threat.ToString("F0"));
            }
            if (!MilitaryConfig.Execute) sb.Append("  [shadow — no orders issued]");
            MelonLogger.Msg(sb.ToString());
        }

        static Vector3 FindNest(Team team)
        {
            try
            {
                var structs = team.Structures;
                if (structs == null) return Vector3.zero;
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
    }
}
