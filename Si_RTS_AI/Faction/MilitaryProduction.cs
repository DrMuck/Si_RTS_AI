using MelonLoader;
using Silica;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Si_RTS_AI.Faction
{
    /// <summary>
    /// AN ARMY HAS TO BE BUILT BEFORE IT CAN BE COMMANDED.
    ///
    /// This is the production half of the old MilitaryManager, which also picked
    /// targets and coordinated an army and so held its own copy of decisions
    /// DefencePlanner and BattalionManager were making differently. That overlap
    /// is why MilitaryEnabled stayed false for a month. The target-picking and
    /// army-coordination halves are gone — MissionPlanner owns intent, the
    /// battalion manager owns units, and this owns spending.
    ///
    /// TWO THINGS IT DECIDES, and both are one rule each:
    ///
    ///   WHEN there is money for an army. Not a budget share, which would be a
    ///   tuned constant standing in for a decision. The economy gets everything
    ///   while it is behind its worker trajectory AND still converting cash into
    ///   workers that earn; once it is on track, or once yield is falling and the
    ///   answer is ground rather than workers, holding cash back buys nothing.
    ///   Measured on 2026-08-07: rounds sit on 100-200k unspent from minute
    ///   twelve while expansion is limited by placement and not by cash.
    ///
    ///   WHAT to build. The most expensive thing each producer offers, which is
    ///   the game's own statement about tier. No unit list in our source: a
    ///   balance mod rewrites these and a hardcoded name could not know.
    ///
    /// The corollary from MILITARY_TACTICS §3 is the reason this exists at all
    /// and not later: cash cannot be converted to army on demand. The steamroll
    /// army has to be produced DURING the hold, not after the decision to push.
    /// </summary>
    internal static class MilitaryProduction
    {
        const float TICK_S = 3f;
        const float LOG_S  = 30f;

        static float _lastTickAt, _lastLogAt;

        /// <summary>Queue depth per producer. Shallow on purpose — a deep queue
        /// is cash committed to units the situation has not asked for yet, and
        /// the producer refills within a tick of finishing anyway.</summary>
        const int QUEUE_DEPTH = 1;

        // Workers, excluded from "combat unit" by name because they are the only
        // two the alien tree offers that are not one.
        static readonly HashSet<string> WorkerUnitNames =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Shrimp", "Queen" };

        // Producer structures we queue units at.
        static readonly HashSet<string> ProducerStructureNames =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "Lesser Spawning Cyst",
                "Greater Spawning Cyst",
                "Colossus Cyst",
                "Grand Spawner",
            };

        /// <summary>Higher-tier producers nobody else builds — EcoPlanner handles
        /// Bio Cache / Cyst / Node, TechPlanner handles Cortex. In tier order.</summary>
        static readonly string[] HigherTierProducerNames =
        {
            "Greater Spawning Cyst",
            "Colossus Cyst",
            "Grand Spawner",
        };

        const float STRUCTURE_PLACEMENT_CADENCE_S = 15f;

        internal static int QueuedThisRound;
        internal static int SpentThisRound;

        /// <summary>
        /// Lesser Cysts this layer has taken off the economy.
        ///
        /// A Lesser Cyst is the ONLY producer both sides want, and the shrimp
        /// producer keeps every one of them at queue depth two — so a military
        /// share expressed only on our side would have quietly done nothing at
        /// all, every tick, forever, while looking configured. One owner per
        /// producer: the economy skips what is claimed here.
        ///
        /// Empty whenever the layer is off or the economy is still converting,
        /// which is the normal state for the first ten minutes of a round.
        /// </summary>
        static readonly HashSet<Structure> _claimed = new HashSet<Structure>();

        internal static bool IsClaimed(Structure s) =>
            s != null && _claimed.Count > 0 && _claimed.Contains(s);

        static bool _catalogLogged;
        static readonly HashSet<string> _failuresLogged = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        static readonly Dictionary<string, ConstructionData> _producerStructureCds
            = new Dictionary<string, ConstructionData>(StringComparer.OrdinalIgnoreCase);
        static float _lastStructurePlacementAt;

        /// <summary>
        /// Producer structures we have ASKED for and not yet seen appear.
        ///
        /// The existing count reads built structures plus ConstructionSites, and
        /// a placement request becomes neither of those immediately — so on
        /// NarakaCity 2026-08-07 the same Greater Spawning Cyst was ordered at
        /// t+108s, t+125s and t+140s, three times, for 9,000 cash. The opener's
        /// entire planned budget was 9,000. One Greater Cyst was ever built; the
        /// economy never recovered and finished the round on 18 Bio Caches
        /// against a soak baseline of 26.
        ///
        /// So remember what we asked for, and stop asking. The entry expires so a
        /// request the game silently dropped cannot block the slot forever.
        /// </summary>
        static readonly Dictionary<string, float> _requestedAt =
            new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);

        /// <summary>How long a placement request is believed before we conclude
        /// it never happened. Comfortably longer than the time for a request to
        /// turn into a ConstructionSite, short enough that a lost order costs one
        /// window rather than the round.</summary>
        const float REQUEST_TTL_S = 60f;

        internal static void ResetForNewRound()
        {
            _lastTickAt = _lastLogAt = _lastStructurePlacementAt = 0f;
            QueuedThisRound = SpentThisRound = 0;
            _catalogLogged = false;
            _claimed.Clear();
            _requestedAt.Clear();
            _pickLogged.Clear();
            _allProducersBusy = false;
            _leftoverBudget = 0;
            _saturatedSince = -1f;
            _wantProducers = 1;
            _failuresLogged.Clear();
            _producerStructureCds.Clear();
        }

        internal static void Tick(Team team)
        {
            if (!Planning.MilitaryConfig.Enabled || !Planning.MilitaryConfig.Produce || team == null)
            {
                // Hand every Cyst back the moment we stop wanting them, or the
                // economy keeps skipping producers nobody is using.
                _claimed.Clear();
                return;
            }
            if (!TestHarnessNs.TestHarness.IsRoundActive) return;

            float now = Time.time;
            if (now - _lastTickAt < TICK_S) return;
            _lastTickAt = now;

            try
            {
                _claimed.Clear();
                int budget = Planning.MissionPlanner.SpendableCash(team);
                if (budget > 0)
                {
                    PlaceProducerStructures(team, now, ref budget);
                    QueueUnits(team, ref budget);
                }
                _leftoverBudget = budget;
                UpdateProducerDemand(now);
                MaybeLog(now, team, budget);
            }
            catch (Exception ex) { MelonLogger.Warning("[MIL/PROD] tick threw: " + ex.Message); }
        }

        // ================================================================
        // Units
        // ================================================================

        static void QueueUnits(Team team, ref int budget)
        {
            var structs = team.Structures;
            if (structs == null) return;

            var producersByType = new Dictionary<string, List<Structure>>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < structs.Count; i++)
            {
                var st = structs[i];
                if (st?.ObjectInfo == null || st.IsDestroyed) continue;
                string typeName = st.ObjectInfo.DisplayName ?? "";
                if (!ProducerStructureNames.Contains(typeName)) continue;
                bool functional = false;
                try { functional = st.IsFunctional; } catch { }
                if (!functional) continue;
                if (!producersByType.TryGetValue(typeName, out var list))
                {
                    list = new List<Structure>();
                    producersByType[typeName] = list;
                }
                list.Add(st);
            }
            if (producersByType.Count == 0) return;

            LogCatalogOnce(producersByType);

            // Reset the observation for this pass; the loop below sets it false
            // the moment any producer had a free slot we could have used.
            _allProducersBusy = true;

            foreach (var kv in producersByType)
            {
                var producers = kv.Value;
                IEnumerable<Structure> targetSet;

                if (string.Equals(kv.Key, "Lesser Spawning Cyst", StringComparison.OrdinalIgnoreCase))
                {
                    // Lesser Cysts are the economy's producers too, and combat
                    // units there cost SHRIMP THROUGHPUT, not just cash. So the
                    // share is gated on the same signal as the money: while the
                    // economy is behind and still converting, this is zero and
                    // every Lesser Cyst keeps making workers.
                    float share = EcoStillConverting() ? 0f : Planning.MilitaryConfig.LesserCystShare;
                    if (share <= 0f) continue;
                    int n = Mathf.Max(1, Mathf.RoundToInt(producers.Count * share));
                    int step = Mathf.Max(1, producers.Count / n);
                    // A deterministic stride so the SAME Cysts stay dedicated —
                    // a rotating set would leave half-built units everywhere.
                    var picked = new List<Structure>();
                    for (int i = 0; i < producers.Count; i += step) picked.Add(producers[i]);
                    for (int i = 0; i < picked.Count; i++) _claimed.Add(picked[i]);
                    targetSet = picked;
                }
                else
                {
                    targetSet = producers;   // higher tiers offer no worker anyway
                }

                foreach (var s in targetSet)
                {
                    if (budget <= 0) return;
                    int queueDepth = 0;
                    try { queueDepth = s.ProductionQueue?.Count ?? 0; } catch { }
                    if (queueDepth >= QUEUE_DEPTH) continue;
                    // A free slot exists, so throughput is not what is stopping
                    // us — whatever happens next, this pass does not count as
                    // saturated.
                    _allProducersBusy = false;

                    var opts = new List<ConstructionData>();
                    if (s.ConstructionOptions != null)
                        foreach (var opt in s.ConstructionOptions)
                        {
                            if (opt?.ObjectInfo == null) continue;
                            if (WorkerUnitNames.Contains(opt.ObjectInfo.DisplayName ?? "")) continue;
                            opts.Add(opt);
                        }
                    if (opts.Count == 0)
                    {
                        LogFailureOnce(kv.Key + ":empty",
                                       $"[MIL/PROD] {kv.Key} offers no combat unit");
                        continue;
                    }

                    // WHAT THE ARCHIVE SAYS IS WORTH A CAP SLOT, not what costs
                    // the most.
                    //
                    // Cost was standing in for tier, and tier is not usefulness:
                    // at a Lesser Spawning Cyst the most expensive option is the
                    // Dragonfly, which trades at 0.20 under commander AI, and the
                    // first played round duly bought twenty-six of them. The
                    // ordering now comes from 2,619 recorded games, scored per
                    // cap point and weighted by what we can currently SEE — see
                    // Planning.UnitPrior.
                    //
                    // Falls back to the old rule with no file present, because a
                    // missing prior must not stop the bot building an army.
                    var mix = Planning.UnitPrior.Loaded
                        ? Perception.ThreatMap.EnemyMix : null;
                    if (Planning.UnitPrior.Loaded)
                    {
                        opts.Sort((a, b) =>
                        {
                            string why;
                            float sa = Planning.UnitPrior.Score(a.ObjectInfo?.DisplayName, mix, out why);
                            float sb = Planning.UnitPrior.Score(b.ObjectInfo?.DisplayName, mix, out why);
                            int c = sb.CompareTo(sa);
                            // Ties broken by price, so a tie between two
                            // unmeasured options still prefers the better tier.
                            return c != 0 ? c : SafeCost(b).CompareTo(SafeCost(a));
                        });
                        LogPickOnce(kv.Key, opts, mix);
                    }
                    else opts.Sort((a, b) => SafeCost(b).CompareTo(SafeCost(a)));
                    var reasons = new System.Text.StringBuilder();
                    bool queued = false;
                    foreach (var opt in opts)
                    {
                        int cost = SafeCost(opt);
                        if (cost > budget) { reasons.Append(opt.ObjectInfo?.DisplayName).Append("=overBudget "); continue; }
                        ProductionActionResult res;
                        try { res = s.Construct(opt); }
                        catch (Exception ex)
                        {
                            reasons.Append(opt.ObjectInfo?.DisplayName).Append("=EX(").Append(ex.Message).Append(") ");
                            continue;
                        }
                        if (res == ProductionActionResult.Success)
                        {
                            budget -= cost;
                            SpentThisRound += cost;
                            QueuedThisRound++;
                            queued = true;
                            break;
                        }
                        reasons.Append(opt.ObjectInfo?.DisplayName).Append('=').Append(res).Append(' ');
                    }
                    if (!queued)
                        LogFailureOnce(kv.Key, $"[MIL/PROD] {kv.Key} queued nothing: {reasons}");
                }
            }
        }

        // ================================================================
        // Higher-tier producer structures
        // ================================================================

        static void PlaceProducerStructures(Team team, float now, ref int budget)
        {
            if (now - _lastStructurePlacementAt < STRUCTURE_PLACEMENT_CADENCE_S) return;

            try
            {
                var structs = team.Structures;
                if (structs != null)
                    for (int i = 0; i < structs.Count; i++)
                    {
                        var st = structs[i];
                        if (st?.ConstructionOptions == null) continue;
                        foreach (var opt in st.ConstructionOptions)
                        {
                            if (opt?.ObjectInfo == null) continue;
                            string n = opt.ObjectInfo.DisplayName ?? "";
                            if (_producerStructureCds.ContainsKey(n)) continue;
                            for (int hi = 0; hi < HigherTierProducerNames.Length; hi++)
                                if (HigherTierProducerNames[hi] == n) { _producerStructureCds[n] = opt; break; }
                        }
                    }
            }
            catch { }
            if (_producerStructureCds.Count == 0) return;

            Vector3 nestPos = FindNestPos(team);
            if (nestPos == Vector3.zero) return;

            var have = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var built = team.Structures;
                if (built != null)
                    for (int i = 0; i < built.Count; i++)
                    {
                        var st = built[i];
                        if (st?.ObjectInfo == null || st.IsDestroyed) continue;
                        string n = st.ObjectInfo.DisplayName ?? "";
                        if (_producerStructureCds.ContainsKey(n))
                            have[n] = have.TryGetValue(n, out int v) ? v + 1 : 1;
                    }
                var sites = ConstructionSite.ConstructionSites;
                if (sites != null)
                    for (int i = 0; i < sites.Count; i++)
                    {
                        var cs = sites[i];
                        if (cs == null || cs.IsDestroyed || cs.ObjectInfo == null) continue;
                        if (cs.Team != team) continue;
                        string n = cs.ObjectInfo.DisplayName ?? "";
                        if (_producerStructureCds.ContainsKey(n))
                            have[n] = have.TryGetValue(n, out int v) ? v + 1 : 1;
                    }
            }
            catch { }

            // HOW MANY, NOT WHETHER. One of each was the rule, and it made a
            // single Greater Spawning Cyst the ceiling on the entire army:
            // measured on 2026-08-08, 41 Behemoths against 255 Shockers across a
            // forty-minute round, not because the bot preferred Shockers but
            // because one producer cannot make more Behemoths than that.
            //
            // The count follows the same logic WorkerPlan uses for Cysts — how
            // much throughput does the money we cannot spend justify. One
            // producer converts roughly its unit cost every build time; if cash
            // is piling up faster than the producers can consume it, the answer
            // is another producer, not a deeper queue.
            int want = WantedProducerCount();
            foreach (var name in HigherTierProducerNames)
            {
                if (!_producerStructureCds.TryGetValue(name, out var cd)) continue;
                have.TryGetValue(name, out int c);
                if (c >= want) continue;
                // Already asked for, and the request has not aged out.
                if (_requestedAt.TryGetValue(name, out float askedAt) &&
                    now - askedAt < REQUEST_TTL_S) continue;
                int cost = SafeCost(cd);
                if (cost > budget) return;
                Vector3 at = ProducerAnchor(team, nestPos);
                bool fired = false;
                try { fired = AlienConstruction.TryBuildStructureByCd(team, cd, at); }
                catch (Exception ex) { MelonLogger.Warning("[MIL/PROD] TryBuildStructureByCd threw: " + ex.Message); }
                _lastStructurePlacementAt = now;   // rate-limit whether it fired or not
                if (fired)
                {
                    budget -= cost;
                    SpentThisRound += cost;
                    _requestedAt[name] = now;
                    MelonLogger.Msg($"[MIL/PROD] placed {name} #{c + 1}/{want} at " +
                                    $"({at.x:F0},{at.z:F0})" +
                                    (at == nestPos ? " (Nest — nothing worth covering yet)"
                                                   : " (toward the ground we are defending)"));
                }
                return;
            }
        }

        /// <summary>
        /// How many of each higher-tier producer the unspent money justifies.
        ///
        /// A producer is worth building when cash is arriving faster than the
        /// producers we have can turn it into units. That is the same question
        /// the economy asks about Cysts, and it has the same answer: count the
        /// throughput, not the buildings.
        ///
        /// Deliberately crude and deliberately capped. It is a rate comparison,
        /// not a plan — MILITARY_PRODUCTION_DESIGN describes the blueprint this
        /// should eventually become, siting producers against the frontier
        /// rather than counting them against the bank.
        /// </summary>
        /// <summary>
        /// ARE THE PRODUCERS WE HAVE ABLE TO ABSORB THE MONEY WE HAVE?
        ///
        /// That is the whole rule, and it is the only one here that cannot rot.
        /// It asks nothing about the clock, nothing about a target army size and
        /// nothing about a curve fitted to other people's games — DrMuck, on
        /// being shown one: "we dont need to rotate around hard coded numbers,
        /// this is only an indication."
        ///
        /// It is also exactly the condition MILITARY_TACTICS §3 names as the
        /// production ceiling: "rising unspent cash while producers saturated".
        /// Observed directly rather than inferred from a derivative — after a
        /// pass we know whether every producer was already busy AND whether
        /// money was still left over. Both true means throughput is the
        /// constraint and another producer is the answer. Either false means the
        /// constraint is somewhere else and a new building would idle.
        ///
        /// The count only ever moves by one, and only after the condition has
        /// held for a while, because a producer ordered on one busy tick is a
        /// producer paid for out of the army.
        /// </summary>
        static bool _allProducersBusy;
        static int  _leftoverBudget;
        static float _saturatedSince = -1f;
        static int  _wantProducers = 1;

        /// <summary>How long the constraint must hold before it is believed. A
        /// producer takes most of a minute to build and then wants paying, so a
        /// momentary pile-up is not evidence of anything.</summary>
        const float SATURATED_FOR_S = 45f;

        static void UpdateProducerDemand(float now)
        {
            int cap = Planning.MilitaryConfig.MaxProducersPerType;
            if (cap <= 1) { _wantProducers = Mathf.Max(1, cap); return; }

            // Leftover has to be worth a producer, or we would add one to soak
            // up pocket change.
            bool throughputBound = _allProducersBusy && _leftoverBudget >= 4000;
            if (!throughputBound) { _saturatedSince = -1f; return; }

            if (_saturatedSince < 0f) { _saturatedSince = now; return; }
            if (now - _saturatedSince < SATURATED_FOR_S) return;

            _saturatedSince = now;              // restart the clock for the next one
            if (_wantProducers < cap)
            {
                _wantProducers++;
                MelonLogger.Msg($"[MIL/PROD] every producer busy with {_leftoverBudget} " +
                                $"still unspent for {SATURATED_FOR_S:F0}s — " +
                                $"asking for {_wantProducers} of each (cap {cap})");
            }
        }

        static int WantedProducerCount() => Mathf.Max(1, _wantProducers);

        /// <summary>
        /// WHERE a producer goes, which decides how long its units walk before
        /// they matter.
        ///
        /// DrMuck: "Military cant apply pressure from the distance ... it could
        /// be more efficient to place producers more to the front lines or FOBs
        /// to save delay in walking distance."
        ///
        /// He is right and the old rule was the worst case: everything at the
        /// Nest, so every unit walked the full radius of the base before
        /// reaching anything. This is the cheap version of the fix — anchor on
        /// the highest-scoring thing DefencePlanner is already worried about,
        /// which is by construction ground that earns and is threatened. A real
        /// FOB siting pass belongs in the blueprint, with the frontier and the
        /// enemy approach as inputs; this only stops us building at the back.
        ///
        /// Falls back to the Nest when nothing is under threat, because a
        /// producer parked at a random expansion in peacetime is worse than one
        /// at home.
        /// </summary>
        static Vector3 ProducerAnchor(Team team, Vector3 nestPos)
        {
            try
            {
                var tasks = Planning.DefencePlanner.Tasks;
                for (int i = 0; i < tasks.Count; i++)
                {
                    if (tasks[i].Kind == "home") continue;
                    // Pull back toward the Nest so it is behind the line it
                    // covers rather than on top of it.
                    return Vector3.Lerp(tasks[i].Pos, nestPos, 0.35f);
                }
            }
            catch { }
            return nestPos;
        }

        // ================================================================
        // Helpers
        // ================================================================

        /// <summary>Is another shrimp still the best use of the next cash?
        /// Defined once, in WorkerPlan, and read rather than re-derived.</summary>
        internal static bool EcoStillConverting() => Planning.WorkerPlan.CanStillConvertCash;

        static int SafeCost(ConstructionData cd)
        {
            try { return cd.ResourceCost; } catch { return 0; }
        }

        static Vector3 FindNestPos(Team team)
        {
            try
            {
                var structs = team.Structures;
                if (structs != null)
                    for (int i = 0; i < structs.Count; i++)
                    {
                        var st = structs[i];
                        if (st?.ObjectInfo == null || st.IsDestroyed) continue;
                        if (st.ObjectInfo.DisplayName == "Nest") return st.transform.position;
                    }
            }
            catch { }
            return Vector3.zero;
        }

        static void LogCatalogOnce(Dictionary<string, List<Structure>> producersByType)
        {
            if (_catalogLogged) return;
            _catalogLogged = true;
            var sb = new System.Text.StringBuilder("[MIL/PROD] producers: ");
            foreach (var kv in producersByType)
                sb.Append(kv.Key).Append('×').Append(kv.Value.Count).Append(' ');
            foreach (var kv in producersByType)
            {
                var sample = kv.Value[0];
                if (sample.ConstructionOptions == null) continue;
                sb.Append("| ").Append(kv.Key).Append(" offers ");
                foreach (var opt in sample.ConstructionOptions)
                {
                    if (opt?.ObjectInfo == null) continue;
                    sb.Append(opt.ObjectInfo.DisplayName).Append('(').Append(SafeCost(opt)).Append(") ");
                }
            }
            MelonLogger.Msg(sb.ToString());
        }

        /// <summary>
        /// The ranking, once per producer type per enemy mix, so a round shows
        /// WHY it built what it built. Re-logged when the mix changes class, not
        /// on every tick — the point is to catch the bot preferring something
        /// indefensible, and that is visible at the moment the ordering changes.
        /// </summary>
        static readonly Dictionary<string, string> _pickLogged =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        static void LogPickOnce(string producer, List<ConstructionData> ordered,
                                IDictionary<string, float> mix)
        {
            string mixKey = Perception.ThreatMap.EnemyMixSummary();
            if (_pickLogged.TryGetValue(producer, out var had) && had == mixKey) return;
            _pickLogged[producer] = mixKey;

            var sb = new System.Text.StringBuilder("[MIL/PROD] ").Append(producer)
                .Append(" ranking vs [").Append(mixKey).Append("]: ");
            for (int i = 0; i < ordered.Count && i < 4; i++)
            {
                string name = ordered[i].ObjectInfo?.DisplayName ?? "?";
                string why;
                Planning.UnitPrior.Score(name, mix, out why);
                sb.Append(i + 1).Append('.').Append(name).Append(" (").Append(why).Append(") ");
            }
            MelonLogger.Msg(sb.ToString());
        }

        static void LogFailureOnce(string key, string message)
        {
            if (!_failuresLogged.Add(key)) return;
            MelonLogger.Msg(message);
        }

        static void MaybeLog(float now, Team team, int budgetLeft)
        {
            if (now - _lastLogAt < LOG_S) return;
            _lastLogAt = now;
            if (QueuedThisRound == 0 && budgetLeft == 0) return;
            int cash = 0;
            try { cash = team.TotalResources; } catch { }
            MelonLogger.Msg($"[MIL/PROD] queued={QueuedThisRound} spent={SpentThisRound} " +
                            $"cash={cash} budget={budgetLeft} " +
                            $"ecoFirst={(EcoStillConverting() ? "yes (workers behind and still earning)" : "no")}");
        }

        internal static string BuildRoundSummaryFragment()
        {
            if (QueuedThisRound == 0) return "";
            return "--- Military production ---\n" +
                   $"  units queued: {QueuedThisRound}, cash spent: {SpentThisRound}\n";
        }
    }
}
