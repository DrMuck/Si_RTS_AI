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
            public Vector3 from;     // what it hangs off — the edge, for drawing and review
            public int     branch;   // which branch of the network this belongs to
            public int     site;     // index of the site (BC) this item serves
            public float   pathM;    // distance from the Nest ALONG the network
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

        /// <summary>How far from a patch a Bio Cache can still be the thing
        /// working it. Wide enough to contain the game's placement slide, which
        /// has been observed at 95m — the test that matters is whether this is
        /// the patch NEAREST to it, not the distance itself.</summary>
        const float BC_WORKS_M = 220f;

        /// <summary>Chain length one site may ask for. Not a distance ceiling —
        /// a far patch is allowed, it just sorts to the back on cost — but a
        /// guard against planning twenty hops to something across the map.</summary>
        const int MAX_HOPS_PER_SITE = 10;

        // ---- Planning ------------------------------------------------------

        /// <summary>Replan on the interval, or immediately when asked. Roots are
        /// live structures, so a replan reconciles with what has been built by
        /// construction — it never restarts from nothing.</summary>
        internal static void MaybeReplan(EcoState s, System.Func<Vector3, bool> explored,
                                         float incomePerSec)
        {
            float now = Time.time;
            if (now - _lastPlanAt < BlueprintConfig.ReplanS) return;
            _lastPlanAt = now;
            try { Replan(s, explored, incomePerSec); }
            catch (System.Exception ex) { MelonLogger.Warning("[BLUEPRINT] replan threw: " + ex.Message); }
        }

        /// <summary>A point the network can grow from. `pathM` is how far it
        /// sits from the Nest ALONG THE NETWORK, which is what a chain and a
        /// relocating shrimp actually have to travel. `kids` counts branches
        /// already hanging off it — the second one is a fork.</summary>
        struct NetPoint { public Vector3 pos; public int branch; public float pathM; public int kids; }

        static readonly List<NetPoint> _net       = new List<NetPoint>(256);
        static int _rootCount;
        static readonly List<int>      _terminals = new List<int>(128);
        static readonly List<float>    _bestCost  = new List<float>(128);
        static readonly List<int>      _bestAnchor= new List<int>(128);
        static readonly List<int>      _bestHops  = new List<int>(128);
        static readonly List<float>    _bestKey   = new List<float>(128);

        static void Replan(EcoState s, System.Func<Vector3, bool> explored, float incomePerSec)
        {
            Items.Clear();
            _net.Clear(); _terminals.Clear();
            _bestCost.Clear(); _bestAnchor.Clear(); _bestHops.Clear(); _bestKey.Clear();

            // 1) SCAN. Everything we own is a root the network may grow from;
            //    every discovered patch nobody works is a terminal.
            if (s.nestPos != Vector3.zero)
                _net.Add(new NetPoint { pos = s.nestPos, branch = BranchKey(s.nestPos, Vector3.zero), pathM = 0f });
            // Roots key on their own bearing from the Nest, so two structures
            // sharing a grid cell but lying on different arms stay different
            // branches — otherwise the executor's one-site-per-branch hold can
            // stall a direction that was never the one building.
            for (int i = 0; i < s.bcs.Count; i++)
                _net.Add(new NetPoint { pos = s.bcs[i].pos,
                                        branch = BranchKey(s.bcs[i].pos, s.bcs[i].pos - s.nestPos),
                                        pathM = Mathf.Sqrt(SqXZ(s.bcs[i].pos, s.nestPos)) });
            for (int i = 0; i < s.nodes.Count; i++)
                _net.Add(new NetPoint { pos = s.nodes[i].pos,
                                        branch = BranchKey(s.nodes[i].pos, s.nodes[i].pos - s.nestPos),
                                        pathM = Mathf.Sqrt(SqXZ(s.nodes[i].pos, s.nestPos)) });
            if (_net.Count == 0) return;
            _rootCount = _net.Count;      // everything before this is standing
            ResolveRootPaths(s.nestPos);

            for (int p = 0; p < s.patches.Count; p++)
            {
                if (s.patches[p].remaining <= 0) continue;
                if (explored != null && !explored(s.patches[p].pos)) continue;

                // IS ANYBODY ALREADY WORKING THIS PATCH?
                //
                // Asking only "is a Bio Cache within 50m" produced doubles.
                // DrMuck, 2026-08-05: two Bio Caches at (2531,2094) and
                // (2638,2102) for one biotics. The game slides a placement —
                // 69m and 95m slides are in this round's log — so a Bio Cache
                // aimed AT a patch can land 100m away, which is close enough to
                // work it and too far to satisfy a 50m test. The plan then saw
                // an unserved patch and built a second one beside the first.
                //
                // A Bio Cache works the patch nearest to it. That, not a radius,
                // is what "served" means; the radius only has to be wide enough
                // to contain a slide.
                bool served = false;
                for (int b = 0; b < s.bcs.Count && !served; b++)
                {
                    float d2 = SqXZ(s.bcs[b].pos, s.patches[p].pos);
                    if (d2 < SERVED_M * SERVED_M) { served = true; break; }
                    if (d2 > BC_WORKS_M * BC_WORKS_M) continue;
                    int nearest = EcoSimulator.NearestActivePatchIdxPublic(s, s.bcs[b].pos);
                    if (nearest == p) served = true;
                }
                if (!served) _terminals.Add(p);
            }
            TerminalCount = _terminals.Count;
            if (_terminals.Count == 0) { Publish(s); return; }

            // Cheapest attachment for each terminal, updated incrementally as
            // the network grows rather than rescanned from scratch.
            for (int t = 0; t < _terminals.Count; t++)
            {
                _bestCost.Add(float.MaxValue); _bestAnchor.Add(-1); _bestHops.Add(0);
                _bestKey.Add(float.MaxValue);
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

                    // Already priced in cash x seconds by Relax — the only
                    // thing left is how many patches the site brings in.
                    float score = _bestKey[t] / brings;
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
                if (anchorPt.kids > 0) branch = BranchKey(anchor, target - anchor);
                anchorPt.kids++;
                _net[_bestAnchor[pick]] = anchorPt;

                float len = Mathf.Sqrt(SqXZ(anchor, target));
                Vector3 prev = anchor;
                if (len > 1f)
                {
                    float dx = (target.x - anchor.x) / len, dz = (target.z - anchor.z) / len;
                    for (int h = 1; h <= hops; h++)
                    {
                        Vector3 np = new Vector3(anchor.x + dx * HopM * h, anchor.y, anchor.z + dz * HopM * h);
                        if (NearAnyNetPoint(np, NODE_MERGE_M)) continue;
                        Items.Add(new Item { kind = Kind.Node, pos = np, from = prev, branch = branch,
                                             site = sites, pathM = anchorPt.pathM + HopM * h, why = "reach" });
                        _net.Add(new NetPoint { pos = np, branch = branch, pathM = anchorPt.pathM + HopM * h });
                        prev = np;
                    }
                }

                Items.Add(new Item { kind = Kind.BioCache, pos = target, from = prev, branch = branch,
                                     site = sites, pathM = anchorPt.pathM + len, why = "tap" });
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

            // 3) WHO CAN STAFF EACH SITE, AND WHEN.
            try { SupplyForecast.Rebuild(s); } catch { }
            var siteInfo = BuildSiteInfo();

            // 4) HOW HARD TO PUSH, AND HOW MANY PRODUCERS TO PAY FOR.
            //    The tree is fixed by now, so the sweep runs against the real
            //    sites it just planned, with the same producer PLACEMENT the
            //    Cyst pass will use — otherwise it prices a strategy nobody
            //    executes.
            if (BlueprintConfig.CystStrategyAuto)
                ExpansionStrategy.KickOff(s, siteInfo, incomePerSec);

            // 5) PRODUCERS, DECIDED SEPARATELY. See CystPass.
            CystPass(s, siteInfo);

            // 6) WHERE THE BRANCHES SHOULD MEET. Shadow only for now.
            BridgePass();

            Publish(s);
        }

        /// <summary>
        /// Best way to reach terminal t from network points in [from,to).
        ///
        /// WHERE TO HANG IT FROM IS THE SAME QUESTION AS WHEN IT PAYS.
        ///
        /// This used to pick purely on cash — fewest hops, ties broken by
        /// distance — and that is what rebuilds a snake. The tip of a chain
        /// three kilometres down the map is one hop from the next patch, so it
        /// always beat a rib hanging two hops off the trunk, however long the
        /// road back to the Nest had become. DrMuck, 2026-08-05: "a big branch
        /// from south to middle is forming again, while an east to west growth
        /// could have happened from the eastern line."
        ///
        /// An anchor deep down a branch is not cheap. Everything hung there
        /// inherits its whole path: the build chain walks it, the shrimps walk
        /// it, and the site pays that latency for the rest of the round. So the
        /// anchor is chosen in the same currency the build order already uses —
        /// cash times seconds — and a rib off the trunk wins on latency what it
        /// loses on an extra node.
        /// </summary>
        static void Relax(EcoState s, int t, int from, int to)
        {
            Vector3 tp = s.patches[_terminals[t]].pos;
            for (int n = from; n < to; n++)
            {
                float d = Mathf.Sqrt(SqXZ(_net[n].pos, tp));
                int hops = d <= BcTapReachM ? 0 : Mathf.CeilToInt((d - BcTapReachM) / HopM);
                if (hops > MAX_HOPS_PER_SITE) continue;
                float cost   = hops * EcoSimulator.NODE_COST + EcoSimulator.BC_COST;
                float pathM  = _net[n].pathM + d;
                float delayS = hops * EcoSimulator.NODE_BUILD_S + EcoSimulator.BC_BUILD_S
                             + pathM / Mathf.Max(1f, EcoSimulator.SHRIMP_SPEED);
                float keyed  = cost * delayS;
                if (_bestAnchor[t] < 0 || keyed < _bestKey[t])
                { _bestKey[t] = keyed; _bestCost[t] = cost; _bestAnchor[t] = n; _bestHops[t] = hops; }
            }
        }

        /// <summary>
        /// HOW FAR A BUILT STRUCTURE REALLY IS FROM THE NEST — along the
        /// network, not across the map.
        ///
        /// The straight-line distance was a lie in exactly the case that
        /// matters. A chain that snakes south and then west ends up with a tip
        /// that is 1,200m from the Nest as the crow flies and 3,000m of road,
        /// and hanging the next site off it inherits the road, not the crow.
        /// Using the crow's number is what made snake tips look like cheap
        /// anchors and rebuilt the snake every replan.
        ///
        /// Plain Dijkstra over the built structures, linked where one could
        /// have been built off the other. Anything the walk cannot reach keeps
        /// its straight-line value — an isolated structure is a NodeManager
        /// problem, not this one.
        /// </summary>
        static void ResolveRootPaths(Vector3 nest)
        {
            int n = _net.Count;
            if (n == 0 || nest == Vector3.zero) return;
            float link = EcoSimulator.BcPlaceReachM;
            float link2 = link * link;

            var dist = new float[n];
            var done = new bool[n];
            int start = -1;
            for (int i = 0; i < n; i++)
            {
                dist[i] = float.MaxValue;
                if (start < 0 && SqXZ(_net[i].pos, nest) < 1f) start = i;
            }
            if (start < 0) return;
            dist[start] = 0f;

            for (int k = 0; k < n; k++)
            {
                int u = -1; float best = float.MaxValue;
                for (int i = 0; i < n; i++)
                    if (!done[i] && dist[i] < best) { best = dist[i]; u = i; }
                if (u < 0) break;
                done[u] = true;
                for (int v = 0; v < n; v++)
                {
                    if (done[v]) continue;
                    float d2 = SqXZ(_net[u].pos, _net[v].pos);
                    if (d2 > link2) continue;
                    float nd = dist[u] + Mathf.Sqrt(d2);
                    if (nd < dist[v]) dist[v] = nd;
                }
            }

            for (int i = 0; i < n; i++)
            {
                if (dist[i] == float.MaxValue) continue;   // keep the fallback
                var p = _net[i];
                p.pathM = dist[i];
                _net[i] = p;
            }
        }

        /// <summary>
        /// A BRANCH IS A PLACE ON THE MAP, NOT A NUMBER IN A LOOP.
        ///
        /// Branch ids were a counter, handed out in whatever order the replan
        /// happened to walk the tree — so every refresh renumbered everything.
        /// DrMuck, 2026-08-05: a Bio Cache finished, "leaded to a change in the
        /// branch color and maybe to confusion", and the north-west direction
        /// off that same structure then failed to start.
        ///
        /// That is not cosmetic. The executor holds each branch to one live
        /// site, so shuffled ids mean it can hold a direction that was never
        /// the one building — one fork of a junction blocking the other because
        /// they swapped numbers. Colour and throttle are the same identity, and
        /// both need to survive a replan.
        ///
        /// So the id is derived from where the branch LEAVES and which way it
        /// GOES: the fork point on a 200m grid, and the bearing in 30-degree
        /// sectors. The same physical branch keeps its identity while the
        /// structure it hangs off stays put, and two directions off one
        /// junction are always different branches — which is exactly the case
        /// that was stalling.
        /// </summary>
        static int BranchKey(Vector3 at, Vector3 dir)
        {
            int gx = Mathf.RoundToInt(at.x / 200f);
            int gz = Mathf.RoundToInt(at.z / 200f);
            int sector = 0;
            float len = Mathf.Sqrt(dir.x * dir.x + dir.z * dir.z);
            if (len > 1f)
            {
                float deg = Mathf.Atan2(dir.z, dir.x) * Mathf.Rad2Deg;
                if (deg < 0f) deg += 360f;
                sector = Mathf.FloorToInt(deg / 30f) + 1;   // 0 reserved for "no direction"
            }
            unchecked
            {
                int h = gx * 73856093 ^ gz * 19349663 ^ sector * 83492791;
                return Mathf.Abs(h) % 997;
            }
        }

        static bool NearAnyNetPoint(Vector3 p, float m)
        {
            for (int i = 0; i < _net.Count; i++)
                if (SqXZ(_net[i].pos, p) < m * m) return true;
            return false;
        }

        // ---- Producers -----------------------------------------------------

        /// <summary>One planned site, with the chain it needs and the answer to
        /// the only question that decides its producer: when could enough
        /// shrimps be standing here without one.</summary>
        internal struct SiteInfo
        {
            public Vector3 pos;
            public int     nodes;      // chain hops this site costs
            public int     item;       // index into Items of the Bio Cache
            public float   arrivalS;   // when migration could staff it; +inf = never
        }

        static List<SiteInfo> BuildSiteInfo()
        {
            int need = Mathf.Max(1, ShrimpGroupPlanner.TypicalGroupCapacity);
            var list = new List<SiteInfo>(16);
            for (int i = 0; i < Items.Count; i++)
            {
                if (Items[i].kind != Kind.BioCache) continue;
                int nodes = 0;
                for (int j = 0; j < Items.Count; j++)
                    if (Items[j].kind == Kind.Node && Items[j].site == Items[i].site) nodes++;
                float arrival = float.PositiveInfinity;
                try { arrival = SupplyForecast.ArrivalTimeFor(Items[i].pos, need); } catch { }
                list.Add(new SiteInfo { pos = Items[i].pos, nodes = nodes, item = i, arrivalS = arrival });
                if (list.Count >= 24) break;
            }
            return list;
        }

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
        static void CystPass(EcoState s, List<SiteInfo> sites)
        {
            float freeAgentM = EcoSimulator.SHRIMP_SPEED * ShrimpGroupPlanner.FreeAgentMaxWalkS;
            float walkReachM = Mathf.Max(freeAgentM, MapProfile.P90PatchM * BlueprintConfig.RelocationSpacings);
            float coverM     = EcoPlannerConfig.Phase2CystCoverageRadiusM;
            bool  capBound   = s.totalShrimps >= BlueprintConfig.ShrimpCapHeadroomFrom;

            var planned = new List<Vector3>(8);
            int added = 0;
            // HOW MANY comes from the strategy sweep when it is running, which
            // prices producers against the shrimp cap and against migration.
            // The config value is the manual answer to the same question.
            int budget = BlueprintConfig.CystStrategyAuto
                ? ExpansionStrategy.Producers : BlueprintConfig.MaxCystsPerPlan;
            // BEHIND THE TRAJECTORY OUTRANKS THE SWEEP.
            //
            // The sweep prices producers over its horizon and has been choosing
            // zero or one; the soak rounds say producer count is the strongest
            // single predictor of where the economy ends up. When the worker
            // curve is behind its reference, that measurement wins — the sweep
            // still decides the rest.
            if (WorkerPlan.BehindSchedule && WorkerPlan.ProducersNeeded > budget)
                budget = WorkerPlan.ProducersNeeded;

            // FORCED RATIO — the experiment arm. One producer per N planned
            // sites, which is DrMuck's "skip 1-4 biotics, then the next one
            // gets a Lesser". Expressed as a density rather than as literal
            // every-Nth-in-build-order so it composes with worst-supplied-first
            // placement instead of fighting it.
            int perSites = BlueprintConfig.ProducerPerSites;
            if (perSites > 0)
                budget = Mathf.Max(1, Mathf.CeilToInt(sites.Count / (float)perSites));

            // PRODUCERS GO WHERE MIGRATION DOES NOT REACH — WHICH IS OUTWARD.
            //
            // This walked the sites in BUILD order, so the nearest ones got the
            // producers. That is backwards, and DrMuck named the reason on
            // 2026-08-05: put the Lesser at the site further out, and the near
            // site you skipped becomes the room that older shrimps relocate
            // into when their patch runs dry. One Cyst then does two jobs —
            // staffs ground nothing can walk to, and seeds the expansion past
            // it — while the near ground staffs itself for free.
            //
            // Ranking by forecast arrival says the same thing without a rule
            // about distance: near sites are where migration lands soonest, so
            // they sort last, and a site nobody can reach sorts first.
            var order = new List<SiteInfo>(sites);
            order.Sort((a, b) => b.arrivalS.CompareTo(a.arrivalS));

            for (int oi = 0; oi < order.Count && added < budget; oi++)
            {
                int i = order[oi].item;
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

                // WOULD A PRODUCER GET THERE FIRST?
                //
                // The comparison prices itself. A Lesser Cyst delivers a full
                // group after its own build plus one shrimp per build interval;
                // migration delivers whoever comes free when a patch runs out,
                // plus the walk. If migration wins that race, the 1,500 buys
                // nothing but a queue against the shrimp cap.
                //
                // No new constant: the deadline IS what a Cyst would take.
                int   need     = Mathf.Max(1, ShrimpGroupPlanner.TypicalGroupCapacity);
                float cystTime = EcoSimulator.CYST_BUILD_S + need * EcoSimulator.SHRIMP_BUILD_S;
                if (order[oi].arrivalS <= cystTime)
                {
                    MelonLogger.Msg($"[BLUEPRINT] no Cyst at ({at.x:F0},{at.z:F0}) — " +
                                    $"{need} shrimps can walk here in {order[oi].arrivalS:F0}s, " +
                                    $"a Cyst would need {cystTime:F0}s");
                    continue;
                }

                planned.Add(at);
                added++;
                Items.Add(new Item
                {
                    kind = Kind.Cyst, pos = at, from = at,
                    branch = Items[i].branch, site = Items[i].site,
                    why  = float.IsInfinity(order[oi].arrivalS) ? "nobody can walk here"
                         : reachableOnFoot ? "understaffed" : "migration too slow",
                });
                MelonLogger.Msg($"[BLUEPRINT] Cyst at ({at.x:F0},{at.z:F0}) — " +
                                (float.IsInfinity(order[oi].arrivalS)
                                    ? $"{need} shrimps could never walk here in time"
                                    : $"migration needs {order[oi].arrivalS:F0}s, a Cyst {cystTime:F0}s"));
            }
        }

        // ---- Bridges -------------------------------------------------------

        internal struct Bridge
        {
            public Vector3 a, b;
            public int branchA, branchB;
            public int hops;      // Nodes needed to close the gap
            public int protects;  // structures on the smaller of the two sides
            public float savedM;  // road the loop removes between its two ends
            public int beyond;    // structures that walk the shorter road
            public int cuts;      // single points of failure it bypasses
        }

        /// <summary>Cross-branch joins worth making, best first. SHADOW —
        /// published, logged and drawn, but nothing builds them yet.</summary>
        internal static readonly List<Bridge> Bridges = new List<Bridge>(4);

        /// <summary>
        /// WHERE A LOOP PAYS IS A TOPOLOGY QUESTION.
        ///
        /// NodeManager finds loops by scanning built structures pairwise under a
        /// distance ceiling, which is why a 1,900m bridge got filtered on its
        /// length rather than judged on what it connects. The blueprint knows
        /// the branch structure BEFORE it is built, so the candidates are named
        /// rather than discovered: two branches that pass close to each other,
        /// each carrying enough structure that losing either one matters.
        ///
        /// Value is the smaller side — a bridge protects what it can carry for
        /// the other, so a spur of three nodes joined to a trunk of forty is
        /// worth three, not forty. Cost is the nodes it takes to close.
        /// </summary>
        static void BridgePass()
        {
            Bridges.Clear();
            if (_rootCount < 4) return;

            // BRIDGE WHAT EXISTS, NOT WHAT IS PLANNED.
            //
            // This searched Items — which holds only UNBUILT work — so every
            // bridge joined two chains that did not exist yet, its first hop had
            // no anchor to build from, and the executor refused it as
            // out-of-reach every single time. One candidate was re-proposed 98
            // times in a round and never laid a node. A bridge is insurance on
            // structures you own; planning one between two futures is not a
            // bridge at all.
            //
            // Over the built network the question is simple: two structures that
            // are FAR APART ALONG THE ROAD but close in a straight line. The
            // Dijkstra pass above already put the road distance on every root.
            // A LOOP IS NOT A SHORTCUT TO THE NEST.
            //
            // The first version priced a link as far-path minus near-path minus
            // gap — the saving on the road HOME. For two branches that are both
            // far out, which is exactly what a big loop joins, that number is
            // near zero or negative, so every worthwhile loop was rejected on
            // value while small shortcuts near the base got built. DrMuck named
            // three links worth having and all three fit the hop budget already;
            // they failed here.
            //
            // What a link is worth is the DETOUR IT REMOVES: how far apart the
            // two ends are along the existing road, against how far apart they
            // are in a straight line. Two ends that are 60m apart and 3km around
            // are the loop worth building.
            var found = new List<Bridge>(8);
            var adj = BuildAdjacency();
            FindCutVertices(adj);
            var byStart = new Dictionary<int, List<int>>();
            float maxSpan = EcoSimulator.NODE_REACH_M + BRIDGE_MAX_HOPS * HopM;
            for (int i = 0; i < _rootCount; i++)
            for (int j = i + 1; j < _rootCount; j++)
            {
                float gap = Mathf.Sqrt(SqXZ(_net[i].pos, _net[j].pos));
                if (gap <= EcoSimulator.NODE_REACH_M || gap > maxSpan) continue;
                if (!byStart.TryGetValue(i, out var l)) { l = new List<int>(4); byStart[i] = l; }
                l.Add(j);
            }

            foreach (var kv in byStart)
            {
                var road = RoadDistancesFrom(kv.Key, adj, out var parent);
                foreach (int j in kv.Value)
                {
                    float gap = Mathf.Sqrt(SqXZ(_net[kv.Key].pos, _net[j].pos));
                    int hops = Mathf.CeilToInt((gap - EcoSimulator.NODE_REACH_M) / HopM);
                    float byRoad = road[j];

                    // Unreachable along the network means the link would JOIN
                    // two severed pieces, which is the most valuable case there
                    // is — price it at the whole span rather than discarding it
                    // as infinite.
                    float saved = float.IsInfinity(byRoad) ? maxSpan * 4f : byRoad - gap;
                    if (saved <= BRIDGE_MIN_SAVED_M) continue;

                    float far = Mathf.Max(_net[kv.Key].pathM, _net[j].pathM);
                    int beyond = 0;
                    for (int k = 0; k < _rootCount; k++) if (_net[k].pathM >= far) beyond++;
                    if (beyond < BRIDGE_MIN_PROTECT) continue;

                    // SINGLE POINTS OF FAILURE ON THAT ROAD.
                    //
                    // DrMuck named three structures whose loss would sever a
                    // branch and asked for links that avoid exactly that. A cut
                    // vertex on the road between the two ends is that structure:
                    // bypass it and the branch behind it survives losing it.
                    int cuts = 0;
                    for (int at = parent[j]; at >= 0 && at != kv.Key; at = parent[at])
                        if (_isCut != null && at < _isCut.Length && _isCut[at]) cuts++;

                    bool startIsNear = _net[kv.Key].pathM <= _net[j].pathM;
                    found.Add(new Bridge
                    {
                        a = startIsNear ? _net[kv.Key].pos : _net[j].pos,
                        b = startIsNear ? _net[j].pos : _net[kv.Key].pos,
                        branchA = _net[kv.Key].branch, branchB = _net[j].branch,
                        hops = hops, protects = beyond,
                        savedM = saved, beyond = beyond, cuts = cuts,
                    });
                }
            }

            found.Sort((p, q) => BridgeValue(q).CompareTo(BridgeValue(p)));
            for (int i = 0; i < found.Count && i < BRIDGE_MAX; i++) Bridges.Add(found[i]);

            // Emit the best one as buildable Nodes, starting at the NEAR end so
            // the first hop hangs off something that is standing.
            if (Bridges.Count == 0) return;
            var win = Bridges[0];
            float len = Mathf.Sqrt(SqXZ(win.a, win.b));
            if (len < 1f) return;
            float ux = (win.b.x - win.a.x) / len, uz = (win.b.z - win.a.z) / len;
            Vector3 prevPt = win.a;
            for (int h = 1; h <= win.hops; h++)
            {
                var np = new Vector3(win.a.x + ux * HopM * h, win.a.y, win.a.z + uz * HopM * h);
                if (NearAnyNetPoint(np, NODE_MERGE_M)) continue;
                Items.Add(new Item
                {
                    kind = Kind.Node, pos = np, from = prevPt,
                    branch = win.branchA, site = -1, pathM = 0f, why = "bridge",
                });
                prevPt = np;
            }
            MelonLogger.Msg($"[BLUEPRINT] bridge: {win.hops} node(s) from " +
                            $"({win.a.x:F0},{win.a.z:F0}) to ({win.b.x:F0},{win.b.z:F0}) — " +
                            $"saves {win.savedM:F0}m of road for {win.beyond} structures, " +
                            $"bypasses {win.cuts} single point(s) of failure");
        }



        /// <summary>
        /// Structures whose loss splits the network — Tarjan's articulation
        /// points over the built graph. A branch hanging off one of these is a
        /// single point of failure, and a link that bypasses one converts that
        /// into a loop. This is the principled answer to WHERE redundancy pays:
        /// anywhere else, a link buys road and nothing else.
        /// </summary>
        static bool[] _isCut;

        static void FindCutVertices(List<int>[] adj)
        {
            _isCut = new bool[_rootCount];
            var disc = new int[_rootCount];
            var low = new int[_rootCount];
            var parent = new int[_rootCount];
            for (int i = 0; i < _rootCount; i++) { disc[i] = -1; parent[i] = -1; }
            int timer = 0;

            // Iterative DFS — a recursive one would blow the stack on a large
            // late-game network.
            var stack = new Stack<(int node, int childIdx)>();
            for (int root = 0; root < _rootCount; root++)
            {
                if (disc[root] >= 0) continue;
                int rootChildren = 0;
                stack.Push((root, 0));
                disc[root] = low[root] = timer++;
                while (stack.Count > 0)
                {
                    var (u, ci) = stack.Pop();
                    if (ci < adj[u].Count)
                    {
                        stack.Push((u, ci + 1));
                        int v = adj[u][ci];
                        if (disc[v] < 0)
                        {
                            parent[v] = u;
                            if (u == root) rootChildren++;
                            disc[v] = low[v] = timer++;
                            stack.Push((v, 0));
                        }
                        else if (v != parent[u])
                        {
                            if (disc[v] < low[u]) low[u] = disc[v];
                        }
                    }
                    else if (parent[u] >= 0)
                    {
                        int p = parent[u];
                        if (low[u] < low[p]) low[p] = low[u];
                        if (p != root && low[u] >= disc[p]) _isCut[p] = true;
                    }
                }
                if (rootChildren > 1) _isCut[root] = true;
            }
        }

        /// <summary>Which built structures can reach which, at building range.</summary>
        static List<int>[] BuildAdjacency()
        {
            var adj = new List<int>[_rootCount];
            float link = EcoSimulator.BcPlaceReachM, link2 = link * link;
            for (int i = 0; i < _rootCount; i++) adj[i] = new List<int>(6);
            for (int i = 0; i < _rootCount; i++)
                for (int j = i + 1; j < _rootCount; j++)
                    if (SqXZ(_net[i].pos, _net[j].pos) <= link2)
                    { adj[i].Add(j); adj[j].Add(i); }
            return adj;
        }

        /// <summary>Road distance from one structure to every other, along the
        /// network as it stands. This is what makes a loop measurable: the gap
        /// between two ends in a straight line means nothing until you know how
        /// far apart they are by road.</summary>
        static float[] RoadDistancesFrom(int src, List<int>[] adj) =>
            RoadDistancesFrom(src, adj, out _);

        static float[] RoadDistancesFrom(int src, List<int>[] adj, out int[] parent)
        {
            parent = new int[_rootCount];
            for (int i = 0; i < _rootCount; i++) parent[i] = -1;
            var dist = new float[_rootCount];
            var done = new bool[_rootCount];
            for (int i = 0; i < _rootCount; i++) dist[i] = float.PositiveInfinity;
            dist[src] = 0f;
            for (int n = 0; n < _rootCount; n++)
            {
                int u = -1; float best = float.PositiveInfinity;
                for (int i = 0; i < _rootCount; i++)
                    if (!done[i] && dist[i] < best) { best = dist[i]; u = i; }
                if (u < 0) break;
                done[u] = true;
                foreach (int v in adj[u])
                {
                    if (done[v]) continue;
                    float nd = dist[u] + Mathf.Sqrt(SqXZ(_net[u].pos, _net[v].pos));
                    if (nd < dist[v]) { dist[v] = nd; parent[v] = u; }
                }
            }
            return dist;
        }

        /// <summary>How much a bypassed single point of failure multiplies what
        /// a link is worth. Redundancy is the reason loops exist, so it is not a
        /// tie-breaker.</summary>
        const float CUT_VERTEX_WEIGHT = 3f;

        /// <summary>Road actually removed before a link is worth its nodes. A
        /// shortcut that saves fifty metres is not a bridge.</summary>
        const float BRIDGE_MIN_SAVED_M = 300f;

        /// <summary>
        /// What a bridge is worth per node it costs. Two terms, both in seconds:
        /// the walk it removes for everything beyond the join, and the
        /// redundancy it buys, priced as the build time of the structures that
        /// would otherwise be lost with one severed link.
        /// </summary>
        static float BridgeValue(Bridge b)
        {
            float walkSaved = Mathf.Max(0f, b.savedM) * b.beyond
                            / Mathf.Max(1f, EcoSimulator.SHRIMP_SPEED);
            // Every cut vertex bypassed is a branch that stops depending on one
            // structure surviving. Weighted against what it protects, because
            // removing a single point of failure in front of forty structures
            // is worth more than in front of four.
            float insurance = b.protects * EcoSimulator.BC_BUILD_S
                            * (1f + b.cuts * CUT_VERTEX_WEIGHT);
            return (walkSaved + insurance) / Mathf.Max(1, b.hops * EcoSimulator.NODE_COST);
        }

        /// <summary>Structures a branch must carry before joining it is worth
        /// anything. Below this the loop protects a stub.</summary>
        const int BRIDGE_MIN_PROTECT = 3;

        /// <summary>Nodes a bridge may cost. Not a map-distance ceiling — the
        /// thing that made the old loop finder reject the bridge that mattered —
        /// but a bound on what one join may spend.</summary>
        /// <summary>Nodes one link may cost. Raised from 6 so a genuinely big
        /// loop is affordable — DrMuck, 2026-08-06: "bridging bigger loops is
        /// more welcome."</summary>
        const int BRIDGE_MAX_HOPS = 8;

        const int BRIDGE_MAX = 3;

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
            for (int i = 0; i < Bridges.Count; i++)
                sb.Append(" | bridge b").Append(Bridges[i].branchA).Append("-b").Append(Bridges[i].branchB)
                  .Append(' ').Append(Bridges[i].hops).Append("n protects ").Append(Bridges[i].protects);
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
