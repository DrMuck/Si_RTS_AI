using MelonLoader;
using Silica;
using Silica.AI;
using System.Collections.Generic;
using UnityEngine;

namespace Si_RTS_AI.Planning
{
    /// <summary>
    /// Shrimp GROUP planner — global allocation of the shrimp pool across
    /// BC-anchored harvest groups.
    ///
    /// Replaces ShrimpRelocator (per-shrimp greedy marginal utility). That
    /// design had a structural defect proven on TheMaw 2026-07-28:
    ///
    ///   t=800s, 202 shrimps, 31 Bio Caches.
    ///     BC13 (495,1030):  75 shrimps  (53 of them inside one 40m cell)
    ///     8 BCs:             0 shrimps
    ///     11 more BCs:      <=3 shrimps
    ///     effective BCs in use: ~10 of 31
    ///
    ///   The relocator saw the pile (its own diag reported over18=4) but
    ///   never moved anyone, because its score multiplied income by a
    ///   walk-time utility `(30s - walk)/30s`. At 9 m/s that is a hard
    ///   270m reach. The nearest alternative BC to BC13 was 300m away and
    ///   the rest 589-2005m, so EVERY alternative scored exactly 0 and the
    ///   pile was unreachable by construction. Total moves for the whole
    ///   16-minute round: 72.
    ///
    ///   The mass migration the user observed (t=620-700: shrimps in
    ///   transit 12 -> 77 -> 12, largest group 59 -> 13 -> 70) is vanilla
    ///   harvest AI re-tasking a whole depleted cluster at once. Nothing
    ///   pre-empted it.
    ///
    /// This planner inverts the model:
    ///
    ///   1. GROUPS. Every Bio Cache / Nest anchors a group. Patches are
    ///      assigned to the nearest anchor within GROUP_PATCH_RADIUS_M.
    ///
    ///   2. CAPACITY. capacity_g = min(HARD_CAP,
    ///                                 patches_g * SHRIMPS_PER_PATCH,
    ///                                 remaining_g / (harvest_rate * MIN_LIFE_S))
    ///      The third term is the anti-swarm mechanism: as a group's
    ///      biotics drain, its capacity falls smoothly, so shrimps are
    ///      bled out gradually LONG before the patch actually empties and
    ///      vanilla re-tasks everyone in one tick.
    ///
    ///   3. WATERFILL. Groups are ranked by income-per-shrimp (cycle time
    ///      against their nearest patch). Two passes: fill every group to
    ///      SOFT_TARGET first (spread = depletion resilience, and the
    ///      crowd curve is flat to ~6-8), then top up toward capacity.
    ///
    ///   4. TRICKLE. At most MOVES_PER_TICK moves per tick, team-wide.
    ///      Rebalancing is continuous and slow by design — the point is to
    ///      never need a wave.
    ///
    ///   5. MOVES MUST PAY FOR THEMSELVES. This started as "NO WALK VETO" —
    ///      written because the old relocator's hard 270m window made TheMaw's
    ///      75-shrimp pile literally unreachable. It fixed that and introduced
    ///      the mirror fault: on the NarakaCity opening, shrimps walked from
    ///      the SOUTHERN biotics to the NORTHERN ones, abandoning the nearest
    ///      Bio Cache for the furthest, to gain a fractionally better cycle.
    ///
    ///      Neither a distance window nor nothing. The test is PAYBACK:
    ///        payback_s = walk_s * marginal_src / (marginal_dst - marginal_src)
    ///      A shrimp with no group has nothing to lose and moves freely; a
    ///      crowded source has low marginal output so draining piles stays
    ///      cheap; a long walk for a small edge no longer passes.
    ///
    ///   6. STICKY. Assignments persist and are re-issued if the shrimp
    ///      drifts back, because vanilla harvest AI will otherwise pull it
    ///      home. Assignment is dropped when its patch dies.
    /// </summary>
    internal static class ShrimpGroupPlanner
    {
        const float TICK_CADENCE_S = 2f;

        // Movement / harvest constants — mirror EcoSimulator.
        const float SHRIMP_SPEED   = 9f;
        const float HARVEST_RATE   = 9.5f;
        const float DEPOSIT_RATE   = 50f;
        const int   CARRY          = 400;
        const float PER_SHRIMP_HARVEST_PER_SEC = 8f;

        // A shrimp counts toward the group whose anchor is nearest within this.
        const float CLAIM_RADIUS_M = 150f;
        // A patch belongs to the group whose anchor is nearest within this.
        const float GROUP_PATCH_RADIUS_M = 200f;

        // SOFT_TARGET is a SPREAD policy, not a capacity: fill every group to
        // this before topping any of them up, so the team covers more patches
        // early and survives one of them depleting.
        const int   SOFT_TARGET = 8;

        // How many shrimps a single patch can usefully carry. This is the
        // CAPACITY figure and it is NOT the same number as SOFT_TARGET —
        // conflating them was a real bug: capacity came out as
        // patches * SOFT_TARGET, so a one-patch group could never exceed 8,
        // counted as full at 8, and donated every further shrimp away. Meanwhile
        // AlienShrimpProducer was happily building up to 15 at that same Bio
        // Cache, so the two halves fought each other and shrimps left biotics
        // that were not remotely crowded.
        //
        // 18 per patch, per the user 2026-07-29. It also matches the crowd
        // model: income is n * CARRY * crowd(n) / cycle, and with crowd 1.0/
        // 0.85/0.70 at 6/12/18 the total is still RISING at 18 (6.0 -> 10.2 ->
        // 12.6 effective). Nothing about the income curve argued for 8.
        const int   PER_PATCH_CAPACITY = 18;
        // Ceiling for one group. Kept in step with AlienShrimpProducer.PER_BC_CAP.
        const int   HARD_CAP    = 18;

        // A group must be able to feed its shrimps for at least this long,
        // else its capacity is cut. Drives graceful pre-depletion bleed-off.
        const float MIN_LIFE_S = 120f;

        // Patches below this are dead to us.
        const int   PATCH_EMPTY_HARD = 200;

        // Trickle rate. 4 per 2s tick = 2 shrimps/sec team-wide; draining a
        // 60-shrimp pile takes ~30s of ticks spread across many destinations,
        // instead of one instantaneous wave.
        const int   MOVES_PER_TICK = 4;
        /// <summary>Cap on the deficit list. Was MOVES_PER_TICK * 4, which
        /// bounded a tick's placements to 16 even when the surplus was 40.</summary>
        const int   NEEDS_CAP = 64;

