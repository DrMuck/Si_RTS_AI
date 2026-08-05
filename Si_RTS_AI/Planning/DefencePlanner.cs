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
    /// SHADOW BY DEFAULT. It publishes decisions and logs them; MilitaryManager
    /// executes only when DefenceExecute is switched on. Every behavioural rule
    /// in this project that skipped that step had to be reverted.
    /// </summary>
    internal static class DefencePlanner
    {
        internal static bool Enabled;         // compute + log
        internal static bool Execute;         // actually order units

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

        /// <summary>Units held at home no matter what the front asks for.</summary>
        internal static int GarrisonFloor;

        /// <summary>Enemy value seen near the Nest, the thing the floor is sized
        /// against. A garrison exists to beat what actually shows up, not a
        /// number somebody picked.</summary>
        internal static float PeakHomeThreat;

        const float TICK_S              = 5f;
        const float ASSET_THREAT_RADIUS = 300f;
        const float HOME_RADIUS_M       = 600f;

        /// <summary>Smallest garrison worth having. One unit cannot stop a raid;
        /// this is the floor under the floor, and everything above it is sized
        /// from what has actually come at us.</summary>
        const int   MIN_GARRISON = 4;

        /// <summary>Threat-to-units conversion for the garrison. Deliberately
        /// crude — until combat.jsonl says what an exchange actually costs, any
        /// precision here would be invented. Revisit with that data.</summary>
        const float THREAT_PER_DEFENDER = 25f;

        static float _lastTickAt, _lastLogAt;

        internal static void ResetForNewRound()
        {
            Tasks.Clear();
            GarrisonFloor = MIN_GARRISON;
            PeakHomeThreat = 0f;
            _lastTickAt = _lastLogAt = 0f;
        }

        internal static void Tick(Team team)
        {
            if (!Enabled || team == null) return;
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
        /// </summary>
        static void UpdateGarrison(Vector3 nest)
        {
            if (nest == Vector3.zero) return;
            float t = 0f;
            try { t = Perception.ThreatMap.ThreatNear(nest, HOME_RADIUS_M); } catch { }
            if (t > PeakHomeThreat) PeakHomeThreat = t;
            GarrisonFloor = Mathf.Max(MIN_GARRISON,
                                      Mathf.CeilToInt(PeakHomeThreat / THREAT_PER_DEFENDER));
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

            var sb = new System.Text.StringBuilder("[DEFENCE] garrison=");
            sb.Append(GarrisonFloor).Append(" (peak home threat ")
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
            if (!Execute) sb.Append("  [shadow — no orders issued]");
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
