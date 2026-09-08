using HarmonyLib;
using MelonLoader;
using Silica;
using Silica.AI;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Si_RTS_AI.Planning
{
    /// <summary>
    /// Map-discovery layer. Before this there was NO active scouting anywhere in
    /// the mod — FoWLayers passively accumulated whatever our own buildings and
    /// harvesters happened to see, and the starter Crab was left to vanilla AI.
    ///
    /// What vanilla did with the Crabs on TheMaw 2026-07-28: 490 move orders and
    /// 147 attack orders, all 7 Crabs built were lost, and they spent the round
    /// oscillating between a handful of points (dst=(-548,146,-85) alone appears
    /// dozens of times). Zero deliberate map coverage.
    ///
    /// That matters for eco, not just intel: EcoPlanner gates BC candidates on
    /// the explored layer (see the PatchExplored filter in EnumerateCandidates),
    /// so unexplored ground is unbuildable ground. On TheMaw the AI sat at 15 BCs
    /// at t=511s and only reached 31 by t=751 — the back half of expansion was
    /// spent discovering terrain a scout could have revealed in the first two
    /// minutes.
    ///
    /// Design — uniform radial fan-out:
    ///
    ///   Every scout owns one ARM of a star centred on the Nest. Arm headings
    ///   are simply (arm / MaxScouts) of a full turn, so N scouts leave in N
    ///   evenly spaced directions — at the default 20, one every 18 degrees.
    ///   Each scout walks its own arm outward through RING_RADII_M, and on
    ///   reaching the end starts a new lap rotated HALF an arm spacing, which
    ///   lands it exactly between two previous arms and fills the gaps.
    ///
    ///   Arms are handed out by greedy max-min spread, so a partially filled
    ///   roster is still spread evenly rather than bunched on one side while
    ///   the rest of the map stays dark.
    ///
    ///   If an undiscovered resource patch sits near a geometric waypoint, the
    ///   scout goes to the patch instead — same walk, better payoff. Patch
    ///   positions come from ResourceArea.AllResourceAreas, which is full-map
    ///   knowledge: we always know WHERE the biotics are, we just have not
    ///   revealed them for placement purposes.
    ///
    ///   The roster is topped up by BUILDING Crabs on a slow trickle, so the
    ///   star fills out even on spawns that hand us only one or two units.
    ///
    /// Scouts are held out of vanilla combat the same way shrimps are (see
    /// AlienShrimpAntiAttack): their move orders are ours alone, and they get
    /// stripped from any group about to fan out attack orders.
    /// </summary>
    internal static class ScoutPlanner
    {
        // Opt-out switch, wired to the TestHarness preferences alongside the
        // other subsystem toggles.
        internal static bool Enabled = true;

        // Units we are willing to conscript — the tier-0 chassis the round
        // starts with. Both are cheap and fast, and neither is worth much in a
        // fight this early.
        static HashSet<string> ALIEN_SCOUTS =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Crab", "Squid" };
        // Sol and Centauri: the Scout is the chassis the round starts with and the
        // Barracks makes it; the Light Quad is the fast one.
        static HashSet<string> HUMAN_SCOUTS =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Scout", "Light Quad" };
        static HashSet<string> SCOUT_UNIT_NAMES = ALIEN_SCOUTS;

        // Scouts work alone, never as a pack — one unit per arm of the star.
        // Wired to the ScoutMaxUnits preference. We now BUILD up to this many,
        // so it is a real target rather than "however many the spawn gave us".
        internal static int MaxScouts = 20;

        // Two scouts must never converge on the same waypoint. Kept small:
        // with 20 uniformly-spaced arms the inner ring is only ~140m between
        // neighbours, so a large separation would veto nearly every waypoint.
        // Its real job is stopping two scouts snapping onto the same patch.
        const float MIN_SCOUT_SEPARATION_M = 200f;

        const float TICK_CADENCE_S = 2f;

        // Star geometry. Arms are spaced 360/MaxScouts apart — one per scout,
        // uniform in every direction. Each scout owns an arm and walks it out
        // ring by ring; when it reaches the end it starts a new lap rotated by
        // HALF an arm spacing, which lands it exactly between two old arms and
        // fills the gaps. Lap parity alternates, so coverage keeps doubling in
        // angular resolution without any bookkeeping.
        // Rings are FRACTIONS of the distance from the Nest to the map edge
        // along each heading, not absolute metres. Fixed radii were the second
        // reason scouts never reached the borders: a Nest parked near one edge
        // (NarakaCity spawns at (2520,1275) on a ~+/-3000 map) had its outward
        // rings clipped to nothing on one side while the far side of the map,
        // 5000m+ away, sat outside the largest ring entirely. As fractions the
        // outermost waypoint IS the border, in every direction, on every map.
        static float[] RING_FRACTIONS = { 0.3f, 0.55f, 0.78f, 0.95f };
        // Don't bother walking a ray that barely leaves the base.
        const float MIN_RAY_M = 300f;
        // If an undiscovered biotics patch sits near the geometric waypoint,
        // walk to the patch instead — same trip, better payoff.
        const float PATCH_SNAP_M = 350f;

        // Waypoint lifecycle.
        //
        // A FIXED timeout was the reason scouts never reached the map borders.
        // At 40s a Crab covers roughly 400m, so every waypoint past the first
        // ring expired before arrival and the scout re-targeted from wherever
        // it happened to be — the v0.7.63 round ended 28 timed-out against 12
        // reached. Progress, not elapsed time, is the right test: a scout that
        // is still closing on its waypoint is working, however long it takes.
        const float ARRIVE_M            = 150f;
        const float STALL_CHECK_S       = 15f;   // window for "did we get closer?"
        const float STALL_PROGRESS_M    = 40f;   // must close at least this much
        const float WAYPOINT_HARD_CAP_S = 300f;  // absolute backstop
        const float REISSUE_S           = 5f;    // out-shout vanilla re-tasking

        const float DIAG_INTERVAL_S = 20f;

        // ---- Filling the gaps: build scouts we weren't given ----
        //
        // MaxScouts is a CEILING, not a target to sprint for. The roster grows
        // slowly all game: BuildTargetAtStart scouts to begin with, plus one
        // more per BUILD_TARGET_RAMP_S, until it reaches MaxScouts. On the
        // defaults that is 2 at spawn, 7 by five minutes, and the full 20 only
        // in a long round — vision keeps improving without ever competing with
        // the opening build order.
        //
        // Units the spawn already gave us are exempt: those are free, so we
        // conscript every one of them up to MaxScouts immediately.
        static string SCOUT_BUILD_UNIT      = "Crab";
        static int   BuildTargetAtStart     = 2;
        const float  BUILD_TARGET_RAMP_S    = 60f;   // +1 allowed scout per minute
        static float ScoutBuildIntervalS    = 30f;   // at most one queued per interval
        const int    SCOUT_BUILD_CASH_FLOOR = 1500;  // only spend genuine surplus
        // Units alive inside this window are treated as spawn-issued and are
        // conscripted regardless of the ceiling.
        const float  STARTER_WINDOW_S       = 45f;

        class Scout
        {
            public Unit    Unit;
            public int     Arm;            // which ray of the star this scout owns
            public int     Ring;           // how far out along that ray
            public int     Lap;            // lap 1 sits halfway between lap 0's arms
            public Vector3 Waypoint;
            public bool    HasWaypoint;
            public float   WaypointAt;
            public float   LastOrderAt;
            // Stall tracking — distance to the waypoint at the last checkpoint.
            public float   LastDist;
            public float   LastProgressAt;
            // Where it was standing last time we looked. A destroyed unit
            // cannot be asked, so the grave is recorded from this.
            public Vector3 LastPos;
            public bool    HaveLastPos;
        }

        /// <summary>
        /// WHERE SCOUTS DIE.
        ///
        /// DrMuck, 2026-08-05: "some of the scout crabs were fed into one
        /// scouting arm, because they were cannon fodder for a HQ." An arm that
        /// runs into an enemy base kills whatever walks it, and the roster then
        /// politely sends the next one down the same arm — the fixed star made
        /// that worse, not better, because a freed arm is exactly the one
        /// furthest from the others and so the most attractive to claim.
        ///
        /// A dead scout is not a wasted Crab, it is a measurement: that ground
        /// is held by somebody. It is also ground we no longer need to reveal,
        /// because the scout revealed it on the way in.
        /// </summary>
        struct Grave { public Vector3 Pos; public float At; public int Arm; }
        static List<Grave> _graves = new List<Grave>(8);

        /// <summary>
        /// WHERE OUR SCOUTS KEEP DYING IS WHERE THEY LIVE.
        ///
        /// A grave is currently only a no-go marker, which throws away the one
        /// thing it reliably tells us. A lone Crab that walks into defended
        /// ground and dies has, in the act of dying, located the enemy — 35 of
        /// them did so this round while the army stood at the nest for want of
        /// an objective.
        ///
        /// This is intelligence a human commander reads the same way, and it
        /// costs nothing: the deaths are already recorded. Returns the centroid
        /// of unforgotten graves and how many voted for it, so the caller can
        /// weigh how much to believe it.
        /// </summary>
        internal static bool DeathGround(out Vector3 centre, out int votes)
        {
            centre = Vector3.zero; votes = 0;
            try
            {
                float now = Time.time;
                Vector3 sum = Vector3.zero;
                for (int i = 0; i < _graves.Count; i++)
                {
                    if (now - _graves[i].At > GRAVE_FORGET_S) continue;
                    sum += _graves[i].Pos; votes++;
                }
                if (votes < 2) { votes = 0; return false; }
                centre = sum / votes;
                return true;
            }
            catch { votes = 0; return false; }
        }

        /// <summary>How much ground one death closes. Not a precise radius —
        /// the point is that a neighbourhood becomes off-limits, not a circle
        /// measured to the metre.</summary>
        const float GRAVE_RADIUS_M = 400f;

        /// <summary>A base can fall, and the ground reopens when it does. One
        /// probe every few minutes is cheap; if it dies again the grave is
        /// simply refreshed.</summary>
        const float GRAVE_FORGET_S = 240f;

        static bool NearGrave(Vector3 p)
        {
            float now = Time.time;
            for (int i = _graves.Count - 1; i >= 0; i--)
            {
                if (now - _graves[i].At > GRAVE_FORGET_S) { _graves.RemoveAt(i); continue; }
                float dx = _graves[i].Pos.x - p.x, dz = _graves[i].Pos.z - p.z;
                if (dx * dx + dz * dz < GRAVE_RADIUS_M * GRAVE_RADIUS_M) return true;
            }
            return false;
        }

        static int GravesOnArm(int arm)
        {
            float now = Time.time;
            int n = 0;
            for (int i = 0; i < _graves.Count; i++)
                if (_graves[i].Arm == arm && now - _graves[i].At <= GRAVE_FORGET_S) n++;
            return n;
        }

        static List<Scout> _scouts = new List<Scout>();
        static HashSet<Unit> _scoutSet = new HashSet<Unit>();
        // Units we handed back to vanilla. Without this the roster churned:
        // release -> Recruit picks the same unit up 2s later -> its arm has
        // nothing left -> release again, forever. The v0.7.63 round logged
        // 4773 [SCOUT] lines, almost all of them that loop.
        static HashSet<Unit> _released = new HashSet<Unit>();
        static float _lastTickAt, _lastDiagAt;

        internal static int WaypointsReached;
        internal static int WaypointsTimedOut;
        internal static int ScoutsLost;

        internal static bool IsScout(Unit u)
        {
            if (u == null) return false;
            try { return Mil.MilContext.AnyScout(u); } catch { return false; }
        }

        // Set while WE issue a scout move, so our own OnMoveOrder prefix lets it
        // through. Same mechanism AlienShrimpAntiAttack uses for the shrimps.
        [ThreadStatic] internal static bool PlannerOverride;

        internal static void ResetForNewRound()
        {
            _scouts.Clear();
            _scoutSet.Clear();
            _released.Clear();
            _graves.Clear();
            _lastTickAt = 0f;
            _lastDiagAt = 0f;
            WaypointsReached = 0;
            WaypointsTimedOut = 0;
            ScoutsLost = 0;
            ScoutsQueued = 0;
            _lastBuildAt = 0f;
            _scoutCd = null;
        }

        internal static void Tick(Team team)
        {
            // A PLAYER IN THE ALIEN COMMANDER SEAT COMMANDS. The game disables its
            // own AI commander then, and so does this layer, exactly as vanilla
            // would: it must never spend a player's cash or move a player's units.
            try { if (!Silica.AI.AIManager.IsCommanderEnabled(team)) return; } catch { }
            if (!Enabled || team == null) return;
            bool human = Faction.Construction.IsHuman(team) && Faction.FactionControl.IsEnabled(team);
            if (!(team.name ?? "").Contains("Alien") && !human) return;
            // One planner, one team at a time: the chassis follow the faction.
            SCOUT_UNIT_NAMES = human ? HUMAN_SCOUTS : ALIEN_SCOUTS;
            SCOUT_BUILD_UNIT = human ? "Scout" : "Crab";
            // A Barracks can spam Scouts at 30 cash: humans start the star with six
            // and top it up every ten seconds (DrMuck: "scouting for humans looks
            // too weak, hindering exploration for expansion HQs").
            BuildTargetAtStart = human ? 6 : 2;
            ScoutBuildIntervalS = human ? 10f : 30f;
            if (!global::Si_RTS_AI.TestHarnessNs.TestHarness.IsRoundActive) return;

            float now = Time.time;
            if (now - _lastTickAt < TICK_CADENCE_S) return;
            _lastTickAt = now;

            try { Run(team, now); }
            catch (Exception ex) { MelonLogger.Warning("[SCOUT] threw: " + ex.Message); }
        }

        static void Run(Team team, float now)
        {
            Vector3 nest = Vector3.zero;
            bool haveNest = false;
            try
            {
                var structs = team.Structures;
                if (structs == null) return;
                for (int i = 0; i < structs.Count; i++)
                {
                    var s = structs[i];
                    if (s == null || s.ObjectInfo == null || s.IsDestroyed) continue;
                    // The home structure: the Nest, or the Headquarters for Sol and
                    // Centauri. Looking for "Nest" only, the planner never ran for a
                    // human team (2026-09-07 21:52: no scout line in a Sol round).
                    string dn = s.ObjectInfo.DisplayName ?? "";
                    if (dn == "Nest" || dn == "Headquarters") { nest = s.transform.position; haveNest = true; break; }
                }
            }
            catch { return; }
            if (!haveNest) return;

            // ---- Roster upkeep ----
            for (int i = _scouts.Count - 1; i >= 0; i--)
            {
                var u = _scouts[i].Unit;
                if (u == null || u.IsDestroyed)
                {
                    if (u != null) _scoutSet.Remove(u);
                    // Mark the ground. The unit is gone, so this is the last
                    // place we saw it — close enough, since GRAVE_RADIUS_M is a
                    // neighbourhood rather than a point.
                    if (_scouts[i].HaveLastPos)
                    {
                        _graves.Add(new Grave
                        { Pos = _scouts[i].LastPos, At = now, Arm = _scouts[i].Arm });
                        MelonLogger.Msg($"[SCOUT] lost a scout at " +
                                        $"({_scouts[i].LastPos.x:F0},{_scouts[i].LastPos.z:F0}) " +
                                        $"on arm {_scouts[i].Arm} — closing that ground for " +
                                        $"{GRAVE_FORGET_S:F0}s");
                    }
                    _scouts.RemoveAt(i);
                    ScoutsLost++;
                }
            }
            if (_scouts.Count < MaxScouts || InStarterWindow()) Recruit(team);
            // Conscription first, production second — only build what the map
            // did not already hand us.
            MaybeBuildScout(team, now);
            if (_scouts.Count == 0) return;

            // ---- Unexplored patches, in world coords ----
            var explored = Perception.MapLayers.FoWLayers.GetExplored(team);
            var unexplored = new List<Vector3>();
            try
            {
                var all = ResourceArea.AllResourceAreas;
                if (all != null)
                {
                    var uType = team.UsableResource;
                    for (int i = 0; i < all.Count; i++)
                    {
                        var ra = all[i];
                        if (ra == null || ra.IsEmpty || ra.ResourceType != uType) continue;
                        var p = ra.SignalCenter;
                        if (explored != null &&
                            explored.IsSet(Perception.MapLayers.GridWorld.CellX(p.x),
                                           Perception.MapLayers.GridWorld.CellZ(p.z))) continue;
                        unexplored.Add(p);
                    }
                }
            }
            catch { }

            // ---- Drive each scout ----
            for (int i = 0; i < _scouts.Count; i++)
            {
                var sc = _scouts[i];
                var u = sc.Unit;
                if (u == null || u.IsDestroyed) continue;
                var pos = u.transform.position;
                sc.LastPos = pos; sc.HaveLastPos = true;

                bool needNew = !sc.HasWaypoint;
                if (!needNew)
                {
                    float dx = pos.x - sc.Waypoint.x, dz = pos.z - sc.Waypoint.z;
                    float dist = Mathf.Sqrt(dx * dx + dz * dz);
                    if (dist <= ARRIVE_M) { needNew = true; WaypointsReached++; }
                    else if (now - sc.LastProgressAt >= STALL_CHECK_S)
                    {
                        // Closed meaningful ground since the last checkpoint?
                        // Then keep going, no matter how far is left.
                        if (sc.LastDist - dist >= STALL_PROGRESS_M)
                        {
                            sc.LastDist = dist;
                            sc.LastProgressAt = now;
                        }
                        else { needNew = true; WaypointsTimedOut++; }
                    }
                    else if (now - sc.WaypointAt > WAYPOINT_HARD_CAP_S)
                    { needNew = true; WaypointsTimedOut++; }
                }

                if (needNew)
                {
                    if (!NextWaypoint(sc, nest, unexplored, explored, out var wp)
                        && !FrontierWaypoint(sc, pos, unexplored, explored, out wp))
                    {
                        // AN EXHAUSTED ARM IS NOT AN EXPLORED MAP.
                        //
                        // This used to release on the arm alone, and the round
                        // of 2026-08-04 shows what that costs: 58 Crabs built,
                        // two alive as scouts, "star exhausted — released" every
                        // twenty seconds, and exploration stuck at 58% with 38
                        // patches still dark. Every ring along a short arm was
                        // already known, so each newly built scout was recruited
                        // and released within the same second, forever.
                        //
                        // Release now needs BOTH tests to fail: the arm has
                        // nothing left AND there is no dark ground anywhere.
                        _scoutSet.Remove(u);
                        _released.Add(u);   // never re-conscript — see _released
                        _scouts.RemoveAt(i);
                        i--;
                        MelonLogger.Msg("[SCOUT] map revealed — released scout back to vanilla control");
                        continue;
                    }
                    sc.Waypoint = wp;
                    sc.HasWaypoint = true;
                    sc.WaypointAt = now;
                    sc.LastDist = Vector3.Distance(new Vector3(pos.x, 0f, pos.z), new Vector3(wp.x, 0f, wp.z));
                    sc.LastProgressAt = now;
                    IssueMove(u, wp);
                    sc.LastOrderAt = now;
                }
                else if (now - sc.LastOrderAt >= REISSUE_S && !StillMoving(u))
                {
                    // Vanilla re-tasking is blocked at the order gate now, so a
                    // scout only needs telling again when it has actually
                    // stopped. Restating every five seconds regardless was 44%
                    // of all order lines in the 2026-08-13 round.
                    IssueMove(u, sc.Waypoint);
                    sc.LastOrderAt = now;
                }
            }

            if (now - _lastDiagAt > DIAG_INTERVAL_S)
            {
                _lastDiagAt = now;
                LogDiag(team, explored, unexplored.Count);
            }
        }

        // Arms are handed out in bit-reversed order within the current arm
        // count, so a PARTIALLY filled star is still spread evenly. With 6
        // arms: 1 scout -> {0}, 2 -> {0,3} (opposite), 3 -> {0,3,1}, and so on.
        // Sequential assignment would send the first two scouts off at 60
        // degrees to each other and leave the whole other half of the map dark
        // until the roster filled up.
        /// <summary>
        /// How many arms the star has. FIXED for the round, not the live scout
        /// count.
        ///
        /// Sizing it by the roster meant the star had two arms when two scouts
        /// were alive — and worse, every arrival or loss re-divided the circle,
        /// so a scout's heading CHANGED underneath it. That is DrMuck's
        /// observation on 2026-08-05 exactly: units turned around before
        /// reaching the west border. They were not turning around, they were
        /// being re-aimed by somebody else's death.
        ///
        /// A fixed star means an arm is a place on the map, not a share of the
        /// roster, and a scout that owns one keeps walking it.
        /// </summary>
        static int ArmCount() => Mathf.Max(4, MaxScouts);

        /// <summary>
        /// Arm index = position in the roster, reassigned whenever the roster
        /// changes. This replaces a "pick the next free arm" scheme that broke
        /// on the real spawn pattern: alien starter units arrive in WAVES —
        /// 1 Crab at t=2s, then 6 more Crabs at t=18s, then 5 Squids at
        /// t=22-24s (identical on Naraka, TheMaw and every map checked). The
        /// arm COUNT therefore grew while arms were being handed out, so the
        /// first scouts kept low indices that were evenly spread across a
        /// 4-arm star but bunched into one quadrant of the 12-arm star the
        /// roster actually became — and once the roster passed the arm count,
        /// the search fell through and handed out duplicate arm 0.
        ///
        /// SUPERSEDED 2026-08-05 — an arm is now claimed once and held for the
        /// unit's life. Re-spreading the survivors sounded self-correcting and
        /// was not: a roster that changes every time a Crab is built or lost
        /// re-aims every scout still walking, so nobody ever arrives anywhere.
        /// </summary>
        static void ReindexArms() { }

        /// <summary>
        /// Claim the free arm that sits furthest from every arm already taken.
        /// With a fixed 20-arm star and three scouts that puts them ~120 apart
        /// rather than three arms deep in one quadrant, and it never disturbs a
        /// scout that is already walking.
        /// </summary>
        static int ClaimArm()
        {
            int arms = ArmCount();
            int best = 0; float bestScore = float.MinValue;
            for (int a = 0; a < arms; a++)
            {
                bool taken = false;
                float gap = float.MaxValue;
                for (int i = 0; i < _scouts.Count; i++)
                {
                    int d = Mathf.Abs(_scouts[i].Arm - a);
                    if (d > arms / 2) d = arms - d;      // shortest way round
                    if (d == 0) { taken = true; break; }
                    if (d < gap) gap = d;
                }
                if (taken) continue;

                // AN ARM THAT KILLED A SCOUT IS THE MOST ATTRACTIVE ARM THERE
                // IS, AND THAT IS THE PROBLEM. It is empty, so it is furthest
                // from everyone — which is exactly how a queue of Crabs ends up
                // feeding one enemy HQ one at a time. Each death makes its arm
                // less attractive by a full arm-spacing of angular gap, so a
                // hostile arm loses to any quiet direction.
                float score = gap - GravesOnArm(a) * ARM_GRAVE_PENALTY;
                if (score > bestScore) { bestScore = score; best = a; }
            }
            return best;
        }

        /// <summary>Arm-spacings of angular gap one dead scout is worth. Set so
        /// a single death moves the next recruit to a different part of the map
        /// rather than nudging it one arm along.</summary>
        const float ARM_GRAVE_PENALTY = 3f;

        static void Recruit(Team team)
        {
            try
            {
                var units = team.Units;
                if (units == null) return;
                // Starter units are free vision — take every one of them, even
                // past the ceiling. Maps hand out a good number of Crabs (and
                // a few Squids) at spawn and there is nothing better for them
                // to do this early. The ceiling only governs units that show
                // up LATER, so we never quietly swallow the whole army.
                int cap = InStarterWindow() ? int.MaxValue : MaxScouts;
                for (int i = 0; i < units.Count && _scouts.Count < cap; i++)
                {
                    var u = units[i];
                    if (u == null || u.ObjectInfo == null || u.IsDestroyed) continue;
                    if (!SCOUT_UNIT_NAMES.Contains(u.ObjectInfo.DisplayName ?? "")) continue;
                    if (_scoutSet.Contains(u) || _released.Contains(u)) continue;
                    int arm = ClaimArm();
                    _scouts.Add(new Scout { Unit = u, Ring = 0, Lap = 0, Arm = arm });
                    _scoutSet.Add(u);
                    MelonLogger.Msg($"[SCOUT] recruited {u.ObjectInfo.DisplayName} " +
                                    $"arm={arm}/{ArmCount()} (scouts={_scouts.Count}, ceiling={MaxScouts})");
                }
            }
            catch { }
        }

        // ---- Scout production ------------------------------------------------
        static ConstructionData? _scoutCd;
        static float _lastBuildAt;
        internal static int ScoutsQueued;

        /// <summary>
        /// How many scouts we are willing to have BUILT by now. Grows one per
        /// BUILD_TARGET_RAMP_S and never exceeds MaxScouts. Conscripted starter
        /// units ignore this — they cost nothing.
        /// </summary>
        /// <summary>Round is young enough that anything alive counts as a starter unit.</summary>
        static bool InStarterWindow()
        {
            try { return Perception.MapLayers.LayerReplay.CurrentRoundTime <= STARTER_WINDOW_S; }
            catch { return false; }
        }

        static int BuildTargetNow()
        {
            float roundT = 0f;
            try { roundT = Perception.MapLayers.LayerReplay.CurrentRoundTime; } catch { }
            int ramped = BuildTargetAtStart + Mathf.FloorToInt(roundT / BUILD_TARGET_RAMP_S);
            return Mathf.Min(ramped, MaxScouts);
        }

        /// <summary>
        /// Top the roster up to MaxScouts by queuing cheap Crabs at a Cyst.
        /// One at a time, on a slow cadence, and never below the cash floor —
        /// this is a trickle to fill the gaps in the star, not a production
        /// line competing with eco.
        /// </summary>
        static void MaybeBuildScout(Team team, float now)
        {
            if (_scouts.Count >= BuildTargetNow()) return;
            if (now - _lastBuildAt < ScoutBuildIntervalS) return;
            try
            {
                if (team.TotalResources < SCOUT_BUILD_CASH_FLOOR) return;

                var structs = team.Structures;
                if (structs == null) return;

                if (_scoutCd == null)
                {
                    for (int i = 0; i < structs.Count && _scoutCd == null; i++)
                    {
                        var opts = structs[i]?.ConstructionOptions;
                        if (opts == null) continue;
                        foreach (var opt in opts)
                        {
                            if (opt?.ObjectInfo == null) continue;
                            if (string.Equals(opt.ObjectInfo.DisplayName, SCOUT_BUILD_UNIT, StringComparison.OrdinalIgnoreCase))
                            { _scoutCd = opt; break; }
                        }
                    }
                }
                if (_scoutCd == null) return;

                for (int i = 0; i < structs.Count; i++)
                {
                    var s = structs[i];
                    if (s == null || s.ObjectInfo == null || s.IsDestroyed) continue;
                    if (s.ConstructionOptions == null || !s.ConstructionOptions.Contains(_scoutCd)) continue;
                    if (s.Construct(_scoutCd) != ProductionActionResult.Success) continue;
                    _lastBuildAt = now;
                    ScoutsQueued++;
                    MelonLogger.Msg($"[SCOUT] queued a {SCOUT_BUILD_UNIT} to fill the star " +
                                    $"({_scouts.Count} scouts, buildTarget={BuildTargetNow()}, " +
                                    $"ceiling={MaxScouts}, built={ScoutsQueued})");
                    return;
                }
            }
            catch (Exception ex) { MelonLogger.Warning("[SCOUT] build threw: " + ex.Message); }
        }

        /// <summary>
        /// Walk the scout straight out along its own arm of the star, ring by
        /// ring. The arm's heading is simply (arm / MaxScouts) of a full turn,
        /// so with N scouts the team fans out uniformly in N directions from
        /// the Nest. At the end of the arm the scout starts a new lap rotated
        /// half an arm-spacing, putting it exactly between two previous arms —
        /// that is what fills the gaps.
        ///
        /// If an undiscovered biotics patch happens to sit near the geometric
        /// waypoint it becomes the destination instead. Same walk, better
        /// payoff, and it keeps the FoW gate on BC placement fed.
        /// </summary>
        static bool NextWaypoint(Scout sc, Vector3 nest, List<Vector3> unexplored,
                                 Perception.MapLayers.LayerB explored, out Vector3 wp)
        {
            wp = Vector3.zero;
            int arms = ArmCount();
            float spacing = 2f * Mathf.PI / arms;
            int stepsTotal = RING_FRACTIONS.Length * 2;   // two laps before we give up

            for (int tries = 0; tries < stepsTotal; tries++)
            {
                if (sc.Ring >= RING_FRACTIONS.Length) { sc.Ring = 0; sc.Lap++; }
                // Lap parity offsets the heading by half a spacing, so odd laps
                // bisect the gaps left by even laps.
                float heading = (sc.Arm + 0.5f * (sc.Lap & 1)) * spacing;
                float frac    = RING_FRACTIONS[sc.Ring];
                sc.Ring++;

                float dirX = Mathf.Cos(heading), dirZ = Mathf.Sin(heading);
                float toEdge = RayLengthToMapEdge(nest, dirX, dirZ);
                if (toEdge < MIN_RAY_M) continue;   // heading runs straight off the map
                float radius = toEdge * frac;

                var geo = new Vector3(nest.x + dirX * radius, nest.y, nest.z + dirZ * radius);
                if (!ClampToMap(ref geo)) continue;

                // Snap onto a nearby undiscovered patch when there is one.
                int best = -1; float bestSq = PATCH_SNAP_M * PATCH_SNAP_M;
                for (int i = 0; i < unexplored.Count; i++)
                {
                    float dx = unexplored[i].x - geo.x, dz = unexplored[i].z - geo.z;
                    float d = dx * dx + dz * dz;
                    if (d < bestSq && !TooCloseToOtherScout(sc, unexplored[i])
                        && !NearGrave(unexplored[i])) { bestSq = d; best = i; }
                }
                if (best >= 0)
                {
                    wp = unexplored[best];
                    unexplored.RemoveAt(best);   // never send two scouts to the same patch
                    return true;
                }

                // Nothing to snap to — take the bare point, unless it is ground
                // we already know or another scout is already headed there.
                if (explored != null &&
                    explored.IsSet(Perception.MapLayers.GridWorld.CellX(geo.x),
                                   Perception.MapLayers.GridWorld.CellZ(geo.z))) continue;
                if (TooCloseToOtherScout(sc, geo)) continue;
                // Somebody already died proving this ground is held — and died
                // revealing it on the way in, so there is nothing to gain.
                if (NearGrave(geo)) continue;
                wp = geo;
                return true;
            }
            return false;   // this arm is fully revealed — release the scout
        }

        /// <summary>
        /// THE STAR IS THE SHAPE, NOT THE LIMIT.
        ///
        /// When a scout's arm holds nothing new, the answer is the nearest dark
        /// ground, not a discharge. Undiscovered biotics come first — they are
        /// what the economy is actually waiting on — and bare unexplored terrain
        /// second, sampled coarsely off the fog layer since a scout reveals a
        /// disk around itself and does not need the exact cell.
        /// </summary>
        static bool FrontierWaypoint(Scout sc, Vector3 from, List<Vector3> unexplored,
                                     Perception.MapLayers.LayerB explored, out Vector3 wp)
        {
            wp = Vector3.zero;

            int best = -1; float bestSq = float.MaxValue;
            for (int i = 0; i < unexplored.Count; i++)
            {
                float dx = unexplored[i].x - from.x, dz = unexplored[i].z - from.z;
                float d = dx * dx + dz * dz;
                if (d < bestSq && !TooCloseToOtherScout(sc, unexplored[i])
                    && !NearGrave(unexplored[i])) { bestSq = d; best = i; }
            }
            if (best >= 0)
            {
                wp = unexplored[best];
                unexplored.RemoveAt(best);
                return true;
            }

            if (explored == null) return false;

            // Coarse sweep of the fog layer for the nearest cell nobody has
            // seen. Stride keeps this cheap; a scout's vision covers far more
            // ground than the gap it skips.
            const int STRIDE = 4;
            var g = Perception.MapLayers.GridWorld.CellSize;
            float bestCellSq = float.MaxValue;
            for (int cz = 0; cz < Perception.MapLayers.GridWorld.Height; cz += STRIDE)
            for (int cx = 0; cx < Perception.MapLayers.GridWorld.Width;  cx += STRIDE)
            {
                if (explored.IsSet(cx, cz)) continue;
                float wx = Perception.MapLayers.GridWorld.OriginX + (cx + 0.5f) * g;
                float wz = Perception.MapLayers.GridWorld.OriginZ + (cz + 0.5f) * g;
                float dx = wx - from.x, dz = wz - from.z;
                float d = dx * dx + dz * dz;
                if (d >= bestCellSq) continue;
                var p = new Vector3(wx, from.y, wz);
                if (TooCloseToOtherScout(sc, p)) continue;
                if (NearGrave(p)) continue;
                bestCellSq = d; wp = p;
            }
            return bestCellSq < float.MaxValue;
        }

        /// <summary>
        /// Scouts are vision, not a squad — two of them standing on the same
        /// ground reveal one disk instead of two. Keeps candidate waypoints
        /// clear of every other scout's live destination.
        /// </summary>
        static bool TooCloseToOtherScout(Scout self, Vector3 p)
        {
            for (int i = 0; i < _scouts.Count; i++)
            {
                var o = _scouts[i];
                if (o == self || !o.HasWaypoint) continue;
                float dx = o.Waypoint.x - p.x, dz = o.Waypoint.z - p.z;
                if (dx * dx + dz * dz < MIN_SCOUT_SEPARATION_M * MIN_SCOUT_SEPARATION_M) return true;
            }
            return false;
        }

        /// <summary>
        /// Distance from <paramref name="from"/> to the map boundary along a
        /// unit heading — standard slab clip against the grid's AABB. This is
        /// what makes the outermost ring land ON the border rather than at an
        /// arbitrary radius that may be short of it or well past it.
        /// </summary>
        static float RayLengthToMapEdge(Vector3 from, float dirX, float dirZ)
        {
            const float MARGIN_M = 100f;
            float minX = Perception.MapLayers.GridWorld.OriginX + MARGIN_M;
            float minZ = Perception.MapLayers.GridWorld.OriginZ + MARGIN_M;
            float maxX = Perception.MapLayers.GridWorld.OriginX
                       + Perception.MapLayers.GridWorld.Width  * Perception.MapLayers.GridWorld.CellSize - MARGIN_M;
            float maxZ = Perception.MapLayers.GridWorld.OriginZ
                       + Perception.MapLayers.GridWorld.Height * Perception.MapLayers.GridWorld.CellSize - MARGIN_M;
            if (maxX <= minX || maxZ <= minZ) return 0f;

            float t = float.MaxValue;
            if (Mathf.Abs(dirX) > 1e-4f)
            {
                float tx = ((dirX > 0f ? maxX : minX) - from.x) / dirX;
                if (tx > 0f) t = Mathf.Min(t, tx);
            }
            if (Mathf.Abs(dirZ) > 1e-4f)
            {
                float tz = ((dirZ > 0f ? maxZ : minZ) - from.z) / dirZ;
                if (tz > 0f) t = Mathf.Min(t, tz);
            }
            return t == float.MaxValue ? 0f : t;
        }

        /// <summary>Clamp into the playable grid with a margin. False if degenerate.</summary>
        static bool ClampToMap(ref Vector3 p)
        {
            const float MARGIN_M = 100f;
            float minX = Perception.MapLayers.GridWorld.OriginX + MARGIN_M;
            float minZ = Perception.MapLayers.GridWorld.OriginZ + MARGIN_M;
            float maxX = Perception.MapLayers.GridWorld.OriginX
                       + Perception.MapLayers.GridWorld.Width  * Perception.MapLayers.GridWorld.CellSize - MARGIN_M;
            float maxZ = Perception.MapLayers.GridWorld.OriginZ
                       + Perception.MapLayers.GridWorld.Height * Perception.MapLayers.GridWorld.CellSize - MARGIN_M;
            if (maxX <= minX || maxZ <= minZ) return false;
            p.x = Mathf.Clamp(p.x, minX, maxX);
            p.z = Mathf.Clamp(p.z, minZ, maxZ);
            return true;
        }

        static bool StillMoving(Unit u)
        {
            try { return u.IsMoving; } catch { return false; }
        }

        static void IssueMove(Unit u, Vector3 pos)
        {
            try
            {
                PlannerOverride = true;
                u.IssueMoveOrder(pos, AgentMoveSpeed.Fast);
            }
            catch (Exception ex) { MelonLogger.Warning("[SCOUT] OnMoveOrder threw: " + ex.Message); }
            finally { PlannerOverride = false; }
        }

        static void LogDiag(Team team, Perception.MapLayers.LayerB explored, int unexploredPatches)
        {
            int set = 0, total = 0;
            if (explored != null)
            {
                var data = explored.Data;
                total = data.Length;
                for (int i = 0; i < data.Length; i++) if (data[i] != 0) set++;
            }
            float pct = total > 0 ? 100f * set / total : 0f;
            MelonLogger.Msg($"[SCOUT] scouts={_scouts.Count} target={BuildTargetNow()} ceiling={MaxScouts} explored={pct:F1}% " +
                            $"undiscoveredPatches={unexploredPatches} reached={WaypointsReached} " +
                            $"timedOut={WaypointsTimedOut} built={ScoutsQueued} lost={ScoutsLost}");
        }

        internal static string BuildRoundSummaryFragment()
        {
            if (WaypointsReached == 0 && WaypointsTimedOut == 0 && ScoutsLost == 0) return "";
            return "--- Scout planner ---\n" +
                   $"  Waypoints reached:  {WaypointsReached}\n" +
                   $"  Waypoints timed out: {WaypointsTimedOut}\n" +
                   $"  Scouts lost:         {ScoutsLost}\n";
        }

        // ---- Keep scouts out of vanilla's hands ----------------------------
        //
        // Both patches mirror AlienShrimpAntiAttack. Harmony happily stacks a
        // second prefix on the same method; the original is skipped if EITHER
        // returns false, which is the behaviour we want.

#if GAME_MAIN
        [HarmonyPatch(typeof(Unit), nameof(Unit.OnMoveOrder))]
        static class Patch_Unit_OnMoveOrder_Scout
        {
            static bool Prefix(Unit __instance)
            {
                bool __result = false;
                try
                {
                    var unit = __instance;
                    if (unit == null || !IsScout(unit)) return true;
#else
        [HarmonyPatch(typeof(AIOrderProcessor), nameof(AIOrderProcessor.IssueOrder))]
        static class Patch_Unit_OnMoveOrder_Scout
        {
            static bool Prefix(AIOrderProcessor __instance, OrderDefinition definition, ref bool __result)
            {
                try
                {
                    if (!OrderCompat.IsMoveOrder(definition)) return true;
                    var unit = __instance.OwnerUnit();
                    if (unit == null || !IsScout(unit)) return true;
#endif
                    if (PlannerOverride) return true;
                    // Hand control back if a human took the commander seat.
                    try
                    {
                        var t = unit.Team;
                        if (t != null && !AIManager.IsCommanderEnabled(t)) return true;
                        if (t != null && !Faction.FactionControl.IsEnabled(t)) return true;   // switched off: vanilla commands
                    }
                    catch { }
                    __result = false;
                    return false;   // vanilla does not get to re-task our scouts
                }
                catch { return true; }
            }
        }

        [HarmonyPatch(typeof(AIGroup), nameof(AIGroup.OnAttackOrder))]
        static class Patch_AIGroup_OnAttackOrder_Scout
        {
            static List<Unit> _scratch = new List<Unit>(4);

            static void Prefix(AIGroup __instance)
            {
                try
                {
                    if (__instance == null || _scoutSet.Count == 0) return;
                    var units = __instance.Units;
                    if (units == null || units.Count == 0) return;

                    _scratch.Clear();
                    for (int i = 0; i < units.Count; i++)
                    {
                        var u = units[i];
                        if (u != null && IsScout(u)) _scratch.Add(u);
                    }
                    for (int i = 0; i < _scratch.Count; i++)
                    {
                        try { __instance.RemoveUnit(_scratch[i]); } catch { }
                    }
                    _scratch.Clear();
                }
                catch { }
            }
        }
    }
}
