using MelonLoader;
using Silica;
using Si_RTS_AI.Perception;
using Si_RTS_AI.Planning;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;

namespace Si_RTS_AI.Mil
{
    /// <summary>
    /// DRMUCK'S TWO ORDERED LISTS, PRICED BY THE KERNEL, WITH EXPECTATIONS.
    ///
    /// An objective is a ranked intent with a place, a price and an expectation
    /// (MILITARY_MODEL section "The model, stated once"). Every fifteen seconds
    /// the candidates are regenerated from Intel, priced with the kernel, and
    /// assembled into a portfolio in the spec's order:
    ///
    ///   constraint    DefendQueen                      never outbid, never done
    ///   by arrival    DefendEco / DefendProduction     funded when a force can beat the eta
    ///   on our ground Intercept                        small, fast or piloted tracks
    ///   offence       DenyExpansion > RaidEco > RaidProduction > BreakArmy > KillHQ*
    ///   cheap         Recon                            one fast unit to a stale base
    ///
    /// *KillHQ is the win condition: when it is affordable it outranks every
    /// other offensive objective, because the four drivers exist to make an HQ
    /// killable (DrMuck: "destruct all enemy HQs is the objective").
    ///
    /// Forces fill the portfolio in order; whatever is left is the reserve, and
    /// that is "hold and push" without a posture machine — the reserve grows
    /// until something offensive becomes affordable, then it leaves.
    ///
    /// A REFRESH RE-SCORES, IT DOES NOT CANCEL. An active objective ends on
    /// completion, on its own expectation failing, or on the kernel telling the
    /// force it is losing. Never a timer, never because something else scored
    /// higher this tick (USER_RULES 11.12).
    ///
    /// EXPECTATIONS are what this project has never had: each objective states
    /// what must become true and by when, checks it, and writes the outcome to
    /// objectives.jsonl with the kernel's prediction. That is the dataset for
    /// learning which kinds pay.
    /// </summary>
    internal static class Objectives
    {
        internal enum Kind
        {
            DefendQueen, DefendEco, DefendProduction, Intercept,
            DenyExpansion, RaidEco, RaidProduction, BreakArmy, KillHQ,
            Recon
        }

        internal enum Status { Proposed, Active, Done, Failed }

        internal enum Pool { Any, Fast, Line }

        internal sealed class Objective
        {
            public int      Id;
            public Kind     Kind;
            public string   Key = "";          // stable identity across refreshes
            public Vector3  Where;
            public float    Radius = 300f;
            public float    RequiredEff;       // effective cash the force must hold
            public float    CeilingEff;        // past this the surplus belongs elsewhere
            public float    DefenceEff;        // what it is priced against
            public float    DeadlineAt = float.PositiveInfinity;   // absolute Time.time
            public float    Gain;              // cash-equivalent value of success
            public float    ExpectedLoss;
            public float    PWin;
            public int      Rank;              // lower first
            public float    Score;             // within rank
            public Pool     PoolPref;
            public bool     Offensive;
            public bool     Attack;            // true: engage; false: stand
            public Status   Status = Status.Proposed;
            public float    CreatedAt, ActivatedAt, EngagedAt, EndedAt;
            public string   Note = "";
            public string   Expectation = "";
            public string   Outcome = "";
            // handles into Intel
            public Intel.Base           BaseRef;
            public Intel.Track          TrackRef;
            public Intel.KnownStructure StructRef;
            public Structure            OwnStructRef;    // the thing we defend
            // what the force reported
            public float AssignedEff, PeakAssignedEff;
            public int   AssignedUnits;
            public bool  ForceEngaged;
            // baselines for expectations
            public int   BaseCountAt;
            public float TrackEffAt, TargetHpAt, ForceEffAtEngage;
            public float LastProgressAt;
        }

        internal static readonly List<Objective> Portfolio = new List<Objective>(16);
        static readonly Dictionary<string, Objective> _byKey = new Dictionary<string, Objective>();
        static readonly Dictionary<string, float> _holdUntil = new Dictionary<string, float>();

        /// <summary>
        /// WHAT A BASE ACTUALLY ANSWERED WITH. The kernel prices a base against
        /// the force it can SEE there; a human HQ answers a two-Crab raid with
        /// infantry that did not exist when the raid was priced. When an
        /// offensive objective fails, the largest enemy force met near the
        /// target is remembered for that base and every later price starts from
        /// it. Half-life ten minutes: a base can be reinforced or stripped.
        /// </summary>
        static readonly Dictionary<int, (float eff, float at)> _baseAnswer = new Dictionary<int, (float, float)>();
        const float ANSWER_HALF_LIFE_S = 600f;

        static float AnsweredWith(Intel.Base b)
        {
            if (b == null || !_baseAnswer.TryGetValue(b.Id, out var a)) return 0f;
            return a.eff * Mathf.Pow(0.5f, (Time.time - a.at) / ANSWER_HALF_LIFE_S);
        }
        static readonly List<Intel.Arrival> _arr = new List<Intel.Arrival>(8);
        static readonly List<Intel.Base> _stale = new List<Intel.Base>(4);

        const float REFRESH_S     = 15f;
        const float CHECK_S       = 2f;
        const float HOLD_AFTER_FAIL_S = 120f;
        const float DEFEND_GRACE_S = 60f;
        const float RAZE_BUDGET_S  = 240f;    // a raid should finish inside this
        const float BASE_REACH_M   = 1200f;   // enemy reinforcements counted from here

        static float _lastRefreshAt, _lastCheckAt, _lastLogAt;
        static int _nextId;
        static Team _team;
        static Vector3 _nest;
        internal static float ArmyEff { get; private set; }

