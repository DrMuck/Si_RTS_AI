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

        // ---- HOW MUCH FORCE, as opposed to how much danger ------------------
        //
        // THE THREAT FIELD IS AN ACCUMULATOR AND CANNOT SIZE A FORCE. Stamp adds
        // a unit's attack rating over a disk the size of its weapon range, once
        // per second, and decay only removes ~1.5% of it in that time — so a
        // single tank standing still drives its cell toward sixty times its own
        // rating, spread over hundreds of cells. That is a fine SHAPE for "where
        // is it dangerous", which is all the ranking ever used it for.
        //
        // It is useless as an amount. Sized against it on 2026-08-07 the home
        // garrison asked for 1,466,026 cash of defenders against a real enemy of
        // perhaps twenty vehicles, so it swallowed every unit the alien built and
        // the defence missions each got zero for the entire round. The army sat
        // at the Nest in a blob while nine Bio Caches were destroyed.
        //
        // So force is measured separately and in CASH: the summed cost of enemy
        // units we can actually see, REPLACED each pass rather than accumulated,
        // and decayed only where we have lost sight. Same currency as a
        // battalion's strength, so a requirement needs no conversion constant at
        // all — the one that existed was invented and was wrong by four orders of
        // magnitude.
        static float[] _value = new float[0];      // believed enemy cash per cell
        static float[] _valueScratch = new float[0]; // what this pass actually saw

        /// <summary>Enemy cash we can see, or saw recently, within a radius.
        /// The number a defending force should be compared against.</summary>
        internal static int ValueNear(Vector3 world, float radiusM)
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
                    sum += _value[z * GridWorld.Width + x];
                }
            return Mathf.RoundToInt(sum);
        }

        /// <summary>Total enemy cash believed on the map.</summary>
        internal static int TotalValue { get; private set; }

        // ---- The two fields, published for the viewer -------------------------
        //
        // DrMuck asked for "a heatmap that shows enemy army pressure". Both
        // fields already exist per cell and were only ever read through point
        // queries, so the whole cost is copying them into the layer type the
        // replay writer speaks. They answer different questions and are worth
        // seeing side by side: PRESSURE is where it is dangerous to stand, which
        // spreads with weapon range; VALUE is where their army actually is.
        static readonly LayerI _pressureLayer = new LayerI();
        static readonly LayerI _valueLayer = new LayerI();

        /// <summary>Danger per cell — attack rating summed over weapon reach and
        /// decayed. Scaled to whole numbers for the layer format.</summary>
        internal static LayerI PressureLayer()
        {
            EnsureSized();
            _pressureLayer.EnsureSized();
            var d = _pressureLayer.Data;
            int n = Mathf.Min(d.Length, _threat.Length);
            for (int i = 0; i < n; i++) d[i] = Mathf.RoundToInt(_threat[i]);
            return _pressureLayer;
        }

        /// <summary>Enemy cash per cell, at the unit's own position.</summary>
        internal static LayerI ValueLayer()
        {
            EnsureSized();
            _valueLayer.EnsureSized();
            var d = _valueLayer.Data;
            int n = Mathf.Min(d.Length, _value.Length);
            for (int i = 0; i < n; i++) d[i] = Mathf.RoundToInt(_value[i]);
            return _valueLayer;
        }

        // ---- WHAT KIND OF PROBLEM they are, not just how much of it -----------
        //
        // Sizing a defence needs the amount; choosing what to BUILD needs the
        // kind. A thousand cash of infantry and a thousand cash of gunships are
        // answered by different units, and until now nothing recorded which one
        // was on the map.
        //
        // Cash-weighted rather than counted, for the same reason the counter
        // table is priced in cash: twenty Militia are not a bigger problem than
        // one Dreadnought merely because there are twenty of them.
        static readonly Dictionary<string, float> _mixNow = new Dictionary<string, float>();
        static readonly Dictionary<string, float> _mix = new Dictionary<string, float>();

        /// <summary>Enemy cash on the map by class — Infantry, Light, Heavy,
        /// UltraHeavy, Air. Empty when we can see nothing, which callers must
        /// read as "no opinion" rather than "no enemy".</summary>
        internal static IDictionary<string, float> EnemyMix => _mix;

        internal static string EnemyMixSummary()
        {
            if (_mix.Count == 0) return "nothing seen";
            var sb = new System.Text.StringBuilder();
            float tot = 0f;
            foreach (var kv in _mix) tot += kv.Value;
            if (tot <= 0f) return "nothing seen";
            foreach (var kv in _mix)
                if (kv.Value > 0f)
                    sb.Append(kv.Key).Append(' ').Append((100f * kv.Value / tot).ToString("F0"))
                      .Append("% ");
            return sb.ToString().TrimEnd();
        }

        struct Hvt { public string Name; public Vector3 Pos; public int Cost; public string Team; }
        static readonly List<Hvt> _hvt = new List<Hvt>(32);

        /// <summary>Total threat currently on the map, as one number. The enemy
        /// army estimate the push trigger compares against — poor, and the only
        /// one available from what we can see.</summary>
        internal static float Total { get; private set; }

        // ---- What we have SEEN of theirs, and still believe is there ---------
        //
        // Stamp() already finds enemy structures through the fog and then threw
        // them away every report. Keeping them is what turns scouting into
        // enemy-structure discovery, which MILITARY_TACTICS §8 names as the one
        // missing capability gating three of the four tactics — and it costs a
        // dictionary, because the sighting was already being made.
        //
        // Memory has to be able to be WRONG and then corrected, or it is just a
        // stale list: an entry whose ground we can currently see, and which was
        // not stamped this pass, is gone and is dropped. Anything we cannot see
        // is remembered at its last sighting, which is the honest state.

        internal struct Known
        {
            public string  Name;
            public string  Team;
            public Vector3 Pos;
            public int     Cost;
            public float   LastSeenAt;
        }

        static readonly Dictionary<string, Known> _known = new Dictionary<string, Known>(64);

        internal static int KnownCount => _known.Count;

        /// <summary>Every enemy structure we have seen and not since watched
        /// disappear. Copied out, because callers rank and filter it.</summary>
        internal static void ForEachKnown(Action<Known> fn)
        {
            if (fn == null) return;
            foreach (var kv in _known)
            {
                try { fn(kv.Value); } catch { }
            }
        }


        // ---- BASES, not buildings --------------------------------------------
        //
        // "Kill the enemy base" is not a sentence the layer could form. It knew
        // individual structures and picked the most expensive one, which is a
        // building, not a base — so the army walked at a Refinery and stopped,
        // and nothing ever said what finishing the job would cost.
        //
        // Structures are grouped by proximity into bases. Single-linkage at
        // BASE_LINK_M: anything within that of any member joins the cluster,
        // which is how a base actually looks — a spread of buildings, not a
        // circle around a centre.

        /// <summary>Two structures this close belong to the same base. Wide
        /// enough to hold a spread-out main, tight enough that a lone forward
        /// Refinery is its own thing and can be denied on its own.</summary>
        const float BASE_LINK_M = 700f;

        internal class Base
        {
            public string  Team;
            public Vector3 Centre;
            public int     Cost;          // everything standing in it, in cash
            public int     Count;
            public bool    HasHq;
            public float   NewestSeenAt;  // youngest member — a fresh expansion
            public readonly List<Known> Members = new List<Known>();
        }

        static readonly List<Base> _bases = new List<Base>(8);
        static float _lastClusterAt;

        /// <summary>Enemy bases as we currently believe them. Rebuilt on a slow
        /// cadence — a base is not a fast-moving thing.</summary>
        internal static IReadOnlyList<Base> Bases => _bases;

        static void RebuildBases(float now)
        {
            if (now - _lastClusterAt < 5f) return;
            _lastClusterAt = now;
            _bases.Clear();
            if (_known.Count == 0) return;

            var pending = new List<Known>(_known.Count);
            foreach (var kv in _known) pending.Add(kv.Value);

            float link2 = BASE_LINK_M * BASE_LINK_M;
            while (pending.Count > 0)
            {
                var seed = pending[pending.Count - 1];
                pending.RemoveAt(pending.Count - 1);
                var b = new Base { Team = seed.Team };
                b.Members.Add(seed);

                // Grow the cluster until nothing else is within reach of ANY
                // member. Quadratic in cluster size and that is fine: a map
                // holds tens of enemy structures, not thousands.
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
                            b.Members.Add(pending[i]);
                            pending.RemoveAt(i);
                            grew = true;
                            break;
                        }
                    }
                }

                Vector3 sum = Vector3.zero;
                for (int m = 0; m < b.Members.Count; m++)
                {
                    var k = b.Members[m];
                    sum += k.Pos;
                    b.Cost += k.Cost;
                    if (k.LastSeenAt > b.NewestSeenAt) b.NewestSeenAt = k.LastSeenAt;
                    if (k.Name != null &&
                        k.Name.IndexOf("Headquarters", StringComparison.OrdinalIgnoreCase) >= 0)
                        b.HasHq = true;
                }
                b.Count = b.Members.Count;
                b.Centre = sum / Mathf.Max(1, b.Members.Count);
                _bases.Add(b);
            }
            _bases.Sort((x, y) => y.Cost.CompareTo(x.Cost));
        }

        /// <summary>Their main, by value. Everything else of theirs is an
        /// expansion by definition, which is what objective #1 needs.</summary>
        internal static Base MainBaseOf(string team)
        {
            for (int i = 0; i < _bases.Count; i++)
                if (_bases[i].Team == team) return _bases[i];   // sorted by cost
            return null;
        }

        // ---- why does the army not know where to go? ------------------------
        //
        // The round said known=2 enemyHQs=0 after 29 minutes while scouting
        // reported explored=87.2% and lost 35 scouts on enemy ground. Those two
        // cannot both be true, so one of them is measured wrong, and every
        // candidate on inspection looked correct. This counts the gate directly:
        // how many enemy structures exist, how many our fog layer accepts, and
        // where the HQ is when we reject it.
        static int _structsSeenTotal, _structsInSight, _hqTotal, _hqInSight;
        static string _hqWhere;

        /// <summary>
        /// WHAT WE HAVE ACTUALLY FOUND, BY NAME.
        ///
        /// DrMuck: "the enemy HQ or at least enemy structures should have been
        /// discovered I guess. Maybe next run you check what buildings were
        /// discovered." A count cannot answer that — known=2 is equally
        /// consistent with two guard towers next to our own expansion and with
        /// a broken sighting path. The names and positions distinguish them
        /// immediately, and if the two turn out to be structures we can see
        /// from home then nothing about the enemy base was ever discovered and
        /// the fog test is not the suspect.
        /// </summary>
        static float _lastDiscoveryLogAt;
        static readonly string NL = System.Environment.NewLine;

        internal static void ReportDiscoveries(Vector3 homePos)
        {
            float now = Time.time;
            if (now - _lastDiscoveryLogAt < 60f) return;
            _lastDiscoveryLogAt = now;

            if (_known.Count == 0)
            {
                MelonLogger.Msg("[DISCOVERED] nothing of theirs is remembered at all");
                return;
            }

            var sb = new System.Text.StringBuilder();
            sb.Append("[DISCOVERED] ").Append(_known.Count).Append(" enemy structures:");
            int n = 0;
            foreach (var kv in _known)
            {
                if (++n > 12) { sb.Append(" ...+").Append(_known.Count - 12).Append(" more"); break; }
                var k = kv.Value;
                float dHome = Vector3.Distance(new Vector3(k.Pos.x, 0f, k.Pos.z),
                                               new Vector3(homePos.x, 0f, homePos.z));
                sb.Append(NL).Append("    ").Append(k.Name)
                  .Append(" (").Append(k.Pos.x.ToString("F0")).Append(',')
                  .Append(k.Pos.z.ToString("F0")).Append(") ")
                  .Append(dHome.ToString("F0")).Append("m from home, cost ").Append(k.Cost)
                  .Append(", last seen ").Append((now - k.LastSeenAt).ToString("F0")).Append("s ago");
            }
            MelonLogger.Msg(sb.ToString());
        }

        internal static string SightReport()
        {
            string r = $"structs {_structsInSight}/{_structsSeenTotal} in sight, " +
                       $"HQ {_hqInSight}/{_hqTotal}, known={_known.Count}, " +
                       $"vision from {MapLayers.FoWLayers.LastStructSources} structures " +
                       $"+ {MapLayers.FoWLayers.LastUnitSources} units";
            if (MapLayers.FoWLayers.LastFailure != null)
                r += " [FOW THREW: " + MapLayers.FoWLayers.LastFailure + "]";
            if (_hqTotal > 0 && _hqInSight == 0 && _hqWhere != null)
                r += " — nearest rejected: " + _hqWhere;
            _structsSeenTotal = _structsInSight = _hqTotal = _hqInSight = 0;
            _hqWhere = null;
            return r;
        }

        static string KeyOf(string team, string name, Vector3 p) =>
            team + "|" + name + "|" + Mathf.RoundToInt(p.x / 20f) + "," + Mathf.RoundToInt(p.z / 20f);

        internal static void ResetForNewRound()
        {
            _lastDiscoveryLogAt = 0f;
            _threat = new float[0];
            _value = new float[0];
            _valueScratch = new float[0];
            _lastTickAt = _lastReportAt = 0f;
            _self = null;
            Total = 0f;
            TotalValue = 0;
            _hvt.Clear();
            _known.Clear();
            _mix.Clear();
            _mixNow.Clear();
            _bases.Clear();
            _lastClusterAt = 0f;
        }

        static void EnsureSized()
        {
            int n = GridWorld.CellCount;
            if (_threat.Length != n)       _threat = new float[n];
            if (_value.Length != n)        _value = new float[n];
            if (_valueScratch.Length != n) _valueScratch = new float[n];
        }

        /// <summary>
        /// Called for EVERY team each tick. Ours sets the viewpoint; everyone
        /// else's units are stamped as threat.
        ///
        /// NO THROTTLE HERE, and that is a fix rather than an omission. This used
        /// to skip when `Time.time - _lastTickAt &lt; TICK_S`, sharing the stamp
        /// with the DECAY clock that Tick sets — so whether the threat field was
        /// ever built at all depended on where the alien team sat in
        /// MP_Strategy.TeamSetups. Alien last: every enemy is stamped, then decay
        /// runs. Alien FIRST: decay stamps the clock, and every enemy team that
        /// tick reads "already ticked" and is silently skipped, forever, on every
        /// tick. The caller is the 1Hz periodic tick, which is the cadence this
        /// wanted in the first place.
        /// </summary>
        internal static void Observe(Team team)
        {
            if (team == null) return;
            string tn = team.name ?? "";
            if (tn.Contains("Alien")) { _self = team; return; }

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
            float total = 0f;
            for (int i = 0; i < _threat.Length; i++) { _threat[i] *= keep; total += _threat[i]; }
            Total = total;

            try { CommitValue(keep); }
            catch (Exception ex) { MelonLogger.Warning("[THREAT] value commit threw: " + ex.Message); }

            try { ForgetWhatWeCanSeeIsGone(now); }
            catch (Exception ex) { MelonLogger.Warning("[THREAT] forget threw: " + ex.Message); }

            try { RebuildBases(now); }
            catch (Exception ex) { MelonLogger.Warning("[THREAT] cluster threw: " + ex.Message); }

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

                    // Value goes in ONE cell — where the unit actually stands.
                    // Danger reaches, a tank does not. Spreading cost over a
                    // weapon-range disk is how the threat field stopped meaning
                    // anything countable.
                    string un = u.ObjectInfo.DisplayName ?? "";
                    int ucost = UnitValues.CostOf(un);
                    _valueScratch[cz * GridWorld.Width + cx] += ucost;

                    string cls = null;
                    try { cls = Planning.UnitPrior.ClassOf(un); } catch { }
                    if (cls != null)
                    {
                        _mixNow.TryGetValue(cls, out float had);
                        _mixNow[cls] = had + ucost;
                    }
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
                _structsSeenTotal++;
                bool inSight = visible == null || visible.IsSet(cx, cz);
                string sname0 = st.ObjectInfo.DisplayName ?? "?";
                if (sname0.IndexOf("Headquarters", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    _hqTotal++;
                    if (inSight) _hqInSight++;
                    else if (_hqWhere == null)
                        _hqWhere = $"{sname0} at ({p.x:F0},{p.z:F0}) cell({cx},{cz})";
                }
                if (!inSight) continue;
                _structsInSight++;
                string sname = sname0;
                int    scost = 0;
                try { scost = st.ObjectInfo.Cost; } catch { }
                string steam = enemy.name ?? "?";
                _hvt.Add(new Hvt { Name = sname, Pos = p, Cost = scost, Team = steam });
                _known[KeyOf(steam, sname, p)] = new Known
                {
                    Name = sname, Team = steam, Pos = p, Cost = scost, LastSeenAt = Time.time,
                };
            }
        }

        /// <summary>
        /// REPLACE WHAT WE CAN SEE, DECAY WHAT WE CANNOT.
        ///
        /// A cell inside our fog is simply told what is standing on it this
        /// second, including nothing at all — that is what keeps the field an
        /// amount rather than a running total. A cell we cannot see keeps its
        /// last reading and fades, which is the honest state of a force that
        /// walked out of sight: probably still there, less certainly so as time
        /// passes.
        ///
        /// Without a fog layer we cannot tell "saw nothing there" from "did not
        /// look", so everything decays and only sightings refresh it.
        /// </summary>
        static void CommitValue(float keep)
        {
            LayerB visible = null;
            try { if (_self != null) visible = FoWLayers.GetActive(_self); } catch { }

            float total = 0f;
            int w = GridWorld.Width;
            for (int i = 0; i < _value.Length; i++)
            {
                bool seen = visible == null ? false : visible.IsSet(i % w, i / w);
                _value[i] = seen ? _valueScratch[i] : _value[i] * keep;
                if (visible == null && _valueScratch[i] > 0f) _value[i] = _valueScratch[i];
                total += _value[i];
                _valueScratch[i] = 0f;
            }
            TotalValue = Mathf.RoundToInt(total);

            // Same replace-what-we-see rule as the value field: the mix is what
            // is on the map now, faded where we have stopped looking, never a
            // running total of everything ever seen.
            var classes = new List<string>(_mix.Keys);
            foreach (var k in _mixNow.Keys) if (!classes.Contains(k)) classes.Add(k);
            foreach (var k in classes)
            {
                _mixNow.TryGetValue(k, out float now);
                _mix.TryGetValue(k, out float was);
                _mix[k] = now > 0f ? now : was * keep;
            }
            _mixNow.Clear();
        }

        /// <summary>
        /// A remembered structure standing on ground we are looking at RIGHT NOW,
        /// which nothing stamped this pass, has been destroyed. Drop it.
        ///
        /// Without this the memory only ever grows and a razed Refinery stays a
        /// push objective forever — which is worse than not remembering at all,
        /// because the army would keep walking to it.
        /// </summary>
        static void ForgetWhatWeCanSeeIsGone(float now)
        {
            if (_known.Count == 0 || _self == null) return;
            LayerB visible = null;
            try { visible = FoWLayers.GetActive(_self); } catch { }
            if (visible == null) return;

            List<string> gone = null;
            foreach (var kv in _known)
            {
                // Stamping runs on the same cadence as this, so anything seen
                // within a couple of ticks is current.
                if (now - kv.Value.LastSeenAt < TICK_S * 3f) continue;
                int cx = GridWorld.CellX(kv.Value.Pos.x), cz = GridWorld.CellZ(kv.Value.Pos.z);
                if (cx < 0 || cz < 0 || cx >= GridWorld.Width || cz >= GridWorld.Height) continue;
                if (!visible.IsSet(cx, cz)) continue;         // out of sight, still believed
                (gone ??= new List<string>()).Add(kv.Key);
            }
            if (gone == null) return;
            for (int i = 0; i < gone.Count; i++) _known.Remove(gone[i]);
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
            sb.Append("[THREAT] danger=").Append((int)total).Append(" peak=").Append((int)peak)
              .Append(" enemyValue=").Append(TotalValue)
              .Append(" known=").Append(_known.Count)
              .Append(" bases=").Append(_bases.Count);
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
