using MelonLoader;
using Sandbox.UnitGenerator;
using Silica;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Si_RTS_AI.Faction
{
    /// <summary>
    /// Direct Shrimp queuing at every Lesser Spawning Cyst on managed Alien teams.
    ///
    /// Why not rely on stock's AIUnitHandler + Phase 3.1 override:
    ///   We proved via 76 [P31] samples that AIUnitHandler DOES decide "Harvest preset
    ///   needs a Shrimp" repeatedly, and our postfix returns Shrimp every time. But
    ///   only 9 shrimps actually got built across a full round. Stock's picking-a-unit
    ///   step happens far more often than its actually-queueing-that-unit-in-a-Cyst
    ///   step, and the ratio isn't in our favour.
    ///
    /// So we go direct: every AI tick, for every Cyst we own that can build Shrimp,
    /// call Cyst.Construct(shrimpCd). Cyst's queue accepts what it can; the rest are
    /// no-ops. The game's normal cost + unit-cap gates still apply — we're not
    /// bypassing those.
    ///
    /// Target: SHRIMPS_PER_BC per owned BioCache (the user's "10-15 per biotics" spec).
    /// Called from the AlienConstruction.HandleTick prefix on Alien teams with override.
    /// </summary>
    internal static class AlienShrimpProducer
    {
        // Per-BC cap — user rule 2026-07-06: "up to 20 shrimps per biocache".
        // Live observation showed 255 total workers = still too many. Tightened
        // to 15/BC (user "20 too much"). Previously team-total cap 12×BCs let
        // one BC pile to 40+ while others sat empty; per-BC cap fixes the
        // distribution too.
        // 18 — user 2026-07-29: "groups of up to 18 shrimps per biotics can be
        // ok". Kept in step with ShrimpGroupPlanner.HARD_CAP; when these two
        // disagreed the producer built shrimps the group planner then evicted.
        // The team-wide ceiling is the real brake and is map-derived (see
        // ShrimpGroupPlanner.TeamCapacity), so a higher per-BC number no longer
        // risks the 255-worker overproduction that forced this down to 15.
        static int  PER_BC_CAP => Planning.BlueprintConfig.WorkerCapPerBioCache;
        // Radius around a BC that counts as "shrimps belonging to this BC".
        // Larger than the vanilla harvest range so we cover in-transit shrimps
        // returning to deposit. 60m ~ 2x harvest range on the maps we watch.
        const float BC_SHRIMP_RADIUS_M  = 60f;
        const float BC_SHRIMP_RADIUS_SQ = BC_SHRIMP_RADIUS_M * BC_SHRIMP_RADIUS_M;
        // Team-total safety ceiling. User 2026-07-08: 185 (was 200) — expect
        // ~200 on map counting in-flight queued shrimps between spawn and
        // arrival at the destination BC.
        //
        // THIS IS OURS, NOT THE GAME'S. Under the current balance config a Shrimp
        // has UnitCapValue 0 — workers cost no population at all — so this number
        // is a server-performance limit and nothing more. It was mistaken for the
        // game's unit cap on 2026-08-05, which produced a whole argument about
        // economy and army competing for slots that does not happen here.
        //
        // If a config DOES make workers cost population, Perception.UnitCaps says
        // so and the army's claim becomes real; that is read at runtime rather
        // than assumed, in either direction.
        const int   SHRIMP_HARD_CAP = 185;

        /// <summary>
        /// How many future expansion sites to hold unit-cap room for, so a
        /// freshly built Cyst can staff its own patch instead of waiting for
        /// migrants. Multiplied by what a site on THIS map actually feeds, and
        /// capped at a quarter of the ceiling so it can never starve production.
        /// </summary>
        const int   EXPANSION_SITES_HELD = 2;

        /// <summary>
        /// Fraction of the map's CURRENT live capacity worth producing toward.
        /// The gap absorbs the decay that happens while the shrimps are being
        /// built and walking — without it the fleet is permanently just over
        /// what the ground can feed, which is what generates the migration.
        /// </summary>
        const float SUSTAINABLE_FRACTION = 0.90f;

        /// <summary>Bank above which unspent cash is a worse problem than a
        /// surplus worker. Roughly a Cyst plus the Bio Cache and chain that
        /// would go with it — if that much is idle, the economy is not short of
        /// anything except workers.</summary>
        const int   SURPLUS_FOR_MORE_WORKERS = 8000;
        // Money management: each queued Shrimp reserves its cost. User 2026-07-07
        // asked to shorten to 1-2 max to free cash for tech / other placements.
        // Went to 1: at 20 Cysts that frees ~1500 cash (75 × 20) — enough for a
        // Cortex. Shrimps still spawn plenty fast because every Cyst refills its
        // queue slot immediately after a shrimp pops.
        /// <summary>
        /// How deep to keep each Cyst's queue.
        ///
        /// One meant the Cyst went IDLE the instant a shrimp popped and stayed
        /// idle until our next tick — we run off the game AI tick at roughly
        /// three seconds against a fifteen-second shrimp, so that is up to a
        /// fifth of every producer's output given away for a 160-cash saving.
        /// DrMuck asked the right question: "are the opener lesser cyst
        /// producing shrimps without a time gap? I mean one cyst produces 4
        /// shrimps per minute." With a depth of one, no — with two, the next
        /// shrimp is already queued when the current one finishes.
        /// </summary>
        const int CYST_QUEUE_MAX_DEFAULT = 2;

        /// <summary>
        /// Queue depth allowed per Cyst, EXCLUDING the one already building.
        ///
        /// The constant was 2 and the intent above was "two in the Cyst: one
        /// building, one waiting". But Structure.ProductionQueue does not count
        /// the unit currently under construction, so >= 2 permitted two QUEUED
        /// on top of one in progress. DrMuck, watching a co-op round: "I see in
        /// some cyst 3 shrimps piling up... third one is added while the one
        /// building is at 1/3 build progress."
        ///
        /// So 1 gives what the comment always described: one building, one
        /// queued behind it, no gap when the first finishes and no third
        /// shrimp's 160 credits frozen two builds ahead.
        ///
        /// Live from rtsai.json as cystQueueMax.
        /// </summary>
        static int CystQueueMax
            => Math.Max(1, Planning.RtsaiConfig.Int("cystQueueMax", CYST_QUEUE_MAX_DEFAULT));

        // Called every AI tick. Cache _shrimpCd lazily.
        static ConstructionData? _shrimpCd;

        internal static int Queued;
        internal static int Skipped;

        internal static void Tick(Team team)
        {
            int qBefore = Queued;
            long ts = System.Diagnostics.Stopwatch.GetTimestamp();
            try { Run(team); }
            catch (Exception ex) { MelonLogger.Warning("[RTSA/P32] Shrimp producer threw: " + ex.Message); }
            long ms = (System.Diagnostics.Stopwatch.GetTimestamp() - ts) * 1000L / System.Diagnostics.Stopwatch.Frequency;
            Si_RTS_AI.RecentModWork.AddShrimpProducer(ms, Queued - qBefore);
        }

        // Per-BC accounting entry. Reference type so we can mutate the
        // queuedForHere counter as we walk the Cysts.
        class BcInfo
        {
            public Vector3 Pos;
            public int     NearbyLive;      // shrimps within BC_SHRIMP_RADIUS
            public int     QueuedForHere;   // shrimps queued at Cysts we've mapped to this BC this tick
        }

        static void Run(Team team)
        {
            EnsureShrimpCd(team);
            if (_shrimpCd == null) return;

            var structs = team.Structures;
            if (structs == null) return;

            // 1) Enumerate BCs and record their positions. No BCs → nothing to do.
            var bcs = new List<BcInfo>();
            for (int i = 0; i < structs.Count; i++)
            {
                var s = structs[i];
                if (s?.ObjectInfo == null || s.IsDestroyed) continue;
                if (!string.Equals(s.ObjectInfo.DisplayName, "Bio Cache", StringComparison.OrdinalIgnoreCase)) continue;
                bcs.Add(new BcInfo { Pos = s.transform.position });
            }
            if (bcs.Count == 0) return;

            // 2) Count nearby live shrimps per BC — assign each shrimp to its
            //    closest BC within BC_SHRIMP_RADIUS. Shrimps beyond that radius
            //    from every BC are "in-transit / lost / at Nest" and don't count
            //    against any BC's cap.
            int liveTotal = 0;
            var units = team.Units;
            if (units != null)
            {
                for (int i = 0; i < units.Count; i++)
                {
                    var u = units[i];
                    if (u?.ObjectInfo == null || u.IsDestroyed) continue;
                    if (!string.Equals(u.ObjectInfo.DisplayName, "Shrimp", StringComparison.OrdinalIgnoreCase)) continue;
                    // One-shot per round; self-disarms after the first success.
                    Planning.EcoSimulator.TryResolveShrimpSpeed(u);
                    var p = u.transform.position;
                    int nearestIdx = -1;
                    float nearestDsq = BC_SHRIMP_RADIUS_SQ;
                    for (int bi = 0; bi < bcs.Count; bi++)
                    {
                        float dx = bcs[bi].Pos.x - p.x;
                        float dz = bcs[bi].Pos.z - p.z;
                        float dsq = dx * dx + dz * dz;
                        if (dsq < nearestDsq) { nearestDsq = dsq; nearestIdx = bi; }
                    }
                    if (nearestIdx >= 0) bcs[nearestIdx].NearbyLive++;
                    liveTotal++;
                }
            }

            // 3) Count all currently-queued shrimps across the team — used as
            //    the safety-net hard cap so we don't overshoot server-perf.
            int queuedTotal = 0;
            for (int qi = 0; qi < structs.Count; qi++)
            {
                var qs = structs[qi];
                if (qs?.ProductionQueue == null) continue;
                try { queuedTotal += qs.ProductionQueue.Count; } catch { }
            }
            // Two ceilings, whichever is tighter:
            //   SHRIMP_HARD_CAP  — server-performance limit, map-independent.
            //   TeamCapacity     — what this map's live biotics can actually
            //                      feed, summed over the harvest groups.
            // The second matters because map size varies 5.5x in total biotics
            // (TheMaw 31 patches / 682k vs IndustrialQuarter 172 / 3.78M) and
            // is configurable per map via Si_MapBalance, so no fixed number is
            // right everywhere. Producing past it just builds the next pile-up.
            int mapCap = int.MaxValue;
            try
            {
                int tc = Planning.ShrimpGroupPlanner.TeamCapacity;
                if (tc > 0) mapCap = tc;
            }
            catch { }
            // PRODUCE AGAINST CAPACITY THAT WILL STILL EXIST.
            //
            // TeamCapacity is what the live biotics can feed RIGHT NOW, and it
            // falls continuously as patches drain — so producing up to it
            // guarantees being over it minutes later, with the surplus turning
            // into migration. NarakaCity 2026-08-02 at 10min: shrimps=141
            // against totalCap=130, migrated=112, reissued=607, with one group
            // holding 15 shrimps on a patch already at cap=0.
            //
            // Those eleven surplus shrimps are not idle, they are the churn:
            // they have nowhere to be, so every pass re-homes them and every
            // depletion sends another wave. Holding a margin under current
            // capacity costs a little income now and removes the wave.
            if (mapCap != int.MaxValue) mapCap = Mathf.FloorToInt(mapCap * SUSTAINABLE_FRACTION);

            int effectiveCap = Math.Min(SHRIMP_HARD_CAP, mapCap);

            // THE EARLY RAMP IS NOT A PILE-UP.
            //
            // Every brake above was added against a MID-GAME failure — shrimps
            // over live capacity, migration waves, expansions that could not
            // staff themselves. Applied from the first minute they do something
            // else entirely: they hold the opening below the rate the economy
            // needs to compound. Measured on NarakaCity 2026-08-05, against
            // DrMuck's 100k-by-10-minutes benchmark:
            //
            //     t=205s  20 shrimps      t=506s  44 shrimps
            //     t=325s  34 shrimps      t=746s  57 shrimps
            //
            // Six producers can build 24 shrimps a minute. We added three a
            // minute, with 42,000 cash idle — so it was never cash, it was this
            // ceiling. DrMuck: "might need to allow more shrimps early to reach
            // that goal."
            //
            // Before Phase 2 there is nowhere to expand to and nothing has
            // depleted yet, so the map-capacity model is at its least
            // informative exactly when the ramp matters most. Rule 5.6 already
            // says what the answer is early — around 18 per biotics — so use it
            // as a FLOOR while Phase 1 lasts, and let the map model bind only
            // when it asks for more. Phase 2 keeps every brake it had.
            // IDLE CASH MEANS THE CEILING IS WRONG.
            //
            // Phase 1 alone was not enough. Same round, one version later:
            // shrimps sat at exactly 33 from t=325s to t=446s — two minutes
            // flat, four producers running, nothing queued — because Phase 2
            // had begun and the map-capacity model took over again at a number
            // barely above where the ramp had reached. DrMuck: "number of
            // workers stall... why do we cap early shrimp production so much.
            // Even for opener with 18 shrimp per cyst would be 72 shrimp for
            // the 4 lessers."
            //
            // The capacity model is a statement about what the ground can feed
            // SUSTAINABLY. It is a good brake on overproduction and a bad brake
            // on a bank that has nowhere else to go: cash sitting unspent is
            // strictly worse than a worker who might later have to walk. So the
            // per-Bio-Cache allowance also applies whenever there is real
            // surplus, and it stops applying the moment that surplus is spent —
            // which makes it self-limiting rather than another constant.
            bool idleCash = false;
            try { idleCash = team.TotalResources >= SURPLUS_FOR_MORE_WORKERS; } catch { }
            // Behind the reference curve, the sustainable-capacity model is a
            // statement about the long run — and the long run is exactly what
            // is being lost while it holds. See WorkerPlan.
            bool behind = Planning.WorkerPlan.BehindSchedule;
            if (bcs.Count > 0 && (!Planning.EcoPlanner.CurrentPhaseIsExpand || idleCash || behind))
                effectiveCap = Math.Min(SHRIMP_HARD_CAP, Math.Max(effectiveCap, bcs.Count * PER_BC_CAP));

            // KEEP HEADROOM FOR EXPANSIONS THAT DO NOT EXIST YET.
            //
            // Filling to the cap early is self-defeating: a new expansion Cyst
            // then cannot produce ANYTHING locally, so the only way to staff it
            // is to walk shrimps in from older sites — which is the mass
            // migration. NarakaCity 2026-07-31, shrimps against unit cap 185:
            //
            //     t~4min  shrimps=52   totalCap=52    reissued=30
            //     t~6min  shrimps=87   totalCap=128   reissued=163
            //     t~8min  shrimps=133  totalCap=140   reissued=476
            //     t~10min shrimps=196  totalCap=205   reissued=939
            //
            // Production was capped out, patch capacity tracked just behind the
            // shrimp count the whole way, and by 8min there was nowhere to put
            // anyone — every depletion became a scramble. User 2026-07-31:
            // "building too many shrimps early can hurt later in aggressive
            // expansion, because the shrimp cap limit is reached too quick and
            // it is not possible to build shrimps at the expansions."
            //
            // Only from Phase 2 — the opening should ramp flat out, since there
            // is nothing to expand to yet.
            // Sized from the MAP, not a constant. How many shrimps a site can
            // feed falls out of patch storage, patch distribution and how many
            // patches a Bio Cache reaches — all map-specific and editable via
            // Si_MapBalance, so a fixed number is right nowhere. And no headroom
            // at all if there is nothing left to expand to.
            if (Planning.EcoPlanner.CurrentPhaseIsExpand
                && Planning.ShrimpGroupPlanner.FreePatchCount > 0)
            {
                int perSite = Planning.ShrimpGroupPlanner.TypicalGroupCapacity;
                if (perSite > 0)
                {
                    // HOLD ROOM FOR THE SITES ACTUALLY GOING UP, NOT A CONSTANT.
                    //
                    // A flat two sites' worth of unit cap was held back whether
                    // or not anything was being built, so when expansion was
                    // slow the reservation just sat there and production stalled
                    // against it — DrMuck watched shrimps stick around 57 with
                    // 70,000 cash unspent. Reserve for what is under
                    // construction, and when nothing is, produce.
                    int sitesGoingUp = 0;
                    try { sitesGoingUp = Planning.EcoPlanner.UnfinishedBcCount(team); } catch { }
                    int headroom = Math.Min(Math.Max(1, sitesGoingUp) * perSite, effectiveCap / 4);
                    effectiveCap = Math.Max(1, effectiveCap - headroom);
                }
            }

            if (liveTotal + queuedTotal >= effectiveCap)
            {
                _blockedTeamCap++;
                MaybeReport(liveTotal, queuedTotal, effectiveCap, 0);
                return;
            }

            // THE OPENING'S OWN CYSTS COME BEFORE MARGINAL SHRIMPS.
            //
            // A Lesser Cyst is a shrimp factory, so buying twelve shrimps with
            // its 1,500 trades everything that factory would ever produce for
            // one batch now. Nothing was making that trade explicitly: the
            // fourth Cyst of the opening waited fifty seconds while cash
            // oscillated between 630 and 1,940 — every income tick went into a
            // shrimp — and then skipped itself on the stall timer.
            //
            // Only the opening's planned Cysts, and only until they are placed.
            int openerReserve = 0;
            try { openerReserve = Planning.OpenerPlanner.PendingCystCash; } catch { }
            if (openerReserve > 0)
            {
                int cashNow = 0;
                try { cashNow = team.TotalResources; } catch { }
                if (cashNow - Planning.EcoSimulator.SHRIMP_COST < openerReserve) return;
            }

            // 4) For each Cyst that can produce Shrimp, find its nearest BC
            //    and check whether that BC is already at cap (live + queued
            //    bound here). Skip if saturated.
            //
            // Frontier bias (Phase 2+, user 2026-07-08): iterate Cysts from
            // FURTHEST-from-Nest to nearest, so mid/late-game shrimp production
            // fills forward expansion Cysts first before backfilling the
            // starter cluster. Phase 1 iterates in team-structures order
            // (nearest first = fast starter ramp).
            Vector3 nestPos = Vector3.zero;
            for (int ni = 0; ni < structs.Count; ni++)
            {
                var st = structs[ni];
                if (st?.ObjectInfo == null || st.IsDestroyed) continue;
                if (st.ObjectInfo.DisplayName == "Nest") { nestPos = st.transform.position; break; }
            }
            var iterOrder = new List<int>();
            for (int i = 0; i < structs.Count; i++) iterOrder.Add(i);
            bool isPhase2 = false;
            try { isPhase2 = Planning.EcoPlanner.CurrentPhase == Planning.EcoPlanner.PlanPhase.Phase2_Expand; } catch { }
            if (isPhase2 && nestPos != Vector3.zero)
            {
                iterOrder.Sort((ai, bi) =>
                {
                    var sa = structs[ai]; var sb = structs[bi];
                    float da = 0f, db = 0f;
                    try { var d = sa.transform.position - nestPos; da = d.x * d.x + d.z * d.z; } catch { }
                    try { var d = sb.transform.position - nestPos; db = d.x * d.x + d.z * d.z; } catch { }
                    return db.CompareTo(da);   // descending — furthest first
                });
            }

            int cystsSeen = 0;
            for (int ii = 0; ii < iterOrder.Count; ii++)
            {
                int i = iterOrder[ii];
                var s = structs[i];
                if (s == null || s.ObjectInfo == null || s.IsDestroyed) continue;
                if (s.ConstructionOptions == null || !s.ConstructionOptions.Contains(_shrimpCd)) continue;
                // ONE OWNER PER PRODUCER. A Cyst the military layer has claimed
                // makes combat units and nothing else. Without this the claim is
                // decorative: we keep every Cyst at queue depth two, so the
                // military's stride would find no free slot and its configured
                // share would silently produce nothing at all.
                // Empty set — and so no effect — unless the layer is on AND the
                // economy has stopped being able to convert the cash.
                if (MilitaryProduction.IsClaimed(s)) continue;
                cystsSeen++;

                // Nearest BC to this Cyst (unbounded distance — every Cyst
                // has SOME closest BC; used to decide which BC's cap this
                // spawn counts against).
                var cystPos = s.transform.position;
                int nearestBcIdx = -1;
                float nearestBcDsq = float.MaxValue;
                for (int bi = 0; bi < bcs.Count; bi++)
                {
                    float dx = bcs[bi].Pos.x - cystPos.x;
                    float dz = bcs[bi].Pos.z - cystPos.z;
                    float dsq = dx * dx + dz * dz;
                    if (dsq < nearestBcDsq) { nearestBcDsq = dsq; nearestBcIdx = bi; }
                }
                if (nearestBcIdx < 0) continue;

                var bc = bcs[nearestBcIdx];
                // Cap by the group planner's capacity model rather than a flat
                // constant: a BC whose patches are nearly drained can only feed
                // a couple more shrimps, and spawning 15 there just builds the
                // next migration wave. Falls back to PER_BC_CAP before the
                // first group tick has published a snapshot.
                int cap = PER_BC_CAP;
                try { cap = Math.Min(PER_BC_CAP, Planning.ShrimpGroupPlanner.CapacityForNearestBc(bc.Pos)); } catch { }
                if (bc.NearbyLive + bc.QueuedForHere >= cap)
                {
                    // KEEP PUMPING FOR GROUND SOMEONE ELSE IS SHORT OF.
                    //
                    // A Cyst used to stop the moment its OWN Bio Cache filled,
                    // which is why production collapsed the instant the
                    // blueprint started adding sites -- 93% of nominal at three
                    // producers, 23-30% at eight, with bcCap climbing to 26.
                    // Every added producer made it worse, because each one only
                    // ever asked about the Bio Cache beside it.
                    //
                    // The delivery half already worked: ShrimpGroupPlanner
                    // spills over-capacity shrimps onto untapped ground and
                    // pileUpMaxDetourM defaults to 0, meaning no distance limit.
                    // The producer simply never made a surplus to spill.
                    //
                    // Measured before enabling, NarakaCity 2026-08-11:
                    //     bcCap=27  remoteFree raw=68 transit=68 nearest=446m
                    //     bcCap=36  remoteFree raw=59 transit=59 nearest=446m
                    // ~60 free worker slots team-wide while producers idled, and
                    // transit == raw, so pileUpMaxPerPatch never binds -- the
                    // free capacity is spread thin, not concentrated. That was
                    // the conflict DrMuck asked to rule out, and it is ruled out.
                    //
                    // But nearest free capacity was 377-446m, about 45s of
                    // walking, so this is NOT unconditional. A shrimp is only
                    // worth 160 credits if it reaches work in time, hence the
                    // walk budget.
                    NoteRemoteDemand(bcs, nearestBcIdx);
                    if (!RemoteSupplyEnabled ||
                        !HasRemoteDemand(bcs, nearestBcIdx, RemoteSupplyMaxWalkS))
                    {
                        Skipped++; _blockedBcCap++;
                        continue;
                    }
                    _remoteSupplied++;
                }

                // Per-Cyst queue-depth check — keeps cash-reservation shallow.
                int queueDepth = 0;
                try { queueDepth = s.ProductionQueue?.Count ?? 0; } catch { }
                if (queueDepth >= CystQueueMax)
                {
                    Skipped++; _busy++;
                    continue;
                }

                // Fire — cost + cap enforced inside Construct.
                try
                {
                    var res = s.Construct(_shrimpCd);
                    if (res == ProductionActionResult.Success)
                    {
                        Queued++;
                        bc.QueuedForHere++;
                    }
                    else
                    {
                        Skipped++; _refused++;
                    }
                }
                catch (Exception ex) { MelonLogger.Warning("[RTSA/P32] Cyst.Construct threw: " + ex.Message); }
            }

            MaybeReport(liveTotal, queuedTotal, effectiveCap, cystsSeen);
        }

        // ---- Is production continuous? --------------------------------------
        //
        // Four shrimps per Cyst per minute is the ceiling. Anything below it is
        // either a CEILING (team cap, per-Bio-Cache cap) or a GAP (the producer
        // was free and we did not fill it), and those want opposite fixes — so
        // count them apart rather than inferring from the shrimp curve.
        static int _blockedTeamCap, _blockedBcCap, _busy, _refused;
        static float _lastReportAt;
        static int _shrimpsAtLastReport = -1;

        static void MaybeReport(int live, int queued, int cap, int cysts)
        {
            float now = Time.time;
            if (now - _lastReportAt < 30f) return;
            float dt = now - _lastReportAt;
            _lastReportAt = now;
            if (_shrimpsAtLastReport < 0) { _shrimpsAtLastReport = live; ResetCounters(); return; }

            int gained = live - _shrimpsAtLastReport;
            _shrimpsAtLastReport = live;
            float perMin = dt > 0f ? gained * 60f / dt : 0f;
            float ceiling = cysts * 60f / Mathf.Max(1f, Planning.EcoSimulator.SHRIMP_BUILD_S);

            Planning.WorkerPlan.NoteDuty(perMin, ceiling);
            MelonLogger.Msg($"[SHRIMP/PROD] {perMin:F1}/min against {ceiling:F1}/min from {cysts} producers " +
                            $"({(ceiling > 0f ? perMin / ceiling * 100f : 0f):F0}%) — " +
                            $"live={live} queued={queued} cap={cap} | " +
                            $"blocked: teamCap={_blockedTeamCap} bcCap={_blockedBcCap} " +
                            $"alreadyBusy={_busy} refused={_refused}" +
                            (_blockedBcCap > 0 || _remoteSupplied > 0
                                ? $" | remoteFree raw={_remoteFreeRaw} transit={_remoteFreeTransit} nearest={_remoteFreeNearestM}m" +
                                  $" supplied={_remoteSupplied}"
                                : ""));
            ResetCounters();
        }

        static void ResetCounters()
        {
            _blockedTeamCap = _blockedBcCap = _busy = _refused = 0;
            _remoteFreeRaw = _remoteFreeTransit = 0; _remoteFreeNearestM = -1; _remoteSupplied = 0;
        }

        // ---- REMOTE DEMAND, MEASURED BUT NOT YET ACTED ON ------------------
        //
        // A Cyst stops the moment ITS OWN nearest Bio Cache is full. It never
        // asks whether anywhere else needs shrimps, so production collapses
        // exactly when the blueprint starts adding sites -- NarakaCity
        // 2026-08-11, 93% of nominal at 3 producers before blueprint rev=1,
        // then 23-35% at 4-8 producers, with bcCap climbing 0 -> 26.
        //
        // The relocator would carry the surplus: it already spills over-capacity
        // shrimps onto untapped ground, and pileUpMaxDetourM defaults to 0
        // meaning NO distance limit. So the delivery half works; the producer
        // never creates anything to deliver.
        //
        // Before opening that tap, measure what it would open it BY. Two
        // numbers, because they can differ a lot:
        //
        //   raw      free capacity at other Bio Caches within a free agent's
        //            walk, ignoring how fast the relocator can actually move
        //            shrimps there.
        //   transit  the same, but each destination capped at pileUpMaxPerPatch
        //            -- the number the relocator may have walking toward one
        //            place at once. Production above this outruns delivery and
        //            just queues shrimps at the source at 160 credits each.
        //
        // If raw is large and transit is near zero, the capacity is real but
        // unreachable in time and opening the tap would burn cash. DrMuck
        // raised exactly this: "need to see if the production demand does not
        // conflict with shrimp pile up manager".
        static int _remoteFreeRaw, _remoteFreeTransit;
        static int _remoteSupplied;

        /// <summary>May a Cyst keep producing when its own Bio Cache is full,
        /// for capacity elsewhere the relocator can reach? rtsai.json,
        /// live-reloaded, default off.</summary>
        static bool RemoteSupplyEnabled
            => Planning.RtsaiConfig.Bool("remoteSupplyEnabled", false);

        /// <summary>How far, in seconds of walking, free capacity may be and
        /// still count as demand. 60s at 9 m/s is about 540m -- past the
        /// 377-446m the nearest free slots measured at, and short enough that
        /// the shrimp still works for most of the horizon.</summary>
        static float RemoteSupplyMaxWalkS
            => Planning.RtsaiConfig.Float("remoteSupplyMaxWalkS", 60f);

        /// <summary>Is there free worker capacity within the walk budget, and
        /// can the relocator actually be moving shrimps toward it? Bounded per
        /// destination by pileUpMaxPerPatch so production cannot outrun
        /// delivery even if the spread changes.</summary>
        static bool HasRemoteDemand(List<BcInfo> bcs, int selfIdx, float maxWalkS)
        {
            try
            {
                float reachM = Planning.EcoSimulator.SHRIMP_SPEED * Math.Max(1f, maxWalkS);
                float reachSq = reachM * reachM;
                int perDest = Math.Max(1, Planning.RtsaiConfig.Int("pileUpMaxPerPatch", 10));
                var from = bcs[selfIdx].Pos;

                for (int i = 0; i < bcs.Count; i++)
                {
                    if (i == selfIdx) continue;
                    float dx = bcs[i].Pos.x - from.x, dz = bcs[i].Pos.z - from.z;
                    if (dx * dx + dz * dz > reachSq) continue;

                    int capI = PER_BC_CAP;
                    try { capI = Math.Min(PER_BC_CAP,
                            Planning.ShrimpGroupPlanner.CapacityForNearestBc(bcs[i].Pos)); } catch { }
                    int free = Math.Min(capI - (bcs[i].NearbyLive + bcs[i].QueuedForHere), perDest);
                    if (free > 0) return true;
                }
            }
            catch { }
            return false;
        }
        static int _remoteFreeNearestM = -1;

        static void NoteRemoteDemand(List<BcInfo> bcs, int selfIdx)
        {
            try
            {
                // NO RADIUS FILTER. The first version of this bounded the scan
                // by SHRIMP_SPEED x FreeAgentMaxWalkS -- 30s of walking, 270m --
                // and reported raw=0 on every tick with bcCap up to 16. That was
                // the radius answering, not the demand: Bio Caches sit 200-700m
                // apart, so almost nothing is ever inside 270m.
                //
                // The delivery path does not use that radius. The relocator's
                // SPILL sends over-capacity shrimps to untapped ground with
                // pileUpMaxDetourM defaulting to 0, which means no distance
                // limit at all. Meanwhile the shrimp manager was reporting
                // "shrimps=66 groups=13 used=9 empty=4 totalCap=119" -- four
                // empty groups and 53 free slots -- while producers sat blocked.
                //
                // So scan the whole team and report how FAR the nearest free
                // capacity is, rather than pretending anything past 270m does
                // not exist.
                int perDest = Math.Max(1, Planning.RtsaiConfig.Int("pileUpMaxPerPatch", 10));
                var from = bcs[selfIdx].Pos;

                int raw = 0, transit = 0; float nearestFreeSq = float.MaxValue;
                for (int i = 0; i < bcs.Count; i++)
                {
                    if (i == selfIdx) continue;

                    int capI = PER_BC_CAP;
                    try { capI = Math.Min(PER_BC_CAP,
                            Planning.ShrimpGroupPlanner.CapacityForNearestBc(bcs[i].Pos)); } catch { }
                    int free = capI - (bcs[i].NearbyLive + bcs[i].QueuedForHere);
                    if (free <= 0) continue;

                    raw     += free;
                    transit += Math.Min(free, perDest);

                    float dx = bcs[i].Pos.x - from.x, dz = bcs[i].Pos.z - from.z;
                    float dsq = dx * dx + dz * dz;
                    if (dsq < nearestFreeSq) nearestFreeSq = dsq;
                }
                if (raw     > _remoteFreeRaw)     _remoteFreeRaw     = raw;
                if (transit > _remoteFreeTransit) _remoteFreeTransit = transit;
                if (nearestFreeSq < float.MaxValue)
                {
                    int m = (int)Math.Sqrt(nearestFreeSq);
                    if (_remoteFreeNearestM < 0 || m < _remoteFreeNearestM) _remoteFreeNearestM = m;
                }
            }
            catch { }
        }

        static void EnsureShrimpCd(Team team)
        {
            if (_shrimpCd != null) return;
            var structs = team.Structures;
            if (structs == null) return;
            for (int i = 0; i < structs.Count; i++)
            {
                var s = structs[i];
                if (s == null || s.ConstructionOptions == null) continue;
                foreach (var opt in s.ConstructionOptions)
                {
                    if (opt?.ObjectInfo == null) continue;
                    if (string.Equals(opt.ObjectInfo.DisplayName, "Shrimp", StringComparison.OrdinalIgnoreCase))
                    {
                        _shrimpCd = opt;
                        // Shrimp cost/build time are modded too (base 160 /
                        // 15s here); the eco sim needs the real values.
                        Planning.EcoSimulator.SetShrimpCd(opt);
                        return;
                    }
                }
            }
        }

        internal static void ResetForNewRound()
        {
            Queued = 0;
            Skipped = 0;
        }

        internal static string BuildRoundSummaryFragment()
        {
            if (Queued == 0 && Skipped == 0) return "";
            return "--- Phase 3.2 slice 1 (direct Shrimp queuing at Cysts) ---\n" +
                   $"  Shrimps queued this round:  {Queued}\n" +
                   $"  Skipped (queue full / fail): {Skipped}\n";
        }
    }
}