        // A relocation must repay its walk within this. Generous enough that
        // genuinely better ground is still worth crossing to, tight enough that
        // a marginal edge across the map is not.
        const float MOVE_PAYBACK_LIMIT_S = 120f;
        // Max walk into a group that has its own Cyst: it will fill itself.
        const float SELF_SUFFICIENT_MAX_WALK_S = 25f;

        /// <summary>
        /// How far a newly produced shrimp may be sent. Roughly one patch
        /// spacing — far enough to reach a neighbouring group that needs it,
        /// not far enough to cross the base. Groups beyond this are meant to be
        /// staffed by their own Cyst, which is what the Phase 2 production
        /// headroom exists to make possible.
        /// </summary>
        const float FREE_AGENT_MAX_WALK_S = 30f;

        /// <summary>
        /// How much nearer a group with its own Cyst has to be to win. At 0.65 a
        /// producer-less patch is preferred while it is under ~1.5x the walk —
        /// worth going out of the way for, not worth crossing the map for.
        /// </summary>
        const float NO_PRODUCER_DISCOUNT = 0.65f;

        /// <summary>
        /// Untapped ground has no Bio Cache yet, so a shrimp sent there waits
        /// for the expansion before it can deposit anything. Priced above its
        /// real distance to reflect that.
        /// </summary>
        const float UNTAPPED_PENALTY = 1.35f;

        // Per-unit gates.
        // A shrimp that just arrived somewhere is protected from being made a
        // donor again for this long — without it, a group that overshoots by
        // one bounces the same shrimp back and forth.
        const float REASSIGN_COOLDOWN_S = 60f;
        const float ASSIGN_TTL_S        = 240f;  // forget stale assignments
        const float REISSUE_DRIFT_M     = 220f;  // drifted this far from its target -> re-order
        const float REISSUE_COOLDOWN_S  = 15f;

        struct Assignment
        {
            public Vector3 Target;
            public float   AssignedAt;
            public float   LastOrderAt;
        }

        static readonly Dictionary<Unit, Assignment> _assign = new Dictionary<Unit, Assignment>();
        static float _lastTickAt;
        static float _lastDiagAt;
        static float _lastHintLogAt;

        internal static int MigratedThisRound;
        internal static int ReissuedThisRound;

        // Snapshot published for AlienShrimpProducer so production respects
        // the same capacity model instead of a flat per-BC constant.
        struct BcCap { public Vector3 Pos; public int Capacity; public int Current; public Vector3 Best; public bool HasProducer; public int Patches; }
        static BcCap[] _capSnapshot = new BcCap[0];

        /// <summary>
        /// Total shrimps the map's LIVE biotics can usefully feed right now —
        /// the sum of every group's capacity. -1 until the first tick.
        ///
        /// Maps differ enormously in how much there is to harvest. Measured
        /// from the Si_MapBalance dumps on 2026-07-19:
        ///
        ///     TheMaw            31 patches      682,000 biotics
        ///     Citadel           54           1,188,000
        ///     NarakaCity       107           2,354,000
        ///     IndustrialQuarter 172           3,784,000
        ///
        /// A flat team-wide shrimp ceiling cannot be right across a 5.5x
        /// spread. TheMaw is exactly what it looks like when it is wrong: we
        /// ran 202 shrimps over 31 patches and 75 of them ended up stacked on
        /// a single Bio Cache, because production kept going long after the
        /// map had run out of places to put them. Per-patch totals are a
        /// MapBalance setting too, so this reads live amounts rather than
        /// assuming any particular value.
        /// </summary>
        public static int TeamCapacity { get; private set; } = -1;

        // ---- Bridge to the eco planner --------------------------------------
        //
        // When a patch drains, its group's capacity collapses and every shrimp
        // on it becomes surplus at once. Our waterfill — like vanilla — then
        // sends them to the NEXT NEAREST patch, which is usually already worked
        // or already has shrimps walking to it. Observed at ~12:30 on
        // NarakaCity: northern shrimps all relocating onto one northern patch
        // while the patch EAST of it sat free.
        //
        // The better destination is often ground we have not expanded to yet,
        // which shrimp management cannot use on its own — there is no Bio Cache
        // to deposit at. So it ASKS: the best uncovered patch near the displaced
        // shrimps is published here and EcoPlanner biases expansion toward it.
        // The chain gets built while the shrimps are still walking.
        //
        // User rule 5.5a, and the bridge between shrimp management and Phase 2
        // expansion they asked for.
        public struct ExpansionHint
        {
            public Vector3 Pos;        // patch the displaced shrimps want
            public int     Shrimps;    // how many are looking for a home
            public float   AtTime;     // Time.time when published
        }
        static ExpansionHint _hint;
        const float HINT_TTL_S = 60f;

        public static bool TryGetExpansionHint(out ExpansionHint hint)
        {
            hint = _hint;
            return hint.Shrimps > 0 && Time.time - hint.AtTime < HINT_TTL_S;
        }

        /// <summary>
        /// Capacity of the group anchored at the BC nearest to <paramref name="pos"/>.
        /// Returns HARD_CAP when no snapshot exists yet (round start), so
        /// production is never blocked by a cold cache.
        /// </summary>
        public static int CapacityForNearestBc(Vector3 pos)
        {
            var snap = _capSnapshot;
            if (snap == null || snap.Length == 0) return HARD_CAP;
            int best = -1; float bd = float.MaxValue;
            for (int i = 0; i < snap.Length; i++)
            {
                float dx = snap[i].Pos.x - pos.x, dz = snap[i].Pos.z - pos.z;
                float d = dx * dx + dz * dz;
                if (d < bd) { bd = d; best = i; }
            }
            return best >= 0 ? snap[best].Capacity : HARD_CAP;
        }

        public static void ResetForNewRound()
        {
            _assign.Clear();
            _lastTickAt = 0f;
            _capSnapshot = new BcCap[0];
            TeamCapacity = -1;
            _hint = default;
            MigratedThisRound = 0;
            ReissuedThisRound = 0;
        }

        public static void MaybeRun(Team team)
        {
            if (team == null) return;
            string tn = team.name ?? "";
            if (!tn.Contains("Alien")) return;
            if (!global::Si_RTS_AI.TestHarnessNs.TestHarness.IsRoundActive) return;

            float now = Time.time;

            // Supervision runs EVERY tick; the full regroup stays on its
            // cadence. See Supervise for why the two are separated.
            try { Supervise(team, now); }
            catch (System.Exception ex) { MelonLogger.Warning("[SHRIMP-SUP] threw: " + ex.Message); }

            if (now - _lastTickAt < TICK_CADENCE_S) return;
            _lastTickAt = now;

            int movesBefore = MigratedThisRound;
            long ts = System.Diagnostics.Stopwatch.GetTimestamp();
            try { Run(team); }
            catch (System.Exception ex) { MelonLogger.Warning("[SHRIMP-GRP] threw: " + ex.Message); }
            long ms = (System.Diagnostics.Stopwatch.GetTimestamp() - ts) * 1000L / System.Diagnostics.Stopwatch.Frequency;
            Si_RTS_AI.RecentModWork.AddShrimpRelocator(ms, MigratedThisRound - movesBefore);
        }

