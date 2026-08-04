using MelonLoader;
using System.Collections.Generic;
using UnityEngine;

namespace Si_RTS_AI.Planning
{
    /// <summary>
    /// PHASE 2 AS A BLUEPRINT, NOT AS A SERIES OF CHOICES.
    ///
    /// Greedy per-tick branching (NaturalBranching, v0.14.x) fixed the travelling
    /// front — growth happened in every direction at once — but it never had a
    /// picture of the whole job. Each frontier point asked "what is nearest to
    /// ME, now", so around 18 minutes the map filled with nodes nobody had
    /// planned and nothing could say whether they were the cheap way to cover
    /// the ground or the expensive one.
    ///
    /// DrMuck, 2026-08-04: scan the biotics, plan the whole branched network
    /// judged on SPEED and COST, then build to that plan and refresh it as more
    /// ground is discovered.
    ///
    /// That is a Steiner tree: terminals are the untapped patches, the roots are
    /// everything we already own, and Nodes are the Steiner points we pay for.
    /// Optimal Steiner is NP-hard, so this is the standard greedy construction
    /// (Takahashi-Matsuyama): repeatedly attach the terminal that is cheapest to
    /// reach FROM THE NETWORK AS IT NOW STANDS, and let each new chain become
    /// network for the ones after it. Trunks, twigs and sharing fall out of that
    /// one rule — a second patch behind the first pays only for the hops past
    /// it, so branches merge instead of running in parallel.
    ///
    /// Two properties matter more than optimality:
    ///   - It is ONE decision, held between refreshes. Nothing here re-decides a
    ///     multi-step commitment every tick, which is the failure mode behind
    ///     most of the 2026-08-03/04 defects.
    ///   - It is reviewable BEFORE it is built. "41 patches covered by 22 sites
    ///     and 57 nodes" is a sentence greedy could never produce, and the plan
    ///     is written to disk and served to the layers viewer so the shape can
    ///     be looked at rather than inferred from what got built.
    ///
    /// The Cyst decision is deliberately NOT part of the tree cost. Where to put
    /// producers is a shrimp question — who can walk here, and is there unit-cap
    /// room to produce at all — so it runs as a separate pass over the sites the
    /// tree chose.
    /// </summary>
    internal static class Blueprint
    {
        internal enum Kind { Node, BioCache, Cyst }

        internal struct Item
        {
            public Kind    kind;
            public Vector3 pos;
            public int     branch;   // which branch of the network this belongs to
            public int     site;     // index of the site (BC) this item serves
            public string  why;
        }

        /// <summary>The current plan, in build order. Cheapest coverage first,
        /// which is also soonest-paying first.</summary>
        internal static readonly List<Item> Items = new List<Item>(128);

        // Plan summary, for the log, the viewer and the round report.
        internal static int   Revision;
        internal static int   TerminalCount;      // untapped patches the scan found
        internal static int   CoveredCount;       // of those, ones this plan reaches
        internal static int   SiteCount;          // Bio Caches in the plan
        internal static int   NodeCount;          // Nodes in the plan
        internal static int   CystCount;
        internal static int   PlanCost;           // total cash the plan asks for
        internal static float PlannedAtRoundS;

        static float _lastPlanAt = -999f;

        internal static void ResetForNewRound()
        {
            Items.Clear();
            _attempts.Clear();
            Revision = 0; TerminalCount = CoveredCount = SiteCount = NodeCount = CystCount = PlanCost = 0;
            _lastPlanAt = -999f;
            BlueprintStore.ResetForNewRound();
        }

        // ---- Asking the game twice a second is not persistence -------------
        //
        // The plan is held for 30s and the plan tick is 8s, so every item in it
        // was being re-requested four times per refresh. The game refuses a
        // repeat while its own placement search is pending, so the extra three
        // are pure noise: 768 refused Node requests against 75 accepted ones in
        // eight minutes on NarakaCity 2026-08-04, which also buried the fires
        // that did land.
        //
        // A placement search resolves in milliseconds. If the structure is not
        // standing 20 seconds later, asking again is right; asking every tick
        // never was.

