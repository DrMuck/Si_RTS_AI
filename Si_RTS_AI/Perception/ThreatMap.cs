using MelonLoader;
using Silica;
using System;
using System.Collections.Generic;
using UnityEngine;
using Si_RTS_AI.Perception.MapLayers;

namespace Si_RTS_AI.Perception
{
    /// <summary>
    /// Where the enemy is dangerous, and what of theirs is worth hitting.
    ///
    /// First piece of the military layer, and deliberately the first: every
    /// later part depends on it. A production planner needs to know what it is
    /// producing against; map control needs to know which ground is contested;
    /// a strategy chooser needs to know whether it is ahead or behind.
    ///
    /// Two products:
    ///
    ///   THREAT FIELD   per-cell intensity, decaying. Built only from what we
    ///                  can actually SEE, so it doubles as memory — a force
    ///                  seen 40s ago still shows, fainter, which is what makes
    ///                  it usable for prediction later rather than only for
    ///                  reaction.
    ///   HIGH-VALUE     enemy structures ranked by the game's own Cost, so the
    ///     TARGETS      ranking follows balance changes instead of a table I
    ///                  would have to maintain.
    ///
    /// Weights come from the game too: UIAttackRating for how dangerous a unit
    /// is, TargetingDistance for how far that danger reaches. Nothing here
    /// hardcodes a unit list, which matters because Si_UnitBalance rewrites
    /// these values.
    ///
    /// FOG IS RESPECTED. Only units inside our active fog-of-war contribute.
    /// The AI could read every enemy position from the server, and that would be
    /// both unfair and misleading — a planner trained on omniscience makes
    /// decisions it could never justify from what it can see. Decay is what
    /// turns limited sight into usable knowledge.
    ///
    /// SHADOW: reports only.
    /// </summary>
    internal static class ThreatMap
    {
        /// <summary>Threat halves roughly every HALF_LIFE_S of not being seen.
        /// Long enough to remember a push that ducked out of sight, short
        /// enough that a cleared area stops looking dangerous.</summary>
        const float HALF_LIFE_S   = 45f;
        const float TICK_S        = 1f;
        const float REPORT_S      = 20f;
        const int   HVT_REPORTED  = 3;

        static float[] _threat = new float[0];
        static float _lastTickAt, _lastReportAt;
        static Team _self;

        struct Hvt { public string Name; public Vector3 Pos; public int Cost; public string Team; }
        static readonly List<Hvt> _hvt = new List<Hvt>(32);

        internal static void ResetForNewRound()
        {
            _threat = new float[0];
            _lastTickAt = _lastReportAt = 0f;
            _self = null;
            _hvt.Clear();
        }

        static void EnsureSized()
        {
            int n = GridWorld.CellCount;
            if (_threat.Length != n) _threat = new float[n];
        }

        /// <summary>Called for EVERY team each tick. Ours sets the viewpoint;
        /// everyone else's units are stamped as threat.</summary>
        internal static void Observe(Team team)
        {
            if (team == null) return;
            string tn = team.name ?? "";
            if (tn.Contains("Alien")) { _self = team; return; }

            float now = Time.time;
            if (now - _lastTickAt < TICK_S) return;      // stamping is the expensive half

            EnsureSized();
            try { Stamp(team); }
            catch (Exception ex) { MelonLogger.Warning("[THREAT] stamp threw: " + ex.Message); }
        }

        /// <summary>Called once per tick for our own team: decay, then report.</summary>
        internal static void Tick(Team team)
        {
            if (team == null || !(team.name ?? "").Contains("Alien")) return;
            float now = Time.time;
            if (now - _lastTickAt < TICK_S) return;
            float dt = _lastTickAt <= 0f ? TICK_S : now - _lastTickAt;
            _lastTickAt = now;

            EnsureSized();
            // Exponential decay, framed as a half-life so the constant means
            // something in seconds rather than being a tuned multiplier.
            float keep = Mathf.Pow(0.5f, dt / HALF_LIFE_S);
            for (int i = 0; i < _threat.Length; i++) _threat[i] *= keep;

            if (now - _lastReportAt < REPORT_S) return;
            _lastReportAt = now;
            try { Report(team); }
            catch (Exception ex) { MelonLogger.Warning("[THREAT] report threw: " + ex.Message); }
        }