        struct Patch { public Vector3 pos; public int remaining; }

        class Group
        {
            public Vector3 Anchor;
            public int     Patches;
            public long    Remaining;
            public Vector3 BestPatch;        // nearest patch to the anchor
            public float   BestPatchDist;
            public float   Value;            // income/sec per shrimp at crowd 1
            public bool    HasLocalCyst;     // can it produce its own shrimps?
            // Move targets = the group's own patches, each with a live shrimp
            // count. Arrivals go to the least-loaded patch so a group never
            // stacks everyone on one point (the TheMaw failure mode was 53
            // shrimps inside a single 40m cell).
            public readonly List<Vector3> Targets     = new List<Vector3>();
            public readonly List<int>     TargetLoad  = new List<int>();
            public Vector3 PickTarget()
            {
                if (Targets.Count == 0) return BestPatch;
                int best = 0;
                for (int i = 1; i < Targets.Count; i++)
                    if (TargetLoad[i] < TargetLoad[best]) best = i;
                TargetLoad[best]++;
                return Targets[best];
            }
            public int     Capacity;
            public bool    HasProducer;   // a Cyst of ours feeds this group
            public int     Desired;
            public int     Current;
            public readonly List<Unit> Members = new List<Unit>();
        }

        /// <summary>
        /// Per-tick supervision: catch shrimps that are producing NOTHING and
        /// fix them immediately, without waiting for the next regroup and
        /// without spending the migration budget.
        ///
        /// The distinction that matters is CORRECTION vs OPTIMISATION. The
        /// waterfill and its MOVES_PER_TICK trickle exist to stop working
        /// shrimps being mass-migrated for a marginal gain — that throttle is
        /// right for optimisation. A shrimp standing on a patch whose capacity
        /// has fallen to zero is not a marginal case: it earns nothing at all,
        /// so there is no cost to weigh and no reason to queue behind three
        /// other moves. Measured NarakaCity 2026-07-30: groups sat at
        /// "cur=12/des=0/cap=0" and "cur=13/des=0/cap=0" for minutes while the
        /// trickle handled four moves per tick against surpluses of 20-40.
        ///
        /// Capacity falls continuously as patches depleted (18 -> 13 -> 7 -> 2
        /// -> 0), so this case is not an edge — it is the normal end state of
        /// every patch on the map.
        /// </summary>
        /// <summary>
        /// Shrimps are stranded with nowhere holding spare capacity. The full
        /// pass already computes an ExpansionHint from the homeless count and
        /// the patch list; this only records that the condition is live between
        /// those passes, so the reason a shrimp is idle is never invisible.
        /// </summary>
        static void WantExpansionNear(Vector3 from)
        {
            _strandedNoRoom++;
            float now = Time.time;
            if (now - _lastStrandedLogAt < 20f) return;
            _lastStrandedLogAt = now;
            MelonLogger.Msg($"[SHRIMP-SUP] {_strandedNoRoom} shrimps stranded near " +
                            $"({from.x:F0},{from.z:F0}) — every live patch is full, need expansion");
            _strandedNoRoom = 0;
        }

        static int   _strandedNoRoom;
        static float _lastStrandedLogAt;

        static bool StructureNearPos(Team team, string name, Vector3 pos, float radiusM)
        {
            try
            {
                var structs = team?.Structures;
                if (structs == null) return false;
                float r2 = radiusM * radiusM;
                for (int i = 0; i < structs.Count; i++)
                {
                    var st = structs[i];
                    if (st == null || st.ObjectInfo == null || st.IsDestroyed) continue;
                    if ((st.ObjectInfo.DisplayName ?? "") != name) continue;
                    Vector3 q = st.transform.position;
                    float dx = q.x - pos.x, dz = q.z - pos.z;
                    if (dx * dx + dz * dz <= r2) return true;
                }
            }
            catch { }
            return false;
        }

        /// <summary>How much biotics this shrimp is carrying right now.</summary>
        static int CarriedBy(Unit u)
        {
            try
            {
                var holders = u.ResourceHolders;
                if (holders == null) return 0;
                int total = 0;
                for (int i = 0; i < holders.Count; i++)
                {
                    var h = holders[i];
                    if (h != null) total += h.AmountStored;
                }
                return total;
            }
            catch { return 0; }
        }

