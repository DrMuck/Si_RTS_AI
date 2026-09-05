using HarmonyLib;
using MelonLoader;
using Silica;
using Silica.AI;
using Si_RTS_AI.Perception;
using Si_RTS_AI.Planning;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Si_RTS_AI.Mil
{
    /// <summary>
    /// THE EXECUTOR: WHICH UNITS GO, FORMED HOW, AND WHEN TO LEAVE OR RETREAT.
    ///
    /// A force is a group of combat units bound to one objective:
    ///
    ///     Forming -> Staging -> Advancing -> Engaged -> Done
    ///                                     \-> Withdrawing -> Forming
    ///
    /// FORMING fills from the free pool nearest the objective and leaves only
    /// when its effective force meets the objective's price (never trickle).
    /// STAGING is one formation move to a rally point short of the objective,
    /// outside enemy reach; the game paces the group to its slowest member and
    /// holds the shape (StrategyMode.PerformMoveAttack, new in 0.9.46). It
    /// advances when the slowest member arrives or after stagingPatience.
    /// ADVANCING is an attack order on the objective's target or an attack-move
    /// to its ground. ENGAGED asks the kernel every few seconds with the force
    /// as it now stands; below RefuseBelow it WITHDRAWS — the first retreat rule
    /// this project has had.
    ///
    /// THE RESERVE is everything the portfolio did not claim. It stands where
    /// the walk-time field says the worst arrival to what we defend is
    /// shortest, weighted by earnings and by where incursions have come from,
    /// with Defend orders on the nearest structure so it fights what comes
    /// without walking off. It is also the nucleus of the next push.
    ///
    /// Orders go out on change, not on a clock, and never to a unit that has a
    /// target. Two Harmony gates keep the game's own commander from re-tasking
    /// what we hold, as before.
    /// </summary>
    internal static class Forces
    {
        internal enum State { Forming, Staging, Advancing, Engaged, Withdrawing, Standing }
        internal enum Pool { Fast, Swarm, Line, Heavy }

        internal sealed class Force
        {
            public int   Id;
            public Objectives.Objective Obj;    // null for the reserve
            public State Phase = State.Forming;
            public readonly List<Unit> Units = new List<Unit>();
            public float Eff, Cash;
            public Vector3 Rally, Dest;
            public float StateSince, LastOrderAt, LastTargetCheckAt, ContactSince;
            public Vector3 LastOrderDest;
            public Target LastTarget;
            public bool  Flying;
            public float PeakEff;
            public int   OrdersIssued;
            public Vector3 StartPos;            // where it formed, for the staging walk estimate
            // Kind#objective/force: a second wave for the same objective is a
            // separate force, and under the old name its Staging lines interleaved
            // with the first wave's Engaged lines as if one force were flapping.
            public string Name => Obj == null ? "reserve" : $"{Obj.Kind}#{Obj.Id}/{Id}";
        }

        internal static readonly List<Force> Active = new List<Force>(8);
        static Force _reserve;
        static float _lastTickAt, _lastLogAt;
        static int _nextId;
        static Team _team;

        const float TICK_S            = 2f;
        const float ARRIVED_M         = 90f;
        const float ORDER_MOVED_M     = 90f;
        const float ORDER_BACKSTOP_S  = 60f;
        const float MIN_REORDER_S     = 15f;    // a force turns at most this often
        const float CONTACT_HOLD_S    = 6f;     // contact en route must last this long to count
        const float BIG_JUMP_M        = 300f;   // unless the destination is somewhere else entirely
        const float ENGAGE_RADIUS_M   = 600f;
        const float KERNEL_CHECK_S    = 4f;
        const float RESERVE_REPOSITION_S = 40f;
        const float RESERVE_LEASH_M   = 300f;

        internal static int OrdersIssued;
        internal static int StrippedFromVanillaAttack;
        internal static int Withdrawals;

        internal static void ResetForNewRound()
        {
            Active.Clear(); _reserve = null;
            _lastTickAt = _lastLogAt = 0f; _nextId = 0; _team = null;
            OrdersIssued = 0; StrippedFromVanillaAttack = 0; Withdrawals = 0;
            _lastReserveMoveAt = 0f; _reservePoint = Vector3.zero;
        }

        internal static void Tick(Team team)
        {
            if (!MilConfig.Enabled || team == null) return;
            _team = team;
            float now = Time.time;
            if (now - _lastTickAt < TICK_S) return;
            _lastTickAt = now;
            try
            {
                Prune(now);
                // THE RESERVE IS THE LEFTOVER, NOT A HOLDER. Its units go back
                // to the pool every tick so an objective can claim them; what
                // nobody claims returns to it below.
                if (_reserve != null) _reserve.Units.Clear();
                var free = FreeCombatUnits(team);
                Assign(free, now);
                Reserve(free, now);
                Judge(now);
                if (MilConfig.Execute && CanCommand(team)) Execute(now);
                if (now - _lastLogAt >= 20f) { _lastLogAt = now; Log(); }
            }
            catch (Exception ex) { MelonLogger.Warning("[FORCE] tick threw: " + ex.Message); }
        }

        static bool CanCommand(Team team)
        {
            try { return AIManager.IsCommanderEnabled(team); } catch { return true; }
        }

        // ---- pools ----------------------------------------------------------------

        internal static Pool PoolOf(Unit u)
        {
            string n = "";
            try { n = u.ObjectInfo?.DisplayName ?? ""; } catch { }
            bool flyer = UnitStats.IsFlyer(n);
            try { if (u.IsFlying) flyer = true; } catch { }
            float speed = UnitStats.SpeedOf(n);
            try { float ns = u.NormalSpeed; if (ns > 0.5f) speed = ns; } catch { }
            if (flyer || speed >= 20f) return Pool.Fast;
            string cap = UnitStats.CapTypeOf(n);
            try { cap = u.ObjectInfo.UnitCapType.ToString(); } catch { }
            int cost = 0;
            try { cost = UnitValues.CostOf(n); } catch { }
            if (cap == "Secondary") return Pool.Swarm;
            return cost >= 3000 ? Pool.Heavy : Pool.Line;
        }

        static bool Prefers(Objectives.Pool pref, Pool p)
        {
            switch (pref)
            {
                case Objectives.Pool.Fast: return p == Pool.Fast;
                case Objectives.Pool.Line: return p == Pool.Line || p == Pool.Heavy;
                default: return true;
            }
        }

        // ---- rosters ----------------------------------------------------------

        static readonly HashSet<Unit> _held = new HashSet<Unit>();

        static List<Unit> FreeCombatUnits(Team team)
        {
            _held.Clear();
            for (int f = 0; f < Active.Count; f++)
                for (int i = 0; i < Active[f].Units.Count; i++) _held.Add(Active[f].Units[i]);
            if (_reserve != null) for (int i = 0; i < _reserve.Units.Count; i++) _held.Add(_reserve.Units[i]);

            var free = new List<Unit>(32);
            try
            {
                var units = team.Units;
                if (units == null) return free;
                for (int i = 0; i < units.Count; i++)
                {
                    var u = units[i];
                    if (u == null || u.ObjectInfo == null || u.IsDestroyed) continue;
                    string n = u.ObjectInfo.DisplayName ?? "";
                    if (n == "Shrimp" || n == "Queen") continue;
                    if (_held.Contains(u)) continue;
                    bool scout = false;
                    try { scout = ScoutPlanner.IsScout(u); } catch { }
                    if (scout) continue;
                    bool piloted = false;
                    try { piloted = u.ControlledBy != null; } catch { }
                    if (piloted) continue;          // a player is driving it
                    free.Add(u);
                }
            }
            catch { }
            return free;
        }

        static void Prune(float now)
        {
            for (int f = Active.Count - 1; f >= 0; f--)
            {
                var force = Active[f];
                PruneUnits(force);
                var o = force.Obj;
                bool over = o == null || o.Status == Objectives.Status.Done || o.Status == Objectives.Status.Failed;
                bool dropped = o != null && !Objectives.Portfolio.Contains(o);
                if (over || dropped)
                {
                    if (force.Units.Count > 0)
                        MilLog.Msg($"[FORCE] {force.Name} released ({force.Units.Count} units, {force.Eff:F0} eff) — " +
                                   (over ? o?.Outcome ?? "objective over" : "objective dropped from the portfolio"));
                    Stop(force);
                    force.Units.Clear();
                    Active.RemoveAt(f);
                }
            }
            if (_reserve != null) PruneUnits(_reserve);
        }

        static void PruneUnits(Force force)
        {
            for (int i = force.Units.Count - 1; i >= 0; i--)
            {
                var u = force.Units[i];
                bool gone = u == null || u.IsDestroyed;
                bool scout = false, piloted = false;
                if (!gone) { try { scout = ScoutPlanner.IsScout(u); piloted = u.ControlledBy != null; } catch { } }
                if (gone || scout || piloted) force.Units.RemoveAt(i);
            }
            Measure(force);
        }

        static void Measure(Force force)
        {
            var f = new Kernel.Force();
            bool allFly = force.Units.Count > 0;
            for (int i = 0; i < force.Units.Count; i++)
            {
                string n = "";
                try { n = force.Units[i].ObjectInfo?.DisplayName ?? ""; } catch { }
                f.Add(n);
                if (!UnitStats.IsFlyer(n)) allFly = false;
            }
            force.Eff = f.Effective(); force.Cash = f.Cash(); force.Flying = allFly;
            if (force.Eff > force.PeakEff) force.PeakEff = force.Eff;
            if (force.Obj != null)
            {
                float sum = 0f; int n = 0;
                for (int ff = 0; ff < Active.Count; ff++) if (Active[ff].Obj == force.Obj) { sum += Active[ff].Eff; n += Active[ff].Units.Count; }
                force.Obj.AssignedEff = sum; force.Obj.AssignedUnits = n;
                if (sum > force.Obj.PeakAssignedEff) force.Obj.PeakAssignedEff = sum;
            }
        }

        // ---- assignment --------------------------------------------------------

        static void Assign(List<Unit> free, float now)
        {
            var portfolio = Objectives.Portfolio;
            float raidBudget = Objectives.ArmyEff * MilConfig.RaidShare;
            float raidHeld = 0f;
            for (int f = 0; f < Active.Count; f++)
                if (IsRaid(Active[f].Obj)) raidHeld += Active[f].Eff;

            for (int i = 0; i < portfolio.Count; i++)
            {
                var o = portfolio[i];
                if (o.Status == Objectives.Status.Done || o.Status == Objectives.Status.Failed) continue;
                var force = Find(o);
                // The Queen's force is the reserve itself: it stands at home
                // whenever nothing better is known, and the reserve's standing
                // point already weights the Nest. A separate garrison would
                // freeze units that the forecast says are not needed yet.
                if (o.Kind == Objectives.Kind.DefendQueen)
                {
                    if (force != null) { Active.Remove(force); }
                    continue;
                }
                if (IsRaid(o) && force == null && raidHeld >= raidBudget) continue;

                // A committed force does not absorb reinforcements. THE NEXT WAVE
                // IS A NEW FORCE FOR THE SAME OBJECTIVE, raised while the first
                // is out and only up to the doctrine's ceiling — "commit once,
                // commit everything" for the win condition, rather than a
                // reserve of 65k standing at home while 40k fights at their HQ.
                bool wave = false;
                if (force != null && force.Phase != State.Forming && force.Phase != State.Staging)
                {
                    if (!o.Offensive || o.Kind == Objectives.Kind.Recon) continue;
                    float committed = AssignedTo(o);
                    float ceilingAll = Mathf.Max(o.RequiredEff, o.CeilingEff);
                    if (committed >= ceilingAll) continue;
                    float avail0 = 0f;
                    for (int k = 0; k < free.Count; k++)
                    {
                        string fn = ""; try { fn = free[k].ObjectInfo?.DisplayName ?? ""; } catch { }
                        avail0 += Kernel.EffectiveOf(fn);
                    }
                    // A wave is worth sending when it is at least half the price.
                    if (avail0 < o.RequiredEff * 0.5f) continue;
                    force = null; wave = true;
                }
                if (force == null && free.Count == 0) continue;

                float ceiling = Mathf.Max(o.RequiredEff, o.CeilingEff);
                if (wave) ceiling = Mathf.Max(o.RequiredEff, o.CeilingEff - AssignedTo(o));
                float have = force?.Eff ?? 0f;
                if (!wave && have >= o.RequiredEff) continue;

                // ALL OR NOTHING. A force is raised only when the free pool can
                // pay the whole price now; otherwise the units stay in the
                // reserve, standing where they are useful, until it can. The
                // first round spread one unit across seven objectives, each
                // Forming forever: the trickle in a different coat.
                if (force == null)
                {
                    float avail = 0f;
                    for (int k = 0; k < free.Count; k++)
                    {
                        string fn = ""; try { fn = free[k].ObjectInfo?.DisplayName ?? ""; } catch { }
                        avail += Kernel.EffectiveOf(fn);
                    }
                    // DEFENCE PRE-EMPTS AN OFFENSIVE FORCE THAT HAS NOT YET FOUGHT.
                    // The spec ranks defence above offence; a raid still walking
                    // to its target is the cheapest force to turn around, and
                    // an economy eaten while the army is out is the one failure
                    // that cannot be recovered from. Engaged forces are left
                    // alone: pulling units out of a fight is how a push dies.
                    bool imminent = float.IsInfinity(o.DeadlineAt) ? o.DefenceEff > 0f : o.DeadlineAt - now <= MilConfig.PreemptWithinS;
                    if (avail < o.RequiredEff && o.Rank <= 1 && !o.Offensive && imminent)
                    {
                        for (int pf = Active.Count - 1; pf >= 0 && avail < o.RequiredEff; pf--)
                        {
                            var other = Active[pf];
                            if (other.Obj == null || !other.Obj.Offensive) continue;
                            if (other.Phase == State.Engaged || other.Phase == State.Withdrawing) continue;
                            Objectives.NotePreempted(other.Obj, $"{o.Kind}#{o.Id} needs {o.RequiredEff:F0} eff and only {avail:F0} is free", now);
                            for (int u = 0; u < other.Units.Count; u++)
                            {
                                free.Add(other.Units[u]);
                                string fn = ""; try { fn = other.Units[u].ObjectInfo?.DisplayName ?? ""; } catch { }
                                avail += Kernel.EffectiveOf(fn);
                            }
                            other.Units.Clear();
                            Active.RemoveAt(pf);
                        }
                    }
                    if (!wave && avail < o.RequiredEff) continue;
                }

                // Take preferred pool first, nearest to the objective, until the price is met.
                int taken = 0;
                for (int pass = 0; pass < 2 && have < o.RequiredEff; pass++)
                {
                    for (int k = free.Count - 1; k >= 0 && have < o.RequiredEff; k--) { }
                    while (have < o.RequiredEff && free.Count > 0)
                    {
                        int idx = Nearest(free, o.Where, pass == 0 ? o.PoolPref : Objectives.Pool.Any);
                        if (idx < 0) break;
                        var u = free[idx];
                        string n = ""; try { n = u.ObjectInfo?.DisplayName ?? ""; } catch { }
                        float e = Kernel.EffectiveOf(n);
                        if (IsRaid(o) && raidHeld + e > raidBudget && have > 0f) break;
                        if (force == null)
                        {
                            force = new Force { Id = ++_nextId, Obj = o, StateSince = now };
                            Active.Add(force);
                            if (wave) MilLog.Msg($"[FORCE] {force.Name} second wave: {AssignedTo(o):F0} eff already out, ceiling {Mathf.Max(o.RequiredEff, o.CeilingEff):F0}");
                        }
                        free.RemoveAt(idx);
                        force.Units.Add(u);
                        have += e; taken++;
                        if (IsRaid(o)) raidHeld += e;
                        if (have >= ceiling) break;
                    }
                }
                if (force != null)
                {
                    Measure(force);
                    if (taken > 0 && o.Status == Objectives.Status.Proposed) Objectives.NoteActivated(o, now);
                }
            }
        }

        static bool IsRaid(Objectives.Objective o) =>
            o != null && (o.Kind == Objectives.Kind.RaidEco || o.Kind == Objectives.Kind.RaidProduction);

        static Force Find(Objectives.Objective o)
        {
            for (int f = 0; f < Active.Count; f++)
                if (Active[f].Obj == o && (Active[f].Phase == State.Forming || Active[f].Phase == State.Staging)) return Active[f];
            for (int f = 0; f < Active.Count; f++) if (Active[f].Obj == o) return Active[f];
            return null;
        }

        static float AssignedTo(Objectives.Objective o)
        {
            float e = 0f;
            for (int f = 0; f < Active.Count; f++) if (Active[f].Obj == o) e += Active[f].Eff;
            return e;
        }

        static int Nearest(List<Unit> pool, Vector3 to, Objectives.Pool pref)
        {
            int best = -1; float bd = float.MaxValue;
            for (int i = 0; i < pool.Count; i++)
            {
                var u = pool[i];
                if (pref != Objectives.Pool.Any && !Prefers(pref, PoolOf(u))) continue;
                Vector3 p;
                try { p = u.transform.position; } catch { continue; }
                float dx = p.x - to.x, dz = p.z - to.z, d = dx * dx + dz * dz;
                if (d < bd) { bd = d; best = i; }
            }
            return best;
        }

        // ---- the reserve ---------------------------------------------------------

        static Vector3 _reservePoint;
        static float _lastReserveMoveAt, _reserveFallBackUntil;
        static readonly List<(Vector3 pos, float weight)> _assets = new List<(Vector3, float)>();
        static readonly List<Vector3> _candidates = new List<Vector3>();

        static void Reserve(List<Unit> free, float now)
        {
            if (_reserve == null) _reserve = new Force { Id = 0, Obj = null, Phase = State.Standing, StateSince = now };
            for (int i = 0; i < free.Count; i++) _reserve.Units.Add(free[i]);
            free.Clear();
            Measure(_reserve);

            // Where it stands: re-decided slowly, on the walk-time field.
            bool urgent = false;
            if (_reserve.Units.Count > 0 && _reservePoint != Vector3.zero)
            {
                float th = Intel.EffectiveNear(Centroid(_reserve), ENGAGE_RADIUS_M);
                urgent = th > 0f && Kernel.Ratio(_reserve.Eff + OwnDefenceNear(Centroid(_reserve)), th) < MilConfig.DefendRefuseBelow;
            }
            if (!urgent && now - _lastReserveMoveAt < RESERVE_REPOSITION_S && _reservePoint != Vector3.zero) return;
            _lastReserveMoveAt = now;
            Vector3 nest = Intel.Nest;
            if (nest == Vector3.zero) return;

            _assets.Clear();
            _assets.Add((nest, 3f));
            try
            {
                BcIncome.ForEach((pos, lifetime, recent) =>
                {
                    if (recent <= 0) return;
                    float w = 0.3f + recent / 4000f;
                    int sector = Intel.SectorOf(pos - nest);
                    w += Intel.SectorWeight(sector) * 0.5f;
                    _assets.Add((pos, Mathf.Min(w, 3f)));
                });
            }
            catch { }
            // Candidates: our own structures (the network is the ground we can hold).
            _candidates.Clear();
            try
            {
                var structs = _team.Structures;
                if (structs != null)
                    for (int i = 0; i < structs.Count; i += 2)
                    {
                        var s = structs[i];
                        if (s?.ObjectInfo == null || s.IsDestroyed) continue;
                        _candidates.Add(s.transform.position);
                    }
            }
            catch { }
            if (_candidates.Count == 0) _candidates.Add(nest);

            // A forward base pulls the reserve to it: the next push starts there.
            Vector3 fob = ProductionV3.ForwardBase;
            if (fob != Vector3.zero) _assets.Add((fob, 1f));

            // THE RESERVE IS NOT A FORCE THAT FIGHTS WHATEVER ARRIVES. Round two:
            // eleven engagements on the reserve's ground, 24k lost for 14k, at
            // a standing point pulled forward by the FOB. When what stands
            // within reach outmatches it (spires counted on our side), it gives
            // ground toward the Nest for a while rather than trade at parity.
            if (_reserve.Units.Count > 0)
            {
                Vector3 c = Centroid(_reserve);
                float theirs = Intel.EffectiveNear(c, ENGAGE_RADIUS_M);
                float ours = _reserve.Eff + OwnDefenceNear(c);
                if (theirs > 0f && Kernel.Ratio(ours, theirs) < MilConfig.DefendRefuseBelow)
                {
                    _reserveFallBackUntil = now + 90f;
                    MilLog.Every("reserve:fallback", 30f, $"[FORCE] reserve gives ground: {Kernel.Describe(ours, theirs)} — standing nearer the Nest for 90s");
                }
            }
            if (now < _reserveFallBackUntil)
            {
                _assets.Clear(); _assets.Add((nest, 1f));
            }

            float speed = 9f;
            var pt = Fields.BestStandingPoint(_candidates, _assets, speed, out float worst);
            if (pt == Vector3.zero) pt = nest;
            if ((pt - _reservePoint).sqrMagnitude > 150f * 150f)
                MilLog.Msg($"[FORCE] reserve stands at ({pt.x:F0},{pt.z:F0}) — worst weighted arrival {worst:F0}s, " +
                           $"{_reserve.Units.Count} units {_reserve.Eff:F0} eff");
            _reservePoint = pt;
            _reserve.Dest = pt;
        }

        internal static Vector3 ReservePoint => _reservePoint;
        internal static float ReserveEff => _reserve?.Eff ?? 0f;
        internal static int ReserveUnits => _reserve?.Units.Count ?? 0;

        // ---- state machine ------------------------------------------------------

        static void Judge(float now)
        {
            for (int f = 0; f < Active.Count; f++)
            {
                var force = Active[f];
                var o = force.Obj;
                if (o == null) continue;
                // THE PRICE CAN RISE WHILE THE FORCE WALKS. A raid priced against
                // two scouts finds their army came home; the refresh re-reads the
                // defence every fifteen seconds, and a force that has not yet
                // fought turns back when the kernel would now refuse the fight.
                if ((force.Phase == State.Staging || force.Phase == State.Advancing) && o.Offensive &&
                    o.DefenceEff > 0f && Kernel.Ratio(force.Eff, o.DefenceEff) < Doctrine.RefuseBelow)
                {
                    Withdrawals++;
                    string why = $"the price rose: {Kernel.Describe(force.Eff, o.DefenceEff)}";
                    force.Rally = Fields.RallyFor(Centroid(force), o.Where, MilConfig.StandoffM * 2f, force.Flying);
                    SetPhase(force, State.Withdrawing, now, why);
                    Objectives.NoteWithdrawn(o, why, now);
                    continue;
                }
                switch (force.Phase)
                {
                    case State.Forming:
                        if (force.Units.Count > 0 && (force.Eff >= o.RequiredEff || (force.Eff >= o.RequiredEff * 0.5f && AssignedTo(o) > force.Eff)))
                        {
                            force.StartPos = Centroid(force);
                            force.Rally = Fields.RallyFor(force.StartPos, o.Where, MilConfig.StandoffM, force.Flying);
                            SetPhase(force, State.Staging, now,
                                     $"{force.Units.Count} units {force.Eff:F0}/{o.RequiredEff:F0} eff, rally ({force.Rally.x:F0},{force.Rally.z:F0})");
                        }
                        break;
                    case State.Staging:
                    {
                        // Contact at the rally does not send the force off to its
                        // far target: the units fight where they stand and staging
                        // goes on. Only a losing fight ends it.
                        if (AnyFighting(force) && Losing(force, o, now, out string lost))
                        {
                            Withdraw(force, o, now, lost);
                            break;
                        }
                        // Patience scales with the walk: the slowest member on
                        // the field from where it formed, plus the margin.
                        float walk = Fields.WalkTimeBetween(force.StartPos, force.Rally, SlowestSpeed(force), force.Flying);
                        if (float.IsInfinity(walk)) walk = 120f;
                        bool close = Spread(force, force.Rally, out float worst) || now - force.StateSince > walk + MilConfig.StagingPatienceS;
                        if (close) SetPhase(force, State.Advancing, now,
                                            worst <= ARRIVED_M ? "assembled" : $"patience out, straggler {worst:F0}m");
                        break;
                    }
                    case State.Advancing:
                    {
                        bool atObjective = (Centroid(force) - o.Where).sqrMagnitude < (o.Radius + ENGAGE_RADIUS_M) * (o.Radius + ENGAGE_RADIUS_M);
                        // CONTACT HAS TO LAST. A stray shot at one member made the
                        // whole force "engaged" for a tick and "advancing" again
                        // twenty seconds later, and each flip was a log line and a
                        // kernel check. Contact en route now has to persist for
                        // CONTACT_HOLD_S before it counts; at the objective it counts
                        // at once, because that is the fight the force came for.
                        bool contact = AnyFighting(force) || (atObjective && EnemyNear(force));
                        if (!contact) force.ContactSince = 0f;
                        else if (force.ContactSince <= 0f) force.ContactSince = now;
                        if (contact && (atObjective || now - force.ContactSince >= CONTACT_HOLD_S))
                        {
                            SetPhase(force, State.Engaged, now, atObjective ? "contact at the objective" : "contact en route");
                            if (atObjective) Objectives.NoteEngaged(o, force.Eff, now);
                        }
                        break;
                    }
                    case State.Engaged:
                    {
                        if (now - force.LastTargetCheckAt < KERNEL_CHECK_S) break;
                        if (Losing(force, o, now, out string why))
                        {
                            Withdraw(force, o, now, why);
                        }
                        else if (!AnyFighting(force) && !EnemyNear(force) && now - force.StateSince > 20f)
                        {
                            SetPhase(force, State.Advancing, now, "no contact, pressing on");
                        }
                        else if (!o.ForceEngaged &&
                                 (Centroid(force) - o.Where).sqrMagnitude < (o.Radius + ENGAGE_RADIUS_M) * (o.Radius + ENGAGE_RADIUS_M))
                        {
                            Objectives.NoteEngaged(o, force.Eff, now);
                        }
                        break;
                    }
                    case State.Withdrawing:
                        if (Spread(force, force.Rally, out _) || now - force.StateSince > 90f)
                        {
                            // Back to the pool: the objective has already failed.
                            SetPhase(force, State.Forming, now, "regrouped");
                        }
                        break;
                }
            }
        }

        /// <summary>
        /// Outnumbered at contact is a reason to leave NOW, not after losses:
        /// the archive says nothing predicts a parity fight and below it the
        /// answer is disengage. Our own spires count on our side, exactly as
        /// theirs count on theirs when a base is priced: a defence force at a
        /// spire-covered cluster was withdrawing at 0.65 while the spires it
        /// stood beside would have carried the fight. Throttled to KERNEL_CHECK_S.
        /// </summary>
        static bool Losing(Force force, Objectives.Objective o, float now, out string why)
        {
            why = null;
            if (now - force.LastTargetCheckAt < KERNEL_CHECK_S) return false;
            force.LastTargetCheckAt = now;
            float ours = force.Eff + OwnDefenceNear(Centroid(force));
            float theirs = Intel.EffectiveNear(Centroid(force), ENGAGE_RADIUS_M) + DefenceStructsNear(o, Centroid(force));
            float ratio = Kernel.Ratio(ours, theirs);
            var band = Doctrine.Classify(ratio);
            float refuseAt = o.Offensive ? Doctrine.RefuseBelow : MilConfig.DefendRefuseBelow;
            if (theirs > 0f && ratio < refuseAt)
            {
                why = $"{Kernel.Describe(ours, theirs)} — {(o.Offensive ? Doctrine.Advice(band) : "clearly losing our own ground")}";
                return true;
            }
            return false;
        }

        static void Withdraw(Force force, Objectives.Objective o, float now, string why)
        {
            Withdrawals++;
            force.Rally = Fields.RallyFor(Centroid(force), o.Where, MilConfig.StandoffM * 2f, force.Flying);
            SetPhase(force, State.Withdrawing, now, why);
            Objectives.NoteWithdrawn(o, why, now);
        }

        /// <summary>An order may be replaced when the last one is older than
        /// MIN_REORDER_S, or when the force has never been given a target.</summary>
        static bool MayReorder(Force force, float now)
            => force.LastTarget == null && force.LastOrderAt <= 0f || now - force.LastOrderAt >= MIN_REORDER_S;

        /// <summary>A destination that jumped far enough to be a different place
        /// altogether is worth a turn at any time.</summary>
        static bool BigJump(Force force, Vector3 dest)
        {
            float dx = force.LastOrderDest.x - dest.x, dz = force.LastOrderDest.z - dest.z;
            return dx * dx + dz * dz > BIG_JUMP_M * BIG_JUMP_M;
        }

        static void SetPhase(Force force, State s, float now, string why)
        {
            // A PHASE IS NOT AN ORDER. This used to wipe LastOrderDest so the next
            // tick issued a fresh order for every phase change, and forces flip
            // between Staging, Engaged and Advancing every few seconds in bursts:
            // public round 2026-09-05 23:47, 275 such flips, 95 within ten seconds
            // of the previous one. Every flip turned a Goliath from "attack the
            // HQ" to "walk to the rally" and back — DrMuck: "they seem to rotate a
            // lot". Orders now change only when the destination or the target
            // changes, and not more often than MIN_REORDER_S apart.
            force.Phase = s; force.StateSince = now;
            MilLog.Msg($"[FORCE] {force.Name} -> {s}: {why}");
        }

        static float DefenceStructsNear(Objectives.Objective o, Vector3 at)
        {
            float d = 0f;
            Intel.ForEachKnown(k =>
            {
                if (k.Class != Intel.StructClass.Defense) return;
                float dx = k.Pos.x - at.x, dz = k.Pos.z - at.z;
                if (dx * dx + dz * dz <= ENGAGE_RADIUS_M * ENGAGE_RADIUS_M) d += k.Cost * MilConfig.DefenceWeight;
            });
            return d;
        }

        /// <summary>Effective value of our Defense-class structures within the
        /// engagement radius of a point, weighted like the enemy's.</summary>
        static float OwnDefenceNear(Vector3 at)
        {
            float d = 0f;
            try
            {
                var structs = _team?.Structures;
                if (structs == null) return 0f;
                for (int i = 0; i < structs.Count; i++)
                {
                    var s = structs[i];
                    if (s?.ObjectInfo == null || s.IsDestroyed) continue;
                    bool def = false; try { def = (s.ObjectInfo.StructureType & StructureType.Defense) != 0; } catch { }
                    if (!def) continue;
                    float dx = s.transform.position.x - at.x, dz = s.transform.position.z - at.z;
                    if (dx * dx + dz * dz > ENGAGE_RADIUS_M * ENGAGE_RADIUS_M) continue;
                    int cost = 0; try { cost = s.ObjectInfo.Cost; } catch { }
                    d += cost * MilConfig.DefenceWeight;
                }
            }
            catch { }
            return d;
        }

        static bool EnemyNear(Force force) => Intel.EffectiveNear(Centroid(force), ENGAGE_RADIUS_M * 0.75f) > 0f;

        static bool AnyFighting(Force force)
        {
            int n = 0;
            for (int i = 0; i < force.Units.Count; i++) if (IsFighting(force.Units[i])) n++;
            return n >= Mathf.Max(1, force.Units.Count / 4);
        }

        static bool IsFighting(Unit u)
        {
            try { return u.Target != null && u.CurrentTarget != null; } catch { }
            try { return u.CurrentTarget != null; } catch { return false; }
        }

        static float SlowestSpeed(Force force)
        {
            float s = float.MaxValue;
            for (int i = 0; i < force.Units.Count; i++)
            {
                string n = ""; try { n = force.Units[i].ObjectInfo?.DisplayName ?? ""; } catch { }
                s = Mathf.Min(s, UnitStats.SpeedOf(n));
            }
            return s == float.MaxValue ? 9f : s;
        }

        internal static Vector3 Centroid(Force force)
        {
            Vector3 c = Vector3.zero; int n = 0;
            for (int i = 0; i < force.Units.Count; i++)
            {
                try { c += force.Units[i].transform.position; n++; } catch { }
            }
            return n == 0 ? force.Dest : c / n;
        }

        /// <summary>True when every member is within ARRIVED_M of the point.</summary>
        static bool Spread(Force force, Vector3 at, out float worst)
        {
            worst = 0f;
            for (int i = 0; i < force.Units.Count; i++)
            {
                Vector3 p;
                try { p = force.Units[i].transform.position; } catch { continue; }
                float dx = p.x - at.x, dz = p.z - at.z, d = Mathf.Sqrt(dx * dx + dz * dz);
                if (d > worst) worst = d;
            }
            return worst <= ARRIVED_M * 1.5f;
        }

        // ---- orders ----------------------------------------------------------------

        [ThreadStatic] internal static bool PlannerOverride;
        static readonly List<BaseGameObject> _scratch = new List<BaseGameObject>(64);

        static void Execute(float now)
        {
            for (int f = 0; f < Active.Count; f++)
            {
                var force = Active[f];
                var o = force.Obj;
                if (o == null || force.Units.Count == 0) continue;
                switch (force.Phase)
                {
                    case State.Forming:
                        // Gather toward the rally of the objective so the group
                        // is not scattered when it fills — a move, not an attack.
                        if (force.Units.Count >= 2 && (now - force.LastOrderAt > 20f))
                        {
                            var gather = Fields.RallyFor(Centroid(force), o.Where, MilConfig.StandoffM * 1.5f, force.Flying);
                            if (Moved(force, gather)) MoveFormation(force, gather, now);
                        }
                        break;
                    case State.Staging:
                        if (Moved(force, force.Rally) || Stalled(force, now)) MoveFormation(force, force.Rally, now);
                        break;
                    case State.Advancing:
                    case State.Engaged:
                    {
                        bool may = MayReorder(force, now);
                        if (!o.Attack)
                        {
                            if ((Moved(force, o.Where) && (may || BigJump(force, o.Where))) || Stalled(force, now)) MoveFormation(force, o.Where, now);
                            break;
                        }
                        var target = TargetOf(o);
                        if (target != null)
                        {
                            bool targetGone = force.LastTarget != null && force.LastTarget != target && TargetDead(force.LastTarget);
                            if ((force.LastTarget != target && (may || force.LastTarget == null || targetGone)) || Stalled(force, now))
                                AttackTarget(force, target, now);
                        }
                        else
                        {
                            Vector3 at = o.TrackRef != null ? o.TrackRef.Predicted() : o.Where;
                            if ((Moved(force, at) && (may || BigJump(force, at))) || Stalled(force, now)) AttackMove(force, at, now);
                        }
                        break;
                    }
                    case State.Withdrawing:
                        if (Moved(force, force.Rally)) { StopAll(force); MoveFormation(force, force.Rally, now); }
                        break;
                }
            }
            ExecuteReserve(now);
        }

        static void ExecuteReserve(float now)
        {
            if (_reserve == null || _reserve.Units.Count == 0 || _reservePoint == Vector3.zero) return;
            // Only units off the leash and not busy are told; a standing guard is
            // not re-ordered into a huddle.
            var stray = new List<BaseGameObject>();
            for (int i = 0; i < _reserve.Units.Count; i++)
            {
                var u = _reserve.Units[i];
                Vector3 p;
                try { p = u.transform.position; } catch { continue; }
                float dx = p.x - _reservePoint.x, dz = p.z - _reservePoint.z;
                if (dx * dx + dz * dz <= RESERVE_LEASH_M * RESERVE_LEASH_M) continue;
                if (IsFighting(u)) continue;
                stray.Add(u);
            }
            if (stray.Count == 0) return;
            if (now - _reserve.LastOrderAt < 10f) return;
            _reserve.LastOrderAt = now;
            Issue(() => StrategyMode.PerformMoveAttack(stray, _reservePoint, null, AgentMoveSpeed.Fast, false), stray.Count);
            _reserve.OrdersIssued += stray.Count;
        }

        static bool TargetDead(Target t)
        {
            try { return t == null || t.gameObject == null || !t.gameObject.activeInHierarchy; } catch { return true; }
        }

        static Target TargetOf(Objectives.Objective o)
        {
            try
            {
                if (o.StructRef?.Ref != null && !o.StructRef.Ref.IsDestroyed) return o.StructRef.Ref.Target;
                if (o.TrackRef?.Leader != null && !o.TrackRef.Leader.IsDestroyed) return o.TrackRef.Leader.Target;
                if (o.BaseRef != null)
                {
                    // Prefer the HQ, else the most expensive standing member.
                    Intel.KnownStructure pick = null;
                    for (int i = 0; i < o.BaseRef.Members.Count; i++)
                    {
                        var m = o.BaseRef.Members[i];
                        if (m.Ref == null || m.Ref.IsDestroyed) continue;
                        if (m.Class == Intel.StructClass.HQ) { pick = m; break; }
                        if (pick == null || m.Cost > pick.Cost) pick = m;
                    }
                    if (pick != null) return pick.Ref.Target;
                }
            }
            catch { }
            return null;
        }

        static bool Moved(Force force, Vector3 dest)
        {
            if (float.IsNaN(force.LastOrderDest.x)) return true;
            float dx = force.LastOrderDest.x - dest.x, dz = force.LastOrderDest.z - dest.z;
            return dx * dx + dz * dz > ORDER_MOVED_M * ORDER_MOVED_M;
        }

        /// <summary>Standing still well short of where it was sent, most of the
        /// force. Units that have ARRIVED are not stalled and are not told
        /// again — that was a fresh order to every unit standing on its
        /// objective every thirty seconds.</summary>
        static bool Stalled(Force force, float now)
        {
            if (now - force.LastOrderAt < ORDER_BACKSTOP_S) return false;
            int idle = 0, away = 0;
            Vector3 dest = force.LastOrderDest;
            for (int i = 0; i < force.Units.Count; i++)
            {
                var u = force.Units[i];
                Vector3 p; try { p = u.transform.position; } catch { continue; }
                float dx = p.x - dest.x, dz = p.z - dest.z;
                if (dx * dx + dz * dz <= ARRIVED_M * ARRIVED_M * 4f) continue;
                away++;
                bool moving = true;
                try { moving = u.IsMoving; } catch { }
                if (!moving && !IsFighting(u)) idle++;
            }
            return away > 0 && idle > away / 2;
        }

        static void Gather(Force force)
        {
            _scratch.Clear();
            for (int i = 0; i < force.Units.Count; i++)
            {
                var u = force.Units[i];
                if (u == null || u.IsDestroyed) continue;
                if (IsFighting(u)) continue;      // never interrupt a fight
                _scratch.Add(u);
            }
        }

        static void MoveFormation(Force force, Vector3 dest, float now)
        {
            Gather(force);
            if (_scratch.Count == 0) return;
            force.LastOrderDest = dest; force.LastOrderAt = now; force.LastTarget = null;
            var list = new List<BaseGameObject>(_scratch);
            Issue(() => StrategyMode.PerformMoveAttack(list, dest, null, AgentMoveSpeed.Fast, false), list.Count);
            force.OrdersIssued += list.Count;
        }

        /// <summary>
        /// ONE ATTACK ORDER PER UNIT, NO COHESION GROUP. The formation move is
        /// kept for the staging leg only: its UnitCohesionGroup re-issues slot
        /// moves to every member while they walk, and in round five that was
        /// roughly two orders for every one this layer issued — 13,000 military
        /// order events against 4,370 of ours. An attack order on the target
        /// does not go through the group, and the units already stand together
        /// when they leave the rally.
        /// </summary>
        static void AttackTarget(Force force, Target target, float now)
        {
            Gather(force);
            if (_scratch.Count == 0) return;
            Vector3 at;
            try { at = target.transform.position; } catch { return; }
            force.LastOrderDest = at; force.LastOrderAt = now; force.LastTarget = target;
            var def = AttackDefinition();
            int n = 0;
            for (int i = 0; i < _scratch.Count; i++)
            {
                var u = _scratch[i];
                var agent = u.OrderAgent;
                if (agent == null) continue;
                if (def != null)
                    Issue(() => agent.IssueOrder(def, new OrderTarget(at, target), OrderIssueParams.Ai(AgentMoveSpeed.Fast)), 1);
                else
                    Issue(() => agent.IssueResolvedOrder(at, target, OrderIssueParams.Ai(AgentMoveSpeed.Fast)), 1);
                n++;
            }
            force.OrdersIssued += n;
        }

        static void AttackMove(Force force, Vector3 dest, float now)
        {
            Gather(force);
            if (_scratch.Count == 0) return;
            force.LastOrderDest = dest; force.LastOrderAt = now; force.LastTarget = null;
            var def = AttackDefinition();
            if (def == null) { MoveFormation(force, dest, now); return; }
            int n = 0;
            for (int i = 0; i < _scratch.Count; i++)
            {
                var u = _scratch[i];
                var agent = u.OrderAgent;
                if (agent == null) continue;
                int idx = i;
                Issue(() => agent.IssueOrder(def, new OrderTarget(dest), OrderIssueParams.Ai(AgentMoveSpeed.Fast)), 1);
                n++;
            }
            force.OrdersIssued += n;
        }

        static void StopAll(Force force)
        {
            var def = StopDefinition();
            if (def == null) return;
            for (int i = 0; i < force.Units.Count; i++)
            {
                var agent = force.Units[i]?.OrderAgent;
                if (agent == null) continue;
                Issue(() => agent.IssueOrder(def, OrderTarget.None, OrderIssueParams.Ai(AgentMoveSpeed.Fast)), 0);
            }
        }

        static void Stop(Force force) { if (MilConfig.Execute) StopAll(force); }

        static OrderDefinition AttackDefinition()
        {
            try { return OrderDefinitionRegistry.Attack; } catch { return null; }
        }

        static OrderDefinition StopDefinition()
        {
            try { return OrderDefinitionRegistry.Stop; } catch { return null; }
        }

        static void Issue(Action a, int count)
        {
            try
            {
                PlannerOverride = true;
                a();
                OrdersIssued += count;
            }
            catch (Exception ex) { MelonLogger.Warning("[FORCE] order threw: " + ex.Message); }
            finally { PlannerOverride = false; }
        }

        // ---- ownership and the vanilla gates ----------------------------------------

        internal static bool Owns(Unit u)
        {
            if (u == null || !MilConfig.Enabled || !MilConfig.Execute) return false;
            try { if (ScoutPlanner.IsScout(u)) return false; } catch { }
            return _held.Contains(u);
        }

        /// <summary>
        /// The game's own commander keeps thinking for the alien team and would
        /// re-task what we hold. Every order it raises funnels through
        /// AIOrderProcessor.IssueOrder; ours carry PlannerOverride. Anything
        /// else aimed at a unit we hold is refused. Inert while execute is off.
        /// </summary>
        [HarmonyPatch(typeof(AIOrderProcessor), nameof(AIOrderProcessor.IssueOrder))]
        static class Patch_IssueOrder_Gate
        {
            static bool Prefix(AIOrderProcessor __instance, OrderDefinition definition, ref bool __result)
            {
                try
                {
                    if (PlannerOverride) return true;
                    var unit = __instance.OwnerUnit();
                    if (unit == null || !Owns(unit)) return true;
                    try
                    {
                        var t = unit.Team;
                        if (t != null && !AIManager.IsCommanderEnabled(t)) return true;
                    }
                    catch { }
                    __result = false;
                    return false;
                }
                catch { return true; }
            }
        }

        // THE AIGROUP STRIP IS GONE. It removed our units from the game's AI
        // groups inside OnAttackOrder, and the group's network data then carried
        // references to units it no longer held: 43,000 "AIGroup::ReadData:
        // Failed to read unit" / "UnitGroup wrong data type" errors in one
        // round's Game.log, each with a stack trace, on the server. The order
        // gate above already refuses the group's per-unit orders for anything
        // we hold, which is all the strip ever achieved.

        // ---- reporting -------------------------------------------------------------

        internal static int UnitsCommanded()
        {
            int n = _reserve?.Units.Count ?? 0;
            for (int f = 0; f < Active.Count; f++) n += Active[f].Units.Count;
            return n;
        }

        /// <summary>Share of army value that is on an objective or standing where it
        /// was asked to, for Utilisation.</summary>
        internal static void Engagement(out float engagedEff, out float totalEff)
        {
            engagedEff = 0f; totalEff = 0f;
            for (int f = 0; f < Active.Count; f++)
            {
                totalEff += Active[f].Eff;
                if (Active[f].Phase != State.Forming) engagedEff += Active[f].Eff;
            }
            if (_reserve != null)
            {
                totalEff += _reserve.Eff;
                // A reserve that is where it should be is doing its job; one that
                // is idling at spawners is not.
                float inPlace = 0f;
                for (int i = 0; i < _reserve.Units.Count; i++)
                {
                    Vector3 p; try { p = _reserve.Units[i].transform.position; } catch { continue; }
                    if ((p - _reservePoint).sqrMagnitude <= RESERVE_LEASH_M * RESERVE_LEASH_M)
                    {
                        string n = ""; try { n = _reserve.Units[i].ObjectInfo?.DisplayName ?? ""; } catch { }
                        inPlace += Kernel.EffectiveOf(n);
                    }
                }
                engagedEff += inPlace * 0.5f;
            }
        }

        static void Log()
        {
            var sb = new System.Text.StringBuilder("[FORCE] ");
            sb.Append("reserve ").Append(_reserve?.Units.Count ?? 0).Append("u ").Append((_reserve?.Eff ?? 0f).ToString("F0"))
              .Append(" eff at (").Append(_reservePoint.x.ToString("F0")).Append(',').Append(_reservePoint.z.ToString("F0")).Append(')');
            for (int f = 0; f < Active.Count; f++)
            {
                var force = Active[f];
                var c = Centroid(force);
                sb.Append(" | ").Append(force.Name).Append(' ').Append(force.Phase).Append(' ')
                  .Append(force.Units.Count).Append("u ").Append(force.Eff.ToString("F0")).Append('/')
                  .Append((force.Obj?.RequiredEff ?? 0f).ToString("F0")).Append(" at (")
                  .Append(c.x.ToString("F0")).Append(',').Append(c.z.ToString("F0")).Append(") -> (")
                  .Append(force.Obj?.Where.x.ToString("F0")).Append(',').Append(force.Obj?.Where.z.ToString("F0")).Append(')');
            }
            sb.Append(" | orders=").Append(OrdersIssued).Append(" withdrawals=").Append(Withdrawals);
            if (StrippedFromVanillaAttack > 0) sb.Append(" vanillaAttacksBlocked=").Append(StrippedFromVanillaAttack);
            if (!MilConfig.Execute) sb.Append(" [shadow — no orders]");
            MilLog.Msg(sb.ToString());
        }
    }
}
