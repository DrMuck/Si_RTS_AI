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

        // A relocation must repay its walk within this. Generous enough that
        // genuinely better ground is still worth crossing to, tight enough that
        // a marginal edge across the map is not.
        const float MOVE_PAYBACK_LIMIT_S = 120f;
        // Max walk into a group that has its own Cyst: it will fill itself.
        const float SELF_SUFFICIENT_MAX_WALK_S = 25f;

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
        struct BcCap { public Vector3 Pos; public int Capacity; }
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
            public int     Desired;
            public int     Current;
            public readonly List<Unit> Members = new List<Unit>();
        }

        static void Run(Team team)
        {
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
                }
            }
            catch { return; }
            if (shrimps.Count == 0) return;

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
                int keep = Mathf.Min(g.Current, g.Capacity);
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
                for (int k = 0; k < deficit && needs.Count < MOVES_PER_TICK * 4; k++) needs.Add(gi);
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

            int moved = 0;
            for (int ni = 0; ni < needs.Count && moved < MOVES_PER_TICK && donors.Count > 0; ni++)
            {
                var dst = groups[needs[ni]];
                int bestD = -1; float bestSq = float.MaxValue;
                for (int di = 0; di < donors.Count; di++)
                {
                    var u = donors[di].Key;
                    int srcGi = donors[di].Value;
                    if (_assign.TryGetValue(u, out var a0) && now - a0.AssignedAt < REASSIGN_COOLDOWN_S) continue;
                    float d = SqDist(u.transform.position, dst.BestPatch);
                    if (!MoveIsWorthIt(srcGi >= 0 ? groups[srcGi] : null, dst, Mathf.Sqrt(d))) continue;
                    if (d < bestSq) { bestSq = d; bestD = di; }
                }
                if (bestD < 0) break;

                var donor = donors[bestD].Key;
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

        static void PublishCapacities(List<Group> groups)
        {
            var snap = new BcCap[groups.Count];
            int total = 0;
            for (int i = 0; i < groups.Count; i++)
            {
                snap[i] = new BcCap { Pos = groups[i].Anchor, Capacity = groups[i].Capacity };
                total += groups[i].Capacity;
            }
            _capSnapshot = snap;
            TeamCapacity = total;
        }

        /// <summary>Shrimps alive at the last group pass — the other half of the
        /// capacity picture.</summary>
        internal static int TeamShrimps { get; private set; }

        /// <summary>
        /// Is another harvesting site worth building yet?
        ///
        /// A Bio Cache only earns through shrimps standing on it. While the
        /// patches we already hold have unfilled slots, a further site adds
        /// capacity nobody can staff and takes 500 that shrimps needed.
        /// NarakaCity 2026-07-30: a fifth Bio Cache went up at 16:25:53 with
        /// shrimps=40 against totalCap=56 — sixteen slots already empty. User:
        /// "a fifth bio cache placed despite it isnt used early on. That draws
        /// money important to build shrimps."
        ///
        /// Deliberately a FILL FRACTION rather than "completely full": waiting
        /// for the last slot would stall expansion permanently, since the final
        /// slots on a patch are the least worth filling.
        /// </summary>
        const float EXPANSION_FILL_FRACTION = 0.85f;

        internal static bool ExpansionWarranted(out int shrimps, out int capacity)
        {
            shrimps = TeamShrimps;
            capacity = TeamCapacity;
            if (capacity <= 0) return true;              // nothing held yet
            return shrimps >= capacity * EXPANSION_FILL_FRACTION;
        }

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
            if (src == null) return true;              // free agent: nothing to lose

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