        static void Supervise(Team team, float now)
        {
            if (now - _lastSuperviseAt < SUPERVISE_CADENCE_S) return;
            _lastSuperviseAt = now;

            var snap = _capSnapshot;
            if (snap == null || snap.Length == 0) return;

            // Somewhere with room. Without a destination there is nothing to do.
            int stranded = 0, rescued = 0, unloading = 0;
            var units = team.Units;
            if (units == null) return;

            for (int i = 0; i < units.Count; i++)
            {
                var u = units[i];
                if (u == null || u.ObjectInfo == null || u.IsDestroyed) continue;
                if (u.ObjectInfo.DisplayName != "Shrimp") continue;

                Vector3 p = u.transform.position;

                // Is the group it is standing on dead?
                int here = -1; float hereSq = float.MaxValue;
                for (int gi = 0; gi < snap.Length; gi++)
                {
                    float dx = snap[gi].Pos.x - p.x, dz = snap[gi].Pos.z - p.z;
                    float d = dx * dx + dz * dz;
                    if (d < hereSq) { hereSq = d; here = gi; }
                }
                if (here < 0 || hereSq > GROUP_PATCH_RADIUS_M * GROUP_PATCH_RADIUS_M) continue;
                if (snap[here].Capacity > 0) continue;      // still worth harvesting

                // NO PATCHES IS NOT A DEPLETED PATCH.
                //
                // The Nest's group has Capacity 0 because it serves no biotics
                // at all, not because its biotics ran out. Every shrimp spawns
                // there, so treating it as stranded evacuated each new shrimp
                // the moment it appeared — NarakaCity 2026-07-31 logged
                // "1 stranded, 1 relocated" every production cycle — and since
                // this pass assigns on a 15s cooldown while the regroup runs
                // every 2s, the two disagreed and shrimps bounced.
                //
                // A freshly produced shrimp is not an emergency. Leave it to the
                // normal allocation, which places free agents without spending
                // the migration budget anyway.
                if (snap[here].Patches == 0) continue;

                stranded++;

                // Don't re-order the same shrimp every tick while it walks.
                if (_assign.TryGetValue(u, out var a)
                    && now - a.LastOrderAt < REISSUE_COOLDOWN_S) continue;

                // CARRYING? DEPOSIT FIRST.
                //
                // A shrimp that walks off a dead patch with a full load throws
                // that load away. The group anchor IS its Bio Cache, so send it
                // there; next pass it will be empty and get its real
                // destination.
                if (CarriedBy(u) > 0)
                {
                    IssueMove(u, snap[here].Pos);
                    _assign[u] = new Assignment { Target = snap[here].Pos,
                                                  AssignedAt = now, LastOrderAt = now };
                    unloading++;
                    continue;
                }

                // DESTINATION: prefer somewhere that is NOT already filling
                // itself. A group with its own Cyst produces shrimps locally and
                // will reach its own capacity without help, so sending walkers
                // there wastes the walk and crowds a patch that was already
                // spoken for. Untapped ground is worth more, even a bit further.
                // A PREFERENCE, NOT A PRECEDENCE.
                //
                // "No local producer" used to win OUTRIGHT, so a producer-less
                // group 1500m away beat a good one 200m away and shrimps walked
                // across the map. NarakaCity 2026-08-02: shrimps at
                // (2189,738) sent far south when (1658,893) was the better
                // home, and others sent to (1361,1549) instead of biotics just
                // north of them.
                //
                // The reasoning behind the preference still holds — a group with
                // its own Cyst fills itself, so walkers there are wasted — but
                // it is worth SOME extra walk, not any amount of it. Scored as
                // an effective distance so the two trade off, which is the same
                // correction the spread term and the staffing gate needed.
                int dst = -1; float dstScore = float.MaxValue;
                for (int gi = 0; gi < snap.Length; gi++)
                {
                    if (snap[gi].Capacity - snap[gi].Current <= 0) continue;
                    float dx = snap[gi].Best.x - p.x, dz = snap[gi].Best.z - p.z;
                    float d = Mathf.Sqrt(dx * dx + dz * dz);
                    float score = d * (snap[gi].HasProducer ? 1f : NO_PRODUCER_DISCOUNT);
                    if (score < dstScore) { dstScore = score; dst = gi; }
                }
                float dstSq = dstScore * dstScore;
                // UNTAKEN GROUND COMPETES WITH DEVELOPED GROUND.
                //
                // A patch with no Bio Cache has no group, so it was invisible
                // here and a displaced shrimp could only walk to somewhere
                // already built — past closer, untouched biotics. Migration is
                // the signal that expansion is due: if untaken ground is nearer
                // than the best existing home, ask for a Bio Cache there and
                // walk the shrimp toward it. It arrives about when the Bio Cache
                // does instead of crowding a patch that was already spoken for.
                var freeP = _freePatches;
                int freeIdx = -1; float freshSq = float.MaxValue;
                for (int fi = 0; fi < freeP.Length; fi++)
                {
                    float dx = freeP[fi].x - p.x, dz = freeP[fi].z - p.z;
                    float d = dx * dx + dz * dz;
                    if (d < freshSq) { freshSq = d; freeIdx = fi; }
                }
                // Untapped ground is scored WORSE than a standing group at the
                // same distance, not better: there is no Bio Cache there yet, so
                // a shrimp arriving early has nowhere to deposit until the
                // expansion lands. It still wins when it is much closer, or when
                // nothing else has room.
                float freeScore = Mathf.Sqrt(freshSq) * UNTAPPED_PENALTY;
                if (freeIdx >= 0 && (dst < 0 || freeScore < dstScore))
                {
                    _hint = new ExpansionHint { Pos = freeP[freeIdx], Shrimps = stranded, AtTime = now };
                    IssueMove(u, freeP[freeIdx]);
                    _assign[u] = new Assignment { Target = freeP[freeIdx],
                                                  AssignedAt = now, LastOrderAt = now };
                    rescued++;
                    if (now - _lastHintLogAt > 20f)
                    {
                        _lastHintLogAt = now;
                        MelonLogger.Msg($"[SHRIMP-SUP] displaced shrimps heading to UNTAPPED " +
                                        $"({freeP[freeIdx].x:F0},{freeP[freeIdx].z:F0}) " +
                                        $"{Mathf.Sqrt(freshSq):F0}m away — asking for expansion there");
                    }
                    continue;
                }

                if (dst < 0)
                {
                    // Nowhere has room and no untaken ground either. That is an
                    // expansion problem, not a shrimp problem — say so.
                    WantExpansionNear(p);
                    continue;
                }

                IssueMove(u, snap[dst].Best);
                _assign[u] = new Assignment { Target = snap[dst].Best, AssignedAt = now, LastOrderAt = now };
                rescued++;
            }

            if (rescued > 0 || unloading > 0)
                MelonLogger.Msg($"[SHRIMP-SUP] depleted-patch shrimps: {stranded} stranded, " +
                                $"{unloading} depositing first, {rescued} relocated");
        }

        const float SUPERVISE_CADENCE_S = 1f;
        static float _lastSuperviseAt;

