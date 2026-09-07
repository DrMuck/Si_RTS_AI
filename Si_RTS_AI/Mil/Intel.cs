using MelonLoader;
using Silica;
using Si_RTS_AI.Perception;
using Si_RTS_AI.Perception.MapLayers;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Si_RTS_AI.Mil
{
    /// <summary>
    /// THE ENEMY AS THINGS THAT MOVE, NOT AS STAMPS.
    ///
    /// ThreatMap answers "where is it dangerous" as a field. Every decision the
    /// spec asks for needs something the field cannot hold: WHICH group, HOW
    /// FAST, WHICH WAY, WHEN does it arrive, and is a player driving it. That is
    /// a TRACK — a cluster of visible enemy units followed over time with a
    /// position, a velocity, a composition and a confidence that fades when we
    /// stop seeing it.
    ///
    /// One structure, four spec requirements (MILITARY_MODEL section 3):
    ///   enemy army estimate    SUM track effective force x confidence
    ///   predicted movement     heading + speed -> arrival at each asset of ours
    ///   slip-through detection a forecast on the Nest while the main force is away
    ///   FPS player tracking    Unit.ControlledBy on any member
    ///
    /// CORRIDORS are the only prediction the data supports before a track exists:
    /// where enemy groups have ENTERED our ground before. A histogram of entry
    /// cells and Nest-relative sectors with a ten-minute half-life, read by the
    /// reserve and the spire planner.
    ///
    /// STRUCTURES AND BASES moved here from ThreatMap with two additions the
    /// spec needed and never had: FirstSeenAt (a fresh expansion is objective #1)
    /// and a class from the game's own ObjectInfo taxonomy — HQ, Resource,
    /// Production, Defense — so "raid their eco" is a query and not a name list.
    ///
    /// FOG IS RESPECTED throughout: only units and structures inside our active
    /// fog are observed. Memory is what turns limited sight into knowledge.
    /// </summary>
    internal static class Intel
    {
        // ---- tuning that is geometry, not doctrine --------------------------
        const float STAMP_S          = 0.5f;    // observation cadence
        const float LINK_M           = 250f;    // units this close are one group
        const float GATE_M           = 300f;    // match a track within this of its prediction
        const float VEL_EMA          = 0.35f;   // velocity smoothing per observation
        const float UNSEEN_HALF_S    = 90f;     // confidence half-life once unseen
        const float EXTRAPOLATE_S    = 30f;     // predict along heading this long, then hold
        const float DROP_CONF        = 0.12f;   // forget below this
        const float HEADING_TOL_DEG  = 35f;     // "coming at us" cone
        const float MOVING_MPS       = 2.5f;    // below this a track is standing
        const float CORRIDOR_HALF_S  = 600f;
        const float BASE_LINK_M      = 700f;
        const float BASE_DEFENCE_M   = 500f;
        internal const float RECON_STALE_S = 120f;
        const float REPORT_S         = 30f;
        /// <summary>A remembered structure on VISIBLE ground that has not been
        /// stamped for this long is gone. The first version used three stamp
        /// intervals (1.5s) against a 1Hz tick, and a single late tick made every
        /// visible enemy structure vanish and reappear, which read as "target
        /// destroyed" to a raid twelve seconds old.</summary>
        const float FORGET_GRACE_S   = 6f;

        internal enum StructClass { Other, HQ, Resource, Production, Defense, Research }

        internal sealed class Track
        {
            public int     Id;
            public string  Team = "";
            public Vector3 Pos;
            public Vector3 Vel;
            public float   FirstSeenAt, LastSeenAt, LastUpdateAt;
            public float   Confidence = 1f;
            public readonly Kernel.Force Force = new Kernel.Force();
            public int     Count, Piloted;
            public float   Effective, Cash, Reach, MeanSpeed;
            public bool    InsideOurGround;
            public Unit    Leader;              // a live member, for Attack orders
            public readonly Dictionary<string, float> ClassCash =
                new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);

            public float Speed => new Vector2(Vel.x, Vel.z).magnitude;
            public bool  Moving => Speed >= MOVING_MPS;
            public bool  Fast => MeanSpeed >= 20f;
            public bool  Seen => Time.time - LastSeenAt < STAMP_S * 3f;

            /// <summary>Where we think it is now: extrapolated for a while, then held.</summary>
            public Vector3 Predicted()
            {
                float dt = Mathf.Min(Time.time - LastSeenAt, EXTRAPOLATE_S);
                var p = Pos + Vel * dt; p.y = Pos.y;
                return p;
            }
        }

        internal sealed class KnownStructure
        {
            public string      Name = "";
            public string      Team = "";
            public Vector3     Pos;
            public int         Cost;
            public float       Hp, MaxHp;
            public StructClass Class;
            public float       FirstSeenAt, LastSeenAt;
            public Structure   Ref;             // live handle while it exists, for Attack orders
        }

        internal sealed class Base
        {
            public string  Team = "";
            public Vector3 Centre;
            public int     Cost, Count;
            public bool    HasHq, IsMain;
            public float   FirstSeenAt, LastSeenAt;
            public float   AgeS => Time.time - FirstSeenAt;
            public float   StaleS => Time.time - LastSeenAt;
            public int     Growth;              // structures gained over the growth window
            public float   TotalHp;
            public float   LocalDefenceEff;     // tracks + defence structures within BASE_DEFENCE_M
            public int     Harvesters;
            public readonly List<KnownStructure> Members = new List<KnownStructure>();
            /// <summary>Stable across rebuilds: inherited from whichever previous
            /// base shared the most members. A base that grows or shifts its
            /// centre is the same base, and an objective aimed at it must not
            /// be re-keyed every time a Refinery appears.</summary>
            public int     Id;
            public string  Key => Team + "#" + Id;
        }
        static int _nextBaseId;

        internal struct Arrival
        {
            public Track Track;
            public float EtaS;
            public float Effective;
            public float Confidence;
        }

        // ---- state ----------------------------------------------------------
        static readonly List<Track> _tracks = new List<Track>(32);
        static readonly Dictionary<string, KnownStructure> _known =
            new Dictionary<string, KnownStructure>(128);
        static readonly List<Base> _bases = new List<Base>(8);
        static readonly Dictionary<string, (float at, int count)> _baseCountHist =
            new Dictionary<string, (float, int)>();
        static readonly List<(Vector3 pos, string team)> _harvesters = new List<(Vector3, string)>();
        static float[] _corridor = new float[0];
        static readonly float[] _sector = new float[8];
        static float _lastStampAt, _lastDecayAt, _lastReportAt;
        static int _nextTrackId;
        static Team _self;
        static Vector3 _nest;
        static bool _fogNull;

        internal static IReadOnlyList<Track> Tracks => _tracks;
        internal static IReadOnlyList<Base> Bases => _bases;
        internal static int KnownStructureCount => _known.Count;
        internal static Vector3 Nest => _nest;

        /// <summary>Enemy effective force we believe is on the map, confidence-weighted.</summary>
        internal static float EnemyEffective { get; private set; }
        internal static int   EnemyCash { get; private set; }
        internal static int   EnemyPiloted { get; private set; }

        internal static void ResetForNewRound()
        {
            _tracks.Clear(); _known.Clear(); _bases.Clear(); _baseCountHist.Clear(); _teamPeak.Clear();
            _harvesters.Clear();
            _corridor = new float[0];
            Array.Clear(_sector, 0, _sector.Length);
            _lastStampAt = _lastDecayAt = _lastReportAt = 0f;
            _nextTrackId = 0; _nextBaseId = 0; _self = null; _nest = Vector3.zero;
            EnemyEffective = 0f; EnemyCash = 0; EnemyPiloted = 0;
        }

        // ---- tick -----------------------------------------------------------

        /// <summary>Called once per 1 Hz tick for the alien team; observes every
        /// enemy team at its own cadence.</summary>
        internal static void Tick(Team self)
        {
            if (self == null) return;
            _self = self;
            float now = Time.time;
            if (now - _lastStampAt < STAMP_S) return;
            float dt = _lastStampAt <= 0f ? STAMP_S : now - _lastStampAt;
            _lastStampAt = now;

            try
            {
                _nest = FindNest(self);
                EnsureCorridor();
                LayerB vis = null;
                try { vis = GameFow.ActiveLayer(self); } catch { }
                _fogNull = vis == null;

                _harvesters.Clear();
                _enemyAlive.Clear();
                var teams = Team.Teams;
                if (teams != null)
                    for (int t = 0; t < teams.Count; t++)
                    {
                        var other = teams[t];
                        if (other == null || other == self) continue;
                        bool enemy = true;
                        try { enemy = Team.GetTeamsAreEnemy(self, other); } catch { }
                        if (!enemy) continue;
                        try { if (other.Structures != null && other.Structures.Count > 0) _enemyAlive.Add(other.name ?? "?"); } catch { }
                        ObserveUnits(other, vis, now, dt);
                        ObserveStructures(other, vis, now);
                    }
                ForgetGone(vis, now);
                Decay(now, vis);
                RebuildBases(now);
                Summarise();
                if (now - _lastReportAt >= REPORT_S) { _lastReportAt = now; Report(); }
            }
            catch (Exception ex) { MelonLogger.Warning("[INTEL] tick threw: " + ex.Message); }
        }

        // ---- units -> clusters -> tracks --------------------------------------

        struct Seen { public Unit U; public Vector3 P; public string Name; public int Cost; public bool Piloted; public float Speed; public float Reach; }
        static readonly List<Seen> _seen = new List<Seen>(128);
        static readonly List<List<int>> _clusters = new List<List<int>>(16);

        static void ObserveUnits(Team enemy, LayerB vis, float now, float dt)
        {
            _seen.Clear();
            var units = enemy.Units;
            if (units == null) return;
            string tn = enemy.name ?? "?";
            for (int i = 0; i < units.Count; i++)
            {
                var u = units[i];
                if (u == null || u.ObjectInfo == null || u.IsDestroyed) continue;
                Vector3 p;
                try { p = u.transform.position; } catch { continue; }
                int cx = GridWorld.CellX(p.x), cz = GridWorld.CellZ(p.z);
                if (vis != null && !vis.IsSet(cx, cz)) continue;

                string name = u.ObjectInfo.DisplayName ?? "?";
                bool harvester = false;
                try { harvester = u.ObjectInfo.UnitType == UnitType.Harvester; } catch { }
                if (harvester) { _harvesters.Add((p, tn)); continue; }

                int cost = 0;
                try { cost = UnitValues.CostOf(name); } catch { }
                if (cost <= 0) { try { cost = u.ObjectInfo.Cost; } catch { } }
                bool piloted = false;
                try { piloted = u.ControlledBy != null; } catch { }
                float speed = UnitStats.SpeedOf(name);
                try { float ns = u.NormalSpeed; if (ns > 0.5f) speed = ns; } catch { }
                float reach = 0f;
                try { reach = u.TargetingDistance; } catch { }
                _seen.Add(new Seen { U = u, P = p, Name = name, Cost = cost, Piloted = piloted, Speed = speed, Reach = reach });
            }
            if (_seen.Count == 0) return;

            Cluster();

            for (int c = 0; c < _clusters.Count; c++)
            {
                var members = _clusters[c];
                Vector3 centre = Vector3.zero;
                for (int i = 0; i < members.Count; i++) centre += _seen[members[i]].P;
                centre /= members.Count;

                var tr = Match(tn, centre, now);
                if (tr == null)
                {
                    tr = new Track { Id = ++_nextTrackId, Team = tn, Pos = centre, FirstSeenAt = now,
                                     LastSeenAt = now, LastUpdateAt = now };
                    _tracks.Add(tr);
                    MilLog.Quiet($"[INTEL] new track #{tr.Id} {tn} at ({centre.x:F0},{centre.z:F0}) {members.Count} units");
                }
                else
                {
                    float span = Mathf.Max(0.1f, now - tr.LastUpdateAt);
                    Vector3 v = (centre - tr.Pos) / span; v.y = 0f;
                    // A cluster that gained members jumps its centroid; damp
                    // the velocity so a merge does not read as a sprint.
                    if (v.magnitude > 60f) v = v.normalized * 60f;
                    tr.Vel = tr.LastSeenAt == tr.FirstSeenAt ? v : Vector3.Lerp(tr.Vel, v, VEL_EMA);
                    tr.Pos = centre;
                }
                tr.LastSeenAt = tr.LastUpdateAt = now;
                tr.Confidence = 1f;
                Fill(tr, members);
                NoteCorridor(tr, now);
            }
        }

        static void Fill(Track tr, List<int> members)
        {
            tr.Force.Units.Clear();
            tr.ClassCash.Clear();
            tr.Count = members.Count; tr.Piloted = 0; tr.Cash = 0f; tr.Reach = 0f;
            float spd = 0f; Unit leader = null; int leaderCost = -1;
            for (int i = 0; i < members.Count; i++)
            {
                var s = _seen[members[i]];
                tr.Force.Add(s.Name);
                if (s.Piloted) tr.Piloted++;
                tr.Cash += s.Cost;
                spd += s.Speed;
                if (s.Reach > tr.Reach) tr.Reach = s.Reach;
                if (s.Cost > leaderCost) { leaderCost = s.Cost; leader = s.U; }
                string cls = null;
                try { cls = Planning.UnitPrior.ClassOf(s.Name); } catch { }
                if (cls != null)
                {
                    tr.ClassCash.TryGetValue(cls, out float had);
                    tr.ClassCash[cls] = had + s.Cost;
                }
            }
            tr.MeanSpeed = members.Count > 0 ? spd / members.Count : 9f;
            tr.Effective = tr.Force.Effective();
            tr.Leader = leader;
        }

        /// <summary>Single-linkage at LINK_M over the seen list. Tens to a few
        /// hundred units, so the quadratic pass is fine.</summary>
        static void Cluster()
        {
            _clusters.Clear();
            int n = _seen.Count;
            var label = new int[n];
            for (int i = 0; i < n; i++) label[i] = -1;
            float link2 = LINK_M * LINK_M;
            var stack = new Stack<int>();
            for (int i = 0; i < n; i++)
            {
                if (label[i] >= 0) continue;
                int id = _clusters.Count;
                var list = new List<int>();
                _clusters.Add(list);
                label[i] = id; stack.Push(i);
                while (stack.Count > 0)
                {
                    int a = stack.Pop();
                    list.Add(a);
                    for (int b = 0; b < n; b++)
                    {
                        if (label[b] >= 0) continue;
                        float dx = _seen[a].P.x - _seen[b].P.x, dz = _seen[a].P.z - _seen[b].P.z;
                        if (dx * dx + dz * dz > link2) continue;
                        label[b] = id; stack.Push(b);
                    }
                }
            }
        }

        static Track Match(string team, Vector3 centre, float now)
        {
            Track best = null; float bd = float.MaxValue;
            for (int i = 0; i < _tracks.Count; i++)
            {
                var t = _tracks[i];
                if (t.Team != team) continue;
                if (t.LastUpdateAt == now) continue;          // already matched this pass
                var p = t.Predicted();
                float dx = p.x - centre.x, dz = p.z - centre.z;
                float d = dx * dx + dz * dz;
                float gate = GATE_M + t.Speed * Mathf.Min(now - t.LastSeenAt, EXTRAPOLATE_S);
                if (d > gate * gate) continue;
                if (d < bd) { bd = d; best = t; }
            }
            return best;
        }

        static void Decay(float now, LayerB vis)
        {
            float dt = _lastDecayAt <= 0f ? STAMP_S : now - _lastDecayAt;
            _lastDecayAt = now;
            float keep = Mathf.Pow(0.5f, dt / UNSEEN_HALF_S);
            float ckeep = Mathf.Pow(0.5f, dt / CORRIDOR_HALF_S);
            for (int i = 0; i < _corridor.Length; i++) _corridor[i] *= ckeep;
            for (int i = 0; i < _sector.Length; i++) _sector[i] *= ckeep;

            for (int i = _tracks.Count - 1; i >= 0; i--)
            {
                var t = _tracks[i];
                if (t.LastSeenAt == now) continue;
                t.Confidence *= keep;
                // Looking at where it should be and seeing nothing is evidence.
                if (vis != null)
                {
                    var p = t.Predicted();
                    if (vis.IsSet(GridWorld.CellX(p.x), GridWorld.CellZ(p.z))) t.Confidence *= 0.5f;
                }
                if (t.Confidence < DROP_CONF)
                {
                    MilLog.Quiet($"[INTEL] track #{t.Id} lost (unseen {now - t.LastSeenAt:F0}s)");
                    _tracks.RemoveAt(i);
                }
            }
        }

        static void Summarise()
        {
            float eff = 0f; int cash = 0, pil = 0;
            var byTeam = new Dictionary<string, float>();
            for (int i = 0; i < _tracks.Count; i++)
            {
                var t = _tracks[i];
                eff += t.Effective * t.Confidence;
                cash += Mathf.RoundToInt(t.Cash * t.Confidence);
                pil += t.Piloted;
                byTeam.TryGetValue(t.Team, out float had);
                byTeam[t.Team] = had + t.Effective * t.Confidence;
            }
            float now = Time.time;
            foreach (var kv in byTeam)
            {
                if (!_teamPeak.TryGetValue(kv.Key, out var pk) ||
                    kv.Value >= pk.eff * Mathf.Pow(0.5f, (now - pk.at) / TEAM_PEAK_HALF_S))
                    _teamPeak[kv.Key] = (kv.Value, now);
            }
            // THE AGGREGATE IS FLOORED LIKE THE PER-TEAM ESTIMATE. The Maw,
            // 2026-09-07 06:48: EnemyEffectiveOf() said 4,000 for Sol from the
            // first second, but this sum said 2,696, so the siege flag that
            // hangs on it came at 116 s — after the starter reserve had already
            // chased raiders and died. Every enemy team still holding a
            // structure counts for at least its starting army.
            float floor = 0f;
            for (int i = 0; i < _enemyAlive.Count; i++)
            {
                float f = MilConfig.EnemyStartEff;
                if (_teamPeak.TryGetValue(_enemyAlive[i], out var pk))
                    f = Mathf.Max(f, pk.eff * Mathf.Pow(0.5f, (now - pk.at) / TEAM_PEAK_HALF_S));
                floor += f;
            }
            foreach (var kv in _teamPeak)
                if (!_enemyAlive.Contains(kv.Key))
                    floor += kv.Value.eff * Mathf.Pow(0.5f, (now - kv.Value.at) / TEAM_PEAK_HALF_S);
            EnemyEffective = Mathf.Max(eff, floor); EnemyCash = cash; EnemyPiloted = pil;
        }

        // ---- corridors --------------------------------------------------------

        static void EnsureCorridor()
        {
            if (_corridor.Length != GridWorld.CellCount) _corridor = new float[GridWorld.CellCount];
        }

        static bool OnOurGround(Vector3 p)
        {
            try { return ControlMap.ControlGain(p, GridWorld.CellSize) < 0.5f; } catch { return false; }
        }

        static void NoteCorridor(Track t, float now)
        {
            bool inside = OnOurGround(t.Pos);
            if (inside && !t.InsideOurGround)
            {
                int cx = GridWorld.CellX(t.Pos.x), cz = GridWorld.CellZ(t.Pos.z);
                _corridor[cz * GridWorld.Width + cx] += Mathf.Max(1f, t.Effective / 1000f);
                if (_nest != Vector3.zero)
                    _sector[SectorOf(t.Pos - _nest)] += Mathf.Max(1f, t.Effective / 1000f);
                MilLog.Quiet($"[INTEL] track #{t.Id} entered our ground at ({t.Pos.x:F0},{t.Pos.z:F0}) " +
                             $"sector {SectorOf(t.Pos - _nest)} eff {t.Effective:F0}");
            }
            t.InsideOurGround = inside;
        }

        internal static int SectorOf(Vector3 rel)
        {
            float a = Mathf.Atan2(rel.z, rel.x);
            int s = Mathf.FloorToInt((a + Mathf.PI) / (2f * Mathf.PI) * 8f);
            return Mathf.Clamp(s, 0, 7);
        }

        /// <summary>Weight of remembered incursions per Nest-relative sector (8).</summary>
        internal static float SectorWeight(int sector) => _sector[Mathf.Clamp(sector, 0, 7)];

        internal static float CorridorAt(Vector3 p)
        {
            EnsureCorridor();
            return _corridor[GridWorld.CellZ(p.z) * GridWorld.Width + GridWorld.CellX(p.x)];
        }

        internal static float[] CorridorField() { EnsureCorridor(); return _corridor; }

        /// <summary>Direction the incursions have come from, as a unit vector from
        /// the Nest, or zero when nothing has ever come.</summary>
        internal static Vector3 CorridorBearing()
        {
            Vector3 v = Vector3.zero; float tot = 0f;
            for (int s = 0; s < 8; s++)
            {
                float a = -Mathf.PI + (s + 0.5f) / 8f * 2f * Mathf.PI;
                v += new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)) * _sector[s];
                tot += _sector[s];
            }
            return tot <= 0f ? Vector3.zero : v.normalized;
        }

        // ---- forecast ---------------------------------------------------------

        /// <summary>
        /// Which tracks are coming at this ground, and when. A track inside the
        /// radius counts now; a moving track whose heading lies inside the cone
        /// counts at distance over its speed. Walk-time from Fields refines the
        /// straight-line eta when the field is built.
        /// </summary>
        internal static void ForecastFor(Vector3 asset, float radiusM, float horizonS, List<Arrival> into)
        {
            into.Clear();
            for (int i = 0; i < _tracks.Count; i++)
            {
                var t = _tracks[i];
                var p = t.Predicted();
                Vector3 to = asset - p; to.y = 0f;
                float dist = to.magnitude;
                if (dist <= radiusM + t.Reach)
                {
                    into.Add(new Arrival { Track = t, EtaS = 0f, Effective = t.Effective, Confidence = t.Confidence });
                    continue;
                }
                if (!t.Moving) continue;
                // A velocity needs a few observations to mean anything: fresh
                // clusters raised 29 defences in round two, 28 of which nothing
                // ever reached.
                if (Time.time - t.FirstSeenAt < 10f) continue;
                float ang = Vector3.Angle(t.Vel, to);
                if (ang > HEADING_TOL_DEG) continue;
                // Straight-line only. The field refinement here cost one
                // Dijkstra per track per site per refresh — hundreds a pass on
                // a sixty-site base — and was the server hitch DrMuck saw.
                float eta = (dist - radiusM) / Mathf.Max(1f, t.Speed);
                if (eta > horizonS) continue;
                into.Add(new Arrival { Track = t, EtaS = eta, Effective = t.Effective, Confidence = t.Confidence });
            }
            into.Sort((a, b) => a.EtaS.CompareTo(b.EtaS));
        }

        /// <summary>Effective enemy force within a radius, confidence-weighted.</summary>
        /// <summary>Distance to the nearest known enemy base centre; float.MaxValue when none is known.</summary>
        internal static float NearestEnemyBaseDist(Vector3 p)
        {
            float best = float.MaxValue;
            try
            {
                for (int i = 0; i < Bases.Count; i++)
                {
                    var b = Bases[i];
                    float dx = b.Centre.x - p.x, dz = b.Centre.z - p.z;
                    float d = Mathf.Sqrt(dx * dx + dz * dz);
                    if (d < best) best = d;
                }
            }
            catch { }
            return best;
        }
        internal static float EffectiveNear(Vector3 p, float radiusM)
        {
            float e = 0f; float r2 = radiusM * radiusM;
            for (int i = 0; i < _tracks.Count; i++)
            {
                var t = _tracks[i]; var q = t.Predicted();
                float dx = q.x - p.x, dz = q.z - p.z;
                if (dx * dx + dz * dz <= r2) e += t.Effective * t.Confidence;
            }
            return e;
        }

        /// <summary>Effective force of ONE enemy team, confidence-weighted. The
        /// price of killing a Centauri base is what Centauri can answer with,
        /// not what Sol is doing on the other side of the map.</summary>
        internal static float EnemyEffectiveOf(string team)
        {
            float e = 0f;
            for (int i = 0; i < _tracks.Count; i++)
                if (_tracks[i].Team == team) e += _tracks[i].Effective * _tracks[i].Confidence;
            // AN ARMY SEEN FIVE MINUTES AGO IS PROBABLY STILL THERE. Tracks drop
            // when the scout that saw them dies, and the price of their HQ then
            // fell to whatever two Crabs could see. The team's largest recent
            // army decays with a five-minute half-life and floors the estimate.
            if (_teamPeak.TryGetValue(team, out var pk))
                e = Mathf.Max(e, pk.eff * Mathf.Pow(0.5f, (Time.time - pk.at) / TEAM_PEAK_HALF_S));
            // AN ENEMY IS NEVER WEAKER THAN ITS STARTING ARMY. At thirty seconds
            // into a Whispering Plains round (2026-09-06 15:59) the only Sol unit
            // seen was one scout, the HQ was priced at 928 and called affordable,
            // and the starter army marched off to die while four raiders took the
            // bare Nest. What has not been seen is not absent.
            return Mathf.Max(e, MilConfig.EnemyStartEff);
        }

        const float TEAM_PEAK_HALF_S = 300f;
        static readonly List<string> _enemyAlive = new List<string>();
        static readonly Dictionary<string, (float eff, float at)> _teamPeak = new Dictionary<string, (float, float)>();

        internal static Track NearestTrack(Vector3 p, float maxM)
        {
            Track best = null; float bd = maxM * maxM;
            for (int i = 0; i < _tracks.Count; i++)
            {
                var q = _tracks[i].Predicted();
                float dx = q.x - p.x, dz = q.z - p.z, d = dx * dx + dz * dz;
                if (d < bd) { bd = d; best = _tracks[i]; }
            }
            return best;
        }

        // ---- structures and bases --------------------------------------------

        static string KeyOf(string team, string name, Vector3 p) =>
            team + "|" + name + "|" + Mathf.RoundToInt(p.x / 20f) + "," + Mathf.RoundToInt(p.z / 20f);

        static StructClass Classify(ObjectInfo oi)
        {
            try
            {
                if (oi.StructureSelectionType == StructureSelectionType.HQ) return StructClass.HQ;
                var st = oi.StructureType;
                if ((st & StructureType.Defense) != 0)    return StructClass.Defense;
                if ((st & StructureType.Resource) != 0)   return StructClass.Resource;
                if ((st & StructureType.Research) != 0)   return StructClass.Research;
                if ((st & StructureType.Production) != 0) return StructClass.Production;
            }
            catch { }
            return StructClass.Other;
        }

        static void ObserveStructures(Team enemy, LayerB vis, float now)
        {
            var structs = enemy.Structures;
            if (structs == null) return;
            string tn = enemy.name ?? "?";
            for (int i = 0; i < structs.Count; i++)
            {
                var st = structs[i];
                if (st == null || st.ObjectInfo == null || st.IsDestroyed) continue;
                Vector3 p;
                try { p = st.transform.position; } catch { continue; }
                int cx = GridWorld.CellX(p.x), cz = GridWorld.CellZ(p.z);
                if (vis != null && !vis.IsSet(cx, cz)) continue;
                string name = st.ObjectInfo.DisplayName ?? "?";
                string key = KeyOf(tn, name, p);
                if (!_known.TryGetValue(key, out var k))
                {
                    k = new KnownStructure { Name = name, Team = tn, Pos = p, FirstSeenAt = now,
                                             Class = Classify(st.ObjectInfo) };
                    try { k.Cost = st.ObjectInfo.Cost; } catch { }
                    _known[key] = k;
                    MilLog.Quiet($"[INTEL] discovered {tn} {name} ({k.Class}) at ({p.x:F0},{p.z:F0}) cost {k.Cost}");
                }
                k.LastSeenAt = now; k.Ref = st;
                try
                {
                    var dm = st.DamageManager;
                    if (dm != null) { k.Hp = dm.Health; k.MaxHp = dm.MaxHealth; }
                }
                catch { }
                if (k.MaxHp <= 0f) { try { k.MaxHp = st.ObjectInfo.MaxHealth; k.Hp = k.MaxHp; } catch { } }
            }

        }

        /// <summary>Once per tick, after every team has been stamped: a
        /// remembered structure standing on ground we can see, unstamped for
        /// FORGET_GRACE_S, has been destroyed.</summary>
        static void ForgetGone(LayerB vis, float now)
        {
            if (vis == null) return;
            List<string> gone = null;
            foreach (var kv in _known)
            {
                var k = kv.Value;
                if (now - k.LastSeenAt < FORGET_GRACE_S) continue;
                if (!vis.IsSet(GridWorld.CellX(k.Pos.x), GridWorld.CellZ(k.Pos.z))) continue;
                (gone ??= new List<string>()).Add(kv.Key);
            }
            if (gone != null)
                for (int i = 0; i < gone.Count; i++)
                {
                    var k = _known[gone[i]];
                    MilLog.Quiet($"[INTEL] {k.Team} {k.Name} at ({k.Pos.x:F0},{k.Pos.z:F0}) is gone");
                    _known.Remove(gone[i]);
                }
        }

        internal static void ForEachKnown(Action<KnownStructure> fn)
        {
            foreach (var kv in _known) { try { fn(kv.Value); } catch { } }
        }

        static float _lastClusterAt;

        static void RebuildBases(float now)
        {
            if (now - _lastClusterAt < 5f) return;
            _lastClusterAt = now;
            var oldBases = new List<Base>(_bases);
            _bases.Clear();
            if (_known.Count == 0) return;

            var pending = new List<KnownStructure>(_known.Values);
            float link2 = BASE_LINK_M * BASE_LINK_M;
            while (pending.Count > 0)
            {
                var seed = pending[pending.Count - 1];
                pending.RemoveAt(pending.Count - 1);
                var b = new Base { Team = seed.Team, FirstSeenAt = seed.FirstSeenAt };
                b.Members.Add(seed);
                bool grew = true;
                while (grew)
                {
                    grew = false;
                    for (int i = pending.Count - 1; i >= 0; i--)
                    {
                        if (pending[i].Team != b.Team) continue;
                        for (int m = 0; m < b.Members.Count; m++)
                        {
                            float dx = pending[i].Pos.x - b.Members[m].Pos.x;
                            float dz = pending[i].Pos.z - b.Members[m].Pos.z;
                            if (dx * dx + dz * dz > link2) continue;
                            b.Members.Add(pending[i]); pending.RemoveAt(i); grew = true; break;
                        }
                    }
                }
                Vector3 sum = Vector3.zero;
                for (int m = 0; m < b.Members.Count; m++)
                {
                    var k = b.Members[m];
                    sum += k.Pos; b.Cost += k.Cost;
                    b.TotalHp += k.MaxHp > 0f ? k.MaxHp : 3000f;
                    if (k.Class == StructClass.HQ) b.HasHq = true;
                    if (k.FirstSeenAt < b.FirstSeenAt) b.FirstSeenAt = k.FirstSeenAt;
                    if (k.LastSeenAt > b.LastSeenAt) b.LastSeenAt = k.LastSeenAt;
                }
                b.Count = b.Members.Count;
                b.Centre = sum / Mathf.Max(1, b.Count);

                // Identity: the old base sharing the most members, else new.
                Base parent = null; int best = 0;
                for (int o = 0; o < oldBases.Count; o++)
                {
                    if (oldBases[o].Team != b.Team) continue;
                    int shared = 0;
                    for (int m = 0; m < b.Members.Count; m++)
                        if (oldBases[o].Members.Contains(b.Members[m])) shared++;
                    if (shared > best) { best = shared; parent = oldBases[o]; }
                }
                if (parent != null) { b.Id = parent.Id; oldBases.Remove(parent); }
                else b.Id = ++_nextBaseId;

                // Local defence: tracks within reach, plus its own defence structures.
                float def = EffectiveNear(b.Centre, BASE_DEFENCE_M);
                for (int m = 0; m < b.Members.Count; m++)
                    if (b.Members[m].Class == StructClass.Defense)
                        def += b.Members[m].Cost * MilConfig.DefenceWeight;
                b.LocalDefenceEff = def;

                for (int h = 0; h < _harvesters.Count; h++)
                {
                    if (_harvesters[h].team != b.Team) continue;
                    float dx = _harvesters[h].pos.x - b.Centre.x, dz = _harvesters[h].pos.z - b.Centre.z;
                    if (dx * dx + dz * dz <= link2) b.Harvesters++;
                }

                // Growth over the last three minutes.
                string key = b.Key;
                if (_baseCountHist.TryGetValue(key, out var hist))
                {
                    if (now - hist.at >= 180f) { b.Growth = b.Count - hist.count; _baseCountHist[key] = (now, b.Count); }
                    else b.Growth = parent?.Growth ?? 0;
                }
                else { _baseCountHist[key] = (now, b.Count); b.Growth = 0; }

                _bases.Add(b);
            }
            _bases.Sort((x, y) => y.Cost.CompareTo(x.Cost));
            var mainSeen = new HashSet<string>();
            for (int i = 0; i < _bases.Count; i++)
                _bases[i].IsMain = mainSeen.Add(_bases[i].Team);
        }

        /// <summary>Bases worth a look before we price them: candidate objectives
        /// we have not seen for RECON_STALE_S.</summary>
        internal static void StaleBases(List<Base> into)
        {
            into.Clear();
            for (int i = 0; i < _bases.Count; i++)
                if (_bases[i].StaleS > RECON_STALE_S) into.Add(_bases[i]);
        }

        internal static int KnownEnemyHqs()
        {
            int n = 0;
            for (int i = 0; i < _bases.Count; i++) if (_bases[i].HasHq) n++;
            return n;
        }

        // ---- helpers ------------------------------------------------------------

        static Vector3 FindNest(Team team)
        {
            try
            {
                var structs = team.Structures;
                if (structs != null)
                    for (int i = 0; i < structs.Count; i++)
                    {
                        var s = structs[i];
                        if (s?.ObjectInfo == null || s.IsDestroyed) continue;
                        string dn = s.ObjectInfo.DisplayName ?? "";
                        if (dn == "Nest" || dn == "Headquarters") return s.transform.position;
                    }
            }
            catch { }
            return _nest;
        }

        static void Report()
        {
            var sb = new System.Text.StringBuilder("[INTEL] ");
            sb.Append("tracks=").Append(_tracks.Count)
              .Append(" enemyEff=").Append(EnemyEffective.ToString("F0"))
              .Append(" cash=").Append(EnemyCash)
              .Append(" piloted=").Append(EnemyPiloted)
              .Append(" known=").Append(_known.Count)
              .Append(" bases=").Append(_bases.Count)
              .Append(" hqs=").Append(KnownEnemyHqs());
            try { sb.Append(" | fog: ").Append(GameFow.Report()); } catch { }
            if (_fogNull) sb.Append(" | FOG LAYER NULL — sightings ungated");
            int shown = 0;
            for (int i = 0; i < _tracks.Count && shown < 4; i++)
            {
                var t = _tracks[i]; var p = t.Predicted();
                sb.Append(" | #").Append(t.Id).Append(' ').Append(Short(t.Team))
                  .Append(' ').Append(t.Count).Append("u eff ").Append(t.Effective.ToString("F0"))
                  .Append(" at (").Append(p.x.ToString("F0")).Append(',').Append(p.z.ToString("F0")).Append(')')
                  .Append(t.Moving ? $" {t.Speed:F0}m/s" : " standing")
                  .Append(t.Piloted > 0 ? $" PILOTED x{t.Piloted}" : "")
                  .Append(t.Seen ? "" : $" conf {t.Confidence:F2}");
                shown++;
            }
            for (int i = 0; i < _bases.Count && i < 3; i++)
            {
                var b = _bases[i];
                sb.Append(" | base ").Append(Short(b.Team)).Append(b.IsMain ? " MAIN" : " exp")
                  .Append(b.HasHq ? " HQ" : "").Append(' ').Append(b.Count).Append("s/")
                  .Append(b.Cost).Append(" at (").Append(b.Centre.x.ToString("F0")).Append(',')
                  .Append(b.Centre.z.ToString("F0")).Append(") age ").Append((b.AgeS / 60f).ToString("F0"))
                  .Append("m def ").Append(b.LocalDefenceEff.ToString("F0"))
                  .Append(b.Growth != 0 ? $" growth {b.Growth:+#;-#}" : "")
                  .Append(b.StaleS > RECON_STALE_S ? $" stale {b.StaleS:F0}s" : "");
            }
            MilLog.Msg(sb.ToString());
        }

        internal static string Short(string team)
        {
            if (string.IsNullOrEmpty(team)) return "?";
            if (team.Contains("Sol")) return "Sol";
            if (team.Contains("Cent")) return "Cen";
            if (team.Contains("Alien")) return "Ali";
            return team;
        }
    }
}
