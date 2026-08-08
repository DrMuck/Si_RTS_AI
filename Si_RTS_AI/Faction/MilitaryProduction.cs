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
        static readonly Dictionary<string, Vector3> _pendingSite =
            new Dictionary<string, Vector3>(StringComparer.OrdinalIgnoreCase);
        static readonly Dictionary<string, int> _pendingCount =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

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
            _pendingSite.Clear();
            _pendingCount.Clear();
            _pickLogged.Clear();
            _typeBusy.Clear();
            _typeSaturatedSince.Clear();
            _typeWant.Clear();
            _gateLogged.Clear();
            _leftoverBudget = 0;
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
                Perception.Utilisation.NoteProducers(_busyThisPass, _totalThisPass);
                UpdateProducerDemand(now, team);
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

            // Reset the observation for this pass, per type. A type with no
            // producers at all is not "busy" — it is absent, and absence is
            // handled by the first-of-each rule rather than by saturation.
            _typeBusy.Clear();
            foreach (var kvp in producersByType) _typeBusy[kvp.Key] = true;
            _busyThisPass = 0;
            _totalThisPass = 0;

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
                    _totalThisPass++;
                    if (queueDepth >= QUEUE_DEPTH) { _busyThisPass++; continue; }
                    // A free slot here is NOT yet evidence of slack. We are the
                    // thing that fills it, and we are looking at it in the
                    // instant before we do — so a producer running flat out
                    // shows a free slot on every single pass.
                    //
                    // That is why "asking for another" never fired: one Greater
                    // Spawning Cyst, pinned on 45-second Behemoths, read as
                    // not-saturated forever because our own tick caught it
                    // between units. Slack means the slot was free AND WE COULD
                    // NOT USE IT — see below, where a failed queue marks it.

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
                    {
                        // Free slot we could not fill: genuine slack. Money, tech
                        // or options are the constraint, not building count.
                        _typeBusy[kv.Key] = false;
                        LogFailureOnce(kv.Key, $"[MIL/PROD] {kv.Key} queued nothing: {reasons}");
                    }
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
            // A FORWARD SITE IS A REASON BY ITSELF.
            //
            // Producer count is a THROUGHPUT question and the FOB is a POSITION
            // question, and answering the second with the first is why DrMuck
            // kept seeing no FOB: the blueprint planned one for minutes at a
            // time — "Forward (1036,-786) ... on our network front, covered by
            // our own force" — and nothing built it, because home production was
            // not saturated enough to justify a second building.
            //
            // But we do not want a second Greater Cyst there for its output. We
            // want units to appear 3,000m closer to the enemy. That is worth one
            // building whether or not the first one is busy.
            var plan = Planning.MilitaryBlueprint.NextSite(team);
            bool forwardWanted =
                plan != null &&
                plan.Purpose == Planning.MilitaryBlueprint.Purpose.Forward &&
                !NearAnyExisting(team, plan.Pos, 350f);

            foreach (var name in HigherTierProducerNames)
            {
                if (!_producerStructureCds.TryGetValue(name, out var cd)) continue;
                int want = WantedProducerCount(name);
                // One extra allowance, for the forward site only, and only up to
                // the configured cap.
                if (forwardWanted)
                    want = Mathf.Min(want + 1, Planning.MilitaryConfig.MaxProducersPerType);
                have.TryGetValue(name, out int c);

                // Did the last request for this type actually land?
                if (_pendingSite.TryGetValue(name, out var lastAt) &&
                    _requestedAt.TryGetValue(name, out float orderedAt) &&
                    now - orderedAt >= REQUEST_TTL_S)
                {
                    _pendingCount.TryGetValue(name, out int before);
                    if (c <= before)
                        MelonLogger.Warning(
                            $"[MIL/PROD] {name} was ordered at ({lastAt.x:F0},{lastAt.z:F0}) " +
                            $"{REQUEST_TTL_S:F0}s ago and never appeared — the ground is " +
                            "probably out of build range of our network.");
                    _pendingSite.Remove(name);
                    _pendingCount.Remove(name);
                }

                if (c >= want) continue;
                // Already asked for, and the request has not aged out.
                if (_requestedAt.TryGetValue(name, out float askedAt) &&
                    now - askedAt < REQUEST_TTL_S) continue;
                int cost = SafeCost(cd);
                if (cost > budget) return;
                var site = ProducerSite(team);
                Vector3 at = site.Pos == Vector3.zero ? nestPos : site.Pos;
                bool fired = false;
                try { fired = AlienConstruction.TryBuildStructureByCd(team, cd, at); }
                catch (Exception ex) { MelonLogger.Warning("[MIL/PROD] TryBuildStructureByCd threw: " + ex.Message); }
                _lastStructurePlacementAt = now;   // rate-limit whether it fired or not
                if (fired)
                {
                    budget -= cost;
                    SpentThisRound += cost;
                    _requestedAt[name] = now;
                    // A request that never becomes a building is worth saying
                    // out loud. Fifteen identical "placed" lines and no producer
                    // read as success in the log and were a silent refusal.
                    _pendingSite[name] = at;
                    _pendingCount[name] = c;
                    MelonLogger.Msg($"[MIL/PROD] placed {name} #{c + 1}/{want} at " +
                                    $"({at.x:F0},{at.z:F0}) — {site.Purpose}: {site.Why}");
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
        /// <summary>
        /// SATURATION IS A QUESTION PER PRODUCER TYPE, not per army.
        ///
        /// The first version asked "was EVERY producer busy", and the answer was
        /// no, forever: a round runs fifteen Lesser Spawning Cysts turning out
        /// 220-cash Shockers in seconds, so at any instant one of them has a free
        /// slot. Meanwhile the single Greater Spawning Cyst was pinned for
        /// forty-five seconds at a time building Behemoths and never got a
        /// second building — 42 Behemoths against 144 Shockers, and the log line
        /// "every producer busy" fired zero times across a whole round.
        ///
        /// The question is always "are the GREATER cysts saturated", and the
        /// Lesser cysts have no vote in it.
        /// </summary>
        static readonly Dictionary<string, bool> _typeBusy =
            new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        static readonly Dictionary<string, float> _typeSaturatedSince =
            new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        static readonly Dictionary<string, int> _typeWant =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        static int  _leftoverBudget;
        static int  _busyThisPass, _totalThisPass;

        /// <summary>How long the constraint must hold before it is believed. A
        /// producer takes most of a minute to build and then wants paying, so a
        /// momentary pile-up is not evidence of anything.</summary>
        const float SATURATED_FOR_S = 45f;

        /// <summary>
        /// WHAT THE PRODUCERS WE ALREADY HAVE EAT, PER SECOND.
        ///
        /// A producer that never idles converts one unit per build time, so its
        /// draw is cost/buildTime — a Behemoth at 1,200 over 45s is about 27/s.
        /// Summed over every military producer, that is what the economy must
        /// sustain just to keep the current buildings busy.
        ///
        /// Read from live ConstructionData rather than a table, so a balance
        /// change moves it, and taken from the most expensive thing the producer
        /// offers because that is the upper bound on what it can consume.
        /// </summary>
        static float ProducerDraw(Team team, string forType, out float perNewProducer)
        {
            perNewProducer = 0f;
            float total = 0f;
            try
            {
                var structs = team.Structures;
                if (structs == null) return 0f;
                for (int i = 0; i < structs.Count; i++)
                {
                    var st = structs[i];
                    if (st?.ObjectInfo == null || st.IsDestroyed) continue;
                    string tn = st.ObjectInfo.DisplayName ?? "";
                    if (!ProducerStructureNames.Contains(tn)) continue;
                    // ONLY WHAT THE MILITARY IS ACTUALLY FEEDING. The first
                    // version summed every Lesser Spawning Cyst on the map and
                    // reported a draw of 333/s — but fifteen of those belong to
                    // the economy and are making shrimps. Counting the
                    // economy's producers against the military's budget shut the
                    // gate on its own.
                    if (IsLesser(tn) && !IsClaimed(st)) continue;
                    bool functional = false;
                    try { functional = st.IsFunctional; } catch { }
                    if (!functional) continue;

                    float draw = DrawOf(st);
                    total += draw;
                    if (string.Equals(tn, forType, StringComparison.OrdinalIgnoreCase)
                        && draw > perNewProducer)
                        perNewProducer = draw;
                }
            }
            catch { }
            return total;
        }

        /// <summary>Is a producer of ours already standing near this ground?</summary>
        static bool NearAnyExisting(Team team, Vector3 pos, float radiusM)
        {
            float r2 = radiusM * radiusM;
            try
            {
                var structs = team.Structures;
                if (structs == null) return false;
                for (int i = 0; i < structs.Count; i++)
                {
                    var st = structs[i];
                    if (st?.ObjectInfo == null || st.IsDestroyed) continue;
                    string n = st.ObjectInfo.DisplayName ?? "";
                    if (!ProducerStructureNames.Contains(n)) continue;
                    Vector3 p = st.transform.position;
                    float dx = p.x - pos.x, dz = p.z - pos.z;
                    if (dx * dx + dz * dz <= r2) return true;
                }
            }
            catch { }
            return false;
        }

        static bool IsLesser(string typeName) =>
            string.Equals(typeName, "Lesser Spawning Cyst", StringComparison.OrdinalIgnoreCase);

        /// <summary>Cash per second one producer consumes if it never idles.</summary>
        static float DrawOf(Structure s)
        {
            try
            {
                if (s.ConstructionOptions == null) return 0f;
                float best = 0f;
                foreach (var opt in s.ConstructionOptions)
                {
                    if (opt?.ObjectInfo == null) continue;
                    if (WorkerUnitNames.Contains(opt.ObjectInfo.DisplayName ?? "")) continue;
                    int cost = SafeCost(opt);
                    float bt = 0f;
                    try { bt = opt.TotalConstructionTime; } catch { }
                    if (cost <= 0 || bt <= 0.01f) continue;
                    float d = cost / bt;
                    if (d > best) best = d;
                }
                return best;
            }
            catch { return 0f; }
        }

        /// <summary>How far ahead income is projected: roughly how long a
        /// producer takes to become useful, its build plus its first unit.
        /// Ordering against income that has not arrived yet is the entire point
        /// — DrMuck: "usually eco starts to bump up around 4-5 min". Ordering
        /// against income that never will is the failure mode, which is why the
        /// trend is clamped at zero before it is used.</summary>
        const float PRODUCER_LEAD_S = 60f;

        /// <summary>Seconds of the NEW producer's appetite the bank must cover
        /// for banked cash alone to justify it. Not a schedule — a ratio between
        /// money we are holding and the rate the thing would eat it. Two minutes
        /// is long enough that the building is not stranded and short enough
        /// that a large bank always converts.</summary>
        const float BANK_COVERS_S = 120f;

        static readonly Dictionary<string, float> _gateLogged =
            new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Say so when the ECONOMY is what is holding production back,
        /// so "still just one cyst" never again costs a round of guessing.</summary>
        static void LogIncomeGateOnce(string name, float income, float projected,
                                      float draw, float newDraw)
        {
            float now = Time.time;
            if (_gateLogged.TryGetValue(name, out float at) && now - at < 60f) return;
            _gateLogged[name] = now;
            MelonLogger.Msg($"[MIL/PROD] {name} is saturated but cannot be fed: " +
                            $"income {income:F0}/s ({projected:F0}/s projected) against " +
                            $"{draw:F0}/s committed and {newDraw:F0}/s more needed, " +
                            $"and the bank holds {_leftoverBudget} " +
                            $"({newDraw * BANK_COVERS_S:F0} would do it).");
        }

        /// <summary>
        /// HOW MANY PRODUCERS THE MONEY CAN ACTUALLY SUPPORT — computed, not
        /// crept toward.
        ///
        /// DrMuck, seeing one forward Cyst appear: "we have so much more money to
        /// spend that could support probably 10 cyst blasting behemoth." He is
        /// right, and the old rule could not have got there: it added ONE
        /// producer per 45 seconds of continuous saturation, so reaching ten
        /// would have taken seven and a half minutes of everything going
        /// perfectly, and the cap stopped it at six regardless.
        ///
        /// Neither number described the money. The money describes itself:
        ///
        ///     sustainable = income/s / drawPerProducer
        ///     from the bank = bank / (drawPerProducer x BANK_COVERS_S)
        ///
        /// The first is what we can feed forever. The second is how many MORE
        /// the money already sitting there could run for a couple of minutes —
        /// which is the right way to spend a bank, because a bank is a one-off
        /// and a producer built from it converts dead cash into army before the
        /// cash stops existing.
        ///
        /// Still gated on saturation, and that gate matters more now than when
        /// the growth was incremental: a large bank would otherwise buy ten
        /// buildings that stand idle. If what we have is not busy, more will not
        /// help, whatever the bank says.
        /// </summary>
        static void UpdateProducerDemand(float now, Team team)
        {
            int cap = Planning.MilitaryConfig.MaxProducersPerType;
            if (cap <= 1) return;

            float income = 0f, trend = 0f;
            try
            {
                income = Perception.BcIncome.EarnedPerSec();
                trend  = Mathf.Max(0f, Perception.EcoRateSampler.GetIncomeTrend(team));
            }
            catch { }
            float projected = income + trend * PRODUCER_LEAD_S;

            foreach (var name in HigherTierProducerNames)
            {
                _typeBusy.TryGetValue(name, out bool busy);

                float draw = ProducerDraw(team, name, out float newDraw);
                if (newDraw <= 0f) newDraw = draw > 0f ? draw : 25f;

                // What the economy could keep busy, from flow and from stock.
                int fromRate = Mathf.FloorToInt(projected / newDraw);
                int fromBank = Mathf.FloorToInt(_leftoverBudget / (newDraw * BANK_COVERS_S));
                int affordable = Mathf.Clamp(fromRate + fromBank, 1, cap);

                _typeWant.TryGetValue(name, out int want);
                if (want < 1) want = 1;

                // Growing needs BOTH the money and the evidence that the current
                // buildings cannot absorb it. Shrinking needs neither — we never
                // demolish, so want only ratchets up and the cap is the brake.
                if (affordable > want && busy && _leftoverBudget >= 4000)
                {
                    if (!_typeSaturatedSince.TryGetValue(name, out float since))
                    { _typeSaturatedSince[name] = now; continue; }
                    if (now - since < SATURATED_FOR_S) continue;

                    _typeSaturatedSince[name] = now;
                    _typeWant[name] = affordable;
                    MelonLogger.Msg($"[MIL/PROD] every {name} busy and {_leftoverBudget} unspent — " +
                                    $"income {income:F0}/s supports {fromRate}, the bank another " +
                                    $"{fromBank} at {newDraw:F0}/s each — asking for " +
                                    $"{affordable} (cap {cap})");
                }
                else if (!busy)
                {
                    _typeSaturatedSince.Remove(name);
                }
                else if (affordable <= want && busy && _leftoverBudget >= 4000)
                {
                    LogIncomeGateOnce(name, income, projected, draw, newDraw);
                    _typeSaturatedSince.Remove(name);
                }
            }
        }

        static int WantedProducerCount(string producerName)
        {
            _typeWant.TryGetValue(producerName, out int w);
            return Mathf.Clamp(w < 1 ? 1 : w, 1, Mathf.Max(1, Planning.MilitaryConfig.MaxProducersPerType));
        }

        /// <summary>Where the next producer goes, and what it is for. The
        /// decision belongs to MilitaryBlueprint — this only asks.</summary>
        static Planning.MilitaryBlueprint.Site ProducerSite(Team team) =>
            Planning.MilitaryBlueprint.NextSite(team);

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