        static void Run(Team team)
        {
            float nowT = Time.time;
            int placedFirstTime = 0;
            // ---- Patches ----
            var patches = new List<Patch>();
            try
            {
                var all = ResourceArea.AllResourceAreas;
                if (all == null) return;
                var uType = team.UsableResource;
                for (int i = 0; i < all.Count; i++)
                {
                    var ra = all[i];
                    if (ra == null || ra.IsEmpty || ra.ResourceType != uType) continue;
                    int rem = ra.ResourceAmountCurrent;
                    if (rem <= PATCH_EMPTY_HARD) continue;
                    patches.Add(new Patch { pos = ra.SignalCenter, remaining = rem });
                }
            }
            catch { return; }
            if (patches.Count == 0) return;

            // ---- Anchors (Bio Cache + Nest) ----
            var groups = new List<Group>();
            try
            {
                var structs = team.Structures;
                if (structs == null) return;
                for (int i = 0; i < structs.Count; i++)
                {
                    var st = structs[i];
                    if (st == null || st.ObjectInfo == null || st.IsDestroyed) continue;
                    var n = st.ObjectInfo.DisplayName;
                    if (n == "Bio Cache" || n == "Nest")
                        groups.Add(new Group { Anchor = st.transform.position, BestPatchDist = float.MaxValue });
                }
            }
            catch { return; }
            if (groups.Count == 0) return;

            // Which groups can produce their own shrimps? A group with a Cyst
            // fills itself; one without has to be walked into, and early on
            // that walk is pure lost harvest time — user 2026-07-29:
            // "shrimps are relocated to the southern biotics early, this is
            // inefficient, relocation eats up precious early harvesting time".
            try
            {
                var structs2 = team.Structures;
                if (structs2 != null)
                {
                    const float LOCAL_CYST_M2 = 150f * 150f;
                    for (int i = 0; i < structs2.Count; i++)
                    {
                        var st = structs2[i];
                        if (st == null || st.ObjectInfo == null || st.IsDestroyed) continue;
                        var n2 = st.ObjectInfo.DisplayName;
                        if (n2 != "Lesser Spawning Cyst" && n2 != "Nest") continue;
                        var cp = st.transform.position;
                        for (int gi = 0; gi < groups.Count; gi++)
                        {
                            if (groups[gi].HasLocalCyst) continue;
                            float dx = groups[gi].Anchor.x - cp.x, dz = groups[gi].Anchor.z - cp.z;
                            if (dx * dx + dz * dz <= LOCAL_CYST_M2) groups[gi].HasLocalCyst = true;
                        }
                    }
                }
            }
            catch { }

            // ---- Assign patches to nearest anchor within GROUP_PATCH_RADIUS ----
            float patchRadSq = GROUP_PATCH_RADIUS_M * GROUP_PATCH_RADIUS_M;
            for (int pi = 0; pi < patches.Count; pi++)
            {
                int best = -1; float bd = patchRadSq;
                for (int gi = 0; gi < groups.Count; gi++)
                {
                    float dx = groups[gi].Anchor.x - patches[pi].pos.x;
                    float dz = groups[gi].Anchor.z - patches[pi].pos.z;
                    float d = dx * dx + dz * dz;
                    if (d < bd) { bd = d; best = gi; }
                }
                if (best < 0) continue;   // patch no BC can service yet
                var g = groups[best];
                g.Patches++;
                g.Remaining += patches[pi].remaining;
                g.Targets.Add(patches[pi].pos);
                g.TargetLoad.Add(0);
                float dist = Mathf.Sqrt(bd);
                if (dist < g.BestPatchDist) { g.BestPatchDist = dist; g.BestPatch = patches[pi].pos; }
            }

            // ---- Capacity + value ----
            int totalCapacity = 0;
            for (int gi = 0; gi < groups.Count; gi++)
            {
                var g = groups[gi];
                if (g.Patches == 0) { g.Capacity = 0; g.Value = 0f; continue; }

                int capByPatches   = g.Patches * PER_PATCH_CAPACITY;
                int capByRemaining = (int)(g.Remaining / (PER_SHRIMP_HARVEST_PER_SEC * MIN_LIFE_S));
                g.Capacity = Mathf.Clamp(Mathf.Min(capByPatches, capByRemaining), 0, HARD_CAP);

                float cycle = 2f * g.BestPatchDist / SHRIMP_SPEED
                            + (float)CARRY / HARVEST_RATE
                            + (float)CARRY / DEPOSIT_RATE;
                g.Value = CARRY / cycle;
                // Does this group make its own shrimps? If so it will fill
                // itself and is a poor destination for someone else's.
                g.HasProducer = StructureNearPos(team, "Lesser Spawning Cyst",
                                                 g.Anchor, GROUP_PATCH_RADIUS_M);
                totalCapacity += g.Capacity;
            }

            // ---- Current membership ----
            var shrimps = new List<Unit>();
            var ownerOf = new Dictionary<Unit, int>();
            try
            {
                var units = team.Units;
                if (units == null) return;
                float claimSq = CLAIM_RADIUS_M * CLAIM_RADIUS_M;
                for (int i = 0; i < units.Count; i++)
                {
                    var u = units[i];
                    if (u == null || u.ObjectInfo == null || u.IsDestroyed) continue;
                    if (u.ObjectInfo.DisplayName != "Shrimp") continue;
                    shrimps.Add(u);
                    var p = u.transform.position;
                    int best = -1; float bd = claimSq;
                    for (int gi = 0; gi < groups.Count; gi++)
                    {
                        float dx = groups[gi].Anchor.x - p.x, dz = groups[gi].Anchor.z - p.z;
                        float d = dx * dx + dz * dz;
                        if (d < bd) { bd = d; best = gi; }
                    }
                    ownerOf[u] = best;
                    if (best < 0) continue;
                    var og = groups[best];
                    og.Current++;
                    og.Members.Add(u);
                    // Charge the shrimp to its nearest patch inside the group so
                    // PickTarget can steer new arrivals to the emptiest one.
                    int nt = -1; float ntd = float.MaxValue;
                    for (int ti = 0; ti < og.Targets.Count; ti++)
                    {
                        float d = SqDist(og.Targets[ti], p);
                        if (d < ntd) { ntd = d; nt = ti; }
                    }
                    if (nt >= 0) og.TargetLoad[nt]++;

                    // PLACE EVERY SHRIMP ONCE, NOT JUST THE ONES WE MOVE.
                    //
                    // Only relocated shrimps ever got an _assign entry, so a
                    // shrimp already standing in the right group was never
                    // ordered, never drift-checked, and left entirely to vanilla
                    // micro-AI — which is what wanders off the patch. Our own
                    // numbers were clean while the wandering continued
                    // (NarakaCity 2026-07-31: migrated=1, reissued=0 at 31
                    // shrimps) precisely because we were not touching them.
                    //
                    // One order on first sight pins it to its group's target;
                    // after that the existing drift check (REISSUE_DRIFT_M, on a
                    // REISSUE_COOLDOWN_S timer) keeps it there without thrash.
                    if (og.Capacity > 0 && !_assign.ContainsKey(u) && nt >= 0)
                    {
                        Vector3 tgt = og.Targets[nt];
                        IssueMove(u, tgt);
                        _assign[u] = new Assignment { Target = tgt, AssignedAt = nowT, LastOrderAt = nowT };
                        placedFirstTime++;
                    }
                }
            }
            catch { return; }
            if (shrimps.Count == 0) return;
            if (placedFirstTime > 0)
                MelonLogger.Msg($"[SHRIMP-GRP] took control of {placedFirstTime} unassigned shrimps");

            // ---- Waterfill: spread to SOFT_TARGET first, then top up ----
            var order = new List<int>(groups.Count);
            for (int gi = 0; gi < groups.Count; gi++) order.Add(gi);
            order.Sort((a, b) => groups[b].Value.CompareTo(groups[a].Value));

            int pool = shrimps.Count;

            // ---- INCUMBENCY FIRST ----
            //
            // A shrimp already harvesting a live patch stays there. Only the
            // genuinely free ones — newly produced, or sitting on a patch that
            // is depleted or over capacity — get allocated by value below.
            //
            // Without this the waterfill is a pure greedy fill by value: the
            // highest-value group takes SOFT_TARGET (8) before any other group
            // gets a single shrimp, so with fewer than 8 shrimps the second
            // group is allotted NONE. A freshly built Bio Cache on a full patch
            // always has the highest value, which is why every southern
            // expansion triggered a wholesale migration. Measured NarakaCity
            // 2026-07-30 at t=3min: "[2410,1430 cur=5/des=0] [2315,705
            // cur=0/des=6]" — five shrimps working a healthy northern patch
            // were all reassigned south the instant its Bio Cache appeared, and
            // reissued climbed 4 -> 22 -> 38 -> 41 as they thrashed.
            //
            // Relocation for real reasons still works: a depleted patch sets
            // Capacity 0, so its shrimps fall into the pool and are reassigned.
            for (int gi = 0; gi < groups.Count; gi++)
            {
                var g = groups[gi];
                // RULE: a shrimp moves off a patch only when that patch is
                // EMPTY — not because capacity shrank.
                //
                // Capacity falls continuously as a patch depletes (18 -> 13 ->
                // 7 -> 2 -> 0), so keeping only min(Current, Capacity) meant a
                // steady trickle of relocation all round for no real gain: the
                // patch was still worth harvesting, the shrimps were already
                // standing on it, and the walk was pure loss. User 2026-07-30:
                // relocate when the biotics is empty, not before.
                //
                // HARD_CAP still applies, so a genuine pile-up (40 on one
                // group was observed) still sheds down to a workable number.
                int keep = g.Capacity > 0 ? Mathf.Min(g.Current, HARD_CAP) : 0;
                g.Desired += keep; pool -= keep;
            }
            if (pool < 0) pool = 0;

            for (int oi = 0; oi < order.Count && pool > 0; oi++)
            {
                var g = groups[order[oi]];
                int give = Mathf.Min(Mathf.Min(SOFT_TARGET, g.Capacity) - g.Desired, pool);
                if (give <= 0) continue;
                g.Desired += give; pool -= give;
            }
            for (int oi = 0; oi < order.Count && pool > 0; oi++)
            {
                var g = groups[order[oi]];
                int give = Mathf.Min(g.Capacity - g.Desired, pool);
                if (give <= 0) continue;
                g.Desired += give; pool -= give;
            }
            // Leftovers (more shrimps than the map's live capacity) spill onto
            // the viable groups one lap at a time rather than jamming one.
            var viable = new List<int>();
            for (int oi = 0; oi < order.Count; oi++)
                if (groups[order[oi]].Capacity > 0) viable.Add(order[oi]);
            for (int lap = 0; pool > 0 && viable.Count > 0; lap++)
                for (int vi = 0; vi < viable.Count && pool > 0; vi++) { groups[viable[vi]].Desired++; pool--; }

            TeamShrimps = shrimps.Count;
            PublishCapacities(groups);
            PublishFreePatches(patches, groups);

            // ---- Sticky re-issue: pull drifters back to their assignment ----
            float now = Time.time;
            PruneAssignments(now);
            for (int si = 0; si < shrimps.Count; si++)
            {
                var u = shrimps[si];
                if (!_assign.TryGetValue(u, out var a)) continue;
                if (now - a.AssignedAt > ASSIGN_TTL_S) { _assign.Remove(u); continue; }
                if (now - a.LastOrderAt < REISSUE_COOLDOWN_S) continue;
                var p = u.transform.position;
                float dx = p.x - a.Target.x, dz = p.z - a.Target.z;
                if (dx * dx + dz * dz < REISSUE_DRIFT_M * REISSUE_DRIFT_M) continue;
                IssueMove(u, a.Target);
                a.LastOrderAt = now;
                _assign[u] = a;
                ReissuedThisRound++;
            }

            // ---- Fill deficits from surpluses, MOVES_PER_TICK at a time ----
            var needs = new List<int>();          // group indices, one entry per missing shrimp
            for (int oi = 0; oi < order.Count; oi++)
            {
                var gi = order[oi];
                int deficit = groups[gi].Desired - groups[gi].Current;
                for (int k = 0; k < deficit && needs.Count < NEEDS_CAP; k++) needs.Add(gi);
            }

            // Donor pool: free agents first (in transit, cheap to redirect),
            // then the excess of over-subscribed groups.
            // (unit, groupIdx) — the source group is needed to price the move.
            var donors = new List<KeyValuePair<Unit, int>>();
            for (int si = 0; si < shrimps.Count; si++)
                if (ownerOf[shrimps[si]] < 0 && !_assign.ContainsKey(shrimps[si]))
                    donors.Add(new KeyValuePair<Unit, int>(shrimps[si], -1));
            for (int gi = 0; gi < groups.Count; gi++)
            {
                var g = groups[gi];
                int surplus = g.Current - g.Desired;
                if (surplus <= 0) continue;
                // Prefer the members physically farthest from the group's own
                // best patch — they are the ones on the outside of the jam.
                g.Members.Sort((x, y) =>
                    SqDist(y.transform.position, g.BestPatch).CompareTo(SqDist(x.transform.position, g.BestPatch)));
                for (int k = 0; k < surplus && k < g.Members.Count; k++)
                    donors.Add(new KeyValuePair<Unit, int>(g.Members[k], gi));
            }

            // TWO BUDGETS, NOT ONE.
            //
            // The trickle limit exists so working shrimps are not mass-migrated.
            // It has no business throttling shrimps that are not working yet: a
            // newly produced one spawns at its Cyst with no assignment, and
            // redirecting it costs nothing because it has not started a harvest
            // cycle. Charging those against the same 4-per-tick budget meant
            // production outran redistribution all round — measured NarakaCity
            // 2026-07-30, moved=1..4 per tick against surpluses of 20-40, with
            // one group holding 40 shrimps on capacity 17 and another 12 on a
            // depleted patch (cap=0).
            int moved = 0, movedWorking = 0;
            for (int ni = 0; ni < needs.Count && donors.Count > 0; ni++)
            {
                if (movedWorking >= MOVES_PER_TICK && !AnyFreeDonor(donors)) break;
                var dst = groups[needs[ni]];
                int bestD = -1; float bestSq = float.MaxValue;
                for (int di = 0; di < donors.Count; di++)
                {
                    var u = donors[di].Key;
                    int srcGi = donors[di].Value;
                    if (_assign.TryGetValue(u, out var a0) && now - a0.AssignedAt < REASSIGN_COOLDOWN_S) continue;
                    // Once the trickle budget is spent, only shrimps that are
                    // not working yet may still be placed.
                    if (movedWorking >= MOVES_PER_TICK && srcGi >= 0) continue;
                    float d = SqDist(u.transform.position, dst.BestPatch);
                    if (!MoveIsWorthIt(srcGi >= 0 ? groups[srcGi] : null, dst, Mathf.Sqrt(d))) continue;
                    if (d < bestSq) { bestSq = d; bestD = di; }
                }
                // One unservable need must not end the pass. This was a break,
                // so a single need with no acceptable donor cancelled every
                // remaining move in the tick.
                if (bestD < 0) continue;

                var donor = donors[bestD].Key;
                if (donors[bestD].Value >= 0) movedWorking++;
                donors.RemoveAt(bestD);
                var target = dst.PickTarget();
                IssueMove(donor, target);
                _assign[donor] = new Assignment { Target = target, AssignedAt = now, LastOrderAt = now };
                dst.Current++;
                moved++;
                MigratedThisRound++;
            }

            // ---- Publish an expansion hint if shrimps have nowhere good to go ----
            //
            // "Nowhere good" = more surplus shrimps than the deficits of every
            // group put together. That is the signal that our WORKED ground is
            // full and the answer is more ground, not more shuffling.
            int surplusTotal = 0, deficitTotal = 0;
            Vector3 from = Vector3.zero; bool haveFrom = false;
            for (int gi = 0; gi < groups.Count; gi++)
            {
                int d = groups[gi].Current - groups[gi].Desired;
                if (d > 0)
                {
                    surplusTotal += d;
                    if (!haveFrom) { from = groups[gi].Anchor; haveFrom = true; }
                }
                else deficitTotal += -d;
            }
            int homeless = surplusTotal - deficitTotal;
            if (homeless > 0 && haveFrom)
            {
                int best = -1; float bestScore = 0f;
                float groupRadSq = GROUP_PATCH_RADIUS_M * GROUP_PATCH_RADIUS_M;
                for (int pi = 0; pi < patches.Count; pi++)
                {
                    bool covered = false;
                    for (int gi = 0; gi < groups.Count && !covered; gi++)
                    {
                        float gx = groups[gi].Anchor.x - patches[pi].pos.x;
                        float gz = groups[gi].Anchor.z - patches[pi].pos.z;
                        if (gx * gx + gz * gz <= groupRadSq) covered = true;
                    }
                    if (covered) continue;           // a Bio Cache already serves it
                    float dx = patches[pi].pos.x - from.x, dz = patches[pi].pos.z - from.z;
                    float dist = Mathf.Sqrt(dx * dx + dz * dz);
                    if (dist < 1f) continue;
                    // Close AND rich; distance dominates since they must walk.
                    float score = patches[pi].remaining / dist;
                    if (score > bestScore) { bestScore = score; best = pi; }
                }
                if (best >= 0)
                {
                    _hint = new ExpansionHint { Pos = patches[best].pos, Shrimps = homeless, AtTime = now };
                    if (now - _lastHintLogAt > 20f)
                    {
                        _lastHintLogAt = now;
                        MelonLogger.Msg($"[SHRIMP-GRP] {homeless} shrimps have no home — " +
                                        $"want expansion at ({patches[best].pos.x:F0},{patches[best].pos.z:F0})");
                    }
                }
            }

            if (now - _lastDiagAt > 30f)
            {
                _lastDiagAt = now;
                LogDiag(groups, order, shrimps.Count, totalCapacity, moved);
            }
        }

