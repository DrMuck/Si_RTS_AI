using MelonLoader;
using System.Collections.Generic;
using UnityEngine;

namespace Si_RTS_AI.Planning
{
    /// <summary>
    /// HOW HARD TO PUSH, AND HOW MANY PRODUCERS TO PAY FOR.
    ///
    /// The blueprint answers WHERE the network goes and in what order. It does
    /// not answer the two questions DrMuck raised on 2026-08-05, which are the
    /// genuinely strategic ones:
    ///
    ///   1. HOW MANY LESSER CYSTS. Too many and the team is against the 200
    ///      shrimp cap by mid-game, which costs twice: shrimps pile up at the
    ///      starter patches early, and every later expansion is unstaffable
    ///      because nothing can be produced any more. Skipping a Cyst is not a
    ///      saving of 1,500 — it is a decision that a group which finishes a
    ///      near patch WALKS to the next one, which is how a human plays it and
    ///      is a vanilla game mechanic we get for free.
    ///
    ///   2. HOW FAR AHEAD TO BUILD. Reaching further pays eventually and costs
    ///      now, because the shrimps that must staff it are a long walk away.
    ///      Harvest inside-out, harvest homogeneously, or somewhere between.
    ///
    /// Both are trade-offs over time under a shared cap, which is precisely what
    /// a simulator can settle and no placement rule can. So: enumerate the
    /// strategies, simulate each against the CURRENT world plus the blueprint's
    /// own next sites, and take the one that earns most over the horizon.
    ///
    /// Enumerated rather than beam-searched on purpose. The decision is two
    /// small integers, so the space is a few dozen points — a beam would prune
    /// a space we can evaluate exhaustively, and pruning is where beams go
    /// wrong. The simulator is doing the work either way.
    ///
    /// Depends on EcoSimulator modelling relocation-on-depletion (added the same
    /// day). Without it, a site with no producer looks permanently dead and the
    /// answer is always "more Cysts" — which is the bias that produced the
    /// problem in the first place.
    /// </summary>
    internal static class ExpansionStrategy
    {
        /// <summary>Producers the blueprint may plan right now.</summary>
        internal static int Producers = 2;

        /// <summary>Unbuilt sites the executor may commit to. This is the
        /// aggressiveness dial: small is inside-out, large is homogeneous.</summary>
        internal static int SitesAhead = 4;

        internal static float LastChosenAt;
        internal static string LastReport = "";

        static System.Threading.Tasks.Task _running;

        internal static void ResetForNewRound()
        {
            Producers = 2; SitesAhead = 4; LastChosenAt = 0f; LastReport = "";
            _running = null;
        }

        /// <summary>
        /// Twelve 300-second simulations is not main-thread work. The sweep runs
        /// on a snapshot, exactly like the beam does, and its answer lands on the
        /// next plan that asks for it — a strategy that was right thirty seconds
        /// ago is still right now, which is the point of deciding it slowly.
        /// </summary>
        internal static void KickOff(EcoState live, List<Blueprint.SiteInfo> sites, float incomePerSec)
        {
            if (_running != null && !_running.IsCompleted) return;
            var snapshot = live.Clone();
            var copy = new List<Blueprint.SiteInfo>(sites);
            _running = System.Threading.Tasks.Task.Run(() => Choose(snapshot, copy, incomePerSec));
        }

        // The strategies on offer. Kept coarse deliberately — the point is to
        // find which REGIME the map and the round are in, not to fine-tune a
        // number that will be re-decided in thirty seconds anyway.
        static readonly int[] PRODUCER_OPTIONS = { 0, 1, 2, 4, 6 };
        static readonly int[] AHEAD_OPTIONS    = { 2, 4, 8, 12 };

        /// <summary>
        /// Horizon. Long enough for a far site to be built, staffed and to start
        /// PAYING BACK — otherwise every distant option loses by construction
        /// and the answer is always "stay home".
        ///
        /// 300s was not long enough, and the symptom was exactly what DrMuck
        /// reported on 2026-08-05: "the expansion is too slow now to support
        /// fast map control". A site eight deep in the plan came up around 160s
        /// into the window, spent the next stretch pulling shrimps off
        /// productive near patches, and the horizon closed before it had earned
        /// any of that back — so eight-ahead scored below four-ahead every
        /// single sweep. At 600s the same site has time to be a producer rather
        /// than a cost, which is the honest comparison.
        ///
        /// This does NOT price map control, which is a military question the
        /// eco simulator has no business answering. It only stops the horizon
        /// from silently arguing against expansion.
        /// </summary>
        const float HORIZON_S = 600f;