        /// <summary>
        /// OUTNUMBERED. The enemy's estimated strength exceeds the whole army.
        /// Great Erg and Monument Valley, 2026-09-07: Sol rushed at minute four
        /// to twelve with 5,000 to 9,000 strength, the starter army bled out
        /// defending outlying biotic centres one force at a time, the economy
        /// spent to the last credit so military production had no budget, and
        /// the Nest fell. Under siege the rules change: the army has right of
        /// way over expansion, no defence force leaves the Nest for an eco
        /// site, and the Nest may take a second spire below the cash floor.
        /// </summary>
        internal static bool UnderSiege { get; private set; }
        const float SIEGE_MARGIN = 1.0f;
        internal static int   ArmyCash { get; private set; }
        internal static float DpsPerEff { get; private set; } = 0.05f;

        /// <summary>Price of the cheapest offensive objective the army cannot yet
        /// afford — what the reserve is building toward. 0 when nothing is known.</summary>
        internal static float NextOffensivePrice { get; private set; }
        internal static string NextOffensiveWhat { get; private set; } = "";

        internal static void ResetForNewRound()
        {
            _forecastSince.Clear();
            Portfolio.Clear(); _byKey.Clear(); _holdUntil.Clear(); _baseAnswer.Clear();
            _lastRefreshAt = _lastCheckAt = _lastLogAt = 0f;
            _nextId = 0; _team = null; _nest = Vector3.zero;
            ArmyEff = 0f; ArmyCash = 0; NextOffensivePrice = 0f; NextOffensiveWhat = "";
        }

        internal static void Tick(Team team)
        {
            if (!MilConfig.Enabled || team == null) return;
            // A PLAYER IN THE ALIEN COMMANDER SEAT COMMANDS. The game disables its
            // own AI commander then, and so does this layer, exactly as vanilla
            // would: it must never spend a player's cash or move a player's units.
            try { if (!Silica.AI.AIManager.IsCommanderEnabled(team)) return; } catch { }
            _team = team;
            float now = Time.time;
            try
            {
                if (now - _lastCheckAt >= CHECK_S)
                {
                    _lastCheckAt = now;
                    MeasureArmy(team, now);
                    CheckExpectations(now);
                }
                if (now - _lastRefreshAt >= REFRESH_S)
                {
                    _lastRefreshAt = now;
                    Refresh(team, now);
                }
                if (now - _lastLogAt >= 15f) { _lastLogAt = now; Log(); }
            }
            catch (Exception ex) { MelonLogger.Warning("[OBJ] tick threw: " + ex.Message); }
        }

        // ---- measurement -----------------------------------------------------

        static void MeasureArmy(Team team, float now)
        {
            var f = new Kernel.Force();
            try
            {
                var units = team.Units;
                if (units != null)
                    for (int i = 0; i < units.Count; i++)
                    {
                        var u = units[i];
                        if (u?.ObjectInfo == null || u.IsDestroyed) continue;
                        string n = u.ObjectInfo.DisplayName ?? "";
                        if (n == "Shrimp" || n == "Queen") continue;
                        f.Add(n);
                    }
            }
            catch { }
            ArmyEff = f.Effective();
            ArmyCash = f.Cash();
            bool siege = Intel.EnemyEffective > ArmyEff * SIEGE_MARGIN;
            if (siege != UnderSiege)
                MilLog.Msg(siege ? $"[OBJ] UNDER SIEGE: enemy ~{Intel.EnemyEffective:F0} eff against our {ArmyEff:F0} — army has right of way, home guard stays home"
                                 : $"[OBJ] siege lifted: enemy ~{Intel.EnemyEffective:F0} eff against our {ArmyEff:F0}");
            UnderSiege = siege;
            float dps = f.Dps();
            if (ArmyEff > 0f && dps > 0f) DpsPerEff = dps / ArmyEff;
            _nest = Intel.Nest;
            float roundS = 0f;
            try { roundS = Perception.MapLayers.LayerReplay.CurrentRoundTime; } catch { }
            try { ArmyPlan.Update(roundS, ArmyCash); } catch { }
        }

        // ---- refresh -----------------------------------------------------------