        /// <summary>Is any donor a shrimp that is not working a group yet?</summary>
        static bool AnyFreeDonor(List<KeyValuePair<Unit, int>> donors)
        {
            for (int i = 0; i < donors.Count; i++)
                if (donors[i].Value < 0) return true;
            return false;
        }

        /// <summary>
        /// Live patches that no Bio Cache serves yet. Without this the
        /// supervisor can only relocate to EXISTING groups, so a displaced
        /// shrimp walks past untaken ground to crowd a developed site — the
        /// north-to-south runs seen on NarakaCity 2026-07-30 while northern and
        /// western patches sat untouched.
        /// </summary>
        static Vector3[] _freePatches = new Vector3[0];

        static void PublishFreePatches(List<Patch> patches, List<Group> groups)
        {
            var free = new List<Vector3>();
            float radSq = GROUP_PATCH_RADIUS_M * GROUP_PATCH_RADIUS_M;
            for (int pi = 0; pi < patches.Count; pi++)
            {
                if (patches[pi].remaining <= PATCH_EMPTY_HARD) continue;
                bool covered = false;
                for (int gi = 0; gi < groups.Count && !covered; gi++)
                {
                    float dx = groups[gi].Anchor.x - patches[pi].pos.x;
                    float dz = groups[gi].Anchor.z - patches[pi].pos.z;
                    if (dx * dx + dz * dz <= radSq) covered = true;
                }
                if (!covered) free.Add(patches[pi].pos);
            }
            _freePatches = free.ToArray();
        }

