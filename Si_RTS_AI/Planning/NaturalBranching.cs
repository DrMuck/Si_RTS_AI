using MelonLoader;
using System.Collections.Generic;
using UnityEngine;

namespace Si_RTS_AI.Planning
{
    /// <summary>
    /// Expansion as growth rather than as a scoring contest.
    ///
    /// The beam earns its place in the opening, where a short horizon and real
    /// trade-offs make sequencing genuinely hard. Mid-game expansion is not that
    /// problem. It is "there is untapped ground, go and take it", and dressing
    /// that up as an optimisation is what produced a single travelling front:
    /// every candidate scored against the CURRENT frontier, so the cheapest next
    /// placement was always the next hop along the same line.
    ///
    /// Nearly every defect fixed on 2026-08-03/04 was a scoring rule producing a
    /// decision no human would make — the distance-based spread bonus, the
    /// staffing gate, the map-wide afterBc freeze, an incumbency ceiling that
    /// starved 23 empty groups, front spacing measured at the hop instead of the
    /// destination, and a phase promoted by a clock. A greedy nearest-frontier
    /// rule cannot produce any of them. It is omnidirectional by construction,
    /// it cannot strand a latitude band, and when it does something odd the
    /// reason is visible in one line rather than inferred from a score.
    ///
    /// The one judgement worth keeping is what to put on the ground once we
    /// arrive: a Bio Cache alone, or a Bio Cache and a Lesser Cyst. That is
    /// answered from the shrimp side, which already knows whether migration will
    /// staff the site (StaffedFraction, counting shrimps present AND walking)
    /// and whether anyone can reach it on foot at all.
    ///
    /// SHADOW ONLY. Proposes and logs; places nothing. GrowthModel has sat
    /// wired to nothing but LogShadow for weeks precisely because it was never
    /// checked against a round — this gets checked first.
    /// </summary>
    internal static class NaturalBranching
    {
        const float REPORT_S = 30f;
        const int   REPORT_MAX = 6;

        static float _lastReportAt;

        internal static void ResetForNewRound() => _lastReportAt = 0f;

        struct Branch
        {
            public Vector3 From;      // frontier structure we grow out of
            public Vector3 To;        // untapped patch we grow toward
            public float   Dist;
            public bool    WantCyst;  // BC alone, or BC + Lesser
            public string  Why;
        }

