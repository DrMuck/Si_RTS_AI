using MelonLoader;
using Silica;
using System.Collections;
using System.Reflection;
using UnityEngine;

namespace Si_RTS_AI.Planning
{
    /// <summary>
    /// Snapshots the LIVE game state for one team into an EcoState. Handles:
    ///   - Biotics patches (ResourceArea.AllResourceAreas filtered by team's usable type)
    ///   - Finished structures (team.Structures): Nest / Bio Cache / Cyst / Node
    ///   - In-progress construction sites (ConstructionSite.ConstructionSites)
    ///   - Shrimp counts and per-BC assignment (nearest-BC heuristic)
    ///
    /// Called at the top of each EcoPlanner.MaybePlan invocation so decisions
    /// are always made against fresh reality — no cached invariants that could
    /// stale-out after a player disrupts something.
    /// </summary>
    public static class EcoStateBuilder
    {
        // Approximated remaining-time for a construction site we can't read
        // progress from cleanly. Fine for iteration-1 planning — the horizon
        // is 120s so a 20s under/over-estimate barely moves scores.
        const float DEFAULT_BUILD_REMAINING_S = 20f;

        public static EcoState Build(Team team)
        {
            var s = new EcoState();
            if (team == null) return s;
            try { s.cash = team.TotalResources; } catch { }
            try { s.cap  = team.ResourceCapacity; } catch { }

            // ---- Patches ----
            try
            {
                var all = ResourceArea.AllResourceAreas;
                if (all != null)
                {
                    var uType = team.UsableResource;
                    for (int i = 0; i < all.Count; i++)
                    {
                        var ra = all[i];
                        if (ra == null || ra.IsEmpty || ra.ResourceType != uType) continue;
                        s.patches.Add(new EcoState.Patch
                        {
                            pos       = ra.SignalCenter,
                            remaining = ra.ResourceAmountCurrent,
                            maxCap    = ra.ResourceAmountMax,
                        });
                    }
                }
            }
            catch (System.Exception ex) { MelonLogger.Warning("[PLAN/Build] patches: " + ex.Message); }

            // ---- Finished structures ----
            try
            {
                var structs = team.Structures;
                if (structs != null)
                    for (int i = 0; i < structs.Count; i++)
                    {
                        var st = structs[i];
                        if (st == null || st.ObjectInfo == null || st.IsDestroyed) continue;
                        string n = st.ObjectInfo.DisplayName ?? "";
                        Vector3 p = st.transform.position;
                        if (n == "Nest")
                        {
                            s.nestPos = p;
                            // The Nest also has Shrimp in its ConstructionOptions, and
                            // AlienShrimpProducer queues Shrimps in ANY structure that
                            // can build one — Nest included. Model this by adding a
                            // synthetic Cyst at the Nest position: the sim now knows
                            // shrimps grow at ~1 per 20s from t=0, not just from the
                            // first real Cyst. Without this the beam undervalued
                            // building a second BC in the opening (marginal shrimp = 0
                            // since it thought the shrimp pool never grows).
                            s.cysts.Add(new EcoState.Cyst
                            {
                                pos = p, finished = true, readyAt = 0f,
                                nextSpawnAt = EcoSimulator.SHRIMP_BUILD_S,
                            });
                        }
                        else if (n == "Bio Cache")
                        {
                            // A BC in team.Structures may still be under construction —
                            // Silica adds the Structure object at construction START
                            // (not at finish). IsFunctional distinguishes finished
                            // structures from in-progress sites. Missing this filter
                            // was the cause of the "Cyst placed while BC still building"
                            // bug: the state builder reported the BC as finished, so
                            // the enumerator happily proposed a Cyst on it 10s into
                            // the BC's 20s build.
                            bool funcBc = ReadIsFunctional(st);
                            s.bcs.Add(new EcoState.Bc
                            {
                                pos = p, finished = funcBc,
                                readyAt = funcBc ? 0f : EcoSimulator.BC_BUILD_S * 0.5f,   // approximate
                                storage = ReadResourceHolderStored(st), storageCap = 4000,
                            });
                        }
                        else if (n == "Lesser Spawning Cyst")
                        {
                            bool funcCyst = ReadIsFunctional(st);
                            s.cysts.Add(new EcoState.Cyst
                            {
                                pos = p, finished = funcCyst,
                                readyAt = funcCyst ? 0f : EcoSimulator.CYST_BUILD_S * 0.5f,
                                nextSpawnAt = funcCyst ? EcoSimulator.SHRIMP_BUILD_S * 0.5f
                                                       : EcoSimulator.CYST_BUILD_S + EcoSimulator.SHRIMP_BUILD_S,
                            });
                        }
                        else if (n == "Node")
                        {
                            bool funcNode = ReadIsFunctional(st);
                            s.nodes.Add(new EcoState.Node
                            {
                                pos = p, finished = funcNode,
                                readyAt = funcNode ? 0f : EcoSimulator.NODE_BUILD_S * 0.5f,
                            });
                        }
                    }
            }
            catch (System.Exception ex) { MelonLogger.Warning("[PLAN/Build] structs: " + ex.Message); }

            // ---- In-progress construction sites ----
            try
            {
                var sites = ConstructionSite.ConstructionSites;
                if (sites != null)
                    for (int i = 0; i < sites.Count; i++)
                    {
                        var cs = sites[i];
                        if (cs == null || cs.Team != team || cs.IsDestroyed || cs.ObjectInfo == null) continue;
                        string n = cs.ObjectInfo.DisplayName ?? "";
                        Vector3 p = cs.transform.position;
                        float readyAt = DEFAULT_BUILD_REMAINING_S;
                        if (n == "Bio Cache")
                            s.bcs.Add(new EcoState.Bc { pos = p, finished = false, readyAt = readyAt, storage = 0, storageCap = 4000 });
                        else if (n == "Lesser Spawning Cyst")
                            s.cysts.Add(new EcoState.Cyst { pos = p, finished = false, readyAt = readyAt,
                                                            nextSpawnAt = readyAt + EcoSimulator.SHRIMP_BUILD_S });
                        else if (n == "Node")
                            s.nodes.Add(new EcoState.Node { pos = p, finished = false, readyAt = readyAt });
                    }
            }
            catch (System.Exception ex) { MelonLogger.Warning("[PLAN/Build] sites: " + ex.Message); }

            // ---- Shrimps -> assign to nearest finished BC ----
            try
            {
                var units = team.Units;
                if (units != null)
                    for (int i = 0; i < units.Count; i++)
                    {
                        var u = units[i];
                        if (u == null || u.ObjectInfo == null || u.IsDestroyed) continue;
                        if (u.ObjectInfo.DisplayName != "Shrimp") continue;
                        s.totalShrimps++;
                        int bcIdx = ClosestFinishedBcIdx(s, u.transform.position);
                        if (bcIdx >= 0)
                        {
                            s.shrimpsPerBc.TryGetValue(bcIdx, out int cur);
                            s.shrimpsPerBc[bcIdx] = cur + 1;
                        }
                    }
            }
            catch (System.Exception ex) { MelonLogger.Warning("[PLAN/Build] units: " + ex.Message); }

            return s;
        }

