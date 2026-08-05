using MelonLoader;
using Silica;
using Si_RTS_AI.Perception;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Si_RTS_AI.Planning
{
    /// <summary>
    /// TechPlanner — proposes Quantum Cortex / Research Facility placement.
    ///
    /// Timing model — user guidance:
    ///   "Build early but not too early. Fast consecutive teching once
    ///    started. Balance with eco expansion."
    ///
    /// Concrete gating:
    ///   1. Phase 2 eco (base stable — otherwise eco expansion loses to tech)
    ///   2. Cash &gt; cost + ECO_RESERVE (keeps a couple of BCs affordable)
    ///   3. Payback gate: income/s &gt;= cost / PAYBACK_S
    ///        - PAYBACK_FIRST_S = 60s   (opening Cortex — pay it off in 1 min of income)
    ///        - PAYBACK_NEXT_S  = 30s   (subsequent tiers — go fast once eco supports it)
    ///   4. Not already maxed (team.TechnologyTier &lt; TechnologyTierLimitMax)
    ///
    /// Tech-tier discovery uses ConstructionData.ObjectInfo.StructureType ==
    /// StructureType.Research — the game's own flag, same one Si_BuildLimits
    /// keys off. This automatically picks up Quantum Cortex on Alien and
    /// Research Facility on Human without hardcoded names.
    ///
    /// Placement target: near the starter Nest — safe location, easy chain
    /// reach. User confirmed this is where human commanders put Cortex too.
    /// </summary>
    internal class TechPlanner : IActionSource
    {
        public string SourceId => "TechPlanner";

        // Reserve is small — user calibration: "placing tech early enough with just
        // a slight eco penalty is the deal", human commander builds first Cortex
        // ~3 min in. A single delayed BC/Cyst is acceptable.
        // User 2026-07-07: "cortex can be placed right away at 2000 cash
        // reserves than not 2500" — reservation = cortex cost, no extra
        // eco buffer. Nodes/BCs only fire when cash EXCEEDS the reserve,
        // so cash never dips below 2000 while we're saving for tech.
        const int   MIN_ECO_RESERVE  = 0;
        const float PAYBACK_FIRST_S  = 45f;   // kept for future use — currently unused
        const float PAYBACK_NEXT_S   = 20f;
        // Two-tier shrimp thresholds:
        //   - Once first Cyst produces (5 shrimps), start RESERVING cash for
        //     Cortex — the reservation naturally caps eco spending at the
        //     surplus above 2000, so Nodes with cash > 2200 still fire.
        //   - FIRE Cortex at 15 shrimps: income ≈ 75/s can sustain the
        //     research chain (Alpha I → Omega VIII) that follows.
        //     User 2026-07-07: "Add 15 shrimps as condition" — was 10 which
        //     fired before income could keep up with research.
        const int   RESERVE_SHRIMPS   = 5;
        const int   FIRE_SHRIMPS      = 15;

        // Catalog: display-name → ConstructionData for Research-type CDs.
        // Shared across teams; each team gets cataloged on first sight so
        // Alien picks up Quantum Cortex and Human picks up Research Facility.
        static readonly Dictionary<string, ConstructionData> _techCds
            = new Dictionary<string, ConstructionData>(StringComparer.OrdinalIgnoreCase);

        static readonly HashSet<string> _catalogedTeams = new HashSet<string>();

        public void PublishState(BrokerContext ctx)
        {
            EnsureCataloged(ctx.Team);

            int tier = 0, maxTier = 0, watermark = 0;
            try { tier      = ctx.Team.TechnologyTier; }              catch { }
            try { maxTier   = ctx.Team.TechnologyTierLimitMax; }      catch { }
            try { watermark = ctx.Team.TechnologyTierHighestReached; } catch { }

            // If a Cortex exists but tier is still 0, the game hasn't
            // reconciled — Si_TechGlitch's whole point is that this happens.
            // The (false, true) call forces a resync from current structures.
            if (tier == 0 && CountResearchStructures(ctx.Team) > 0)
            {
                TryForceSyncTier(ctx.Team);
                try { tier = ctx.Team.TechnologyTier; } catch { }
                if (tier > 0) MelonLogger.Msg($"[TECH] force-sync raised tier {ctx.Team.name} to T{tier}");
            }

            ctx.SubsystemState[SourceId] = new TechPublishedState
            {
                KnownTiers    = new List<string>(_techCds.Keys),
                CurrentTier   = tier,
                MaxTier       = maxTier,
                WatermarkTier = watermark,
            };

            // Reserve cash for the FIRST Cortex based on BASE-ECO STABILITY,
            // not Phase 2 detection. User 2026-07-07: from 1:30 Nodes were
            // being built that only pay back a bit later — money should be
            // saved for Cortex. Phase 2 fallback fires at 2:30, too late.
            // Instead, gate on BC count as an eco-stability proxy AND cash
            // approaching the target so we don't starve very-early game.
            try
            {
                int shrimps = CountShrimps(ctx.Team);
                int cortexCount = CountResearchStructures(ctx.Team);
                bool tierMaxed = maxTier > 0 && tier >= maxTier;

                // Compute the cheapest tech cost once (Cortex = Alpha I = 2000 in vanilla).
                int cheapest = int.MaxValue;
                foreach (var kv in _techCds)
                {
                    int c = TryGetCost(kv.Value);
                    if (c > 0 && c < cheapest) cheapest = c;
                }

                // Reservation rule — HARD reserve, no projection. CSV data
                // from cashflow_20260707_205950.csv showed the projection
                // over-forecasted income by 5-10x in the 3-Cysts→15-shrimps
                // window. Cash dropped 3000→500 during that period; Cortex
                // arrived 47s late as a result. Plot from t=200s onward showed
                // that a simple hold-2000 rule keeps cash correctly at target.
                //
                // Trigger: 3 committed Cysts (built OR under construction) OR
                // Cortex already exists. Between those, hold 2000 aside for
                // Cortex placement / next research. Eco can only spend the
                // surplus above 2000, which the reservation naturally caps.
                int committedCysts = MoneyBroker.CashFlow.CountCommittedCysts(ctx.Team);
                if (!tierMaxed && _techCds.Count > 0 && cheapest != int.MaxValue)
                {
                    int target = cheapest + MIN_ECO_RESERVE;

                    // THE COMMITTED OPENING OUTRANKS THIS RESERVE.
                    //
                    // The opening is a costed plan whose total was checked
                    // against the bank before it was accepted, so reserving cash
                    // out from under it mid-way breaks a commitment already
                    // made. NarakaCity 2026-07-31: three sites were up at t=94s
                    // with cash=1800, the 4th needed 500+1500, and reserved=2000
                    // held it off until the Cortex fired — the user saw the 4th
                    // Cyst arrive only after tech was ready.
                    //
                    // So while the opening is live, reserve only what is left
                    // above what it still owes. Tech is not deferred, it just
                    // takes surplus rather than taking priority for the ~2
                    // minutes the opening lasts.
                    int openerOwes = OpenerPlanner.OutstandingCost;
                    if (openerOwes > 0)
                        target = Mathf.Min(target, Mathf.Max(0, ctx.Cash - openerOwes));

                    if (cortexCount > 0 || committedCysts >= 3)
                        ctx.CashReservations[SourceId] = target;
                }
                // 1Hz CSV log for post-round validation — plot predicted vs
                // actual cash trajectory in Python. Grep for TECH/RESERVE for
                // human-readable version too.
                float now = Time.time;
                if (now - _lastReserveLogAt >= 1f)
                {
                    _lastReserveLogAt = now;
                    int reservedNow = 0;
                    if (ctx.CashReservations.TryGetValue(SourceId, out int r)) reservedNow = r;
                    int producers = MoneyBroker.CashFlow.CountProducers(ctx.Team);
                    float dtTo15 = MoneyBroker.CashFlow.TimeToReachShrimps(shrimps, FIRE_SHRIMPS, producers);
                    int projected = MoneyBroker.CashFlow.ProjectCashAt(ctx.Team, ctx.Cash, shrimps, producers, dtTo15);
                    float perShrimp = MoneyBroker.CashFlow.MeasuredIncomePerShrimp(ctx.Team, shrimps);
                    float teamIncome = 0f;
                    try { teamIncome = Perception.EcoRateSampler.GetAvgIncomePerSec(ctx.Team); } catch { }
                    string dtStr = dtTo15 >= float.MaxValue ? "inf" : $"{dtTo15:F0}s";
                    MelonLogger.Msg($"[TECH/RESERVE] cash={ctx.Cash} shrimps={shrimps} " +
                                    $"perShrimp={perShrimp:F2}/s producers={producers} " +
                                    $"committedCysts={committedCysts} " +
                                    $"dtTo15={dtStr} projected@dl={projected} " +
                                    $"reserved={reservedNow} openerOwes={OpenerPlanner.OutstandingCost} " +
                                    $"cortex={cortexCount} tier={tier}/{maxTier} " +
                                    $"t={now:F0}s");
                    AppendCsvRow(now, ctx.Cash, teamIncome, perShrimp, shrimps,
                                 producers, committedCysts, dtTo15, projected, reservedNow,
                                 cortexCount, tier, maxTier);
                }
            }
            catch { }
        }

        static float _lastReserveLogAt;

        // CSV logger for prediction vs reality validation. User 2026-07-07:
        // "How about plotting the prediction curves vs. real income rate
        // starting from the next match." One CSV per round in UserData/RTSA/.
        static System.IO.StreamWriter _csvWriter;
        static string _csvPath;

        static void EnsureCsv()
        {
            if (_csvWriter != null) return;
            try
            {
                string dir = System.IO.Path.Combine("UserData", "RTSA");
                System.IO.Directory.CreateDirectory(dir);
                string ts = System.DateTime.Now.ToString("yyyyMMdd_HHmmss");
                _csvPath = System.IO.Path.Combine(dir, $"cashflow_{ts}.csv");
                _csvWriter = new System.IO.StreamWriter(_csvPath, append: false);
                _csvWriter.WriteLine("t_sec,cash,team_income_per_sec,per_shrimp_income,shrimps," +
                                     "producers,committed_cysts,dt_to_15,projected_at_deadline," +
                                     "reserved,cortex_count,tier,max_tier");
                _csvWriter.Flush();
                MelonLogger.Msg($"[TECH/CSV] logging to {_csvPath}");
            }
            catch (Exception ex) { MelonLogger.Warning("[TECH/CSV] EnsureCsv threw: " + ex.Message); }
        }

        static void AppendCsvRow(float t, int cash, float teamIncome, float perShrimp,
            int shrimps, int producers, int committedCysts, float dtTo15,
            int projected, int reserved, int cortexCount, int tier, int maxTier)
        {
            EnsureCsv();
            if (_csvWriter == null) return;
            try
            {
                string dtStr = dtTo15 >= float.MaxValue ? "" : dtTo15.ToString("F1",
                    System.Globalization.CultureInfo.InvariantCulture);
                _csvWriter.WriteLine(
                    $"{t.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)}," +
                    $"{cash}," +
                    $"{teamIncome.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)}," +
                    $"{perShrimp.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)}," +
                    $"{shrimps}," +
                    $"{producers}," +
                    $"{committedCysts}," +
                    $"{dtStr}," +
                    $"{projected}," +
                    $"{reserved}," +
                    $"{cortexCount}," +
                    $"{tier}," +
                    $"{maxTier}");
                _csvWriter.Flush();
            }
            catch (Exception ex) { MelonLogger.Warning("[TECH/CSV] AppendCsvRow threw: " + ex.Message); }
        }

        static void CloseCsv()
        {
            try { _csvWriter?.Flush(); _csvWriter?.Close(); } catch { }
            _csvWriter = null;
            _csvPath = null;
        }

        public List<Proposal> ProposeCandidates(BrokerContext ctx)
        {
            var proposals = new List<Proposal>();
            if (_techCds.Count == 0) return proposals;

            var s = (TechPublishedState)ctx.SubsystemState[SourceId];

            // Base-eco gate — user 2026-07-07 wants Cortex much earlier than
            // Phase 2's ~2:30 fallback. Use BC count as a base-eco proxy:
            // once we have MIN_BCS_FOR_TECH, base eco is stable enough that
            // Cortex placement doesn't starve expansion.
            if (CountShrimps(ctx.Team) < FIRE_SHRIMPS) return proposals;

            // Already maxed — nothing to do.
            if (s.MaxTier > 0 && s.CurrentTier >= s.MaxTier) return proposals;

            // If we already have a completed Cortex — try to research the NEXT
            // tier instead of placing another. The Cortex's ConstructionOptions
            // hold research CDs (Alpha I → Omega VIII), each 2000 cash,
            // advancing team.TechnologyTier by 1. Fire the option indexed by
            // current tier via Structure.Construct — same code path a human
            // commander clicks.
            var existingCortex = FindResearchStructure(ctx.Team);
            if (existingCortex != null)
            {
                var researchProp = ProposeResearchUpgrade(ctx, s, existingCortex);
                if (researchProp.HasValue) proposals.Add(researchProp.Value);
                return proposals;
            }

            // Cortex still under construction (or one being placed) — don't
            // propose ANOTHER placement. CountResearchStructures includes
            // in-progress ConstructionSites.
            if (CountResearchStructures(ctx.Team) > 0) return proposals;

            // Pick cheapest Research CD (typically only one per faction).
            ConstructionData cheapestCd = null;
            string cheapestName = null;
            int cheapestCost = int.MaxValue;
            foreach (var kv in _techCds)
            {
                int c = TryGetCost(kv.Value);
                if (c > 0 && c < cheapestCost)
                {
                    cheapestCost = c;
                    cheapestCd   = kv.Value;
                    cheapestName = kv.Key;
                }
            }
            if (cheapestCd == null) return proposals;

            // Cash gate — must afford AND leave eco reserve.
            if (ctx.Cash < cheapestCost + MIN_ECO_RESERVE) return proposals;

            // Payback gate REMOVED 2026-07-07 per user: "Cortex could be built
            // earlier". In Phase 2 base eco is stable, income is by definition
            // sufficient — waiting for the payback window let eco spend the
            // Cortex cash on Nodes/BCs that were only useful a few seconds
            // later anyway. Cash-gate + reservation upstream do the pacing.

            // Score / urgency.
            //   Score = fixed high value — a tier unlock dwarfs single BCs.
            //   Urgency = 1 once we've already teched once (fast consecutive);
            //             otherwise ramps 0..1 with cash surplus so we don't
            //             tech at the earliest possible moment.
            float score = 20000f;
            float urgency;
            if (s.WatermarkTier > 0)
                urgency = 1f;
            else
                urgency = ctx.Cap > 0
                    ? Mathf.Clamp01((ctx.Cash - MIN_ECO_RESERVE) / (float)ctx.Cap)
                    : 0.5f;

            // Placement target: near the starter Nest.
            Vector3 target = FindNestPosition(ctx.Team);
            if (target == default) return proposals;

            var cd = cheapestCd;
            var cost = cheapestCost;
            var name = cheapestName;
            proposals.Add(new Proposal
            {
                SourceId = SourceId,
                Kind     = "PlaceTech_" + name,
                Target   = target,
                Cost     = cost,
                Score    = score,
                Urgency  = urgency,
                Fire     = (team) => FireTechPlacement(team, cd, target),
            });
            return proposals;
        }

        /// <summary>
        /// Count of team's live Shrimps — direct income-rate signal. Each
        /// working shrimp is worth ~5 cash/s, so this reliably measures
        /// whether eco can absorb the Cortex cost + sustain tier upgrades.
        /// </summary>
        static int CountShrimps(Team team)
        {
            int n = 0;
            try
            {
                var units = team.Units;
                if (units != null)
                    for (int i = 0; i < units.Count; i++)
                    {
                        var u = units[i];
                        if (u?.ObjectInfo == null || u.IsDestroyed) continue;
                        if (string.Equals(u.ObjectInfo.DisplayName, "Shrimp", StringComparison.OrdinalIgnoreCase))
                            n++;
                    }
            }
            catch { }
            return n;
        }

        /// <summary>
        /// Count of Research-type buildings the team owns — counting BOTH already
        /// completed structures AND in-progress ConstructionSites. The completed
        /// list is empty during the 20+ seconds of Cortex construction, so
        /// without the in-progress check the broker would keep firing new
        /// Cortex placements every tick. That was the original spam bug.
        /// </summary>
        static int CountResearchStructures(Team team)
        {
            int n = 0;
            try
            {
                var structs = team.Structures;
                if (structs != null)
                    for (int i = 0; i < structs.Count; i++)
                    {
                        var st = structs[i];
                        if (st?.ObjectInfo == null || st.IsDestroyed) continue;
                        bool isResearch = false;
                        try { isResearch = st.ObjectInfo.StructureType == StructureType.Research; } catch { }
                        if (!isResearch) continue;
                        n++;
                        DumpResearchStructureOptionsOnce(st);
                    }
            }
            catch { }
            try
            {
                var sites = ConstructionSite.ConstructionSites;
                if (sites != null)
                    for (int i = 0; i < sites.Count; i++)
                    {
                        var cs = sites[i];
                        if (cs == null || cs.IsDestroyed) continue;
                        if (cs.Team != team) continue;
                        if (cs.ObjectInfo == null) continue;
                        bool isResearch = false;
                        try { isResearch = cs.ObjectInfo.StructureType == StructureType.Research; } catch { }
                        if (isResearch) n++;
                    }
            }
            catch { }
            return n;
        }

        static readonly HashSet<string> _dumpedResearchNames = new HashSet<string>();

        // Set once we've reflected out how a TechnologyTier list-entry maps to a
        // ConstructionData (the CD you Construct on the Cortex to research the
        // tier). The probe fills these when a Cortex is first seen.
        static System.Reflection.PropertyInfo _tierCdProp;
        static System.Reflection.FieldInfo    _tierCdField;
        static System.Reflection.PropertyInfo _tierLevelProp;
        static System.Reflection.FieldInfo    _tierLevelField;
        static void DumpResearchStructureOptionsOnce(Structure st)
        {
            try
            {
                string name = st?.ObjectInfo?.DisplayName ?? "?";
                if (_dumpedResearchNames.Contains(name)) return;
                _dumpedResearchNames.Add(name);
                MelonLogger.Msg($"[TECH/PROBE] Research structure '{name}':");

                // 1) ConstructionOptions (what it can build)
                try
                {
                    var opts = st.ConstructionOptions;
                    MelonLogger.Msg($"  ConstructionOptions: count={(opts == null ? "null" : opts.Count.ToString())}");
                    if (opts != null)
                        foreach (var opt in opts)
                        {
                            if (opt?.ObjectInfo == null) continue;
                            int cost = 0;
                            try { cost = opt.ResourceCost; } catch { }
                            string stype = "";
                            try { stype = opt.ObjectInfo.StructureType.ToString(); } catch { }
                            MelonLogger.Msg($"    - '{opt.ObjectInfo.DisplayName}' cost={cost} structureType={stype}");
                        }
                }
                catch (Exception ex) { MelonLogger.Warning("  ConstructionOptions threw: " + ex.Message); }

                // 1b) Dump the TechnologyTiers list — that's the research options this Cortex offers.
                try
                {
                    var t = st.GetType();
                    var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
                    var ttField = t.GetField("TechnologyTiers", flags);
                    if (ttField != null)
                    {
                        var list = ttField.GetValue(st);
                        MelonLogger.Msg("  TechnologyTiers list type: " + (list?.GetType().FullName ?? "null"));
                        if (list is System.Collections.IEnumerable en)
                        {
                            int idx = 0;
                            foreach (var item in en)
                            {
                                if (item == null) { MelonLogger.Msg($"  [{idx}] <null>"); idx++; continue; }
                                MelonLogger.Msg($"  [{idx}] type={item.GetType().Name}");
                                var itype = item.GetType();
                                foreach (var p in itype.GetProperties(flags))
                                {
                                    try { MelonLogger.Msg($"    prop {p.PropertyType.Name} {p.Name} = {p.GetValue(item)}"); }
                                    catch { MelonLogger.Msg($"    prop {p.PropertyType.Name} {p.Name} = <threw>"); }
                                }
                                foreach (var f in itype.GetFields(flags))
                                {
                                    try { MelonLogger.Msg($"    field {f.FieldType.Name} {f.Name} = {f.GetValue(item)}"); }
                                    catch { MelonLogger.Msg($"    field {f.FieldType.Name} {f.Name} = <threw>"); }
                                }
                                idx++;
                            }
                        }
                    }
                }
                catch (Exception ex) { MelonLogger.Warning("  TechnologyTiers dump threw: " + ex.Message); }

                // 2) Enumerate all public/instance properties + fields with names touching tier/tech/research/queue/production
                try
                {
                    var t = st.GetType();
                    MelonLogger.Msg($"  Runtime type: {t.FullName}");
                    var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
                    var interesting = new List<string>();
                    foreach (var p in t.GetProperties(flags))
                    {
                        string nm = p.Name;
                        if (nm.Contains("Tier") || nm.Contains("Tech") || nm.Contains("Research") ||
                            nm.Contains("Queue") || nm.Contains("Production") || nm.Contains("Progress") ||
                            nm.Contains("Order"))
                        {
                            try { interesting.Add($"    prop {p.PropertyType.Name} {nm} = {p.GetValue(st)}"); }
                            catch { interesting.Add($"    prop {p.PropertyType.Name} {nm} = <threw>"); }
                        }
                    }
                    foreach (var f in t.GetFields(flags))
                    {
                        string nm = f.Name;
                        if (nm.Contains("Tier") || nm.Contains("Tech") || nm.Contains("Research") ||
                            nm.Contains("Queue") || nm.Contains("Production") || nm.Contains("Progress") ||
                            nm.Contains("Order"))
                        {
                            try { interesting.Add($"    field {f.FieldType.Name} {nm} = {f.GetValue(st)}"); }
                            catch { interesting.Add($"    field {f.FieldType.Name} {nm} = <threw>"); }
                        }
                    }
                    MelonLogger.Msg($"  Tier/Tech/Research/Queue/Production/Progress/Order members ({interesting.Count}):");
                    foreach (var line in interesting) MelonLogger.Msg(line);

                    // 3) Also enumerate methods
                    var mnames = new List<string>();
                    foreach (var m in t.GetMethods(flags))
                    {
                        string nm = m.Name;
                        if (nm.Contains("Tier") || nm.Contains("Tech") || nm.Contains("Research") ||
                            nm.Contains("Queue") || nm.Contains("Production") || nm.Contains("Progress") ||
                            nm.Contains("Order") || nm.Contains("Upgrade"))
                        {
                            var pars = m.GetParameters();
                            string sig = pars.Length == 0 ? "()" :
                                "(" + string.Join(", ", System.Array.ConvertAll(pars, p => p.ParameterType.Name + " " + p.Name)) + ")";
                            mnames.Add($"    method {m.ReturnType.Name} {nm}{sig}");
                        }
                    }
                    MelonLogger.Msg($"  Tier/Tech/Research/Queue/Production/Progress/Order/Upgrade methods ({mnames.Count}):");
                    foreach (var line in mnames) MelonLogger.Msg(line);
                }
                catch (Exception ex) { MelonLogger.Warning("  reflection threw: " + ex.Message); }
            }
            catch (Exception ex) { MelonLogger.Warning("[TECH/PROBE] threw: " + ex.Message); }
        }

        /// <summary>
        /// Find the team's Research structure (completed) — Quantum Cortex on Alien,
        /// Research Facility on Human. Returns the FIRST one; per user rule "only
        /// need one Cortex".
        /// </summary>
        static Structure FindResearchStructure(Team team)
        {
            try
            {
                var structs = team.Structures;
                if (structs != null)
                    for (int i = 0; i < structs.Count; i++)
                    {
                        var st = structs[i];
                        if (st?.ObjectInfo == null || st.IsDestroyed) continue;
                        bool isResearch = false;
                        try { isResearch = st.ObjectInfo.StructureType == StructureType.Research; } catch { }
                        if (isResearch) return st;
                    }
            }
            catch { }
            return null;
        }

        /// <summary>
        /// Build the proposal that researches the next tier on an existing Cortex.
        /// Options list order: options[0]=Alpha I, options[1]=Beta II, ... so
        /// options[currentTier] is what raises tier from currentTier → currentTier+1.
        /// Skips if queue is already busy, cash short, or research already in progress.
        /// </summary>
        const string TECH_SOURCE_ID = "TechPlanner";
        static Proposal? ProposeResearchUpgrade(BrokerContext ctx, TechPublishedState s, Structure cortex)
        {
            try
            {
                var opts = cortex.ConstructionOptions;
                if (opts == null || opts.Count == 0) return null;
                int idx = s.CurrentTier;
                if (idx < 0 || idx >= opts.Count) return null;
                var researchCd = opts[idx];
                if (researchCd == null || researchCd.ObjectInfo == null) return null;

                // Already researching this tier? Skip.
                bool already = false;
                try { already = cortex.IsTechTierAlreadyBeingProduced(researchCd); } catch { }
                if (already) return null;

                int cost = TryGetCost(researchCd);
                if (cost <= 0) return null;

                // Cash gate — leave the small eco reserve.
                if (ctx.Cash < cost + MIN_ECO_RESERVE) return null;

                string name = researchCd.ObjectInfo.DisplayName ?? "Research";
                var pos = cortex.transform.position;
                var rot = cortex.transform.rotation;

                // Score / urgency: research is always highest-priority once available.
                // Fast consecutive teching per user's guidance.
                float score   = 20000f;
                float urgency = 1f;

                return new Proposal
                {
                    SourceId = TECH_SOURCE_ID,
                    Kind     = "Research_" + name,
                    Target   = pos,
                    Cost     = cost,
                    Score    = score,
                    Urgency  = urgency,
                    Fire     = (team) => FireResearchOnCortex(cortex, researchCd, pos, rot),
                };
            }
            catch (Exception ex) { MelonLogger.Warning("[TECH] ProposeResearchUpgrade threw: " + ex.Message); return null; }
        }

        static bool FireResearchOnCortex(Structure cortex, ConstructionData cd, Vector3 pos, Quaternion rot)
        {
            try
            {
                cortex.Construct(cd, pos, rot);
                MelonLogger.Msg($"[TECH] queued research '{cd.ObjectInfo?.DisplayName}' on Cortex — cost={cd.ResourceCost}");
                return true;
            }
            catch (Exception ex) { MelonLogger.Warning("[TECH] FireResearchOnCortex threw: " + ex.Message); return false; }
        }

        static Vector3 FindNestPosition(Team team)
        {
            try
            {
                var structs = team.Structures;
                if (structs != null)
                    for (int i = 0; i < structs.Count; i++)
                    {
                        var st = structs[i];
                        if (st?.ObjectInfo == null || st.IsDestroyed) continue;
                        // Alien Nest OR Human HQ — both are the natural anchor for tech placement.
                        var n = st.ObjectInfo.DisplayName;
                        if (n == "Nest" || n == "Headquarters") return st.transform.position;
                    }
            }
            catch { }
            return default;
        }

        static void EnsureCataloged(Team team)
        {
            if (team == null) return;
            string tn = team.name ?? "";
            if (_catalogedTeams.Contains(tn)) return;
            _catalogedTeams.Add(tn);
            CatalogTechCds(team);
        }

        /// <summary>
        /// Public helper for telemetry. Returns the current tech tier level and
        /// the max allowed tier. Uses team.TechnologyTier (the game's own int
        /// property, populated as Research structures complete).
        /// </summary>
        internal static (int tier, int maxTier) GetTierLevels(Team team)
        {
            if (team == null) return (0, 0);
            EnsureCataloged(team);
            int cur = 0, max = 0;
            try { cur = team.TechnologyTier; }         catch { }
            try { max = team.TechnologyTierLimitMax; } catch { }
            return (cur, max);
        }

        /// <summary>Names of Research structures the team currently has standing.</summary>
        internal static List<string> GetBuiltTechTiers(Team team)
        {
            var result = new List<string>();
            if (team == null) return result;
            EnsureCataloged(team);
            try
            {
                var structs = team.Structures;
                if (structs != null)
                    for (int i = 0; i < structs.Count; i++)
                    {
                        var st = structs[i];
                        if (st?.ObjectInfo == null || st.IsDestroyed) continue;
                        string n = st.ObjectInfo.DisplayName ?? "";
                        if (_techCds.ContainsKey(n) && !result.Contains(n)) result.Add(n);
                    }
            }
            catch { }
            return result;
        }

        static void CatalogTechCds(Team team)
        {
            try
            {
                var structs = team.Structures;
                if (structs == null) return;
                for (int i = 0; i < structs.Count; i++)
                {
                    var s = structs[i];
                    if (s?.ConstructionOptions == null) continue;
                    foreach (var opt in s.ConstructionOptions)
                    {
                        if (opt?.ObjectInfo == null) continue;
                        bool isResearch = false;
                        try { isResearch = opt.ObjectInfo.StructureType == StructureType.Research; }
                        catch { }
                        if (!isResearch) continue;
                        string n = opt.ObjectInfo.DisplayName ?? "";
                        if (string.IsNullOrEmpty(n)) continue;
                        if (!_techCds.ContainsKey(n))
                        {
                            _techCds[n] = opt;
                            MelonLogger.Msg("[TECH] cataloged Research CD: '" + n + "' cost=" + TryGetCost(opt) +
                                            " (team=" + team.name + ")");
                        }
                    }
                }
            }
            catch (Exception ex) { MelonLogger.Warning("[TECH] CatalogTechCds threw: " + ex.Message); }
        }

        static int TryGetCost(ConstructionData cd)
        {
            try { return cd.ResourceCost; } catch { return 0; }
        }

        // Cached method info for team.UpdateTechnologyTier(bool, bool) — the same
        // API Si_TechGlitch calls to force a server-side tier reconciliation. If
        // the (2-arg-bool) overload isn't found we log once and give up.
        static System.Reflection.MethodInfo _updateTierMi;
        static bool _updateTierResolved;
        static void TryForceSyncTier(Team team)
        {
            try
            {
                if (!_updateTierResolved)
                {
                    _updateTierResolved = true;
                    var t = typeof(Team);
                    foreach (var m in t.GetMethods(System.Reflection.BindingFlags.Instance |
                                                   System.Reflection.BindingFlags.Public |
                                                   System.Reflection.BindingFlags.NonPublic))
                    {
                        if (m.Name != "UpdateTechnologyTier") continue;
                        var ps = m.GetParameters();
                        if (ps.Length == 2 && ps[0].ParameterType == typeof(bool) && ps[1].ParameterType == typeof(bool))
                        {
                            _updateTierMi = m;
                            MelonLogger.Msg("[TECH] resolved Team.UpdateTechnologyTier(bool,bool)");
                            break;
                        }
                    }
                    if (_updateTierMi == null)
                    {
                        // Fallback: any UpdateTechnologyTier overload
                        foreach (var m in t.GetMethods(System.Reflection.BindingFlags.Instance |
                                                       System.Reflection.BindingFlags.Public |
                                                       System.Reflection.BindingFlags.NonPublic))
                        {
                            if (m.Name == "UpdateTechnologyTier")
                            {
                                _updateTierMi = m;
                                var ps = m.GetParameters();
                                MelonLogger.Msg("[TECH] resolved Team.UpdateTechnologyTier (fallback overload params=" + ps.Length + ")");
                                break;
                            }
                        }
                    }
                    if (_updateTierMi == null) MelonLogger.Warning("[TECH] no UpdateTechnologyTier overload found");
                }
                if (_updateTierMi == null) return;
                var pars = _updateTierMi.GetParameters();
                var args = new object[pars.Length];
                for (int i = 0; i < pars.Length; i++)
                {
                    if (pars[i].ParameterType == typeof(bool)) args[i] = i == pars.Length - 1;   // last = true
                    else args[i] = System.Type.Missing;
                }
                _updateTierMi.Invoke(team, args);
            }
            catch (Exception ex) { MelonLogger.Warning("[TECH] TryForceSyncTier threw: " + ex.Message); }
        }

        static bool FireTechPlacement(Team team, ConstructionData cd, Vector3 target)
        {
            return Faction.AlienConstruction.TryBuildStructureByCd(team, cd, target);
        }

        internal static void ResetForNewRound()
        {
            _techCds.Clear();
            _catalogedTeams.Clear();
            _dumpedResearchNames.Clear();
            _lastReserveLogAt = 0f;
            CloseCsv();
        }
    }

    internal class TechPublishedState
    {
        public List<string> KnownTiers = new List<string>();
        public int CurrentTier;
        public int MaxTier;
        public int WatermarkTier;
    }
}