        struct Attempt { public Kind kind; public Vector3 pos; public float at; }
        static readonly List<Attempt> _attempts = new List<Attempt>(64);
        const float RETRY_S = 20f;
        const float RETRY_M = 45f;

        internal static bool RecentlyAsked(Kind k, Vector3 p)
        {
            float now = Time.time;
            bool hit = false;
            for (int i = _attempts.Count - 1; i >= 0; i--)
            {
                if (now - _attempts[i].at > RETRY_S) { _attempts.RemoveAt(i); continue; }
                if (hit || _attempts[i].kind != k) continue;
                if (SqXZ(_attempts[i].pos, p) < RETRY_M * RETRY_M) hit = true;
            }
            return hit;
        }

        internal static void NoteAsked(Kind k, Vector3 p) =>
            _attempts.Add(new Attempt { kind = k, pos = p, at = Time.time });

        // ---- Geometry the plan is built against ---------------------------
        //
        // All derived from live ConstructionData (EcoSimulator reads it at round
        // start), so a balance mod that changes structure ranges changes the
        // blueprint with it.

        /// <summary>How close a Bio Cache has to get to its patch before the
        /// chain has arrived. Mirrors EcoPlanner.IsPatchBcReachable so the plan
        /// does not lay a node the executor then judges unnecessary.</summary>
        static float BcTapReachM => EcoSimulator.BcPlaceReachM + 40f;

        /// <summary>One node hop. Same 0.75 of reach the executor steps with,
        /// leaving margin for the game sliding a placement.</summary>
        static float HopM => EcoSimulator.NODE_REACH_M * 0.75f;

        /// <summary>A patch this close to a Bio Cache is already being worked —
        /// the co-harvest radius used everywhere else in the planner.</summary>
        const float SERVED_M = 50f;

        /// <summary>Do not plan a node that lands on top of something we own or
        /// have already planned.</summary>
        const float NODE_MERGE_M = 45f;

        /// <summary>Chain length one site may ask for. Not a distance ceiling —
        /// a far patch is allowed, it just sorts to the back on cost — but a
        /// guard against planning twenty hops to something across the map.</summary>
        const int MAX_HOPS_PER_SITE = 10;

        // ---- Planning ------------------------------------------------------

        /// <summary>Replan on the interval, or immediately when asked. Roots are
        /// live structures, so a replan reconciles with what has been built by
        /// construction — it never restarts from nothing.</summary>
        internal static void MaybeReplan(EcoState s, System.Func<Vector3, bool> explored)
        {
            float now = Time.time;
            if (now - _lastPlanAt < BlueprintConfig.ReplanS) return;
            _lastPlanAt = now;
            try { Replan(s, explored); }
            catch (System.Exception ex) { MelonLogger.Warning("[BLUEPRINT] replan threw: " + ex.Message); }
        }

        /// <summary>A point the network can grow from. `pathM` is how far it
        /// sits from the Nest ALONG THE NETWORK, which is what a chain and a
        /// relocating shrimp actually have to travel. `kids` counts branches
        /// already hanging off it — the second one is a fork.</summary>
        struct NetPoint { public Vector3 pos; public int branch; public float pathM; public int kids; }

        static readonly List<NetPoint> _net       = new List<NetPoint>(256);
        static readonly List<int>      _terminals = new List<int>(128);
        static readonly List<float>    _bestCost  = new List<float>(128);
        static readonly List<int>      _bestAnchor= new List<int>(128);
        static readonly List<int>      _bestHops  = new List<int>(128);