        /// <summary>
        /// Choose against the live state and the plan's own next sites.
        /// Called from Blueprint.Replan, so it runs on the plan's cadence
        /// rather than every tick.
        /// </summary>
        static void Choose(EcoState live, List<Blueprint.SiteInfo> sites, float incomePerSec)
        {
            try
            {
                if (sites.Count == 0) return;

                int bestP = PRODUCER_OPTIONS[0], bestA = AHEAD_OPTIONS[0];
                float bestScore = float.MinValue;
                var sb = new System.Text.StringBuilder("[STRATEGY] horizon=");
                sb.Append(HORIZON_S.ToString("F0")).Append("s shrimps=").Append(live.totalShrimps)
                  .Append(" cash=").Append(live.cash).Append(" |");

                for (int ai = 0; ai < AHEAD_OPTIONS.Length; ai++)
                {
                    sb.Append(" ahead").Append(AHEAD_OPTIONS[ai]).Append(':');
                    for (int pi = 0; pi < PRODUCER_OPTIONS.Length; pi++)
                    {
                        float score = Evaluate(live, sites, PRODUCER_OPTIONS[pi], AHEAD_OPTIONS[ai], incomePerSec);
                        sb.Append(' ').Append(PRODUCER_OPTIONS[pi]).Append('c')
                          .Append('=').Append((score / 1000f).ToString("F1")).Append('k');
                        if (score > bestScore)
                        { bestScore = score; bestP = PRODUCER_OPTIONS[pi]; bestA = AHEAD_OPTIONS[ai]; }
                    }
                }

                Producers = bestP;
                SitesAhead = bestA;
                LastChosenAt = Time.time;
                sb.Append(" => ").Append(bestP).Append(" producers, ")
                  .Append(bestA).Append(" sites ahead");
                LastReport = sb.ToString();
                MelonLogger.Msg(LastReport);
            }
            catch (System.Exception ex)
            { MelonLogger.Warning("[STRATEGY] threw: " + ex.Message); }
        }

        /// <summary>
        /// Build the world this strategy would produce and let it run.
        ///
        /// Sites arrive on a cadence the economy can actually pay for — a plan
        /// that spends faster than it earns does not get built faster, it just
        /// queues, so pricing the schedule against measured income is what keeps
        /// "8 sites ahead" from looking free.
        /// </summary>
        static float Evaluate(EcoState live, List<Blueprint.SiteInfo> sites,
                              int producers, int ahead, float incomePerSec)
        {
            var s = live.Clone();
            s.t = 0f;

            int n = ahead < sites.Count ? ahead : sites.Count;
            float rate = Mathf.Max(1f, incomePerSec);
            float cumulativeCost = 0f;
            int spend = 0;

            // WHICH SITES GET THE PRODUCERS — the ones migration reaches LAST,
            // matching what the Cyst pass will actually build. This used to be
            // "the first N sites", i.e. the nearest ones, which is the worst
            // possible placement: those are precisely the sites migration
            // staffs for free. Pricing producers where they add nothing made
            // every producing strategy look bad, so the sweep's preference for
            // zero was partly an artefact of its own assumption.
            var worstFirst = new List<int>(n);
            for (int i = 0; i < n; i++) worstFirst.Add(i);
            worstFirst.Sort((a, b) => sites[b].arrivalS.CompareTo(sites[a].arrivalS));
            var getsCyst = new HashSet<int>();
            for (int i = 0; i < producers && i < worstFirst.Count; i++) getsCyst.Add(worstFirst[i]);

            for (int i = 0; i < n; i++)
            {
                int cost = EcoSimulator.BC_COST + sites[i].nodes * EcoSimulator.NODE_COST;
                bool cyst = getsCyst.Contains(i);
                if (cyst) cost += EcoSimulator.CYST_COST;
                cumulativeCost += cost;
                spend += cost;

                // Ready when the cash for everything up to here has been earned,
                // but never sooner than the thing takes to build.
                float readyAt = Mathf.Max(cumulativeCost / rate,
                                          (i + 1) * EcoSimulator.BC_BUILD_S);

                s.bcs.Add(new EcoState.Bc
                {
                    pos = sites[i].pos, finished = false, readyAt = readyAt,
                    storage = 0, storageCap = 4000,
                });
                if (cyst)
                    s.cysts.Add(new EcoState.Cyst
                    {
                        pos = sites[i].pos, finished = false,
                        readyAt = readyAt + EcoSimulator.CYST_BUILD_S,
                        nextSpawnAt = readyAt + EcoSimulator.CYST_BUILD_S + EcoSimulator.SHRIMP_BUILD_S,
                    });
            }

            // The bank pays for it. A Cyst that leaves nothing for shrimps is
            // the failure mode this whole comparison exists to expose, and the
            // simulator already refuses to spawn what cannot be paid for.
            s.cash = s.cash - spend;
            if (s.cash < 0) s.cash = 0;

            EcoSimulator.SimulateForward(s, HORIZON_S);
            return s.grossEarned - spend;
        }
    }
}
