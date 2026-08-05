using MelonLoader;
using Silica;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace Si_RTS_AI.Perception
{
    /// <summary>
    /// DID THE ARMY TRADE WELL?
    ///
    /// The economy only started improving once rounds produced numbers. Military
    /// has none: "never trickle units in one by one" and "re-plan, but not every
    /// few seconds" are rules we happen to agree with and cannot currently check.
    /// A critical-mass threshold of 15 is a guess until something counts what was
    /// lost on each side when it was honoured and when it was not.
    ///
    /// So, before any engagement rule is written: one row per engagement.
    ///
    /// HOW AN ENGAGEMENT IS DETECTED, without a death event to hook. Every unit
    /// roster is sampled on a short cadence; a unit that was there and is now
    /// gone is a loss, recorded at its last known position. Losses cluster in
    /// space and time — one within ENGAGEMENT_RADIUS_M and ENGAGEMENT_WINDOW_S of
    /// a live engagement joins it, otherwise it opens a new one — and an
    /// engagement closes when it stops taking losses. That is fuzzy at the edges
    /// (a unit despawning for any other reason reads as a loss) and it needs no
    /// hooks, no patches, and nothing that can break when the game updates.
    ///
    /// Value is the unit's resource cost, so the exchange ratio is in the same
    /// currency as everything else the planner reasons about.
    ///
    /// SHADOW BY NATURE: this observes and writes. It changes no behaviour, so it
    /// can ship while an eco experiment is running and be trusted not to affect
    /// it.
    /// </summary>
    internal static class CombatLog
    {
        const float SAMPLE_INTERVAL_S    = 2f;
        const float ENGAGEMENT_RADIUS_M  = 250f;
        const float ENGAGEMENT_WINDOW_S  = 25f;

        /// <summary>Below this an "engagement" is a stray unit dying somewhere,
        /// not a fight worth a row.</summary>
        const int   MIN_LOSSES_TO_RECORD = 2;

        class Roster { public readonly Dictionary<int, (string name, Vector3 pos)> Units = new Dictionary<int, (string, Vector3)>(); }

        class Engagement
        {
            public Vector3 Centre;
            public float   OpenedAt, LastLossAt;
            public readonly Dictionary<string, int> LostByTeam   = new Dictionary<string, int>();
            public readonly Dictionary<string, int> ValueByTeam  = new Dictionary<string, int>();
            public readonly Dictionary<string, Dictionary<string,int>> Comp =
                new Dictionary<string, Dictionary<string,int>>();
        }

        static readonly Dictionary<Team, Roster> _rosters = new Dictionary<Team, Roster>();
        static readonly List<Engagement> _open = new List<Engagement>();
        static float _lastSampleAt;
        static int   _recorded;

        internal static void ResetForNewRound()
        {
            _rosters.Clear();
            _open.Clear();
            _lastSampleAt = 0f;
            _recorded = 0;
        }

        internal static void Tick()
        {
            float now = Time.time;
            if (now - _lastSampleAt < SAMPLE_INTERVAL_S) return;
            _lastSampleAt = now;

            try
            {
                foreach (var kv in Silica.AI.AIManager.Commanders)
                {
                    var team = kv.Key;
                    if (team == null) continue;
                    SampleTeam(team, now);
                }
            }
            catch (Exception ex) { MelonLogger.Warning("[COMBAT] sample threw: " + ex.Message); }

            CloseStale(now);
        }

        static void SampleTeam(Team team, float now)
        {
            if (!_rosters.TryGetValue(team, out var prev))
            {
                prev = new Roster();
                _rosters[team] = prev;
            }

            var seen = new Dictionary<int, (string, Vector3)>();
            var units = team.Units;
            if (units != null)
            {
                for (int i = 0; i < units.Count; i++)
                {
                    var u = units[i];
                    if (u == null || u.ObjectInfo == null || u.IsDestroyed) continue;
                    int id = u.GetInstanceID();
                    seen[id] = (u.ObjectInfo.DisplayName ?? "?", u.transform.position);
                }
            }

            // Present last time, absent now: a loss, at its last known position.
            string teamName = team.name ?? "?";
            foreach (var kvp in prev.Units)
            {
                if (seen.ContainsKey(kvp.Key)) continue;
                RecordLoss(teamName, kvp.Value.name, kvp.Value.pos, now);
            }

            prev.Units.Clear();
            foreach (var kvp in seen) prev.Units[kvp.Key] = kvp.Value;
        }

        static void RecordLoss(string teamName, string unitName, Vector3 pos, float now)
        {
            Engagement e = null;
            for (int i = 0; i < _open.Count; i++)
            {
                var o = _open[i];
                if (now - o.LastLossAt > ENGAGEMENT_WINDOW_S) continue;
                float dx = o.Centre.x - pos.x, dz = o.Centre.z - pos.z;
                if (dx * dx + dz * dz <= ENGAGEMENT_RADIUS_M * ENGAGEMENT_RADIUS_M) { e = o; break; }
            }
            if (e == null)
            {
                e = new Engagement { Centre = pos, OpenedAt = now };
                _open.Add(e);
            }

            e.LastLossAt = now;
            e.LostByTeam.TryGetValue(teamName, out int n);
            e.LostByTeam[teamName] = n + 1;
            e.ValueByTeam.TryGetValue(teamName, out int v);
            e.ValueByTeam[teamName] = v + CostOf(unitName);
            if (!e.Comp.TryGetValue(teamName, out var comp))
            { comp = new Dictionary<string, int>(); e.Comp[teamName] = comp; }
            comp.TryGetValue(unitName, out int c);
            comp[unitName] = c + 1;
        }

        static void CloseStale(float now)
        {
            for (int i = _open.Count - 1; i >= 0; i--)
            {
                if (now - _open[i].LastLossAt <= ENGAGEMENT_WINDOW_S) continue;
                var e = _open[i];
                _open.RemoveAt(i);

                int totalLosses = 0;
                foreach (var kv in e.LostByTeam) totalLosses += kv.Value;
                if (totalLosses < MIN_LOSSES_TO_RECORD) continue;
                Write(e);
            }
        }

        static void Write(Engagement e)
        {
            try
            {
                var sb = new StringBuilder(384);
                sb.Append('{');
                sb.Append("\"ts\":\"").Append(DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss")).Append("\",");
                sb.Append("\"map\":\"").Append(Esc(MapLayers.LayerReplay.CurrentMap)).Append("\",");
                sb.Append("\"roundT\":").Append(F(MapLayers.LayerReplay.CurrentRoundTime)).Append(',');
                sb.Append("\"durationS\":").Append(F(e.LastLossAt - e.OpenedAt)).Append(',');
                sb.Append("\"x\":").Append(F(e.Centre.x)).Append(",\"z\":").Append(F(e.Centre.z)).Append(',');

                sb.Append("\"lost\":{");
                bool first = true;
                foreach (var kv in e.LostByTeam)
                {
                    if (!first) sb.Append(','); first = false;
                    e.ValueByTeam.TryGetValue(kv.Key, out int val);
                    sb.Append('"').Append(Esc(kv.Key)).Append("\":{\"units\":").Append(kv.Value)
                      .Append(",\"value\":").Append(val).Append(",\"comp\":{");
                    bool f2 = true;
                    if (e.Comp.TryGetValue(kv.Key, out var comp))
                        foreach (var c in comp)
                        {
                            if (!f2) sb.Append(','); f2 = false;
                            sb.Append('"').Append(Esc(c.Key)).Append("\":").Append(c.Value);
                        }
                    sb.Append("}}");
                }
                sb.Append('}');

                // No mission types yet — the strategy planner does not exist, so
                // the field is present and honest rather than invented.
                sb.Append(",\"missionType\":\"unknown\"");
                sb.Append('}');

                string dir = Path.Combine("UserData", "RTSA");
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "combat.jsonl"), sb.ToString() + "\n");

                _recorded++;
                var line = new StringBuilder("[COMBAT] engagement at (")
                    .Append(e.Centre.x.ToString("F0")).Append(',').Append(e.Centre.z.ToString("F0"))
                    .Append(") over ").Append((e.LastLossAt - e.OpenedAt).ToString("F0")).Append("s —");
                foreach (var kv in e.LostByTeam)
                {
                    e.ValueByTeam.TryGetValue(kv.Key, out int val);
                    line.Append(' ').Append(kv.Key).Append(" lost ").Append(kv.Value)
                        .Append(" (").Append(val).Append(" value)");
                }
                MelonLogger.Msg(line.ToString());
            }
            catch (Exception ex) { MelonLogger.Warning("[COMBAT] write threw: " + ex.Message); }
        }

        /// <summary>Unit value in cash, so exchange ratios are in the same
        /// currency the planner already reasons about. Shared with the battalion
        /// manager, which measures strength the same way.</summary>
        static int CostOf(string unitName) => UnitValues.CostOf(unitName);

        static string F(float v) => v.ToString("F0", CultureInfo.InvariantCulture);

        static string Esc(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }
    }
}