        static void Refresh(Team team, float now)
        {
            var fresh = new List<Objective>(16);
            ProposeDefence(team, now, fresh);
            ProposeIntercepts(now, fresh);
            if (MilConfig.Offence) ProposeOffence(now, fresh);
            if (MilConfig.ReconEnabled) ProposeRecon(now, fresh);

            // Merge: an existing ACTIVE objective keeps its identity and force;
            // its price is re-read from the fresh proposal. Proposed ones are
            // simply replaced. Active ones the refresh no longer proposes are
            // left to their expectation check.
            var seen = new HashSet<string>();
            var next = new List<Objective>(16);
            for (int i = 0; i < fresh.Count; i++)
            {
                var f = fresh[i];
                if (_holdUntil.TryGetValue(f.Key, out float until) && now < until) continue;
                if (!seen.Add(f.Key)) continue;
                if (_byKey.TryGetValue(f.Key, out var old) && old.Status == Status.Active)
                {
                    if (old.Kind == Kind.DefendQueen) { old.AssignedEff = f.AssignedEff; old.AssignedUnits = f.AssignedUnits; }
                    old.RequiredEff = f.RequiredEff; old.CeilingEff = f.CeilingEff;
                    old.DefenceEff = f.DefenceEff; old.Gain = f.Gain; old.ExpectedLoss = f.ExpectedLoss;
                    old.PWin = f.PWin; old.Rank = f.Rank; old.Score = f.Score; old.Note = f.Note;
                    old.Where = f.Where; old.BaseRef = f.BaseRef ?? old.BaseRef; old.TrackRef = f.TrackRef ?? old.TrackRef;
                    old.StructRef = f.StructRef ?? old.StructRef;
                    if (!float.IsInfinity(f.DeadlineAt)) old.DeadlineAt = f.DeadlineAt;
                    next.Add(old);
                }
                else
                {
                    f.Id = ++_nextId; f.CreatedAt = now;
                    next.Add(f);
                }
            }
            // Keep active objectives that were not re-proposed (their check ends them).
            for (int i = 0; i < Portfolio.Count; i++)
            {
                var o = Portfolio[i];
                if (o.Status == Status.Active && !seen.Contains(o.Key)) { next.Add(o); seen.Add(o.Key); }
            }
            next.Sort((a, b) =>
            {
                int c = a.Rank.CompareTo(b.Rank);
                if (c != 0) return c;
                if (a.Rank == 1) return a.DeadlineAt.CompareTo(b.DeadlineAt);
                return b.Score.CompareTo(a.Score);
            });
            Portfolio.Clear(); Portfolio.AddRange(next);
            _byKey.Clear();
            for (int i = 0; i < Portfolio.Count; i++) _byKey[Portfolio[i].Key] = Portfolio[i];

            // What the reserve is building toward.
            NextOffensivePrice = 0f; NextOffensiveWhat = "";
            for (int i = 0; i < Portfolio.Count; i++)
            {
                var o = Portfolio[i];
                if (!o.Offensive || o.Kind == Kind.Recon) continue;
                if (o.RequiredEff <= ArmyEff) continue;
                if (NextOffensivePrice <= 0f || o.RequiredEff < NextOffensivePrice)
                { NextOffensivePrice = o.RequiredEff; NextOffensiveWhat = $"{o.Kind} {o.Note}"; }
            }
        }

        // ---- candidates: defence ---------------------------------------------

        static void ProposeDefence(Team team, float now, List<Objective> into)
        {
            if (_nest == Vector3.zero) return;

            // Queen: the constraint.
            Intel.ForecastFor(_nest, 600f, MilConfig.ForecastHorizonS, _arr);
            float coming = SumArrivals(_arr, out float firstEta);
            float need = Mathf.Max(Kernel.PriceToBeat(coming), MilConfig.HomeFloorCash);
            var q = new Objective
            {
                Kind = Kind.DefendQueen, Key = "queen", Where = _nest, Radius = 600f,
                RequiredEff = need, CeilingEff = Mathf.Max(need, coming * Doctrine.WastefulAbove),
                DefenceEff = coming, Rank = 0, Score = float.MaxValue, PoolPref = Pool.Line,
                Attack = false, Status = Status.Active,
                Note = coming > 0f ? $"nest: {_arr.Count} tracks arriving, first in {firstEta:F0}s, eff {coming:F0}"
                                   : "nest floor",
                Expectation = "the Nest stands and nothing reaches it unopposed",
            };
            q.DeadlineAt = coming > 0f ? now + firstEta : float.PositiveInfinity;
            q.AssignedEff = Forces.ReserveEff; q.AssignedUnits = Forces.ReserveUnits;
            into.Add(q);

            // Earning sites and producer clusters with something arriving.
            var sites = new List<(Vector3 pos, long recent, string what, Structure s)>();
            try
            {
                BcIncome.ForEach((pos, lifetime, recent) =>
                {
                    if (recent <= 0) return;
                    if ((pos - _nest).sqrMagnitude < 600f * 600f) return;   // the Queen covers it
                    sites.Add((pos, recent, "eco", null));
                });
            }
            catch { }
            try
            {
                var structs = team.Structures;
                if (structs != null)
                    for (int i = 0; i < structs.Count; i++)
                    {
                        var s = structs[i];
                        if (s?.ObjectInfo == null || s.IsDestroyed) continue;
                        string n = s.ObjectInfo.DisplayName ?? "";
                        if (n.IndexOf("Spawning Cyst", StringComparison.OrdinalIgnoreCase) < 0 ||
                            n.StartsWith("Lesser", StringComparison.OrdinalIgnoreCase)) continue;
                        Vector3 p = s.transform.position;
                        if ((p - _nest).sqrMagnitude < 600f * 600f) continue;
                        bool dup = false;
                        for (int k = 0; k < sites.Count; k++)
                            if (sites[k].what == "producers" && (sites[k].pos - p).sqrMagnitude < 400f * 400f) { dup = true; break; }
                        if (!dup) sites.Add((p, 0, "producers", s));
                    }
            }
            catch { }

            for (int i = 0; i < sites.Count; i++)
            {
                var site = sites[i];
                Intel.ForecastFor(site.pos, 350f, MilConfig.ForecastHorizonS, _arr);
                bool prod = site.what == "producers";
                string key = (prod ? "defprod@" : "defeco@") + Cell(site.pos);
                if (_arr.Count == 0) { _forecastSince.Remove(key); continue; }
                float eff = SumArrivals(_arr, out float eta);
                if (eff <= 0f) { _forecastSince.Remove(key); continue; }
                // A FORECAST HAS TO HOLD. Overnight round 4: 83 DefendEco objectives
                // raised, 75 finished with "nothing arrived, or it left" after one
                // to three minutes — a track that turned away a refresh later had
                // already cost a force its rally walk. A site is defended only
                // once its forecast has held for FORECAST_CONFIRM_S across
                // refreshes; a threat that is real is still there twenty seconds
                // later, and the horizon is three minutes.
                if (!_forecastSince.TryGetValue(key, out float since)) { _forecastSince[key] = now; continue; }
                if (now - since < FORECAST_CONFIRM_S) continue;
                float price = Kernel.PriceToBeat(eff);
                var o = new Objective
                {
                    Kind = prod ? Kind.DefendProduction : Kind.DefendEco,
                    Key = key,
                    Where = site.pos, Radius = 350f,
                    RequiredEff = price, CeilingEff = eff * Doctrine.WastefulAbove, DefenceEff = eff,
                    DeadlineAt = now + eta, Rank = 1, Score = prod ? 1e6f : site.recent,
                    PoolPref = Pool.Any, Attack = true, Offensive = false, OwnStructRef = site.s,
                    Gain = prod ? 6000f : site.recent, ExpectedLoss = Kernel.ExpectedLossOfWinner(price, eff),
                    PWin = Kernel.PWin(price, eff),
                    Note = $"{site.what} at ({site.pos.x:F0},{site.pos.z:F0}): {_arr.Count} tracks, eff {eff:F0}, first in {eta:F0}s" +
                           (site.recent > 0 ? $", earned {site.recent}" : ""),
                    Expectation = $"site holds; no track inside 350m after t+{eta + DEFEND_GRACE_S:F0}s",
                };
                into.Add(o);
            }
        }

