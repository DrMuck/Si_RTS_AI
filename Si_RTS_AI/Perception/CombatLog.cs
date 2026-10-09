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
    /// DID THE ARMY TRADE WELL — AND WHO WAS THERE?
    ///
    /// One row per engagement, detected by roster diffing: a unit present last
    /// sample and gone now is a loss at its last known position; losses within
    /// ENGAGEMENT_RADIUS_M and ENGAGEMENT_WINDOW_S of each other are one fight.
    /// No hooks, no patches, nothing that breaks on a game update.
    ///
    /// V2 (2026-09-05) carries the denominator LEARNING.md section 4 said was
    /// blocking everything: `engaged` — a census of what stood within
    /// CENSUS_RADIUS_M of the first loss, per team, with cash, composition and
    /// how many were piloted — plus whether static defence was in range, which
    /// objective kind owned the ground, and the kernel's p(win) for the alien
    /// side at the moment the fight opened. Those are the confounders that
    /// cannot be reconstructed afterwards.
    /// </summary>
    internal static class CombatLog
    {
        const float SAMPLE_INTERVAL_S    = 2f;
        const float ENGAGEMENT_RADIUS_M  = 250f;
        const float ENGAGEMENT_WINDOW_S  = 25f;
        const float CENSUS_RADIUS_M      = 400f;
        const int   MIN_LOSSES_TO_RECORD = 2;

        class Roster { public readonly Dictionary<int, (string name, Vector3 pos, bool piloted)> Units = new Dictionary<int, (string, Vector3, bool)>(); }

        class Side
        {
            public int Units, Piloted, Cash;
            public readonly Dictionary<string, int> Comp = new Dictionary<string, int>();
            public float Eff;
        }

        class Engagement
        {
            public Vector3 Centre;
            public float   OpenedAt, LastLossAt;
            public readonly Dictionary<string, int> LostByTeam   = new Dictionary<string, int>();
            public readonly Dictionary<string, int> ValueByTeam  = new Dictionary<string, int>();
            public readonly Dictionary<string, Dictionary<string,int>> Comp =
                new Dictionary<string, Dictionary<string,int>>();
            public readonly Dictionary<string, Side> Engaged = new Dictionary<string, Side>();
            public bool  StaticDefence;
            public float PWinAlien = -1f;
            public string Objective = "none";
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

            var seen = new Dictionary<int, (string, Vector3, bool)>();
            var units = team.Units;
            if (units != null)
            {
                for (int i = 0; i < units.Count; i++)
                {
                    var u = units[i];
                    if (u == null || u.ObjectInfo == null || u.IsDestroyed) continue;
                    int id = u.GetInstanceID();
                    bool piloted = false;
                    try { piloted = u.ControlledBy != null; } catch { }
                    seen[id] = (u.ObjectInfo.DisplayName ?? "?", u.transform.position, piloted);
                }
            }

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
                try { Census(e); } catch (Exception ex) { MelonLogger.Warning("[COMBAT] census threw: " + ex.Message); }
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

        /// <summary>Who stood within the census radius when the fight opened.
        /// Read from the rosters — at most one sample old — so no extra
        /// interop; workers are excluded because they are economy, not force.</summary>
        static void Census(Engagement e)
        {
            float r2 = CENSUS_RADIUS_M * CENSUS_RADIUS_M;
            var forces = new Dictionary<string, Mil.Kernel.Force>();
            foreach (var kv in _rosters)
            {
                var team = kv.Key;
                string tn = team?.name ?? "?";
                foreach (var u in kv.Value.Units.Values)
                {
                    float dx = u.pos.x - e.Centre.x, dz = u.pos.z - e.Centre.z;
                    if (dx * dx + dz * dz > r2) continue;
                    if (u.name == "Shrimp" || u.name == "Harvester" || u.name == "Queen") continue;
                    if (!e.Engaged.TryGetValue(tn, out var side)) { side = new Side(); e.Engaged[tn] = side; }
                    side.Units++;
                    if (u.piloted) side.Piloted++;
                    side.Cash += CostOf(u.name);
                    side.Comp.TryGetValue(u.name, out int c); side.Comp[u.name] = c + 1;
                    if (!forces.TryGetValue(tn, out var f)) { f = new Mil.Kernel.Force(); forces[tn] = f; }
                    f.Add(u.name);
                }
            }
            foreach (var kv in forces) e.Engaged[kv.Key].Eff = kv.Value.Effective();

            // Static defence in range, any team.
            try
            {
                var teams = Team.Teams;
                if (teams != null)
                    for (int t = 0; t < teams.Count && !e.StaticDefence; t++)
                    {
                        var structs = teams[t]?.Structures;
                        if (structs == null) continue;
                        for (int i = 0; i < structs.Count; i++)
                        {
                            var s = structs[i];
                            if (s?.ObjectInfo == null || s.IsDestroyed) continue;
                            bool def = false;
                            try { def = (s.ObjectInfo.StructureType & StructureType.Defense) != 0; } catch { }
                            if (!def) continue;
                            float dx = s.transform.position.x - e.Centre.x, dz = s.transform.position.z - e.Centre.z;
                            if (dx * dx + dz * dz <= r2) { e.StaticDefence = true; break; }
                        }
                    }
            }
            catch { }

            // The kernel's call for the alien side against the largest other side.
            float alien = 0f, other = 0f;
            foreach (var kv in e.Engaged)
            {
                if (kv.Key.Contains("Alien")) alien = kv.Value.Eff;
                else other = Mathf.Max(other, kv.Value.Eff);
            }
            if (alien > 0f && other > 0f) e.PWinAlien = Mil.Kernel.PWin(alien, other);
            try { e.Objective = Mil.Objectives.KindAt(e.Centre, ENGAGEMENT_RADIUS_M * 2f); } catch { }
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
                var sb = new StringBuilder(512);
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
                    AppendComp(sb, e.Comp.TryGetValue(kv.Key, out var comp) ? comp : null);
                    sb.Append("}}");
                }
                sb.Append('}');

                sb.Append(",\"engaged\":{");
                first = true;
                foreach (var kv in e.Engaged)
                {
                    if (!first) sb.Append(','); first = false;
                    var s = kv.Value;
                    sb.Append('"').Append(Esc(kv.Key)).Append("\":{\"units\":").Append(s.Units)
                      .Append(",\"value\":").Append(s.Cash).Append(",\"eff\":").Append(F(s.Eff))
                      .Append(",\"piloted\":").Append(s.Piloted).Append(",\"comp\":{");
                    AppendComp(sb, s.Comp);
                    sb.Append("}}");
                }
                sb.Append('}');

                sb.Append(",\"staticDefence\":").Append(e.StaticDefence ? "true" : "false");
                if (e.PWinAlien >= 0f) sb.Append(",\"pWinAlien\":").Append(e.PWinAlien.ToString("F3", CultureInfo.InvariantCulture));
                sb.Append(",\"missionType\":\"").Append(Esc(e.Objective)).Append('"');
                sb.Append(",\"sides\":").Append(e.LostByTeam.Count);
                sb.Append(",\"v\":2");
                sb.Append('}');

                string dir = Config.Paths.LogDir;
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
                line.Append(" | engaged:");
                foreach (var kv in e.Engaged)
                    line.Append(' ').Append(Mil.Intel.Short(kv.Key)).Append(' ').Append(kv.Value.Units).Append("u/")
                        .Append(kv.Value.Eff.ToString("F0")).Append("eff").Append(kv.Value.Piloted > 0 ? $"({kv.Value.Piloted} piloted)" : "");
                if (e.PWinAlien >= 0f) line.Append(" pWinAlien ").Append(e.PWinAlien.ToString("F2"));
                if (e.StaticDefence) line.Append(" +static");
                line.Append(" [").Append(e.Objective).Append(']');
                Mil.MilLog.Msg(line.ToString());
            }
            catch (Exception ex) { MelonLogger.Warning("[COMBAT] write threw: " + ex.Message); }
        }

        static void AppendComp(StringBuilder sb, Dictionary<string, int> comp)
        {
            if (comp == null) return;
            bool f2 = true;
            foreach (var c in comp)
            {
                if (!f2) sb.Append(','); f2 = false;
                sb.Append('"').Append(Esc(c.Key)).Append("\":").Append(c.Value);
            }
        }

        static int CostOf(string unitName) => UnitValues.CostOf(unitName);

        static string F(float v) => v.ToString("F0", CultureInfo.InvariantCulture);

        static string Esc(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }
    }
}