        static void Replan(EcoState s, System.Func<Vector3, bool> explored)
        {
            Items.Clear();
            _net.Clear(); _terminals.Clear();
            _bestCost.Clear(); _bestAnchor.Clear(); _bestHops.Clear();

            // 1) SCAN. Everything we own is a root the network may grow from;
            //    every discovered patch nobody works is a terminal.
            int nextBranch = 0;
            if (s.nestPos != Vector3.zero)
                _net.Add(new NetPoint { pos = s.nestPos, branch = nextBranch++, pathM = 0f });
            for (int i = 0; i < s.bcs.Count; i++)
                _net.Add(new NetPoint { pos = s.bcs[i].pos, branch = nextBranch++,
                                        pathM = Mathf.Sqrt(SqXZ(s.bcs[i].pos, s.nestPos)) });
            for (int i = 0; i < s.nodes.Count; i++)
                _net.Add(new NetPoint { pos = s.nodes[i].pos, branch = nextBranch++,
                                        pathM = Mathf.Sqrt(SqXZ(s.nodes[i].pos, s.nestPos)) });
            if (_net.Count == 0) return;

            for (int p = 0; p < s.patches.Count; p++)
            {
                if (s.patches[p].remaining <= 0) continue;
                if (explored != null && !explored(s.patches[p].pos)) continue;
                bool served = false;
                for (int b = 0; b < s.bcs.Count && !served; b++)
                    if (SqXZ(s.bcs[b].pos, s.patches[p].pos) < SERVED_M * SERVED_M) served = true;
                if (!served) _terminals.Add(p);
            }
            TerminalCount = _terminals.Count;
            if (_terminals.Count == 0) { Publish(s); return; }

            // Cheapest attachment for each terminal, updated incrementally as
            // the network grows rather than rescanned from scratch.
            for (int t = 0; t < _terminals.Count; t++)
            {
                _bestCost.Add(float.MaxValue); _bestAnchor.Add(-1); _bestHops.Add(0);
                Relax(s, t, 0, _net.Count);
            }

            var attached = new bool[_terminals.Count];
            int sites = 0, covered = 0;

            // 2) PLAN. Attach the terminal that is cheapest per patch it brings
            //    in, materialise its chain, and let the chain serve the next
            //    one. Cost sharing is the whole mechanism: nothing else makes
            //    branches merge instead of running side by side.
            while (sites < BlueprintConfig.MaxSites)
            {
                // COST DECIDES THE NETWORK, SPEED DECIDES THE ORDER.
                //
                // Marginal cost alone builds a correct tree in a bad order: once
                // a chain arrives somewhere, the next patch off its head is as
                // cheap as anything near home, so the plan walks one line out to
                // 1,700m before touching the other side of the base. That is the
                // travelling front again, one layer up.
                //
                // So price each attachment in cash AND in the seconds before it
                // earns: the chain has to be built hop by hop, the Bio Cache
                // after it, and a shrimp has to walk out along the network. All
                // three come from live constants, so this is a measurement, not
                // a weighting to tune.
                int pick = -1; float pickScore = float.MaxValue;
                for (int t = 0; t < _terminals.Count; t++)
                {
                    if (attached[t] || _bestAnchor[t] < 0) continue;
                    Vector3 tp = s.patches[_terminals[t]].pos;

                    // A site that lands in a cluster pays once and covers
                    // several — the cluster preference is arithmetic here, not
                    // a bonus rule bolted on top.
                    int brings = 0;
                    for (int u = 0; u < _terminals.Count; u++)
                    {
                        if (attached[u]) continue;
                        if (SqXZ(s.patches[_terminals[u]].pos, tp) < SERVED_M * SERVED_M) brings++;
                    }
                    if (brings <= 0) brings = 1;

                    var an = _net[_bestAnchor[t]];
                    float pathM = an.pathM + Mathf.Sqrt(SqXZ(an.pos, tp));
                    float delayS = _bestHops[t] * EcoSimulator.NODE_BUILD_S
                                 + EcoSimulator.BC_BUILD_S
                                 + pathM / Mathf.Max(1f, EcoSimulator.SHRIMP_SPEED);

                    float score = _bestCost[t] * delayS / brings;
                    if (score < pickScore) { pickScore = score; pick = t; }
                }
                if (pick < 0) break;

                Vector3 target = s.patches[_terminals[pick]].pos;
                var     anchorPt = _net[_bestAnchor[pick]];
                Vector3 anchor = anchorPt.pos;
                int     branch = anchorPt.branch;
                int     hops   = _bestHops[pick];
                int     first  = _net.Count;

                // BRANCH IDENTITY IS WHERE THE TREE FORKS.
                //
                // A chain leaving a point nothing else has left continues that
                // branch; the second chain off the same point starts a new one.
                // Trunks and twigs then colour themselves in the viewer, and the
                // articulation structure NodeManager cares about is visible in
                // the plan rather than discovered after the fact.
                if (anchorPt.kids > 0) branch = nextBranch++;
                anchorPt.kids++;
                _net[_bestAnchor[pick]] = anchorPt;

                float len = Mathf.Sqrt(SqXZ(anchor, target));
                if (len > 1f)
                {
                    float dx = (target.x - anchor.x) / len, dz = (target.z - anchor.z) / len;
                    for (int h = 1; h <= hops; h++)
                    {
                        Vector3 np = new Vector3(anchor.x + dx * HopM * h, anchor.y, anchor.z + dz * HopM * h);
                        if (NearAnyNetPoint(np, NODE_MERGE_M)) continue;
                        Items.Add(new Item { kind = Kind.Node, pos = np, branch = branch, site = sites, why = "reach" });
                        _net.Add(new NetPoint { pos = np, branch = branch, pathM = anchorPt.pathM + HopM * h });
                    }
                }

                Items.Add(new Item { kind = Kind.BioCache, pos = target, branch = branch, site = sites, why = "tap" });
                _net.Add(new NetPoint { pos = target, branch = branch, pathM = anchorPt.pathM + len });

                // Everything inside the new site's co-harvest radius is served
                // by it and costs nothing more.
                attached[pick] = true; covered++;
                for (int u = 0; u < _terminals.Count; u++)
                {
                    if (attached[u]) continue;
                    if (SqXZ(s.patches[_terminals[u]].pos, target) < SERVED_M * SERVED_M)
                    { attached[u] = true; covered++; }
                }

                for (int t = 0; t < _terminals.Count; t++)
                    if (!attached[t]) Relax(s, t, first, _net.Count);

                sites++;
            }

            CoveredCount = covered;

            // 3) PRODUCERS, DECIDED SEPARATELY. See CystPass.
            CystPass(s);

            Publish(s);
        }