        const float FORECAST_CONFIRM_S = 20f;
        static readonly Dictionary<string, float> _forecastSince = new Dictionary<string, float>();

        static float SumArrivals(List<Intel.Arrival> arr, out float firstEta)
        {
            float e = 0f; firstEta = float.PositiveInfinity;
            for (int i = 0; i < arr.Count; i++)
            {
                e += arr[i].Effective * arr[i].Confidence;
                if (arr[i].EtaS < firstEta) firstEta = arr[i].EtaS;
            }
            if (float.IsInfinity(firstEta)) firstEta = 0f;
            return e;
        }

        static void ProposeIntercepts(float now, List<Objective> into)
        {
            var tracks = Intel.Tracks;
            for (int i = 0; i < tracks.Count; i++)
            {
                var t = tracks[i];
                if (!t.InsideOurGround || !t.Seen) continue;
                if (now - t.FirstSeenAt < 4f) continue;            // a fresh cluster, not yet a group
                if (t.Effective < 100f && t.Piloted == 0) continue; // one scout is not an incursion
                bool small = t.Effective < Mathf.Max(3000f, ArmyEff * 0.15f);
                if (!small && t.Piloted == 0) continue;
                // Already answered by a site defence within reach? Let the
                // defence have it; intercepts are for what slips between sites.
                float price = Kernel.PriceToBeat(t.Effective);
                into.Add(new Objective
                {
                    Kind = Kind.Intercept, Key = "intercept#" + t.Id, Where = t.Predicted(), Radius = 300f,
                    RequiredEff = price, CeilingEff = t.Effective * Doctrine.WastefulAbove, DefenceEff = t.Effective,
                    Rank = 2, Score = t.Piloted > 0 ? 1e6f + t.Effective : t.Effective,
                    PoolPref = t.Fast ? Pool.Fast : Pool.Any, Attack = true, TrackRef = t,
                    Gain = t.Cash, ExpectedLoss = Kernel.ExpectedLossOfWinner(price, t.Effective),
                    PWin = Kernel.PWin(price, t.Effective),
                    Note = $"track #{t.Id} {t.Count}u eff {t.Effective:F0}{(t.Piloted > 0 ? " PILOTED" : "")} on our ground",
                    Expectation = "track loses half its force within 90s of contact",
                });
            }
        }

        // ---- candidates: offence ---------------------------------------------