        static void PublishCapacities(List<Group> groups)
        {
            var snap = new BcCap[groups.Count];
            int total = 0;
            for (int i = 0; i < groups.Count; i++)
            {
                snap[i] = new BcCap { Pos = groups[i].Anchor, Capacity = groups[i].Capacity,
                                      Current = groups[i].Current, Best = groups[i].BestPatch,
                                      HasProducer = groups[i].HasProducer,
                                      Patches = groups[i].Patches };
                total += groups[i].Capacity;
            }
            _capSnapshot = snap;
            TeamCapacity = total;
        }

        /// <summary>Shrimps alive at the last group pass — the other half of the
        /// capacity picture.</summary>
        internal static int TeamShrimps { get; private set; }

        /// <summary>
        /// What one group on THIS map typically feeds — TeamCapacity averaged
        /// over the groups that still have any. Map-dependent by construction:
        /// it falls out of patch storage, patch distribution and how many
        /// patches a Bio Cache reaches, all of which vary per map and are
        /// editable via Si_MapBalance. Zero when nothing has capacity.
        /// </summary>
        internal static int TypicalGroupCapacity
        {
            get
            {
                var snap = _capSnapshot;
                if (snap == null || snap.Length == 0) return 0;
                int total = 0, n = 0;
                for (int i = 0; i < snap.Length; i++)
                {
                    if (snap[i].Capacity <= 0) continue;
                    total += snap[i].Capacity; n++;
                }
                return n > 0 ? total / n : 0;
            }
        }