        /// <summary>Cheapest way to reach terminal t from network points in
        /// [from,to). Cost is what the plan will actually pay: hops of Node plus
        /// the Bio Cache at the end.</summary>
        static void Relax(EcoState s, int t, int from, int to)
        {
            Vector3 tp = s.patches[_terminals[t]].pos;
            for (int n = from; n < to; n++)
            {
                float d = Mathf.Sqrt(SqXZ(_net[n].pos, tp));
                int hops = d <= BcTapReachM ? 0 : Mathf.CeilToInt((d - BcTapReachM) / HopM);
                if (hops > MAX_HOPS_PER_SITE) continue;
                float cost = hops * EcoSimulator.NODE_COST + EcoSimulator.BC_COST;
                // Distance breaks ties between equal-cost anchors, so a chain
                // starts from the closest thing we own rather than an arbitrary
                // one at the same hop count.
                float keyed = cost * 100000f + d;
                float bestKeyed = _bestAnchor[t] < 0 ? float.MaxValue
                    : _bestCost[t] * 100000f + Mathf.Sqrt(SqXZ(_net[_bestAnchor[t]].pos, tp));
                if (keyed < bestKeyed)
                { _bestCost[t] = cost; _bestAnchor[t] = n; _bestHops[t] = hops; }
            }
        }

        static bool NearAnyNetPoint(Vector3 p, float m)
        {
            for (int i = 0; i < _net.Count; i++)
                if (SqXZ(_net[i].pos, p) < m * m) return true;
            return false;
        }

        // ---- Producers -----------------------------------------------------