        static void ProposeOffence(float now, List<Objective> into)
        {
            var bases = Intel.Bases;

            for (int i = 0; i < bases.Count; i++)
            {
                var b = bases[i];
                float reinforce = Intel.EffectiveNear(b.Centre, BASE_REACH_M);
                float local = Mathf.Max(b.LocalDefenceEff, Mathf.Max(reinforce, AnsweredWith(b)));

                // KillHQ — the win condition.
                if (b.HasHq)
                {
                    float def = Mathf.Max(local, Intel.EnemyEffectiveOf(b.Team));
                    float beat = Kernel.PriceToBeat(def);
                    float razeNeed = b.TotalHp / (RAZE_BUDGET_S * Mathf.Max(0.01f, DpsPerEff));
                    float price = Mathf.Max(beat, razeNeed);
                    bool affordable = ArmyEff >= price;
                    into.Add(new Objective
                    {
                        Kind = Kind.KillHQ, Key = "killhq@" + b.Key, Where = b.Centre, Radius = 500f,
                        RequiredEff = price, CeilingEff = Mathf.Max(price, def * Doctrine.WastefulAbove), DefenceEff = def,
                        Rank = affordable ? 3 : 7, Score = b.Cost / Mathf.Max(1f, price),
                        PoolPref = Pool.Line, Attack = true, Offensive = true, BaseRef = b,
                        Gain = b.Cost * 2f, ExpectedLoss = Kernel.ExpectedLossOfWinner(price, def),
                        PWin = Kernel.PWin(price, def),
                        Note = $"{Intel.Short(b.Team)} HQ base {b.Count}s/{b.Cost} at ({b.Centre.x:F0},{b.Centre.z:F0}) def {def:F0}" +
                               (affordable ? " AFFORDABLE" : $" (need {price:F0}, have {ArmyEff:F0})"),
                        Expectation = "HQ health falls once engaged; base structure count falls",
                    });
                }

                // DenyExpansion — fresh, or still growing, not their main.
                if (!b.IsMain && (b.AgeS < 360f || b.Growth > 0))
                {
                    float beat = Kernel.PriceToBeat(local);
                    float razeNeed = b.TotalHp / (RAZE_BUDGET_S * Mathf.Max(0.01f, DpsPerEff));
                    float price = Mathf.Max(beat, razeNeed);
                    into.Add(new Objective
                    {
                        Kind = Kind.DenyExpansion, Key = "deny@" + b.Key, Where = b.Centre, Radius = 400f,
                        RequiredEff = price, CeilingEff = Mathf.Max(price, local * Doctrine.WastefulAbove), DefenceEff = local,
                        Rank = 4, Score = (b.Cost + 2000f) / Mathf.Max(1f, Kernel.ExpectedLossOfWinner(price, local) + 500f),
                        PoolPref = Pool.Any, Attack = true, Offensive = true, BaseRef = b,
                        Gain = b.Cost + 2000f, ExpectedLoss = Kernel.ExpectedLossOfWinner(price, local),
                        PWin = Kernel.PWin(price, local),
                        Note = $"{Intel.Short(b.Team)} expansion {b.Count}s/{b.Cost} age {b.AgeS / 60f:F0}m growth {b.Growth:+#;-#;0} def {local:F0}",
                        Expectation = "structure count stops rising within 120s of contact, then falls",
                    });
                }

                // RaidEco — their resource structures and harvesters, weakly held.
                int ecoCost = 0, ecoN = 0; float ecoHp = 0f; Intel.KnownStructure ecoTarget = null;
                for (int m = 0; m < b.Members.Count; m++)
                {
                    var k = b.Members[m];
                    if (k.Class != Intel.StructClass.Resource) continue;
                    ecoCost += k.Cost; ecoN++; ecoHp += k.MaxHp > 0f ? k.MaxHp : 3000f;
                    if (ecoTarget == null || k.Cost > ecoTarget.Cost) ecoTarget = k;
                }
                if (ecoTarget != null || b.Harvesters > 0)
                {
                    float beat = Kernel.PriceToBeat(local);
                    float razeNeed = (ecoTarget != null ? ecoTarget.MaxHp : 1000f) / (RAZE_BUDGET_S * Mathf.Max(0.01f, DpsPerEff));
                    float price = Mathf.Max(beat, razeNeed);
                    float gain = ecoCost + b.Harvesters * 600f;
                    into.Add(new Objective
                    {
                        Kind = Kind.RaidEco, Key = "raideco@" + b.Key,
                        Where = ecoTarget != null ? ecoTarget.Pos : b.Centre, Radius = 300f,
                        RequiredEff = price, CeilingEff = Mathf.Max(price, local * Doctrine.WastefulAbove), DefenceEff = local,
                        Rank = 5, Score = gain / Mathf.Max(1f, Kernel.ExpectedLossOfWinner(price, local) + 500f),
                        PoolPref = Pool.Fast, Attack = true, Offensive = true, BaseRef = b, StructRef = ecoTarget,
                        Gain = gain, ExpectedLoss = Kernel.ExpectedLossOfWinner(price, local),
                        PWin = Kernel.PWin(price, local),
                        Note = $"{Intel.Short(b.Team)} eco: {ecoN} resource structures ({ecoCost}), {b.Harvesters} harvesters, def {local:F0}",
                        Expectation = "target destroyed or >=1:1 cash exchange inside the raze budget",
                    });
                }

                // RaidProduction — factories that are not the HQ.
                Intel.KnownStructure prodTarget = null; int prodCost = 0, prodN = 0;
                for (int m = 0; m < b.Members.Count; m++)
                {
                    var k = b.Members[m];
                    if (k.Class != Intel.StructClass.Production) continue;
                    prodCost += k.Cost; prodN++;
                    if (prodTarget == null || k.Cost > prodTarget.Cost) prodTarget = k;
                }
                if (prodTarget != null)
                {
                    float beat = Kernel.PriceToBeat(local);
                    float razeNeed = prodTarget.MaxHp / (RAZE_BUDGET_S * Mathf.Max(0.01f, DpsPerEff));
                    float price = Mathf.Max(beat, razeNeed);
                    into.Add(new Objective
                    {
                        Kind = Kind.RaidProduction, Key = "raidprod@" + b.Key, Where = prodTarget.Pos, Radius = 300f,
                        RequiredEff = price, CeilingEff = Mathf.Max(price, local * Doctrine.WastefulAbove), DefenceEff = local,
                        Rank = 6, Score = prodCost / Mathf.Max(1f, Kernel.ExpectedLossOfWinner(price, local) + 500f),
                        PoolPref = Pool.Any, Attack = true, Offensive = true, BaseRef = b, StructRef = prodTarget,
                        Gain = prodCost, ExpectedLoss = Kernel.ExpectedLossOfWinner(price, local),
                        PWin = Kernel.PWin(price, local),
                        Note = $"{Intel.Short(b.Team)} production: {prodN} factories ({prodCost}), def {local:F0}",
                        Expectation = "target destroyed inside the raze budget",
                    });
                }
            }

            // BreakArmy — a track that threatens something, at the commit band.
            var tracks = Intel.Tracks;
            for (int i = 0; i < tracks.Count; i++)
            {
                var t = tracks[i];
                if (t.Effective < 2000f || !t.Seen) continue;
                // A group we have watched for a while, not a cluster that formed
                // as a scout passed: BreakArmy forces were raised and released
                // every few seconds against tracks that vanished as fast.
                if (now - t.FirstSeenAt < 20f) continue;
                bool threatens = t.InsideOurGround || t.Moving && _nest != Vector3.zero &&
                                 Vector3.Angle(t.Vel, _nest - t.Pos) < 45f;
                if (!threatens) continue;
                float price = Kernel.PriceToBeat(t.Effective);
                into.Add(new Objective
                {
                    Kind = Kind.BreakArmy, Key = "break#" + t.Id, Where = t.Predicted(), Radius = 350f,
                    RequiredEff = price, CeilingEff = t.Effective * Doctrine.WastefulAbove, DefenceEff = t.Effective,
                    Rank = 7, Score = t.Cash / Mathf.Max(1f, Kernel.ExpectedLossOfWinner(price, t.Effective) + 500f),
                    PoolPref = Pool.Line, Attack = true, Offensive = true, TrackRef = t,
                    Gain = t.Cash, ExpectedLoss = Kernel.ExpectedLossOfWinner(price, t.Effective),
                    PWin = Kernel.PWin(price, t.Effective),
                    Note = $"track #{t.Id} {t.Count}u eff {t.Effective:F0} heading our way",
                    Expectation = "track loses half its force before we do",
                });
            }
        }