        static void Stamp(Team enemy)
        {
            LayerB visible = null;
            try { if (_self != null) visible = FoWLayers.GetActive(_self); } catch { }

            var units = enemy.Units;
            if (units != null)
                for (int i = 0; i < units.Count; i++)
                {
                    var u = units[i];
                    if (u == null || u.ObjectInfo == null || u.IsDestroyed) continue;
                    Vector3 p = u.transform.position;
                    int cx = GridWorld.CellX(p.x), cz = GridWorld.CellZ(p.z);
                    if (cx < 0 || cz < 0 || cx >= GridWorld.Width || cz >= GridWorld.Height) continue;
                    if (visible != null && !visible.IsSet(cx, cz)) continue;   // not our sight

                    float weight = Mathf.Max(1, u.ObjectInfo.UIAttackRating);
                    float reachM = 0f;
                    try { reachM = u.TargetingDistance; } catch { }
                    if (reachM < GridWorld.CellSize) reachM = GridWorld.CellSize;
                    AddDisk(p, reachM, weight);
                }

            // High-value targets are structures, and the game already prices
            // them. Rebuilt each pass rather than accumulated — a razed
            // Refinery should stop being a target immediately.
            var structs = enemy.Structures;
            if (structs == null) return;
            for (int i = 0; i < structs.Count; i++)
            {
                var st = structs[i];
                if (st == null || st.ObjectInfo == null || st.IsDestroyed) continue;
                Vector3 p = st.transform.position;
                int cx = GridWorld.CellX(p.x), cz = GridWorld.CellZ(p.z);
                if (cx < 0 || cz < 0 || cx >= GridWorld.Width || cz >= GridWorld.Height) continue;
                if (visible != null && !visible.IsSet(cx, cz)) continue;
                _hvt.Add(new Hvt
                {
                    Name = st.ObjectInfo.DisplayName ?? "?",
                    Pos = p,
                    Cost = st.ObjectInfo.Cost,
                    Team = enemy.name ?? "?",
                });
            }
        }

        static void AddDisk(Vector3 world, float radiusM, float weight)
        {
            int cx = GridWorld.CellX(world.x), cz = GridWorld.CellZ(world.z);
            int r = Mathf.Max(1, Mathf.CeilToInt(radiusM / GridWorld.CellSize));
            int r2 = r * r;
            for (int dz = -r; dz <= r; dz++)
                for (int dx = -r; dx <= r; dx++)
                {
                    int d2 = dx * dx + dz * dz;
                    if (d2 > r2) continue;
                    int x = cx + dx, z = cz + dz;
                    if (x < 0 || z < 0 || x >= GridWorld.Width || z >= GridWorld.Height) continue;
                    // Falls off toward the edge — the centre is where the unit
                    // actually is, the rim is where it could reach.
                    float fall = 1f - Mathf.Sqrt(d2) / (r + 0.001f);
                    _threat[z * GridWorld.Width + x] += weight * fall;
                }
        }

        /// <summary>Total threat within a radius — the query the military
        /// planner will actually ask.</summary>
        internal static float ThreatNear(Vector3 world, float radiusM)
        {
            EnsureSized();
            int cx = GridWorld.CellX(world.x), cz = GridWorld.CellZ(world.z);
            int r = Mathf.Max(1, Mathf.CeilToInt(radiusM / GridWorld.CellSize));
            float sum = 0f;
            for (int dz = -r; dz <= r; dz++)
                for (int dx = -r; dx <= r; dx++)
                {
                    int x = cx + dx, z = cz + dz;
                    if (x < 0 || z < 0 || x >= GridWorld.Width || z >= GridWorld.Height) continue;
                    if (dx * dx + dz * dz > r * r) continue;
                    sum += _threat[z * GridWorld.Width + x];
                }
            return sum;
        }

        static void Report(Team self)
        {
            float total = 0f, peak = 0f;
            int peakIdx = -1;
            for (int i = 0; i < _threat.Length; i++)
            {
                total += _threat[i];
                if (_threat[i] > peak) { peak = _threat[i]; peakIdx = i; }
            }

            var sb = new System.Text.StringBuilder();
            sb.Append("[THREAT] total=").Append((int)total).Append(" peak=").Append((int)peak);
            if (peakIdx >= 0 && peak > 0.5f)
            {
                var c = GridWorld.CellCenter(peakIdx % GridWorld.Width, peakIdx / GridWorld.Width);
                sb.Append(" at=(").Append(c.x.ToString("F0")).Append(',').Append(c.z.ToString("F0")).Append(')');

                // How close the danger is to home is the number that decides
                // whether we defend or push.
                Vector3 nest = Vector3.zero; bool haveNest = false;
                try
                {
                    var structs = self.Structures;
                    if (structs != null)
                        for (int i = 0; i < structs.Count; i++)
                        {
                            var st = structs[i];
                            if (st == null || st.ObjectInfo == null || st.IsDestroyed) continue;
                            if ((st.ObjectInfo.DisplayName ?? "") != "Nest") continue;
                            nest = st.transform.position; haveNest = true; break;
                        }
                }
                catch { }
                if (haveNest)
                    sb.Append(" distToNest=").Append((int)Vector3.Distance(c, nest)).Append('m');
            }

            _hvt.Sort((a, b) => b.Cost.CompareTo(a.Cost));
            int shown = 0;
            var seen = new HashSet<string>();
            for (int i = 0; i < _hvt.Count && shown < HVT_REPORTED; i++)
            {
                string key = _hvt[i].Name + _hvt[i].Pos.x.ToString("F0") + _hvt[i].Pos.z.ToString("F0");
                if (!seen.Add(key)) continue;
                sb.Append(" | HVT ").Append(_hvt[i].Name)
                  .Append('(').Append(_hvt[i].Pos.x.ToString("F0")).Append(',')
                  .Append(_hvt[i].Pos.z.ToString("F0")).Append(')')
                  .Append(" cost=").Append(_hvt[i].Cost);
                shown++;
            }
            _hvt.Clear();

            MelonLogger.Msg(sb.ToString());
        }
    }
}
