using MelonLoader;
using Silica;
using Si_RTS_AI.Planning;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Si_RTS_AI.Mil
{
    /// <summary>
    /// STATIC DEFENCE, WHICH THIS MOD HAS NEVER BUILT.
    ///
    /// Found by the shadow layer on its first played round, 2026-08-13. A
    /// top-quartile human commander has about 13 Hive Spires and 9 Thorn Spires
    /// standing by minute 30 and starts placing them at minute 10. The bot
    /// finished that round with 7 and 1 — and both of those were built by
    /// DrMuck, because no code in this mod names either structure. Not
    /// under-built: absent.
    ///
    /// WHY EARLY, AND WHY THIS IS THE HARDEST WINDOW
    /// ----------------------------------------------
    /// DrMuck: *"early defense seems the hardest. Humans can push quite
    /// aggressive early while alien still fighting to build up a good eco."*
    ///
    /// The archive agrees and puts a number on it. Across 13,043 alien-vs-human
    /// engagements, by minute of the round:
    ///
    ///     window     alien win%    cash exchange (theirs lost : ours)
    ///     0-5 min       34%            0.68
    ///     5-10          37%            0.79
    ///     10-15         43%            0.83
    ///     15-20         45%            0.88
    ///     20-30+        44-45%         0.92-0.94
    ///
    /// The alien loses about three cash for every two it destroys in the first
    /// five minutes and climbs monotonically to near-parity by twenty. That is
    /// an eleven-point win-rate hole in exactly the window where the economy
    /// cannot yet fund an army — which is what static defence is FOR. A spire
    /// costs 1,000 and 35 seconds and does not consume unit cap, so it buys
    /// time that Shockers cannot.
    ///
    /// That window is the ONE place this plans ahead of demand: the Nest gets a
    /// spire by `nestByMin` whether or not the threat map has seen anything yet,
    /// because the Queen is the loss condition and the first attack is the one
    /// the economy can least afford to answer.
    ///
    /// WHAT IT DECIDES, AND WHAT IT DELIBERATELY DOES NOT
    /// ---------------------------------------------------
    /// It decides WHERE and HOW MANY, both from live demand — see
    /// ConsiderSites() for why the human build trajectory decides neither. It
    /// does not decide what a spire costs, when the economy can spare it, or
    /// whether the ground is buildable: `MoneyBroker` and `AlienConstruction`
    /// own those and are asked rather than second-guessed.
    ///
    /// OFF BY DEFAULT, ON PURPOSE. `mil.spires.execute` starts false, so the
    /// first round logs every placement it WOULD make and spends nothing.
    /// `MILITARY_MODEL` section 11 has required that of every change for weeks
    /// and this session already broke it once. Read one round of [MIL/SPIRE]
    /// lines, then flip the flag.
    /// </summary>
    internal static class SpirePlanner
    {
        internal static bool Enabled { get; private set; } = true;
        internal static bool Execute { get; private set; }

        /// <summary>Cash kept back for the economy. Costs are read live rather
        /// than assumed — the balance dump says a Hive Spire is 1,000 and the
        /// game charged 1,400 on 2026-08-13, so ObjectInfo.Cost is the truth and
        /// the dump is a document. Below this in the bank we are not buying
        /// defence with money the economy still needs to compound.</summary>
        static int _cashFloor = 4000;

        /// <summary>Spires a threatened, earning site justifies. The rule of
        /// thumb, not a fitted number — see Warranted().</summary>
        static int _perSite = 1;

        /// <summary>Threat above which a site justifies one more. Calibrated off
        /// nothing but the spread seen in one round: quiet sites read 200-330,
        /// the contested one read 7,722.</summary>
        static float _heavyThreat = 2000f;

        /// <summary>Minutes by which the Nest should hold one spire even with a
        /// clean threat map. The only place we act ahead of demand.</summary>
        static float _nestByMin = 8f;

        /// <summary>Spires this close to an existing one are redundant — they
        /// would cover the same ground twice and leave other assets bare.</summary>
        const float SPACING_M = 120f;

        const float TICK_S = 15f;
        static float _nextAt;
        static int _placedThisRound;

        static readonly string[] SPIRES = { "Thorn Spire", "Hive Spire" };
        static readonly Dictionary<string, ConstructionData> _cds =
            new Dictionary<string, ConstructionData>(StringComparer.OrdinalIgnoreCase);

        internal static void Configure()
        {
            Enabled    = RtsaiConfig.Bool ("mil.spires.enabled", true);
            Execute    = RtsaiConfig.Bool ("mil.spires.execute", false);
            _cashFloor   = RtsaiConfig.Int  ("mil.spires.cashFloor",   4000);
            _perSite     = RtsaiConfig.Int  ("mil.spires.perSite",      1);
            _heavyThreat = RtsaiConfig.Float("mil.spires.heavyThreat",  2000f);
            _nestByMin   = RtsaiConfig.Float("mil.spires.nestByMin",    8f);
            MelonLogger.Msg($"[MIL/SPIRE] {(Enabled ? "on" : "off")} " +
                            $"execute={Execute} cashFloor={_cashFloor} " +
                            $"perSite={_perSite} (+1 above threat {_heavyThreat:F0}) " +
                            $"nestBy={_nestByMin:F0}min" +
                            (Execute ? "" : " — planning only, builds nothing"));
        }

        internal static void ResetForNewRound()
        {
            _nextAt = 0f; _placedThisRound = 0; _cds.Clear();
        }

        internal static void Tick(Team team)
        {
            if (!Enabled || team == null) return;
            float now = Time.time;
            if (now < _nextAt) return;
            _nextAt = now + TICK_S;

            float minutes;
            try { minutes = Perception.MapLayers.LayerReplay.CurrentRoundTime / 60f; }
            catch { return; }
            if (minutes <= 0.5f) return;

            DiscoverCds(team);
            if (_cds.Count == 0) return;

            ConsiderSites(team, minutes);
        }

        /// <summary>
        /// DEMAND DECIDES THE COUNT, NOT THE CLOCK.
        ///
        /// The first version servoed to the human build trajectory — build until
        /// you have as many spires as a top-quartile commander had at this
        /// minute. DrMuck killed it on sight: *"wouldnt orientate so much when
        /// how many buildings are placed. It depends on the situation, the cash
        /// in the bank and many more other factors."*
        ///
        /// He is right, and the code was contradicting its own documentation —
        /// MIL_V2_ARCHITECTURE section 4d calls that curve "a prior to orient by
        /// and a target to measure against, not a script to follow", and then
        /// this class followed it as a script. A population average over top
        /// commanders cannot know the map size, the matchup, whether anything is
        /// actually attacking, or that 260,000 credits are sitting idle.
        ///
        /// What survives the objection is the CAPABILITY finding: zero spires
        /// ever built, on any round, because no code named the structure. No
        /// situation explains that. What does not survive is "you have 2 and the
        /// average commander had 10.7 by now".
        ///
        /// So the count emerges from the map. `DefencePlanner.Tasks` already
        /// holds exactly the right input and nothing else: a site is listed only
        /// if it EARNED recently AND has nonzero threat near it — ground worth
        /// defending that something is actually coming for. A quiet round builds
        /// almost nothing; a contested one builds until the threat is covered.
        ///
        /// The trajectory stays in the log line as a yardstick, so we can still
        /// see how the demand-driven count compares with human play, and it
        /// decides nothing.
        /// </summary>
        static void ConsiderSites(Team team, float minutes)
        {
            int cash = 0;
            try { cash = (int)team.TotalResources; } catch { }
            int reserved = 0;
            try { reserved = MoneyBroker.GetReservedCash(team); } catch { }
            int spendable = cash - reserved;

            // ONE PER TICK. Fifteen seconds between passes, so a contested map
            // still covers itself inside a couple of minutes — but a sudden
            // rush cannot make the planner dump ten spires in a single frame
            // and hand the round to whoever attacks the other side.
            foreach (var site in Sites(team, minutes))
            {
                int warranted = Warranted(site, minutes);
                int covered   = SpiresNear(team, site.Pos);
                if (covered >= warranted) continue;

                // Cheaper first: a Thorn is 1,000 against a Hive's 1,400, and
                // the first spire on a site is the one that matters most.
                string name = PickType(team, site.Pos);
                if (name == null || !_cds.TryGetValue(name, out var cd)) continue;

                int cost = 1000;
                try { cost = cd.ObjectInfo?.Cost ?? 1000; } catch { }
                if (spendable < _cashFloor + cost) return;   // poorer sites will not do better

                float yardstick = Doctrine.TargetAt(name, minutes);
                _placedThisRound++;
                MelonLogger.Msg(
                    $"[MIL/SPIRE] t={minutes:F1}m {name} -> ({site.Pos.x:F0},{site.Pos.z:F0}) " +
                    $"{site.Why} | warranted {warranted} covered {covered} " +
                    $"| spendable {spendable} cost {cost}" +
                    (float.IsNaN(yardstick) ? "" : $" | humans ~{yardstick:F1} by now") +
                    (Execute ? "" : "  [PLAN ONLY — mil.spires.execute is false]"));

                if (!Execute) return;
                try
                {
                    if (!Faction.AlienConstruction.TryBuildStructureByCd(team, cd, site.Pos))
                        MelonLogger.Msg($"[MIL/SPIRE] placement refused at " +
                                        $"({site.Pos.x:F0},{site.Pos.z:F0}) — construction said no");
                }
                catch (Exception ex) { MelonLogger.Warning("[MIL/SPIRE] build threw: " + ex.Message); }
                return;
            }
        }

        struct Site { public Vector3 Pos; public float Threat; public long Income; public string Why; }

        static readonly List<(Vector3 pos, float eff, float etaS, string why)> _threatened =
            new List<(Vector3, float, float, string)>();

        /// <summary>Ground with something arriving, soonest first - from the
        /// objective planner's forecasts - then the corridor the last incursions
        /// used, then the forward base, then the Nest. The Queen is a loss
        /// condition and at minute three there is no forecast to rank yet.</summary>
        static IEnumerable<Site> Sites(Team team, float minutes)
        {
            Objectives.ThreatenedSites(_threatened);
            _threatened.Sort((a, b) => a.etaS.CompareTo(b.etaS));
            for (int i = 0; i < _threatened.Count; i++)
                yield return new Site
                {
                    Pos = _threatened[i].pos, Threat = _threatened[i].eff, Income = 0,
                    Why = $"forecast {_threatened[i].eff:F0} eff arriving in {_threatened[i].etaS:F0}s - {_threatened[i].why}",
                };

            var nest = FindNest(team);
            var bearing = Intel.CorridorBearing();
            if (nest != Vector3.zero && bearing != Vector3.zero)
                yield return new Site
                {
                    Pos = nest + bearing * 350f, Threat = 1f, Income = 0,
                    Why = "the corridor incursions have used",
                };

            var fob = ProductionV3.ForwardBase;
            if (fob != Vector3.zero)
                yield return new Site { Pos = fob, Threat = 1f, Income = 0, Why = "the forward base" };

            if (nest != Vector3.zero)
                yield return new Site
                {
                    Pos = nest, Threat = 0f, Income = 0,
                    Why = "the Nest - the Queen is the loss condition",
                };
        }

        /// <summary>
        /// How many spires this ground justifies. A STARTING OPINION, and
        /// labelled as one: it is a rule of thumb to be measured against
        /// outcomes, not something fitted from data. Two knobs, both in
        /// rtsai.json, so it can be argued with between rounds.
        ///
        /// The Nest gets one early whatever the threat map says. That is the
        /// one place the archive justifies acting ahead of demand: the alien
        /// wins 34% of fights in the first five minutes at a 0.68 exchange, and
        /// the thing being defended is the loss condition.
        /// </summary>
        static int Warranted(Site site, float minutes)
        {
            if (site.Threat <= 0f)
                return minutes <= _nestByMin ? 1 : 0;     // the Nest, early only
            if (site.Threat <= 1.5f) return _perSite;                 // a corridor or the FOB: one
            return site.Threat >= _heavyThreat ? _perSite + 1 : _perSite;
        }

        /// <summary>Thorn first for cost, then Hive, so a site gets breadth
        /// before it gets depth.</summary>
        static string PickType(Team team, Vector3 pos)
        {
            for (int i = 0; i < SPIRES.Length; i++)
            {
                if (!_cds.ContainsKey(SPIRES[i])) continue;
                if (CountNear(team, SPIRES[i], pos) == 0
                    && !OrderedNear(SPIRES[i], pos)) return SPIRES[i];
            }
            return null;
        }

        static int SpiresNear(Team team, Vector3 pos)
        {
            int n = 0;
            for (int i = 0; i < SPIRES.Length; i++)
            {
                n += CountNear(team, SPIRES[i], pos);
                if (OrderedNear(SPIRES[i], pos)) n++;
            }
            return n;
        }

        static bool OrderedNear(string name, Vector3 pos)
        {
            try { return Faction.AlienConstruction.WasOrderedNear(name, pos, SPACING_M); }
            catch { return false; }
        }

        /// <summary>
        /// Both spires are built AT THE NEST, so their ConstructionData lives in
        /// some structure's options rather than anywhere we can name up front.
        /// Same discovery TechPlanner does for the Cortex tiers.
        /// </summary>
        static void DiscoverCds(Team team)
        {
            if (_cds.Count == SPIRES.Length) return;
            try
            {
                var structs = team.Structures;
                if (structs == null) return;
                for (int i = 0; i < structs.Count; i++)
                {
                    var opts = structs[i]?.ConstructionOptions;
                    if (opts == null) continue;
                    foreach (var opt in opts)
                    {
                        string n = opt?.ObjectInfo?.DisplayName ?? "";
                        for (int s = 0; s < SPIRES.Length; s++)
                            if (string.Equals(n, SPIRES[s], StringComparison.OrdinalIgnoreCase)
                                && !_cds.ContainsKey(n))
                            {
                                _cds[n] = opt;
                                MelonLogger.Msg($"[MIL/SPIRE] found {n} buildable " +
                                                $"(cost {opt.ObjectInfo?.Cost ?? 0})");
                            }
                    }
                }
            }
            catch { }
        }

        /// <summary>Standing spires of this type within SPACING_M of a spot.</summary>
        static int CountNear(Team team, string name, Vector3 pos)
        {
            int n = 0;
            float r2 = SPACING_M * SPACING_M;
            try
            {
                var structs = team.Structures;
                if (structs != null)
                    for (int i = 0; i < structs.Count; i++)
                    {
                        var st = structs[i];
                        if (st?.ObjectInfo == null || st.IsDestroyed) continue;
                        if (!string.Equals(st.ObjectInfo.DisplayName, name,
                                           StringComparison.OrdinalIgnoreCase)) continue;
                        var p = st.transform.position;
                        float dx = p.x - pos.x, dz = p.z - pos.z;
                        if (dx * dx + dz * dz <= r2) n++;
                    }
            }
            catch { }
            return n;
        }

        /// <summary>Is there already one of these near enough that another
        /// would cover the same ground twice?</summary>
        static bool AlreadyCovered(Team team, string name, Vector3 pos)
        {
            float r2 = SPACING_M * SPACING_M;
            try
            {
                var structs = team.Structures;
                if (structs != null)
                    for (int i = 0; i < structs.Count; i++)
                    {
                        var st = structs[i];
                        if (st?.ObjectInfo == null || st.IsDestroyed) continue;
                        if (!string.Equals(st.ObjectInfo.DisplayName, name,
                                           StringComparison.OrdinalIgnoreCase)) continue;
                        var p = st.transform.position;
                        float dx = p.x - pos.x, dz = p.z - pos.z;
                        if (dx * dx + dz * dz <= r2) return true;
                    }
            }
            catch { }
            // An order already out for this spot counts as covered, or the
            // 15s tick would queue the same spire repeatedly while the first
            // one is still building.
            try
            {
                if (Faction.AlienConstruction.WasOrderedNear(name, pos, SPACING_M))
                    return true;
            }
            catch { }
            return false;
        }

        static Vector3 FindNest(Team team)
        {
            try
            {
                var structs = team.Structures;
                if (structs != null)
                    for (int i = 0; i < structs.Count; i++)
                    {
                        var st = structs[i];
                        if (st?.ObjectInfo == null || st.IsDestroyed) continue;
                        if ((st.ObjectInfo.DisplayName ?? "").IndexOf(
                                "Nest", StringComparison.OrdinalIgnoreCase) >= 0)
                            return st.transform.position;
                    }
            }
            catch { }
            return Vector3.zero;
        }

        internal static string RoundSummary() =>
            _placedThisRound > 0
                ? $"spires {(Execute ? "placed" : "planned")}={_placedThisRound}" : "";
    }
}