        static void ProposeRecon(float now, List<Objective> into)
        {
            Intel.StaleBases(_stale);
            for (int i = 0; i < _stale.Count && i < 2; i++)
            {
                var b = _stale[i];
                into.Add(new Objective
                {
                    Kind = Kind.Recon, Key = "recon@" + b.Key, Where = b.Centre, Radius = 400f,
                    RequiredEff = 1f, CeilingEff = 1f, Rank = 8, Score = b.Cost,
                    PoolPref = Pool.Fast, Attack = false, Offensive = true, BaseRef = b,
                    Note = $"overfly {Intel.Short(b.Team)} base at ({b.Centre.x:F0},{b.Centre.z:F0}), unseen {b.StaleS:F0}s",
                    Expectation = "the base is seen again",
                });
            }
        }

        // ---- expectations --------------------------------------------------------

        /// <summary>Called by Forces when a force first has units on an objective.</summary>
        internal static void NoteActivated(Objective o, float now)
        {
            if (o.Status != Status.Proposed) return;
            o.Status = Status.Active; o.ActivatedAt = now; o.LastProgressAt = now;
            o.BaseCountAt = o.BaseRef?.Count ?? 0;
            o.TrackEffAt = o.TrackRef?.Effective ?? 0f;
            o.TargetHpAt = HqHp(o.BaseRef, o.StructRef);
            MilLog.Msg($"[OBJ] #{o.Id} ACTIVE {o.Kind} — {o.Note} | need {o.RequiredEff:F0} eff, pWin {o.PWin:F2}, " +
                       $"expect: {o.Expectation}");
        }

        internal static void NoteEngaged(Objective o, float forceEff, float now)
        {
            if (o.ForceEngaged) return;
            o.ForceEngaged = true; o.EngagedAt = now; o.ForceEffAtEngage = forceEff;
            o.BaseCountAt = o.BaseRef?.Count ?? o.BaseCountAt;
            o.TrackEffAt = o.TrackRef?.Effective ?? o.TrackEffAt;
            o.TargetHpAt = HqHp(o.BaseRef, o.StructRef);
            o.LastProgressAt = now;
            MilLog.Msg($"[OBJ] #{o.Id} ENGAGED {o.Kind} with {forceEff:F0} eff against {o.DefenceEff:F0}");
        }

        static float HqHp(Intel.Base b, Intel.KnownStructure s)
        {
            if (s != null) return s.Hp;
            if (b == null) return 0f;
            float hp = 0f;
            for (int i = 0; i < b.Members.Count; i++) hp += b.Members[i].Hp;
            return hp;
        }

        static Intel.Base LiveBase(Intel.Base b)
        {
            if (b == null) return null;
            var bases = Intel.Bases;
            for (int i = 0; i < bases.Count; i++) if (bases[i].Id == b.Id) return bases[i];
            return null;
        }