        /// <summary>Live patches no Bio Cache serves yet — is there anywhere
        /// left to expand to at all?</summary>
        internal static int FreePatchCount => _freePatches != null ? _freePatches.Length : 0;

        static void LogDiag(List<Group> groups, List<int> order, int shrimps, int totalCap, int moved)
        {
            int used = 0, over = 0, empty = 0;
            for (int gi = 0; gi < groups.Count; gi++)
            {
                if (groups[gi].Current > 0) used++; else empty++;
                if (groups[gi].Current > groups[gi].Desired + 4) over++;
            }
            // Worst three offenders, so the log shows the actual pile if one exists.
            var byCur = new List<int>(order);
            byCur.Sort((a, b) => groups[b].Current.CompareTo(groups[a].Current));
            var sb = new System.Text.StringBuilder();
            for (int k = 0; k < 3 && k < byCur.Count; k++)
            {
                var g = groups[byCur[k]];
                sb.Append(" [").Append(g.Anchor.x.ToString("F0")).Append(',').Append(g.Anchor.z.ToString("F0"))
                  .Append(" cur=").Append(g.Current).Append("/des=").Append(g.Desired)
                  .Append("/cap=").Append(g.Capacity).Append(']');
            }
            MelonLogger.Msg($"[SHRIMP-GRP] shrimps={shrimps} groups={groups.Count} used={used} empty={empty} " +
                            $"overfilled={over} totalCap={totalCap} moved={moved} " +
                            $"migrated={MigratedThisRound} reissued={ReissuedThisRound} top:{sb}");
        }

        static void PruneAssignments(float now)
        {
            if (_assign.Count == 0) return;
            var dead = new List<Unit>();
            foreach (var kv in _assign)
            {
                var u = kv.Key;
                if (u == null || u.IsDestroyed || now - kv.Value.AssignedAt > ASSIGN_TTL_S)
                    dead.Add(kv.Key);
            }
            for (int i = 0; i < dead.Count; i++) _assign.Remove(dead[i]);
        }

        /// <summary>
        /// Does this relocation pay for itself?
        ///
        /// The first version of this planner had NO walk cost at all — the
        /// comment read "NO WALK VETO", written to fix TheMaw where a 75-shrimp
        /// pile was unreachable because the old relocator refused any move over
        /// 270m. That fixed the pile and created the opposite fault: a shrimp
        /// would cross the map to join a group whose cycle time was
        /// fractionally better. Observed on the NarakaCity opening — shrimps
        /// walking from the SOUTHERN biotics to the NORTHERN ones, leaving the
        /// nearest Bio Cache for the furthest, with no gain to show for it.
        ///
        /// The right test is neither a distance window nor nothing: it is
        /// PAYBACK. Moving costs the source's marginal output for the whole
        /// walk, and earns the difference in marginal output thereafter:
        ///
        ///     payback_s = walk_s * marginal_src / (marginal_dst - marginal_src)
        ///
        /// A shrimp with no group (in transit, freshly spawned) has zero
        /// marginal output to lose, so it moves freely — which is what we want,
        /// since production should go where it is needed. A crowded source has
        /// low marginal output, so draining a pile stays cheap and TheMaw's
        /// fix survives. What no longer passes is a long walk for a small edge.
        /// </summary>
        static bool MoveIsWorthIt(Group src, Group dst, float distM)
        {
            float mDst = MarginalIps(dst, dst.Current + 1);
            if (mDst <= 0f) return false;
            if (src == null)
            {
                // A FRESH SHRIMP STILL PAYS FOR THE WALK.
                //
                // This returned true unconditionally — "nothing to lose" —
                // which let a shrimp born at one Cyst be sent clear across the
                // base to any group with a deficit, because with no source
                // group there was nothing to weigh the distance against. That
                // is the wandering seen early on NarakaCity.
                //
                // The walk is the loss: 600m at 9m/s is 67 seconds not
                // harvesting, which is precisely the first-deposit latency the
                // simulator models. Somewhere nearer with room is worth more
                // than a marginally better patch far away.
                return distM / SHRIMP_SPEED <= FREE_AGENT_MAX_WALK_S;
            }

            float mSrc = MarginalIps(src, src.Current);
            if (mSrc <= 0f) return true;               // source earns nothing at the margin
            if (mDst <= mSrc) return false;            // strictly no gain — do not walk

            float walkS = distM / SHRIMP_SPEED;

            // A destination that can produce its own shrimps will fill itself
            // shortly, so walking one across the map to get there early buys
            // almost nothing and costs the whole trip in lost harvesting. Only
            // short hops into a self-sufficient group.
            if (dst.HasLocalCyst && walkS > SELF_SUFFICIENT_MAX_WALK_S) return false;

            float payback = walkS * mSrc / (mDst - mSrc);
            return payback <= MOVE_PAYBACK_LIMIT_S;
        }

        /// <summary>Income/sec added by the Nth shrimp in this group.</summary>
        static float MarginalIps(Group g, int n)
        {
            if (g == null || g.Patches == 0 || n <= 0) return 0f;
            return GroupIps(g, n) - GroupIps(g, n - 1);
        }

        static float GroupIps(Group g, int n)
        {
            if (n <= 0) return 0f;
            float cycle = 2f * g.BestPatchDist / SHRIMP_SPEED
                        + (float)CARRY / HARVEST_RATE
                        + (float)CARRY / DEPOSIT_RATE;
            return n * CARRY / cycle * CrowdFactor(n);
        }

        static float CrowdFactor(int n)
        {
            if (n <= 6)  return 1.0f;
            if (n <= 12) return 0.85f;
            if (n <= 18) return 0.70f;
            return 0.55f;
        }

        static float SqDist(Vector3 a, Vector3 b)
        {
            float dx = a.x - b.x, dz = a.z - b.z;
            return dx * dx + dz * dz;
        }

        static void IssueMove(Unit u, Vector3 pos)
        {
            try
            {
                Faction.AlienShrimpAntiAttack.PlannerOverride = true;
                u.OnMoveOrder(pos, AgentMoveSpeed.Fast);
            }
            catch (System.Exception ex) { MelonLogger.Warning("[SHRIMP-GRP] OnMoveOrder threw: " + ex.Message); }
            finally { Faction.AlienShrimpAntiAttack.PlannerOverride = false; }
        }

        internal static string BuildRoundSummaryFragment()
        {
            if (MigratedThisRound == 0 && ReissuedThisRound == 0) return "";
            return "--- Shrimp group planner ---\n" +
                   $"  Rebalance moves this round: {MigratedThisRound}\n" +
                   $"  Sticky re-issues:           {ReissuedThisRound}\n";
        }
    }
}
