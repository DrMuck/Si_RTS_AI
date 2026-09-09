using MelonLoader;
using Silica;
using Si_RTS_AI.Perception;
using Si_RTS_AI.Planning;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Si_RTS_AI.Mil
{
    /// <summary>
    /// WHAT TO BUILD, HOW MANY PRODUCERS, AND WHERE — FROM DEMAND, NOT A SCORE.
    ///
    /// COMPOSITION IS A QUEUE WITH PROPORTIONS. Demand comes from the portfolio:
    /// every objective's shortfall by pool class, plus what the reserve is
    /// building toward (the cheapest offensive objective it cannot yet afford,
    /// or the price to beat the enemy estimate). At each producer the next unit
    /// maximises demandGap[pool] x valuePerCapSlot x counterFit, and because the
    /// gap shrinks as units are queued the result is a mix, not a winner — which
    /// is the whole difference from the argmax that built 255 Shockers beside
    /// 41 Behemoths.
    ///
    /// HOW MANY PRODUCERS: two evidence rules, nothing else. (a) every producer
    /// of a type busy while cash idles for two minutes of income; (b) demand a
    /// type serves cannot be delivered inside the raze budget by the producers
    /// it has. Both capped by cap room read from Team.UnitCapEntries. The count
    /// ratchets, so a lost producer is rebuilt.
    ///
    /// WHERE: every functional structure of ours is a candidate; each is scored
    /// by the walk-time from it to the fights the portfolio expects, discounted
    /// by danger. The best candidate toward the main offensive objective is the
    /// FORWARD BASE and takes up to fobProducers; the reserve stands there and
    /// the next push starts there (DrMuck: "Fob towards enemy base needs more
    /// producers and behes there").
    ///
    /// MONEY: the opener is untouchable and the economy has right of way when a
    /// placement was refused for cash. Within that, spires with an arrival under
    /// 90s first, then units for funded objectives, then producers, then units
    /// for the reserve.
    /// </summary>
    internal static class ProductionV3
    {
        const float TICK_S = 3f;
        const float LOG_S  = 30f;
        const float PLACE_CADENCE_S = 15f;
        const float SATURATED_FOR_S = 45f;
        const float DELIVERY_BUDGET_S = 240f;
        const float REQUEST_TTL_S = 60f;
        const float FOB_RADIUS_M = 400f;
        const float SITE_REFRESH_S = 20f;

        static HashSet<string> WorkerNames =
            // NOT "Harvester". This set is also consulted on the queue path, and the
            // Ultra Heavy Factory is the ONLY structure that offers a Harvester -
            // adding it here would remove the sole way a human team can ever replace
            // one. Harvesters are kept out of the ARMY in Forces instead.
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Shrimp", "Queen" };

        static HashSet<Structure> _claimed = new HashSet<Structure>();
        internal static bool IsClaimed(Structure s) => s != null && _claimed.Count > 0 && _claimed.Contains(s);

        // producer types we can build, discovered from options
        static Dictionary<string, ConstructionData> _producerCds =
            new Dictionary<string, ConstructionData>(StringComparer.OrdinalIgnoreCase);
        static Dictionary<string, int>   _want = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        static Dictionary<string, float> _saturatedSince = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        static Dictionary<string, bool>  _typeBusy = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        static Dictionary<string, float> _requestedAt = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        static Dictionary<string, string> _pickLogged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        static HashSet<string> _offerLogged = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        static float _lastTickAt, _lastLogAt, _lastPlaceAt, _lastSiteAt;
        static int _busy, _total;
        internal static int QueuedThisRound, SpentThisRound, ProducersPlaced;
        /// <summary>Producers standing (finished) this tick, and how many have a full queue.</summary>
        internal static int ProducerCount => _total;
        internal static int BusyProducerCount => _busy;

        // demand by pool, in effective cash
        static float[] _demand = new float[4];   // Fast, Swarm, Line, Heavy
        static float _demandAny;

        internal static Vector3 ForwardBase { get; private set; }
        internal static string  ForwardWhy { get; private set; } = "";
        static Vector3 _bestSite; static string _bestSiteWhy = "";

        internal static void ResetForNewRound()
        {
            _claimed.Clear(); _producerCds.Clear(); _want.Clear(); _saturatedSince.Clear();
            _discoveryLoggedAt.Clear(); _discoveryHealthy.Clear();
            _typeBusy.Clear(); _requestedAt.Clear(); _pickLogged.Clear(); _offerLogged.Clear();
            CompositionTarget.ResetForNewRound();
            _lastTickAt = _lastLogAt = _lastPlaceAt = _lastSiteAt = 0f;
            _busy = _total = 0; QueuedThisRound = SpentThisRound = ProducersPlaced = 0;
            Array.Clear(_demand, 0, _demand.Length); _demandAny = 0f;
            ForwardBase = Vector3.zero; ForwardWhy = ""; _bestSite = Vector3.zero; _bestSiteWhy = "";
        }

        internal static void Tick(Team team)
        {
            if (!MilConfig.Enabled || team == null) { _claimed.Clear(); return; }
            if (!TestHarnessNs.TestHarness.IsRoundActive) return;
            // A PLAYER IN THE ALIEN COMMANDER SEAT COMMANDS. The game disables its
            // own AI commander then, and so does this layer, exactly as vanilla
            // would: it must never spend a player's cash or move a player's units.
            try { if (!Silica.AI.AIManager.IsCommanderEnabled(team)) return; } catch { }
            float now = Time.time;
            if (now - _lastTickAt < TICK_S) return;
            _lastTickAt = now;
            try
            {
                if (now - _lastSiteAt >= SITE_REFRESH_S) { _lastSiteAt = now; PlanSites(team); }
                if (!MilConfig.Produce) { _claimed.Clear(); return; }
                _claimed.Clear();
                ComputeDemand();
                int budget = SpendableCash(team);
                if (budget > 0)
                {
                    PlaceProducers(team, now, ref budget);
                    QueueUnits(team, ref budget);
                }
                Utilisation.NoteProducers(_busy, _total);
                UpdateWanted(team, now, budget);
                if (now - _lastLogAt >= LOG_S) { _lastLogAt = now; Log(team, budget); }
            }
            catch (Exception ex) { MelonLogger.Warning("[MIL/PROD] tick threw: " + ex.Message); }
        }

        // ---- money -----------------------------------------------------------

        /// <summary>Cash the military may consider its own. Two categorical rules
        /// and one measured one, unchanged from v2 because they were right.</summary>
        internal static int SpendableCash(Team team)
        {
            // UNDER SIEGE THE OPENER YIELDS TOO. The Maw, 2026-09-07 05:10: siege
            // declared at nineteen seconds, but the opener's queue held the
            // military at zero budget until minute five, the first producer
            // came at minute seven, and the Nest fell at fourteen. An opening
            // is worth nothing if the Nest falls before it earns.
            bool siege = false;
            try { siege = Objectives.UnderSiege; } catch { }
            if (!siege)
            {
                try { if (OpenerPlanner.QueueActive) return 0; } catch { }
                try { if (EcoPlanner.EcoStarvedOfCash) return 0; } catch { }
            }
            int cash = 0;
            try { cash = team.TotalResources; } catch { }
            // THE ECONOMY KEEPS ITS NEXT PLACEMENT. The first v3 round spent
            // 229k on the army by minute twenty with cash pinned at 600-4000
            // and the economy cash-blocked 22% of the round: the 45s starvation
            // window only holds the military off AFTER a placement is refused.
            // So a floor of three of the dearest eco action (a Lesser Cyst)
            // stays in the bank at all times, and the full worker reserve while
            // the economy is still converting cash into workers that earn.
            int reserve = WorkerPlan.CanStillConvertCash ? MilitaryConfig.EcoReserve : MilConfig.EcoFloorCash;
            try { reserve += MoneyBroker.GetReservedCash(team); } catch { }
            if (Objectives.UnderSiege) reserve = Mathf.Min(reserve, 1500);
            return Mathf.Max(0, cash - reserve);
        }

        // ---- demand ----------------------------------------------------------

        static void ComputeDemand()
        {
            Array.Clear(_demand, 0, _demand.Length); _demandAny = 0f;
            var portfolio = Objectives.Portfolio;
            for (int i = 0; i < portfolio.Count; i++)
            {
                var o = portfolio[i];
                if (o.Status == Objectives.Status.Done || o.Status == Objectives.Status.Failed) continue;
                if (o.Kind == Objectives.Kind.Recon) continue;
                // Offensive objectives do not each add their full price: the
                // reserve target below already carries the cheapest one, and
                // summing seven unaffordable raids asked for 200,000 eff of
                // production in the first round.
                if (o.Offensive && o.Status != Objectives.Status.Active) continue;
                float gap = o.RequiredEff - o.AssignedEff;
                if (o.Kind == Objectives.Kind.DefendQueen) gap = o.RequiredEff - Forces.ReserveEff;
                if (gap <= 0f) continue;
                switch (o.PoolPref)
                {
                    case Objectives.Pool.Fast: _demand[(int)Forces.Pool.Fast] += gap; break;
                    case Objectives.Pool.Line: _demand[(int)Forces.Pool.Line] += gap; break;
                    default: _demandAny += gap; break;
                }
            }
            // The reserve builds toward the next affordable offensive.
            float target = Mathf.Max(Objectives.NextOffensivePrice,
                                     Kernel.PriceToBeat(Intel.EnemyEffective),
                                     MilConfig.HomeFloorCash);
            float reserveGap = target - Forces.ReserveEff;
            // NO SITTING ON CASH. A human team with nothing threatened and no target
            // yet had zero demand and 28,000 idle at minute six (DrMuck, 2026-09-07
            // 22:10). Cash above the reinvest floor becomes line demand.
            if (Faction.Construction.IsHuman(Intel.Self))
            {
                int cashNow = 0; try { cashNow = Intel.Self.TotalResources; } catch { }
                if (cashNow > REINVEST_FLOOR) reserveGap = Mathf.Max(reserveGap, cashNow - REINVEST_FLOOR);
            }
            if (reserveGap > 0f)
            {
                // HEAVIES GET A SHARE FROM MINUTE EIGHT. Nothing ever asked for the
                // heavy pool, so Scorpions and Colossi were only ever built by
                // accident of the any-pool. DrMuck: "no Scorps and Colossus were
                // built at all, despite they can be very valuable."
                float heavy = 0f;
                try { heavy = Forces.RoundSeconds() >= 480f ? 0.25f : 0f; } catch { }
                _demand[(int)Forces.Pool.Line]  += reserveGap * (0.75f - heavy);
                _demand[(int)Forces.Pool.Fast]  += reserveGap * 0.25f;
                _demand[(int)Forces.Pool.Heavy] += reserveGap * heavy;
            }
        }

        const int REINVEST_FLOOR = 8000;
        static float GapFor(Forces.Pool p) => _demand[(int)p] + _demandAny;
        static float _airCheckedAt = -999f; static bool _airSeen;
        static bool EnemyAirSeen()
        {
            float now = Time.time;
            if (now - _airCheckedAt < 10f) return _airSeen;
            _airCheckedAt = now; _airSeen = false;
            try
            {
                var mix = ThreatMap.EnemyMix;
                if (mix != null)
                    foreach (var kv in mix)
                        if (kv.Value > 0f && UnitStats.IsFlyer(kv.Key)) { _airSeen = true; break; }
            }
            catch { }
            return _airSeen;
        }

        static Forces.Pool PoolOfName(string n)
        {
            if (UnitStats.IsFlyer(n) || UnitStats.SpeedOf(n) >= 20f) return Forces.Pool.Fast;
            string cap = UnitStats.CapTypeOf(n);
            int cost = 0; try { cost = UnitValues.CostOf(n); } catch { }
            if (cap == "Secondary") return Forces.Pool.Swarm;
            return cost >= 3000 ? Forces.Pool.Heavy : Forces.Pool.Line;
        }

        /// <summary>Counter fit against what we can see, from the measured prior;
        /// 1.0 when we see nothing or the unit is unmeasured.</summary>
        static float Fit(string name)
        {
            try
            {
                var mix = ThreatMap.EnemyMix;
                if (mix == null || mix.Count == 0 || !UnitPrior.Loaded) return 1f;
                UnitPrior.Score(name, mix, out _);
                // UnitPrior.Score folds effectiveness and cap in; recover the fit
                // term by dividing out the mix-free score.
                float withMix = UnitPrior.Score(name, mix, out _);
                float noMix = UnitPrior.Score(name, null, out _);
                return noMix > 0f ? Mathf.Clamp(withMix / noMix, 0.3f, 3f) : 1f;
            }
            catch { return 1f; }
        }

        // ---- units -------------------------------------------------------------

        static void QueueUnits(Team team, ref int budget)
        {
            var structs = team.Structures;
            if (structs == null) return;
            CompositionTarget.UseTeam(team);
            _busy = _total = 0;
            foreach (var k in new List<string>(_typeBusy.Keys)) _typeBusy[k] = true;

            float idleMin = 0f;
            try { idleMin = Utilisation.CashIdleMinutes; } catch { }
            int depth = idleMin >= 6f ? 3 : idleMin >= 2f ? 2 : 1;
            bool ecoConverting = WorkerPlan.CanStillConvertCash;
            float lesserShare = ecoConverting ? 0f : MilitaryConfig.LesserCystShare;
            int lesserSeen = 0;

            for (int i = 0; i < structs.Count; i++)
            {
                if (budget <= 0) return;
                var s = structs[i];
                if (s?.ObjectInfo == null || s.IsDestroyed) continue;
                bool functional = false;
                try { functional = s.IsFunctional; } catch { }
                if (!functional) continue;
                var opts = s.ConstructionOptions;
                if (opts == null) continue;
                string tn0 = s.ObjectInfo.DisplayName ?? "?";
                if (!_offerLogged.Contains(tn0))
                {
                    _offerLogged.Add(tn0);
                    try
                    {
                        var names = new System.Text.StringBuilder();
                        var other = new System.Text.StringBuilder();
                        foreach (var o2 in opts)
                        {
                            if (o2?.ObjectInfo == null) continue;
                            bool iu = false; try { iu = o2.IsUnit; } catch { }
                            bool it = false; try { it = o2.IsTechTier; } catch { }
                            if (iu) names.Append(o2.ObjectInfo.DisplayName).Append(' ');
                            else other.Append(o2.ObjectInfo.DisplayName).Append(it ? "(tier) " : " ");
                        }
                        MilLog.Msg($"[MIL/PROD] {tn0} offers: {names}| other: {other}");
                    }
                    catch { }
                }
                bool offersCombat = false;
                foreach (var opt in opts)
                {
                    if (opt?.ObjectInfo == null) continue;
                    bool isUnit = false; try { isUnit = opt.IsUnit; } catch { }
                    if (!isUnit) continue;
                    if (WorkerNames.Contains(opt.ObjectInfo.DisplayName ?? "")) continue;
                    offersCombat = true; break;
                }
                if (!offersCombat) continue;

                string typeName = s.ObjectInfo.DisplayName ?? "";
                bool lesser = typeName.StartsWith("Lesser", StringComparison.OrdinalIgnoreCase);
                if (lesser)
                {
                    // A deterministic stride so the same Cysts stay dedicated.
                    lesserSeen++;
                    if (lesserShare <= 0f) continue;
                    int stride = Mathf.Max(1, Mathf.RoundToInt(1f / lesserShare));
                    if (lesserSeen % stride != 1 && stride != 1) continue;
                    _claimed.Add(s);
                }
                if (!_typeBusy.ContainsKey(typeName)) _typeBusy[typeName] = true;

                int queued = 0;
                try { queued = s.ProductionQueue?.Count ?? 0; } catch { }
                _total++;
                // QUEUE DEPTH. Two per producer, three only when cash has idled six
                // minutes, and never more than one at an Ultra Heavy Factory or a
                // Colossal Spawning Cyst (DrMuck, 2026-09-08: "same rule as aliens").
                int depthHere = typeName.IndexOf("Ultra", StringComparison.OrdinalIgnoreCase) >= 0
                             || typeName.IndexOf("Colossal", StringComparison.OrdinalIgnoreCase) >= 0 ? 1 : depth;
                if (queued >= depthHere) { _busy++; continue; }

                // Pick.
                ConstructionData best = null; float bestScore = 0f; string bestWhy = "";
                foreach (var opt in opts)
                {
                    if (opt?.ObjectInfo == null) continue;
                    bool isUnit = false; try { isUnit = opt.IsUnit; } catch { }
                    if (!isUnit) continue;
                    string n = opt.ObjectInfo.DisplayName ?? "";
                    if (WorkerNames.Contains(n)) continue;
                    // SQUIDS ONLY AGAINST AIR. DrMuck: "a lot of squids are built, too
                    // many; only usable against air or single soldiers."
                    if (n == "Squid" && !EnemyAirSeen()) continue;
                    int cost = 0; try { cost = opt.ResourceCost; } catch { }
                    if (cost <= 0 || cost > budget) continue;
                    if (!CapRoom(team, opt.ObjectInfo)) continue;
                    var pool = PoolOfName(n);
                    float gap = GapFor(pool);
                    // Swarm units are only worth a slot when nothing else is
                    // wanted or as cheap mass for an objective that asked broadly.
                    if (pool == Forces.Pool.Swarm) gap = _demandAny * 0.5f;
                    if (gap <= 0f && idleMin < 3f) continue;
                    int capW = Mathf.Max(1, UnitStats.CapWeightOf(n));
                    try { capW = Mathf.Max(1, opt.ObjectInfo.UnitCapValue); } catch { }
                    // PER CAP SLOT ONLY WHEN THE CAP IS WHAT BINDS. Early, cash is the
                    // constraint and a slot is free, so a unit is worth its fitted
                    // value per cash; pricing per slot then bought Dragonflies
                    // (0.76 of their price, but expensive) for raids. Once the cap
                    // is more than half used, the slot is the scarce thing.
                    float valuePerCap = CapTight(team, opt.ObjectInfo)
                        ? Doctrine.ValueOf(n) * cost / capW
                        : Doctrine.ValueOf(n) * 100f;
                    float fit = Fit(n);
                    float comp = CompositionTarget.Multiplier(n, cost);
                    float score = Mathf.Max(gap, 200f) * valuePerCap * fit * comp;
                    if (score > bestScore) { bestScore = score; best = opt; bestWhy = $"{n}: gap {gap:F0} x {valuePerCap:F0}/cap x fit {fit:F2} x mix {comp:F2}"; }
                }
                if (best == null) { _typeBusy[typeName] = false; continue; }

                ProductionActionResult res;
                try { res = s.Construct(best); }
                catch (Exception ex) { MilLog.Once("prod:" + typeName + ":ex", $"[MIL/PROD] {typeName} Construct threw: {ex.Message}"); continue; }
                if (res == ProductionActionResult.Success)
                {
                    int cost = 0; try { cost = best.ResourceCost; } catch { }
                    budget -= cost; SpentThisRound += cost; QueuedThisRound++;
                    string n = best.ObjectInfo.DisplayName ?? "";
                    var pool = PoolOfName(n);
                    float eff = Kernel.EffectiveOf(n);
                    if (_demand[(int)pool] > 0f) _demand[(int)pool] = Mathf.Max(0f, _demand[(int)pool] - eff);
                    else _demandAny = Mathf.Max(0f, _demandAny - eff);
                    CompositionTarget.NoteQueued(n);
                    LogPick(typeName, bestWhy);
                }
                else
                {
                    _typeBusy[typeName] = false;
                    MilLog.Every("prod:" + typeName + ":" + res, 60f, $"[MIL/PROD] {typeName} could not queue {best.ObjectInfo.DisplayName}: {res}");
                }
            }
        }

        static bool CapTight(Team team, ObjectInfo oi)
        {
            try
            {
                var entry = team.GetUnitTypeCapEntryForUnit(oi);
                if (entry == null || entry.Max <= 0) return false;
                return entry.Current > entry.Max * 0.5f;
            }
            catch { return false; }
        }

        static bool CapRoom(Team team, ObjectInfo oi)
        {
            try
            {
                var entry = team.GetUnitTypeCapEntryForUnit(oi);
                if (entry == null) return true;
                int max = entry.Max;
                if (max <= 0) return true;
                return entry.Current + oi.UnitCapValue <= max;
            }
            catch { return true; }
        }

        static void LogPick(string producer, string why)
        {
            string key = producer;
            string mix = ""; try { mix = ThreatMap.EnemyMixSummary(); } catch { }
            string val = why.Split(':')[0] + "|" + mix;
            if (_pickLogged.TryGetValue(key, out var had) && had == val) return;
            _pickLogged[key] = val;
            MilLog.Msg($"[MIL/PROD] {producer} -> {why} | demand fast {_demand[0]:F0} swarm {_demand[1]:F0} line {_demand[2]:F0} heavy {_demand[3]:F0} any {_demandAny:F0} | seen [{mix}]");
        }

        // ---- producers: how many ----------------------------------------------

        static void UpdateWanted(Team team, float now, int budgetLeft)
        {
            DiscoverProducerCds(team);
            float idleMin = 0f; try { idleMin = Utilisation.CashIdleMinutes; } catch { }
            foreach (var name in new List<string>(_producerCds.Keys))
            {
                int have = CountOf(team, name, includeSites: true);
                _want.TryGetValue(name, out int want);
                if (want < 1) want = 1;
                int cap = Mathf.Max(1, MilitaryConfig.MaxProducersPerType);
                // A PRODUCER THE CAP CANNOT FEED IS A BUILDING THAT IDLES. Round
                // two built 35 producers and ran them 35% busy: the unit cap was
                // full and the demand rule kept asking. Room for two more of the
                // dearest unit this type makes, per producer wanted, or no more.
                int room = CapRoomFor(team, name);
                if (room >= 0 && room < 2 * (have + 1)) { _want[name] = Mathf.Min(want, Mathf.Max(1, have)); continue; }

                // (a) saturated with idle cash
                _typeBusy.TryGetValue(name, out bool busy);
                bool sat = busy && have > 0 && budgetLeft >= 4000 && idleMin >= 2f;
                if (sat)
                {
                    if (!_saturatedSince.TryGetValue(name, out float since)) _saturatedSince[name] = now;
                    else if (now - since >= SATURATED_FOR_S && want < cap && want <= have)
                    {
                        want++; _saturatedSince[name] = now;
                        MilLog.Msg($"[MIL/PROD] every {name} busy, {budgetLeft} idle ({idleMin:F1} min of income) — want {want}");
                    }
                }
                else _saturatedSince.Remove(name);

                // (b) demand the type serves cannot be delivered in time
                float servesEff = 0f, deliverPerS = 0f;
                if (_producerCds.TryGetValue(name, out var cd))
                {
                    // What this producer type makes, and how fast one of them converts.
                    var sample = FindOne(team, name);
                    float bestDraw = 0f; var pools = new HashSet<Forces.Pool>();
                    var opts = sample?.ConstructionOptions;
                    if (opts != null)
                        foreach (var opt in opts)
                        {
                            if (opt?.ObjectInfo == null) continue;
                            bool isUnit = false; try { isUnit = opt.IsUnit; } catch { }
                            if (!isUnit) continue;
                            string n = opt.ObjectInfo.DisplayName ?? "";
                            if (WorkerNames.Contains(n)) continue;
                            pools.Add(PoolOfName(n));
                            float bt = 0f; try { bt = opt.TotalConstructionTime; } catch { }
                            if (bt > 0.5f) bestDraw = Mathf.Max(bestDraw, Kernel.EffectiveOf(n) / bt);
                        }
                    foreach (var p in pools) servesEff += _demand[(int)p];
                    servesEff += _demandAny;
                    deliverPerS = bestDraw * have;
                }
                // Rule (b) needs a FUNCTIONAL producer to measure against: one that
                // is still a construction site delivers nothing yet, and asking
                // for a second on that evidence buys two buildings for one need.
                bool measurable = FindOne(team, name) != null && deliverPerS > 0f;
                if (measurable && servesEff > 0f && deliverPerS * DELIVERY_BUDGET_S < servesEff && budgetLeft >= 4000 && want < cap && want <= have)
                {
                    want++;
                    MilLog.Msg($"[MIL/PROD] {name}: demand {servesEff:F0} eff, {have} producers deliver {deliverPerS * DELIVERY_BUDGET_S:F0} in {DELIVERY_BUDGET_S:F0}s — want {want}");
                }
                _want[name] = want;
            }
        }

        /// <summary>Record a structure option as a producer type if it makes combat units.</summary>
        static void ConsiderProducer(ConstructionData opt)
        {
            if (opt?.ObjectInfo == null) return;
            bool isStruct = false; try { isStruct = opt.IsStructure; } catch { }
            if (!isStruct) return;
            string n = opt.ObjectInfo.DisplayName ?? "";
            if (_producerCds.ContainsKey(n)) return;
            if (n.StartsWith("Lesser", StringComparison.OrdinalIgnoreCase)) return;   // the economy's
            // A producer is a structure whose own options include a combat unit.
            if (!ProducesCombat(opt)) return;
            _producerCds[n] = opt;
            MilLog.Msg($"[MIL/PROD] producer type available: {n} (cost {opt.ResourceCost})");
        }

        static readonly Dictionary<int, float> _discoveryLoggedAt = new Dictionary<int, float>();
        static readonly HashSet<int> _discoveryHealthy = new HashSet<int>();

        static void DiscoverProducerCds(Team team)
        {
            try
            {
                // THE HANDLER'S LIST FIRST. Scanning the options of structures we
                // already own finds the alien's cysts off the Nest, but found
                // nothing at all for Sol and Centauri: _producerCds stayed empty,
                // UpdateWanted iterated nothing, no producer was ever wanted, and
                // the starter Barracks remained the only one - which is why
                // Centauri built only Juggernauts and both human teams held 80-90k
                // cash with "producers busy 1/1" (RiftBasin, 2026-09-08 23:00).
                // HumanConstruction caches what the game itself says the team can
                // build; that list has the factories in it.
                var buildable = Faction.HumanConstruction.BuildableStructures(team);
                if (buildable != null)
                    for (int i = 0; i < buildable.Count; i++)
                        ConsiderProducer(buildable[i]);

                // WHY A TEAM FINDS NOTHING. Sol discovered all five producer types
                // and Centauri, with the same buildings, discovered none at all
                // (NarakaCity 2026-09-08 23:37) - so say what each team was
                // actually offered, once, rather than inferring it from silence.
                int did = team.GetInstanceID();
                float dnow = Time.time;
                _discoveryLoggedAt.TryGetValue(did, out float dlast);
                // Quiet once the team knows its producers; keeps repeating only while
                // something is wrong, which is when this line is worth reading. A
                // faction that discovers nothing is the shape of the MilContext
                // array-blanking bug (see the Array.Copy note in MilContext.Fresh).
                bool healthy = _producerCds.Count > 0;
                if (dlast <= 0f || (!healthy && dnow - dlast >= 60f) || (healthy && dlast > 0f && !_discoveryHealthy.Contains(did)))
                {
                    if (healthy) _discoveryHealthy.Add(did);
                    _discoveryLoggedAt[did] = dnow;
                    int structOpts = 0;
                    try
                    {
                        var st = team.Structures;
                        if (st != null) for (int i = 0; i < st.Count; i++) structOpts += st[i]?.ConstructionOptions?.Count ?? 0;
                    }
                    catch { }
                    MilLog.Msg($"[MIL/PROD] discovery for {team.name}: buildable={(buildable == null ? "null" : buildable.Count.ToString())} " +
                               $"structureOptions={structOpts} known={_producerCds.Count} ctx={Mil.MilContext.Current?.name ?? "none"}");
                    // Centauri saw the same thirteen options as Sol and took none of
                    // them (NarakaCity 2026-09-09 00:12). Say what each one was and
                    // which test threw it out.
                    if (_producerCds.Count == 0 && buildable != null)
                        for (int i = 0; i < buildable.Count; i++)
                        {
                            var o = buildable[i];
                            string nm = "?"; bool st = false;
                            try { nm = o?.ObjectInfo?.DisplayName ?? "<null ObjectInfo>"; } catch { }
                            try { st = o != null && o.IsStructure; } catch { }
                            bool pc = false; try { pc = o != null && ProducesCombat(o); } catch { }
                            MilLog.Msg($"[MIL/PROD]   option {i}: '{nm}' isStructure={st} producesCombat={pc}");
                        }
                }

                var structs = team.Structures;
                if (structs == null) return;
                for (int i = 0; i < structs.Count; i++)
                {
                    var opts = structs[i]?.ConstructionOptions;
                    if (opts == null) continue;
                    foreach (var opt in opts)
                    {
                        ConsiderProducer(opt);
                    }
                }
            }
            catch { }
        }

        /// <summary>Does this structure prefab produce combat units? Read off the
        /// balance dump's production tree via UnitStats: a Greater Cyst offers
        /// Behemoths. Falls back to the name containing "Spawning" when the dump
        /// is absent.</summary>
        static string[] HUMAN_PRODUCERS = { "Barracks", "Light Factory", "Heavy Factory", "Ultra Heavy Factory", "Air Factory" };
        static bool ProducesCombat(ConstructionData cd)
        {
            string n = cd.ObjectInfo?.DisplayName ?? "";
            if (n.IndexOf("Spawning Cyst", StringComparison.OrdinalIgnoreCase) >= 0 ||
                n.IndexOf("Spawner", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            // Sol and Centauri: the balance dump's production tree lists these.
            for (int i = 0; i < HUMAN_PRODUCERS.Length; i++)
                if (string.Equals(n, HUMAN_PRODUCERS[i], StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>How many more of the heaviest unit this producer type makes
        /// the team cap still allows; -1 when the cap is unknown or unlimited.</summary>
        static int CapRoomFor(Team team, string producerName)
        {
            try
            {
                var sample = FindOne(team, producerName);
                var opts = sample?.ConstructionOptions;
                if (opts == null) return -1;
                int heaviest = 0; UnitCapEntry entry = null;
                foreach (var opt in opts)
                {
                    if (opt?.ObjectInfo == null) continue;
                    bool isUnit = false; try { isUnit = opt.IsUnit; } catch { }
                    if (!isUnit) continue;
                    if (WorkerNames.Contains(opt.ObjectInfo.DisplayName ?? "")) continue;
                    int w = opt.ObjectInfo.UnitCapValue;
                    if (w > heaviest) { heaviest = w; entry = team.GetUnitTypeCapEntryForUnit(opt.ObjectInfo); }
                }
                if (entry == null || heaviest <= 0 || entry.Max <= 0) return -1;
                return (entry.Max - entry.Current) / heaviest;
            }
            catch { return -1; }
        }

        static Structure FindOne(Team team, string name)
        {
            try
            {
                var structs = team.Structures;
                if (structs == null) return null;
                for (int i = 0; i < structs.Count; i++)
                {
                    var s = structs[i];
                    if (s?.ObjectInfo == null || s.IsDestroyed) continue;
                    if (string.Equals(s.ObjectInfo.DisplayName, name, StringComparison.OrdinalIgnoreCase)) return s;
                }
            }
            catch { }
            return null;
        }

        static int CountOf(Team team, string name, bool includeSites)
        {
            int n = 0;
            try
            {
                var structs = team.Structures;
                if (structs != null)
                    for (int i = 0; i < structs.Count; i++)
                    {
                        var s = structs[i];
                        if (s?.ObjectInfo == null || s.IsDestroyed) continue;
                        if (string.Equals(s.ObjectInfo.DisplayName, name, StringComparison.OrdinalIgnoreCase)) n++;
                    }
                if (includeSites)
                {
                    var sites = ConstructionSite.ConstructionSites;
                    if (sites != null)
                        for (int i = 0; i < sites.Count; i++)
                        {
                            var cs = sites[i];
                            if (cs == null || cs.IsDestroyed || cs.ObjectInfo == null || cs.Team != team) continue;
                            if (string.Equals(cs.ObjectInfo.DisplayName, name, StringComparison.OrdinalIgnoreCase)) n++;
                        }
                }
            }
            catch { }
            return n;
        }

        static int CountNear(Team team, Vector3 pos, float radiusM)
        {
            int n = 0; float r2 = radiusM * radiusM;
            try
            {
                var structs = team.Structures;
                if (structs != null)
                    for (int i = 0; i < structs.Count; i++)
                    {
                        var s = structs[i];
                        if (s?.ObjectInfo == null || s.IsDestroyed) continue;
                        if (!_producerCds.ContainsKey(s.ObjectInfo.DisplayName ?? "")) continue;
                        var p = s.transform.position;
                        float dx = p.x - pos.x, dz = p.z - pos.z;
                        if (dx * dx + dz * dz <= r2) n++;
                    }
            }
            catch { }
            return n;
        }

        // ---- producers: where -----------------------------------------------------

        static List<(Vector3 pos, float weight)> _fights = new List<(Vector3, float)>();

        static void PlanSites(Team team)
        {
            Vector3 nest = Intel.Nest;
            if (nest == Vector3.zero) return;
            Objectives.FightPoints(_fights);
            // Where incursions come from, and where the enemy's main is, are
            // fight points too — cheaper than waiting to be surprised.
            var bearing = Intel.CorridorBearing();
            if (bearing != Vector3.zero) _fights.Add((nest + bearing * 900f, 1f));
            Intel.Base mainOffence = null;
            for (int i = 0; i < Objectives.Portfolio.Count; i++)
            {
                var o = Objectives.Portfolio[i];
                if (o.Offensive && o.BaseRef != null && (o.Kind == Objectives.Kind.KillHQ || o.Kind == Objectives.Kind.DenyExpansion))
                { mainOffence = o.BaseRef; break; }
            }
            if (mainOffence == null)
            {
                var bases = Intel.Bases;
                for (int i = 0; i < bases.Count; i++) if (bases[i].HasHq) { mainOffence = bases[i]; break; }
                if (mainOffence == null && bases.Count > 0) mainOffence = bases[0];
            }
            if (mainOffence != null) _fights.Add((mainOffence.Centre, 1.5f));
            if (_fights.Count == 0) _fights.Add((nest, 1f));
            // At most six fight points: each is one field solve per site refresh.
            if (_fights.Count > 6) { _fights.Sort((x, y) => y.weight.CompareTo(x.weight)); _fights.RemoveRange(6, _fights.Count - 6); }

            // Candidates: our functional structures. Stride to keep it cheap.
            var cands = new List<Vector3>();
            try
            {
                var structs = team.Structures;
                if (structs != null)
                    for (int i = 0; i < structs.Count; i++)
                    {
                        var s = structs[i];
                        if (s?.ObjectInfo == null || s.IsDestroyed) continue;
                        bool functional = false; try { functional = s.IsFunctional; } catch { }
                        if (!functional) continue;
                        cands.Add(s.transform.position);
                    }
            }
            catch { }
            if (cands.Count == 0) return;
            if (cands.Count > 120)
            {
                var thin = new List<Vector3>(); int step = cands.Count / 120 + 1;
                for (int i = 0; i < cands.Count; i += step) thin.Add(cands[i]);
                cands = thin;
            }

            // Fields from each fight point; score = weighted walk-time + danger.
            var fields = new Fields.Field[_fights.Count];
            for (int f = 0; f < _fights.Count; f++) fields[f] = Fields.Cached(_fights[f].pos, false);
            float wsum = 0f; for (int f = 0; f < _fights.Count; f++) wsum += _fights[f].weight;

            Vector3 best = Vector3.zero; float bestScore = float.MaxValue; string bestWhy = "";
            Vector3 fob = Vector3.zero; float fobScore = float.MaxValue; string fobWhy = "";
            Fields.Field toMain = mainOffence != null ? Fields.Cached(mainOffence.Centre, false) : null;
            int unreachable = 0; string unreachWhy = "";
            for (int c = 0; c < cands.Count; c++)
            {
                var p = cands[c];
                // A CYST WHOSE UNITS CANNOT WALK TO THE ENEMY IS A CYST WASTED.
                // Crimson Peak 13:02: six cysts on the Nest plateau, 67 Behemoths
                // that never crossed the ridge. The game's graph decides.
                if (mainOffence != null && !Reach.AllGroundCanReach(team, p, mainOffence.Centre, out string rw))
                { unreachable++; unreachWhy = rw; if (Reach.Enforce) continue; }
                float walk = 0f;
                for (int f = 0; f < _fights.Count; f++)
                {
                    float s = fields[f].SecondsAt(p, 9f);
                    if (float.IsInfinity(s)) s = 2000f;
                    walk += s * _fights[f].weight;
                }
                walk /= Mathf.Max(0.01f, wsum);
                float danger = Fields.DangerAt(p);
                float score = walk + danger / 40f;
                if (score < bestScore) { bestScore = score; best = p; bestWhy = $"walk {walk:F0}s danger {danger:F0}"; }

                if (toMain != null)
                {
                    float toThem = toMain.SecondsAt(p, 9f);
                    if (float.IsInfinity(toThem)) continue;
                    // Not inside their reach, and held: something of ours stands here.
                    // A FORWARD BASE STANDS WHERE THE ARMY STANDS. The Maw replay
                    // (2026-09-06 22:43): FOBs placed close to the enemy with no army
                    // to cover the build-up, several producers lost. Cover is the
                    // reserve within 500 m or a spire within 250 m, and the enemy
                    // strength within 700 m must not exceed the reserve.
                    bool covered = (Forces.ReservePoint - p).sqrMagnitude < 500f * 500f || SpireNear(team, p, 250f);
                    if (!covered) continue;
                    if (Intel.EffectiveNear(p, 700f) > Forces.ReserveEff) continue;
                    float fs = toThem + danger / 20f;
                    if (fs < fobScore) { fobScore = fs; fob = p; fobWhy = $"{toThem:F0}s from {Intel.Short(mainOffence.Team)} base, danger {danger:F0}{(covered ? ", covered" : "")}"; }
                }
            }
            if (unreachable > 0)
                MilLog.Every("site:unreachable", 120f, $"[MIL/SITE] {unreachable} of {cands.Count} candidate sites cannot reach the enemy base for {unreachWhy}; best ({best.x:F0},{best.z:F0})");
            _bestSite = best; _bestSiteWhy = bestWhy;
            if (fob != Vector3.zero && (fob - ForwardBase).sqrMagnitude > 200f * 200f)
                MilLog.Msg($"[MIL/SITE] forward base at ({fob.x:F0},{fob.z:F0}) — {fobWhy}; general site ({best.x:F0},{best.z:F0}) — {bestWhy}");
            ForwardBase = fob; ForwardWhy = fobWhy;
        }

        static bool SpireNear(Team team, Vector3 p, float r)
        {
            try
            {
                var structs = team.Structures;
                if (structs == null) return false;
                for (int i = 0; i < structs.Count; i++)
                {
                    var s = structs[i];
                    if (s?.ObjectInfo == null || s.IsDestroyed) continue;
                    bool def = false; try { def = (s.ObjectInfo.StructureType & StructureType.Defense) != 0; } catch { }
                    if (!def) continue;
                    if ((s.transform.position - p).sqrMagnitude <= r * r) return true;
                }
            }
            catch { }
            return false;
        }

        static void PlaceProducers(Team team, float now, ref int budget)
        {
            if (now - _lastPlaceAt < PLACE_CADENCE_S) return;
            if (_producerCds.Count == 0) return;
            // IDLE PRODUCERS MEAN THE PRODUCER COUNT IS NOT THE BOTTLENECK. Naraka,
            // 2026-09-07 07:23: every unit was UnmetPrerequisite (research held),
            // eleven cysts stood idle, and this loop placed 107 producers for
            // 582,000 cash while queuing nothing. Whatever stops the standing
            // producers from queuing — prerequisites, budget, the Queen — a new
            // cyst will not fix.
            if (_total >= 1 && _busy == 0) return;
            foreach (var kv in _producerCds)
            {
                string name = kv.Key; var cd = kv.Value;
                _want.TryGetValue(name, out int want); if (want < 1) want = 1;
                int have = CountOf(team, name, includeSites: true);
                // The forward base carries its own allowance.
                int atFob = ForwardBase == Vector3.zero ? 0 : CountNear(team, ForwardBase, FOB_RADIUS_M);
                bool fobWants = ForwardBase != Vector3.zero && atFob < MilConfig.FobProducers &&
                                Intel.Bases.Count > 0 && have >= 1;
                if (have >= want && !fobWants) continue;
                if (_requestedAt.TryGetValue(name, out float at) && now - at < REQUEST_TTL_S) continue;
                int cost = 0; try { cost = cd.ResourceCost; } catch { }
                if (cost + 2000 > budget) continue;

                Vector3 site = fobWants ? ForwardBase : (_bestSite != Vector3.zero ? _bestSite : Intel.Nest);
                if (site == Vector3.zero) continue;
                // Step a little off the anchor so the search does not sit on it.
                Vector3 dir = (Intel.Bases.Count > 0 ? Intel.Bases[0].Centre : site) - site; dir.y = 0f;
                Vector3 at2 = dir.sqrMagnitude > 1f ? site + dir.normalized * 60f : site;
                bool fired = false;
                try { fired = Faction.Construction.TryBuild(team, cd, at2); }
                catch (Exception ex) { MelonLogger.Warning("[MIL/PROD] place threw: " + ex.Message); }
                _lastPlaceAt = now;
                if (fired)
                {
                    budget -= cost; SpentThisRound += cost; ProducersPlaced++;
                    _requestedAt[name] = now;
                    MilLog.Msg($"[MIL/PROD] placed {name} #{have + 1}/{Mathf.Max(want, have + 1)} at ({at2.x:F0},{at2.z:F0}) — " +
                               (fobWants ? $"forward base ({atFob + 1}/{MilConfig.FobProducers}): {ForwardWhy}" : $"best site: {_bestSiteWhy}"));
                }
                else MilLog.Every("prod:place:" + name, 60f, $"[MIL/PROD] {name} placement at ({at2.x:F0},{at2.z:F0}) not accepted");
                return;
            }
        }

        static void Log(Team team, int budgetLeft)
        {
            int cash = 0; try { cash = team.TotalResources; } catch { }
            var sb = new System.Text.StringBuilder("[MIL/PROD] ");
            sb.Append("queued=").Append(QueuedThisRound).Append(" spent=").Append(SpentThisRound)
              .Append(" cash=").Append(cash).Append(" budget=").Append(budgetLeft)
              .Append(" producers busy ").Append(_busy).Append('/').Append(_total)
              .Append(" | demand fast ").Append(_demand[0].ToString("F0")).Append(" swarm ").Append(_demand[1].ToString("F0"))
              .Append(" line ").Append(_demand[2].ToString("F0")).Append(" heavy ").Append(_demand[3].ToString("F0"))
              .Append(" any ").Append(_demandAny.ToString("F0"));
            foreach (var kv in _want) sb.Append(" | ").Append(kv.Key).Append(' ').Append(CountOf(team, kv.Key, true)).Append('/').Append(kv.Value);
            if (ForwardBase != Vector3.zero) sb.Append(" | FOB (").Append(ForwardBase.x.ToString("F0")).Append(',').Append(ForwardBase.z.ToString("F0")).Append(')');
            if (!MilConfig.Produce) sb.Append(" [produce off]");
            MilLog.Msg(sb.ToString());
        }

        internal static string BuildRoundSummaryFragment()
        {
            if (QueuedThisRound == 0 && ProducersPlaced == 0) return "";
            return "--- Military production ---\n" +
                   $"  units queued: {QueuedThisRound}, producers placed: {ProducersPlaced}, cash spent: {SpentThisRound}\n" +
                   (CompositionTarget.Summary().Length > 0 ? $"  mix queued: {CompositionTarget.Summary()}\n" : "");
        }
    }
}
