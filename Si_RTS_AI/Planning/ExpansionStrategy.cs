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
        internal static void KickOff(EcoState live, List<Blueprint.Item> plan, float incomePerSec)
        {
            if (_running != null && !_running.IsCompleted) return;
            var snapshot = live.Clone();
            var planCopy = new List<Blueprint.Item>(plan);
            _running = System.Threading.Tasks.Task.Run(() => Choose(snapshot, planCopy, incomePerSec));
        }

        // The strategies on offer. Kept coarse deliberately — the point is to
        // find which REGIME the map and the round are in, not to fine-tune a
        // number that will be re-decided in thirty seconds anyway.
        static readonly int[] PRODUCER_OPTIONS = { 0, 1, 2, 4 };
        static readonly int[] AHEAD_OPTIONS    = { 2, 4, 8 };

        /// <summary>Horizon. Long enough for a far site to be built, staffed and
        /// to start paying — otherwise every distant option loses by
        /// construction and the answer is always "stay home".</summary>
        const float HORIZON_S = 300f;

        /// <summary>
        /// Choose against the live state and the plan's own next sites.
        /// Called from Blueprint.Replan, so it runs on the plan's cadence
        /// rather than every tick.
        /// </summary>
        static void Choose(EcoState live, List<Blueprint.Item> plan, float incomePerSec)
        {
            try
            {
                // Sites in build order, each with the nodes its chain needs.
                var sites = new List<(Vector3 pos, int nodes)>(16);
                for (int i = 0; i < plan.Count && sites.Count < 12; i++)
                {
                    if (plan[i].kind != Blueprint.Kind.BioCache) continue;
                    int nodes = 0;
                    for (int j = 0; j < plan.Count; j++)
                        if (plan[j].kind == Blueprint.Kind.Node && plan[j].site == plan[i].site) nodes++;
                    sites.Add((plan[i].pos, nodes));
                }
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
        static float Evaluate(EcoState live, List<(Vector3 pos, int nodes)> sites,
                              int producers, int ahead, float incomePerSec)
        {
            var s = live.Clone();
            s.t = 0f;

            int n = ahead < sites.Count ? ahead : sites.Count;
            float rate = Mathf.Max(1f, incomePerSec);
            float cumulativeCost = 0f;
            int spend = 0;

            // Which sites get producers: the first `producers` of them. The plan
            // is ordered near-to-far, so this IS the inside-out reading — and
            // when the sweep prefers 0, it is saying migration will do the job.
            for (int i = 0; i < n; i++)
            {
                int cost = EcoSimulator.BC_COST + sites[i].nodes * EcoSimulator.NODE_COST;
                bool cyst = i < producers;
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