        static int ClosestFinishedBcIdx(EcoState s, Vector3 pos)
        {
            int best = -1;
            float bd = float.MaxValue;
            for (int i = 0; i < s.bcs.Count; i++)
            {
                if (!s.bcs[i].finished) continue;
                var d = (s.bcs[i].pos - pos);
                float sq = d.x * d.x + d.z * d.z;
                if (sq < bd) { bd = sq; best = i; }
            }
            return best;
        }

        // Reflection cache for IsFunctional access. The property is on Structure
        // but not always exposed cleanly through the strongly-typed API given
        // Il2Cpp interop; a cached PropertyInfo is fastest.
        static PropertyInfo? _piIsFunctional;
        static bool ReadIsFunctional(object structure)
        {
            try
            {
                if (_piIsFunctional == null)
                    _piIsFunctional = structure.GetType().GetProperty("IsFunctional",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (_piIsFunctional == null) return true;   // fail-open: assume finished
                var v = _piIsFunctional.GetValue(structure);
                return v is bool b ? b : true;
            }
            catch { return true; }
        }

        // Reflection cache for ResourceHolder access — same pattern
        // ShrimpStateSampler uses (strong-typed API doesn't expose these).
        static PropertyInfo? _piResourceHolders;
        static PropertyInfo? _piAmountStored;
        static int ReadResourceHolderStored(object entity)
        {
            try
            {
                if (_piResourceHolders == null)
                    _piResourceHolders = entity.GetType().GetProperty("ResourceHolders",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (_piResourceHolders == null) return 0;
                var list = _piResourceHolders.GetValue(entity) as IEnumerable;
                if (list == null) return 0;
                foreach (var h in list)
                {
                    if (_piAmountStored == null)
                        _piAmountStored = h.GetType().GetProperty("AmountStored",
                            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (_piAmountStored == null) return 0;
                    var v = _piAmountStored.GetValue(h);
                    return v is int i ? i : 0;
                }
            }
            catch { }
            return 0;
        }
    }
}