        static void CheckExpectations(float now)
        {
            for (int i = 0; i < Portfolio.Count; i++)
            {
                var o = Portfolio[i];
                if (o.Status != Status.Active) continue;
                if (o.BaseRef != null) { var lb = LiveBase(o.BaseRef); if (lb != null) o.BaseRef = lb; }
                string done = null, failed = null;
                switch (o.Kind)
                {
                    case Kind.DefendQueen:
                        break;   // never done; its price moves with the forecast
                    case Kind.DefendEco:
                    case Kind.DefendProduction:
                    {
                        if (o.OwnStructRef != null && o.OwnStructRef.IsDestroyed) { failed = "the asset was destroyed"; break; }
                        float near = Intel.EffectiveNear(o.Where, o.Radius + 100f);
                        if (near <= 0f && now > o.DeadlineAt + DEFEND_GRACE_S) done = "nothing arrived, or it left";
                        break;
                    }
                    case Kind.Intercept:
                    case Kind.BreakArmy:
                    {
                        var t = o.TrackRef;
                        if (t == null || !TrackAlive(t)) { done = "track gone"; break; }
                        if (o.ForceEngaged)
                        {
                            if (t.Effective <= o.TrackEffAt * 0.5f) { done = $"track fell to {t.Effective:F0} from {o.TrackEffAt:F0}"; break; }
                            if (o.AssignedEff <= o.ForceEffAtEngage * 0.5f && t.Effective > o.TrackEffAt * 0.7f)
                            { failed = $"we fell to {o.AssignedEff:F0} of {o.ForceEffAtEngage:F0} while they held"; break; }
                            if (now - o.EngagedAt > 240f && t.Effective > o.TrackEffAt * 0.9f) failed = "no progress in 240s of contact";
                        }
                        else if (now - o.ActivatedAt > 300f) done = "never caught it";
                        o.Where = t.Predicted();
                        break;
                    }
                    case Kind.DenyExpansion:
                    case Kind.KillHQ:
                    {
                        var b = o.BaseRef;
                        if (b == null || !BaseAlive(b)) { done = "base gone"; break; }
                        if (o.Kind == Kind.KillHQ && !b.HasHq) { done = "HQ gone"; break; }
                        if (o.ForceEngaged)
                        {
                            float hp = HqHp(b, null);
                            if (b.Count < o.BaseCountAt || hp < o.TargetHpAt * 0.95f) { o.LastProgressAt = now; o.BaseCountAt = b.Count; o.TargetHpAt = hp; }
                            else if (now - o.LastProgressAt > 180f) failed = "no structure fell and no health moved for 180s of contact";
                            if (o.AssignedEff <= o.ForceEffAtEngage * 0.4f) failed = $"force fell to {o.AssignedEff:F0} of {o.ForceEffAtEngage:F0}";
                        }
                        break;
                    }
                    case Kind.RaidEco:
                    case Kind.RaidProduction:
                    {
                        var s = o.StructRef;
                        if (s != null && !StructAlive(s)) { done = "target destroyed"; break; }
                        if (s == null && (o.BaseRef == null || !BaseAlive(o.BaseRef))) { done = "base gone"; break; }
                        if (o.ForceEngaged)
                        {
                            float hp = s?.Hp ?? 0f;
                            if (s != null && hp < o.TargetHpAt * 0.95f) { o.LastProgressAt = now; o.TargetHpAt = hp; }
                            if (now - o.EngagedAt > RAZE_BUDGET_S + 60f && s != null && hp > 0f)
                                failed = "target still stands past the raze budget";
                            if (o.AssignedEff <= o.ForceEffAtEngage * 0.4f) failed = "raid force lost";
                        }
                        break;
                    }
                    case Kind.Recon:
                    {
                        var b = o.BaseRef;
                        if (b == null) { done = "nothing to see"; break; }
                        if (b.StaleS < 5f) done = "seen";
                        else if (now - o.ActivatedAt > 300f) failed = "never got there";
                        break;
                    }
                }
                if (done != null) End(o, Status.Done, done, now);
                else if (failed != null) End(o, Status.Failed, failed, now);
            }
        }

        static bool TrackAlive(Intel.Track t)
        {
            var tracks = Intel.Tracks;
            for (int i = 0; i < tracks.Count; i++) if (tracks[i] == t) return true;
            return false;
        }

        static bool BaseAlive(Intel.Base b)
        {
            var bases = Intel.Bases;
            for (int i = 0; i < bases.Count; i++) if (bases[i].Id == b.Id) { return true; }
            return false;
        }

        static bool StructAlive(Intel.KnownStructure s)
        {
            bool alive = false;
            Intel.ForEachKnown(k => { if (k == s) alive = true; });
            return alive;
        }

        /// <summary>Forces took this objective's force away for a defence that
        /// outranks it. Not a failure of the objective: it is re-proposed on the
        /// next refresh if it still holds.</summary>
        internal static void NotePreempted(Objective o, string why, float now)
        {
            if (o.Status != Status.Active) return;
            o.Status = Status.Proposed; o.AssignedEff = 0f; o.AssignedUnits = 0; o.ForceEngaged = false;
            // Held, or the next refresh raises the same force for the same
            // objective and the defence pre-empts it again: 103 times in round three.
            _holdUntil[o.Key] = now + HOLD_AFTER_FAIL_S;
            MilLog.Msg($"[OBJ] #{o.Id} PRE-EMPTED {o.Kind} — {why}");
        }

        /// <summary>Forces reports the kernel verdict; the objective ends when a
        /// force is told to withdraw.</summary>
        internal static void NoteWithdrawn(Objective o, string why, float now)
        {
            if (o.Status == Status.Active) End(o, Status.Failed, "withdrew: " + why, now);
        }

        static void End(Objective o, Status s, string why, float now)
        {
            o.Status = s; o.EndedAt = now; o.Outcome = why;
            if (s == Status.Failed) _holdUntil[o.Key] = now + HOLD_AFTER_FAIL_S;
            if (s == Status.Failed && o.Offensive && o.BaseRef != null)
            {
                float met = Mathf.Max(o.DefenceEff, Intel.EffectiveNear(o.Where, 700f));
                // A force that was beaten by something it never saw was beaten by
                // at least its own size over the commit band.
                if (o.ForceEngaged && o.PeakAssignedEff > 0f) met = Mathf.Max(met, o.PeakAssignedEff / Doctrine.CommitAt * 1.5f);
                _baseAnswer[o.BaseRef.Id] = (met, now);
                MilLog.Msg($"[OBJ] base {Intel.Short(o.BaseRef.Team)}#{o.BaseRef.Id} answered with ~{met:F0} eff — remembered");
            }
            MilLog.Msg($"[OBJ] #{o.Id} {(s == Status.Done ? "DONE" : "FAILED")} {o.Kind} — {why} " +
                       $"(active {now - o.ActivatedAt:F0}s, peak force {o.PeakAssignedEff:F0} eff)");
            WriteRow(o, now);
        }