        /// <summary>
        /// WHERE THE SHRIMPS COME FROM IS A SHRIMP QUESTION.
        ///
        /// Kept out of the tree cost on purpose. A Lesser Cyst is 1,500 spent to
        /// grow shrimps at a site; it is waste when migration was going to staff
        /// the site anyway, and it is worse than waste when the team is against
        /// the unit cap — the observed failure was a row of Lessers through the
        /// middle of the map that could not produce while the shrimps were all
        /// east.
        ///
        /// So three tests, in the order they can kill the idea:
        ///   1. Is anything already covering this ground?
        ///   2. Is there cap room to produce at all?
        ///   3. Would migration staff it without us paying?
        /// </summary>
        static void CystPass(EcoState s)
        {
            float freeAgentM = EcoSimulator.SHRIMP_SPEED * ShrimpGroupPlanner.FreeAgentMaxWalkS;
            float walkReachM = Mathf.Max(freeAgentM, MapProfile.P90PatchM * BlueprintConfig.RelocationSpacings);
            float coverM     = EcoPlannerConfig.Phase2CystCoverageRadiusM;
            bool  capBound   = s.totalShrimps >= BlueprintConfig.ShrimpCapHeadroomFrom;

            var planned = new List<Vector3>(8);
            int added = 0;
            // Walk the sites in build order so the near ones get producers first.
            for (int i = 0; i < Items.Count && added < BlueprintConfig.MaxCystsPerPlan; i++)
            {
                if (Items[i].kind != Kind.BioCache) continue;
                Vector3 at = Items[i].pos;

                bool covered = false;
                for (int c = 0; c < s.cysts.Count && !covered; c++)
                    if (SqXZ(s.cysts[c].pos, at) < coverM * coverM) covered = true;
                for (int c = 0; c < planned.Count && !covered; c++)
                    if (SqXZ(planned[c], at) < coverM * coverM) covered = true;
                if (covered) continue;

                if (capBound) continue;   // nothing it produced could exist

                float nearestCyst = float.MaxValue;
                for (int c = 0; c < s.cysts.Count; c++)
                {
                    float d = Mathf.Sqrt(SqXZ(s.cysts[c].pos, at));
                    if (d < nearestCyst) nearestCyst = d;
                }
                float staffed = 0f;
                try { staffed = ShrimpGroupPlanner.StaffedFraction(at); } catch { }
                bool reachableOnFoot = nearestCyst <= walkReachM;
                if (reachableOnFoot && staffed >= BlueprintConfig.StaffedEnough) continue;

                planned.Add(at);
                added++;
                Items.Add(new Item
                {
                    kind = Kind.Cyst, pos = at, branch = Items[i].branch, site = Items[i].site,
                    why  = reachableOnFoot ? "understaffed" : "nobody can walk here",
                });
            }
        }

        // ---- Publish -------------------------------------------------------

        static void Publish(EcoState s)
        {
            Revision++;
            PlannedAtRoundS = Time.time;
            NodeCount = SiteCount = CystCount = 0; PlanCost = 0;
            for (int i = 0; i < Items.Count; i++)
            {
                switch (Items[i].kind)
                {
                    case Kind.Node:     NodeCount++; PlanCost += EcoSimulator.NODE_COST; break;
                    case Kind.BioCache: SiteCount++; PlanCost += EcoSimulator.BC_COST;   break;
                    case Kind.Cyst:     CystCount++; PlanCost += EcoSimulator.CYST_COST; break;
                }
            }

            var sb = new System.Text.StringBuilder("[BLUEPRINT] rev=");
            sb.Append(Revision)
              .Append(" untapped=").Append(TerminalCount)
              .Append(" covered=").Append(CoveredCount)
              .Append(" sites=").Append(SiteCount)
              .Append(" nodes=").Append(NodeCount)
              .Append(" cysts=").Append(CystCount)
              .Append(" cost=").Append(PlanCost)
              .Append(" cash=").Append(s.cash);
            int shown = 0;
            for (int i = 0; i < Items.Count && shown < 5; i++)
            {
                if (Items[i].kind != Kind.BioCache) continue;
                int hops = 0;
                for (int j = 0; j < Items.Count; j++)
                    if (Items[j].kind == Kind.Node && Items[j].site == Items[i].site) hops++;
                sb.Append(" | b").Append(Items[i].branch)
                  .Append(" (").Append(Items[i].pos.x.ToString("F0")).Append(',')
                  .Append(Items[i].pos.z.ToString("F0")).Append(") +").Append(hops).Append("n");
                shown++;
            }
            MelonLogger.Msg(sb.ToString());

            BlueprintStore.Write(s);
        }

        static float SqXZ(Vector3 a, Vector3 b)
        {
            float dx = a.x - b.x, dz = a.z - b.z;
            return dx * dx + dz * dz;
        }
    }
}