        /// <summary>
        /// Every frontier structure reaches for its own nearest untapped patch.
        /// No global contest, so growth happens everywhere at once rather than
        /// wherever one winning score happens to point.
        /// </summary>
        internal static void LogShadow(EcoState s)
        {
            float now = Time.time;
            if (now - _lastReportAt < REPORT_S) return;
            _lastReportAt = now;

            try
            {
                // Anywhere we could branch FROM.
                var sources = new List<Vector3>(32);
                if (s.nestPos != Vector3.zero) sources.Add(s.nestPos);
                for (int i = 0; i < s.bcs.Count; i++)   if (s.bcs[i].finished)   sources.Add(s.bcs[i].pos);
                for (int i = 0; i < s.nodes.Count; i++) sources.Add(s.nodes[i].pos);

                // Anywhere worth reaching: patches with resource left and no
                // Bio Cache close enough to already be working them.
                var targets = new List<Vector3>(64);
                for (int p = 0; p < s.patches.Count; p++)
                {
                    if (s.patches[p].remaining <= 0) continue;
                    bool served = false;
                    for (int b = 0; b < s.bcs.Count; b++)
                    {
                        float dx = s.bcs[b].pos.x - s.patches[p].pos.x;
                        float dz = s.bcs[b].pos.z - s.patches[p].pos.z;
                        if (dx * dx + dz * dz < SERVED_M * SERVED_M) { served = true; break; }
                    }
                    if (!served) targets.Add(s.patches[p].pos);
                }

                // Each source claims its nearest unclaimed target. Claiming
                // keeps two neighbours from both walking to the same patch,
                // which is what spreads growth around the perimeter instead of
                // bunching it.
                var taken = new bool[targets.Count];
                var branches = new List<Branch>(16);
                for (int si = 0; si < sources.Count; si++)
                for (int claim = 0; claim < BRANCHES_PER_SOURCE; claim++)
                {
                    // SEVERAL TAPS OFF ONE POINT, AND SIDEWAYS BY PREFERENCE.
                    //
                    // One claim per source meant a trunk node with biotics on
                    // both sides tapped one and ignored the other, so growth
                    // stayed a line with occasional stubs. Side taps
                    // perpendicular to the trunk are the shape we actually
                    // want: the trunk carries reach outward, the ribs collect
                    // what it passes, and between them they cover ground
                    // rather than cross it.
                    //
                    // "Outward" for a source is its bearing from the Nest, so
                    // perpendicular means across that bearing. A rib is scored
                    // as if it were nearer than it is, which lets it beat a
                    // slightly closer patch that lies straight ahead — where
                    // the trunk is going anyway and will reach on its own.
                    Vector3 outward = sources[si] - s.nestPos;
                    float ol = Mathf.Sqrt(outward.x * outward.x + outward.z * outward.z);
                    bool haveOutward = ol > 1f;
                    float ox = haveOutward ? outward.x / ol : 0f;
                    float oz = haveOutward ? outward.z / ol : 0f;

                    int best = -1; float bestD = float.MaxValue;
                    for (int ti = 0; ti < targets.Count; ti++)
                    {
                        if (taken[ti]) continue;
                        float dx = targets[ti].x - sources[si].x;
                        float dz = targets[ti].z - sources[si].z;
                        float d = Mathf.Sqrt(dx * dx + dz * dz);
                        if (d > MAX_TAP_M) continue;      // not this source's business

                        float eff = d;
                        if (haveOutward && d > 1f)
                        {
                            // 0 straight ahead, 1 straight sideways.
                            float along = Mathf.Abs((dx * ox + dz * oz) / d);
                            eff = d * Mathf.Lerp(1f, PERPENDICULAR_PREFERENCE, 1f - along);
                        }
                        if (eff < bestD) { bestD = eff; best = ti; }
                    }
                    if (best < 0) continue;
                    taken[best] = true;
                    bestD = Mathf.Sqrt(SqDistXZ(targets[best], sources[si]));

                    // THE ONE REAL DECISION: does this site need a producer?
                    //
                    // If migration will staff it, a Lesser is 1,500 spent on
                    // shrimps that were already coming. If nothing can reach it
                    // on foot, it must grow its own or the Bio Cache sits idle.
                    float staffed = 0f;
                    try { staffed = ShrimpGroupPlanner.StaffedFraction(targets[best]); } catch { }
                    // HOW FAR WILL SHRIMPS ACTUALLY COME TO THIS SITE?
                    //
                    // First shadow round said wantCyst=16/16, almost all
                    // "nobody can walk here" — which would rebuild the 1:1
                    // Cyst-to-Bio-Cache ratio we are trying to kill. The rule
                    // was not wrong, the yardstick was: FreeAgentMaxWalkS is
                    // the budget for an idle shrimp wandering to a nearby
                    // patch (~270m), and a NEW expansion site is by definition
                    // further from production than that. Every site therefore
                    // failed the test. The proposals were 658-800m out.
                    //
                    // Walk TIME does not scale with map size — 900m costs the
                    // same 100s anywhere. What scales is the comparison: on a
                    // sparse map like NarakaCity the nearest alternative is
                    // simply further away, so a longer relocation is the
                    // reasonable choice rather than an extravagant one. That
                    // is what P90PatchM measures, and using it means the
                    // threshold follows the map instead of a constant tuned
                    // against whichever map was open at the time.
                    float freeAgentM = EcoSimulator.SHRIMP_SPEED * ShrimpGroupPlanner.FreeAgentMaxWalkS;
                    float spacingM   = MapProfile.P90PatchM;
                    float walkReachM = Mathf.Max(freeAgentM, spacingM * RELOCATION_SPACINGS);

                    float nearestCyst = float.MaxValue;
                    for (int c = 0; c < s.cysts.Count; c++)
                    {
                        float dx = s.cysts[c].pos.x - targets[best].x;
                        float dz = s.cysts[c].pos.z - targets[best].z;
                        float d = Mathf.Sqrt(dx * dx + dz * dz);
                        if (d < nearestCyst) nearestCyst = d;
                    }

                    bool reachableOnFoot = nearestCyst <= walkReachM;
                    bool wantCyst = !(reachableOnFoot && staffed >= STAFFED_ENOUGH);

                    branches.Add(new Branch
                    {
                        From = sources[si], To = targets[best], Dist = bestD,
                        WantCyst = wantCyst,
                        Why = wantCyst
                            ? (reachableOnFoot ? "understaffed" : "nobody can walk here")
                            : "migration covers it",
                    });
                }

                branches.Sort((a, b) => a.Dist.CompareTo(b.Dist));

                var sb = new System.Text.StringBuilder("[BRANCH/SHADOW] sources=");
                sb.Append(sources.Count).Append(" untapped=").Append(targets.Count)
                  .Append(" branches=").Append(branches.Count);
                int withCyst = 0;
                for (int i = 0; i < branches.Count; i++) if (branches[i].WantCyst) withCyst++;
                sb.Append(" wantCyst=").Append(withCyst).Append('/').Append(branches.Count);

                for (int i = 0; i < branches.Count && i < REPORT_MAX; i++)
                {
                    var br = branches[i];
                    sb.Append(" | (").Append(br.To.x.ToString("F0")).Append(',')
                      .Append(br.To.z.ToString("F0")).Append(") ")
                      .Append(br.Dist.ToString("F0")).Append("m ")
                      .Append(br.WantCyst ? "BC+Cyst" : "BC only")
                      .Append(" [").Append(br.Why).Append(']');
                }
                MelonLogger.Msg(sb.ToString());
            }
            catch (System.Exception ex)
            {
                MelonLogger.Warning("[BRANCH/SHADOW] threw: " + ex.Message);
            }
        }