        static void WriteRow(Objective o, float now)
        {
            try
            {
                var sb = new System.Text.StringBuilder(256);
                string F(float v) => v.ToString("F0", CultureInfo.InvariantCulture);
                sb.Append('{')
                  .Append("\"ts\":\"").Append(DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss")).Append("\",")
                  .Append("\"map\":\"").Append(Perception.MapLayers.LayerReplay.CurrentMap).Append("\",")
                  .Append("\"roundT\":").Append(F(Perception.MapLayers.LayerReplay.CurrentRoundTime)).Append(',')
                  .Append("\"id\":").Append(o.Id).Append(',')
                  .Append("\"kind\":\"").Append(o.Kind).Append("\",")
                  .Append("\"status\":\"").Append(o.Status).Append("\",")
                  .Append("\"x\":").Append(F(o.Where.x)).Append(",\"z\":").Append(F(o.Where.z)).Append(',')
                  .Append("\"required\":").Append(F(o.RequiredEff)).Append(',')
                  .Append("\"defence\":").Append(F(o.DefenceEff)).Append(',')
                  .Append("\"peakForce\":").Append(F(o.PeakAssignedEff)).Append(',')
                  .Append("\"pWin\":").Append(o.PWin.ToString("F3", CultureInfo.InvariantCulture)).Append(',')
                  .Append("\"gain\":").Append(F(o.Gain)).Append(',')
                  .Append("\"expectedLoss\":").Append(F(o.ExpectedLoss)).Append(',')
                  .Append("\"activeS\":").Append(F(now - o.ActivatedAt)).Append(',')
                  .Append("\"engaged\":").Append(o.ForceEngaged ? "true" : "false").Append(',')
                  .Append("\"outcome\":\"").Append(Esc(o.Outcome)).Append("\",")
                  .Append("\"note\":\"").Append(Esc(o.Note)).Append("\"}");
                string dir = Path.Combine("UserData", "RTSA");
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "objectives.jsonl"), sb + "\n");
            }
            catch (Exception ex) { MelonLogger.Warning("[OBJ] row write failed: " + ex.Message); }
        }

        static string Esc(string s) => string.IsNullOrEmpty(s) ? "" : s.Replace("\\", "\\\\").Replace("\"", "\\\"");

        static string Cell(Vector3 p) => Mathf.RoundToInt(p.x / 150f) + "," + Mathf.RoundToInt(p.z / 150f);

        // ---- queries for other modules ---------------------------------------------

        /// <summary>Which kind owns the ground at a position — the join key for combat.jsonl.</summary>
        internal static string KindAt(Vector3 pos, float radiusM)
        {
            float r2 = radiusM * radiusM; string best = null;
            for (int i = 0; i < Portfolio.Count; i++)
            {
                var o = Portfolio[i];
                if (o.Status != Status.Active) continue;
                float dx = o.Where.x - pos.x, dz = o.Where.z - pos.z;
                if (dx * dx + dz * dz > r2) continue;
                best = o.Kind.ToString();
            }
            return best ?? "reserve";
        }

        /// <summary>Points the army expects to fight at, weighted — for siting.</summary>
        internal static void FightPoints(List<(Vector3 pos, float weight)> into)
        {
            into.Clear();
            for (int i = 0; i < Portfolio.Count; i++)
            {
                var o = Portfolio[i];
                if (o.Status == Status.Done || o.Status == Status.Failed || o.Kind == Kind.Recon) continue;
                float w = o.Kind == Kind.DefendQueen ? 0.5f : (o.Offensive ? 1f : 2f);
                into.Add((o.Where, w));
            }
        }

        /// <summary>Sites with something arriving — the spire planner's input.</summary>
        internal static void ThreatenedSites(List<(Vector3 pos, float eff, float etaS, string why)> into)
        {
            into.Clear();
            float now = Time.time;
            for (int i = 0; i < Portfolio.Count; i++)
            {
                var o = Portfolio[i];
                if (o.Status == Status.Done || o.Status == Status.Failed) continue;
                if (o.Kind != Kind.DefendEco && o.Kind != Kind.DefendProduction && o.Kind != Kind.DefendQueen) continue;
                if (o.DefenceEff <= 0f) continue;
                into.Add((o.Where, o.DefenceEff, Mathf.Max(0f, o.DeadlineAt - now), o.Note));
            }
        }

        static void Log()
        {
            var sb = new System.Text.StringBuilder("[OBJ] ");
            sb.Append("army ").Append(ArmyEff.ToString("F0")).Append(" eff (").Append(ArmyCash).Append(" cash) ")
              .Append("enemy~").Append(Intel.EnemyEffective.ToString("F0"));
            if (NextOffensivePrice > 0f)
                sb.Append(" | building toward ").Append(NextOffensivePrice.ToString("F0")).Append(" for ").Append(NextOffensiveWhat);
            int shown = 0;
            for (int i = 0; i < Portfolio.Count && shown < 7; i++)
            {
                var o = Portfolio[i];
                if (o.Status == Status.Done || o.Status == Status.Failed) continue;
                shown++;
                sb.Append(" | #").Append(o.Id).Append(' ').Append(o.Kind).Append(' ').Append(o.Status == Status.Active ? "ACTIVE" : "proposed")
                  .Append(" need ").Append(o.RequiredEff.ToString("F0")).Append(" have ").Append(o.AssignedEff.ToString("F0"))
                  .Append(o.Status == Status.Active && o.Kind != Kind.DefendQueen ? $" pWin {o.PWin:F2}" : "")
                  .Append(float.IsInfinity(o.DeadlineAt) ? "" : $" eta {Mathf.Max(0f, o.DeadlineAt - Time.time):F0}s")
                  .Append(" [").Append(o.Note).Append(']');
            }
            if (!MilConfig.Execute) sb.Append(" [shadow — no orders]");
            MilLog.Msg(sb.ToString());
        }
    }
}