        /// <summary>A patch this close to a Bio Cache is already being worked.
        /// Matches the co-harvest radius the planner already uses.</summary>
        const float SERVED_M = 50f;

        /// <summary>Fraction of a site's capacity that migration must already
        /// cover before a Lesser Cyst is considered redundant there.</summary>
        const float STAFFED_ENOUGH = 0.8f;

        /// <summary>Taps one frontier point may claim in a pass. More than one
        /// so a trunk node with biotics either side collects both.</summary>
        const int BRANCHES_PER_SOURCE = 3;

        /// <summary>How far a frontier point will reach for a patch. Beyond
        /// this it is somebody else's, and letting one source claim distant
        /// ground is what turns growth back into a race down a single line.</summary>
        const float MAX_TAP_M = 900f;

        /// <summary>Effective-distance multiplier for a patch lying straight
        /// out along the trunk's own bearing, against one lying across it. Below
        /// 1 for the perpendicular case, so ribs outbid patches ahead of the
        /// trunk — which the trunk reaches anyway.</summary>
        const float PERPENDICULAR_PREFERENCE = 0.6f;

        /// <summary>How many typical patch spacings a shrimp will relocate
        /// across before a site is judged unreachable on foot. Shrimps have been
        /// observed relocating well over 1,000m quite sensibly when the
        /// alternative was further still, so this is a comparison against what
        /// the map offers rather than a fixed distance.</summary>
        const float RELOCATION_SPACINGS = 2f;

        static float SqDistXZ(Vector3 a, Vector3 b)
        {
            float dx = a.x - b.x, dz = a.z - b.z;
            return dx * dx + dz * dz;
        }
    }
}
