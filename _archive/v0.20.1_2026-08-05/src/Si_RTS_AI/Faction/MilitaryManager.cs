using MelonLoader;
using Silica;
using Silica.AI;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Si_RTS_AI.Faction
{
    /// <summary>
    /// BASIC Military Manager for Alien — MVP scaffold. User 2026-07-09:
    /// "Just want to add a basic military manager, we will rebuild it later."
    ///
    /// Responsibilities (all opt-in via MilitaryEnabled preference):
    ///  1. Threat perception — scan enemy units near own Nest + BCs.
    ///  2. Target picking — rank enemy HVTs: HQ, Refinery, Harvester,
    ///     in-progress structures (e.g. HQ under construction).
    ///  3. Production planning — queue Crabs at a fraction of Cysts
    ///     (Crab is cheap tier-0 combat = swarm harassment). Shrimp
    ///     production still runs alongside via AlienShrimpProducer.
    ///  4. Army coordination — group combat units at a rally point,
    ///     only attack once army >= critical mass, defend base when
    ///     threat inbound. "Only use critical army for attacks (not
    ///     sending in units 1 by 1)."
    ///
    /// Requires SuppressCombat=false for actual attack orders to fire.
    /// Defensive orders (recall to base) fire regardless.
    ///
    /// This scaffold does perception + production in production; army
    /// coordination is stubbed to log-only until the AIGroup order-issuing
    /// API is wired. Iteration hook: SendAttackOrder / SendMoveOrder.
    /// </summary>
    internal static class MilitaryManager
    {
        // Config knobs (surfaced via MilitaryConfig preferences).
        internal static bool  Enabled                    = false;
        internal static int   CriticalMassSize          = 15;    // min combat units before attacking
        internal static float CystCrabProductionFraction= 0.25f; // 25% of Cysts make Crabs, rest keep making Shrimps
        internal static float ThreatScanRadiusM         = 400f;  // enemy within this range of a structure = threat
        internal static float TargetScanRadiusM         = 3500f; // pick enemy targets within this range of Nest
        internal const  float TICK_CADENCE_S            = 3f;    // scan + decide every 3s

        static float _lastTickAt;
        // Log-throttle so verbose scan lines don't spam.
        static float _lastLogAt;
        const   float LOG_INTERVAL_S = 15f;

        // Worker unit names — excluded from "combat unit" classification.
        static readonly HashSet<string> WorkerUnitNames =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Shrimp", "Queen" };

        // Producer structures we manage — any Cyst variant + Grand Spawner.
        static readonly HashSet<string> ProducerStructureNames =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "Lesser Spawning Cyst",
                "Greater Spawning Cyst",
                "Colossus Cyst",
                "Grand Spawner",
            };

        // ================================================================
        // Public tick — called from PeriodicTelemetryTick for Alien teams.
        // ================================================================
        internal static void Tick(Team team)
        {
            if (!Enabled) return;
            if (team == null) return;
            if (!FactionControl.IsEnabled(team)) return;
            if (!TestHarnessNs.TestHarness.IsRoundActive) return;

            float now = Time.time;
            if (now - _lastTickAt < TICK_CADENCE_S) return;
            _lastTickAt = now;

            try
            {
                var threats = ScanThreats(team);
                var targets = RankTargets(team);
                var army    = FindCombatUnits(team);

                // User 2026-07-09: "In phase 1 we invest fully on eco. Just
                // once the tech is up and eco is getting better, a higher
                // focus on military can be taken." Gate production on
                // Phase 2 + at least tier 1 (Cortex placed + first research
                // done). Army-command still runs — we can defend base with
                // whatever units we have from the start.
                if (CanProduceMilitary(team))
                {
                    ManageProducerStructures(team);
                    ManageProduction(team);
                }
                ManageArmy(team, army, threats, targets);

                if (now - _lastLogAt >= LOG_INTERVAL_S)
                {
                    _lastLogAt = now;
                    int tier = 0; try { tier = team.TechnologyTier; } catch { }
                    bool phase2 = Planning.EcoPlanner.CurrentPhase == Planning.EcoPlanner.PlanPhase.Phase2_Expand;
                    string prodGate = CanProduceMilitary(team)
                        ? "PROD-ON"
                        : $"PROD-OFF(phase2={phase2},tier={tier})";
                    MelonLogger.Msg($"[MIL] team={team.name} army={army.Count} threats={threats.Count} " +
                                    $"targets={targets.Count} {prodGate} " +
                                    (targets.Count > 0
                                        ? $"topTarget={targets[0].Kind}@({targets[0].Pos.x:F0},{targets[0].Pos.z:F0})"
                                        : "topTarget=none"));
                }
            }
            catch (Exception ex) { MelonLogger.Warning("[MIL] Tick threw: " + ex.Message); }
        }

        // ================================================================
        // Threat perception — enemy units near own structures.
        // ================================================================
        struct Threat { public Unit Unit; public Vector3 Pos; public float NearestOwnDsq; }

        static List<Threat> ScanThreats(Team team)
        {
            var threats = new List<Threat>();
            var ownPositions = new List<Vector3>();
            try
            {
                var structs = team.Structures;
                if (structs != null)
                    for (int i = 0; i < structs.Count; i++)
                    {
                        var st = structs[i];
                        if (st?.ObjectInfo == null || st.IsDestroyed) continue;
                        string n = st.ObjectInfo.DisplayName ?? "";
                        if (n == "Nest" || n == "Bio Cache") ownPositions.Add(st.transform.position);
                    }
            }
            catch { }
            if (ownPositions.Count == 0) return threats;

            float scanSq = ThreatScanRadiusM * ThreatScanRadiusM;

            // Iterate enemy teams (Alien enemies = Human teams).
            try
            {
                var cmds = AIManager.Commanders;
                if (cmds == null) return threats;
                foreach (var kv in cmds)
                {
                    var otherTeam = kv.Key;
                    if (otherTeam == null || otherTeam == team) continue;
                    if (!IsEnemyOf(team, otherTeam)) continue;
                    var units = otherTeam.Units;
                    if (units == null) continue;
                    for (int i = 0; i < units.Count; i++)
                    {
                        var u = units[i];
                        if (u == null || u.IsDestroyed) continue;
                        Vector3 p = u.transform.position;
                        float bestDsq = float.MaxValue;
                        for (int j = 0; j < ownPositions.Count; j++)
                        {
                            float dx = ownPositions[j].x - p.x;
                            float dz = ownPositions[j].z - p.z;
                            float dsq = dx * dx + dz * dz;
                            if (dsq < bestDsq) bestDsq = dsq;
                        }
                        if (bestDsq <= scanSq)
                            threats.Add(new Threat { Unit = u, Pos = p, NearestOwnDsq = bestDsq });
                    }
                }
            }
            catch { }
            return threats;
        }

        // ================================================================
        // Target picking — rank enemy HVTs.
        // ================================================================
        internal struct Target { public string Kind; public Vector3 Pos; public float Priority; }

        static List<Target> RankTargets(Team team)
        {
            var results = new List<Target>();
            Vector3 nestPos = FindNestPos(team);
            if (nestPos == Vector3.zero) return results;
            float scanSq = TargetScanRadiusM * TargetScanRadiusM;

            try
            {
                var cmds = AIManager.Commanders;
                if (cmds == null) return results;
                foreach (var kv in cmds)
                {
                    var otherTeam = kv.Key;
                    if (otherTeam == null || otherTeam == team) continue;
                    if (!IsEnemyOf(team, otherTeam)) continue;

                    // 1) Enemy structures (HQ, Refinery, in-progress)
                    var structs = otherTeam.Structures;
                    if (structs != null)
                        for (int i = 0; i < structs.Count; i++)
                        {
                            var st = structs[i];
                            if (st?.ObjectInfo == null || st.IsDestroyed) continue;
                            Vector3 p = st.transform.position;
                            float dx = p.x - nestPos.x, dz = p.z - nestPos.z;
                            float dsq = dx * dx + dz * dz;
                            if (dsq > scanSq) continue;
                            string n = st.ObjectInfo.DisplayName ?? "";
                            float pri = 0;
                            if (n == "Headquarters")            pri = 100f;
                            else if (n == "Refinery")           pri = 80f;
                            else if (n == "Barracks")           pri = 60f;
                            else if (n == "Research Facility")  pri = 90f;
                            else                                pri = 20f; // any other structure
                            // In-progress bonus: not IsFunctional = still ramping up = cheap kill
                            bool functional = true;
                            try { functional = st.IsFunctional; } catch { }
                            if (!functional) pri += 40f;
                            // Distance falloff: closer = higher priority.
                            pri /= (1f + Mathf.Sqrt(dsq) / 500f);
                            results.Add(new Target { Kind = n, Pos = p, Priority = pri });
                        }

                    // 2) Enemy Harvesters (mobile HVT — kills their eco).
                    var units = otherTeam.Units;
                    if (units != null)
                        for (int i = 0; i < units.Count; i++)
                        {
                            var u = units[i];
                            if (u?.ObjectInfo == null || u.IsDestroyed) continue;
                            string n = u.ObjectInfo.DisplayName ?? "";
                            if (!n.Contains("Harvester")) continue;
                            Vector3 p = u.transform.position;
                            float dx = p.x - nestPos.x, dz = p.z - nestPos.z;
                            float dsq = dx * dx + dz * dz;
                            if (dsq > scanSq) continue;
                            float pri = 70f / (1f + Mathf.Sqrt(dsq) / 500f);
                            results.Add(new Target { Kind = n, Pos = p, Priority = pri });
                        }
                }
            }
            catch { }

            results.Sort((a, b) => b.Priority.CompareTo(a.Priority));
            return results;
        }

        // ================================================================
        // Production planning — queue combat units at each producer type
        // (Lesser/Greater/Colossus Cysts + Grand Spawner). Combat units
        // discovered DYNAMICALLY from each producer's ConstructionOptions;
        // no hardcoded unit names (works across tech tiers and mods).
        //
        // Per-producer strategy:
        //   Lesser Spawning Cyst: fraction (CystCrabProductionFraction) of
        //     them dedicated to combat production; rest keep making Shrimps
        //     via AlienShrimpProducer. Combat unit picked = highest-cost
        //     option (proxy for tier — cheaper Crab loses to a Wasp if the
        //     Cyst has both).
        //   Greater/Colossus/Grand: ALL of them make combat units — no
        //     Shrimp option there anyway. Best-available unit each tick
        //     (Construct returns non-Success if tech tier locks it).
        // ================================================================
        static bool _productionCatalogLogged;
        static readonly HashSet<string> _productionFailReasonsLogged = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        static void ManageProduction(Team team)
        {
            try
            {
                var structs = team.Structures;
                if (structs == null) return;

                // Group producers by type — allows the fraction rule to
                // apply to Lesser Cysts only.
                var producersByType = new Dictionary<string, List<Structure>>(StringComparer.OrdinalIgnoreCase);

                // Also record ALL structure names we see so if our
                // ProducerStructureNames set doesn't match, we can spot it.
                var seenTypeNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < structs.Count; i++)
                {
                    var st = structs[i];
                    if (st?.ObjectInfo == null || st.IsDestroyed) continue;
                    seenTypeNames.Add(st.ObjectInfo.DisplayName ?? "");
                }
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

                // One-time catalog log — dump what we discovered so we can
                // debug "no military units built despite tier 8" cases.
                if (!_productionCatalogLogged && producersByType.Count > 0)
                {
                    _productionCatalogLogged = true;
                    var sb = new System.Text.StringBuilder();
                    sb.Append("[MIL/DIAG] discovered producer structures: ");
                    foreach (var kv in producersByType)
                        sb.Append(kv.Key).Append("×").Append(kv.Value.Count).Append(" ");
                    sb.Append(" — all structure names seen: ");
                    foreach (var n in seenTypeNames) sb.Append(n).Append(",");
                    MelonLogger.Msg(sb.ToString());
                    // Also enumerate options at each producer type once.
                    foreach (var kv in producersByType)
                    {
                        var sample = kv.Value[0];
                        if (sample.ConstructionOptions == null) continue;
                        var opts = new System.Text.StringBuilder();
                        opts.Append("[MIL/DIAG] ").Append(kv.Key).Append(" options: ");
                        foreach (var opt in sample.ConstructionOptions)
                        {
                            if (opt?.ObjectInfo == null) continue;
                            string sType = "?";
                            try { sType = opt.ObjectInfo.StructureType.ToString(); } catch { }
                            opts.Append(opt.ObjectInfo.DisplayName)
                                .Append("[").Append(sType).Append(" cost=").Append(SafeCost(opt)).Append("] ");
                        }
                        MelonLogger.Msg(opts.ToString());
                    }
                }

                foreach (var kv in producersByType)
                {
                    var type = kv.Key;
                    var producers = kv.Value;
                    bool isLesser = type == "Lesser Spawning Cyst";

                    // For Lesser Cysts, use only a fraction — rest keep making
                    // Shrimps via AlienShrimpProducer. Deterministic every-Nth
                    // stride so the same Cysts stay dedicated (no oscillation).
                    IEnumerable<Structure> targetSet;
                    if (isLesser)
                    {
                        int nDedicated = Mathf.Max(1, Mathf.RoundToInt(producers.Count * CystCrabProductionFraction));
                        int step = Mathf.Max(1, producers.Count / nDedicated);
                        var picked = new List<Structure>();
                        for (int i = 0; i < producers.Count; i += step) picked.Add(producers[i]);
                        targetSet = picked;
                    }
                    else
                    {
                        targetSet = producers;   // all higher-tier producers make combat units full-time
                    }

                    foreach (var s in targetSet)
                    {
                        int queueDepth = 0;
                        try { queueDepth = s.ProductionQueue?.Count ?? 0; } catch { }
                        if (queueDepth >= 1) continue;   // shallow queues — free cash for eco / tech

                        // Collect combat-unit options at this producer.
                        // Filter workers by name (Shrimp/Queen). Everything
                        // else at a Cyst is a combat unit — we don't need
                        // the StructureType filter since Cysts don't offer
                        // structure options (only units).
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
                            if (!_productionFailReasonsLogged.Contains(type + ":empty"))
                            {
                                _productionFailReasonsLogged.Add(type + ":empty");
                                MelonLogger.Msg($"[MIL/DIAG] {type} has no eligible combat unit options (all filtered by worker-list / StructureType)");
                            }
                            continue;
                        }

                        // Highest-cost = highest tier proxy. If Construct fails
                        // (tech tier locked, cash short), try next-cheapest.
                        opts.Sort((a, b) => SafeCost(b).CompareTo(SafeCost(a)));
                        bool anyQueued = false;
                        var reasons = new System.Text.StringBuilder();
                        foreach (var opt in opts)
                        {
                            ProductionActionResult res = ProductionActionResult.Success;
                            try { res = s.Construct(opt); }
                            catch (Exception ex) { reasons.Append(opt.ObjectInfo?.DisplayName).Append("=EX(").Append(ex.Message).Append(") "); continue; }
                            if (res == ProductionActionResult.Success)
                            {
                                MelonLogger.Msg($"[MIL] queued {opt.ObjectInfo?.DisplayName} at {type} " +
                                                $"({s.transform.position.x:F0},{s.transform.position.z:F0})");
                                anyQueued = true;
                                break;
                            }
                            reasons.Append(opt.ObjectInfo?.DisplayName).Append("=").Append(res).Append(" ");
                        }
                        // First failure per producer type per round gets logged
                        // so we know why nothing queued.
                        if (!anyQueued && !_productionFailReasonsLogged.Contains(type))
                        {
                            _productionFailReasonsLogged.Add(type);
                            MelonLogger.Msg($"[MIL/DIAG] {type} tried but nothing queued: {reasons}");
                        }
                    }
                }
            }
            catch (Exception ex) { MelonLogger.Warning("[MIL] ManageProduction threw: " + ex.Message); }
        }

        static int SafeCost(ConstructionData cd)
        {
            try { return cd.ResourceCost; } catch { return 0; }
        }

        // ================================================================
        // Higher-tier producer STRUCTURE placement.
        // ================================================================
        // Nobody else builds Greater Spawning Cyst / Colossus Cyst / Grand
        // Spawner — EcoPlanner only handles BC/Cyst/Node, TechPlanner only
        // Cortex. MilitaryManager places them as tech + cash allow. Cadence
        // limited to 15s so we don't spam retries when tech tier not yet met.
        static Dictionary<string, ConstructionData> _producerStructureCds
            = new Dictionary<string, ConstructionData>(StringComparer.OrdinalIgnoreCase);
        static float _lastStructurePlacementAt;
        const   float STRUCTURE_PLACEMENT_CADENCE_S = 15f;
        // Reserve some cash so structure placement doesn't starve eco/tech.
        const   int   STRUCTURE_CASH_RESERVE = 2500;
        // Producer structures we want to build (in tier order, cheapest first).
        static readonly string[] HigherTierProducerNames = new[]
        {
            "Greater Spawning Cyst",
            "Colossus Cyst",
            "Grand Spawner",
        };

        static void ManageProducerStructures(Team team)
        {
            float now = Time.time;
            if (now - _lastStructurePlacementAt < STRUCTURE_PLACEMENT_CADENCE_S) return;

            // Discover CDs for the higher-tier producers by scanning any
            // structure's ConstructionOptions. First tick that finds them
            // caches for reuse.
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
                            if (!_producerStructureCds.ContainsKey(n))
                            {
                                for (int hi = 0; hi < HigherTierProducerNames.Length; hi++)
                                    if (HigherTierProducerNames[hi] == n) { _producerStructureCds[n] = opt; break; }
                            }
                        }
                    }
            }
            catch { }

            if (_producerStructureCds.Count == 0) return;

            int cash = 0;
            try { cash = team.TotalResources; } catch { }

            Vector3 nestPos = FindNestPos(team);
            if (nestPos == Vector3.zero) return;

            // Count how many of each producer we currently have (built + in-progress).
            var existingCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
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
                            existingCounts[n] = existingCounts.TryGetValue(n, out int v) ? v + 1 : 1;
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
                            existingCounts[n] = existingCounts.TryGetValue(n, out int v) ? v + 1 : 1;
                    }
            }
            catch { }

            // Place the first missing one in tier order (cheapest = earliest tier).
            // Cash gate leaves a reserve so eco/tech don't starve.
            foreach (var name in HigherTierProducerNames)
            {
                if (!_producerStructureCds.TryGetValue(name, out var cd)) continue;
                if (existingCounts.TryGetValue(name, out int c) && c > 0) continue;
                int cost = SafeCost(cd);
                if (cash < cost + STRUCTURE_CASH_RESERVE) return;   // wait until we have surplus
                // Fire via the game commander build API — same code path
                // TechPlanner uses for Cortex placement.
                bool fired = false;
                try { fired = AlienConstruction.TryBuildStructureByCd(team, cd, nestPos); }
                catch (Exception ex) { MelonLogger.Warning("[MIL] TryBuildStructureByCd threw: " + ex.Message); }
                _lastStructurePlacementAt = now;   // rate-limit whether fired or not
                if (fired)
                    MelonLogger.Msg($"[MIL] placed {name} near Nest ({nestPos.x:F0},{nestPos.z:F0})");
                return;   // one placement per cadence window
            }
        }

        /// <summary>
        /// Production gate — return true only when eco is stable enough
        /// to justify diverting cash to military. Currently: Phase 2 base
        /// eco settled AND at least tier 1 (Cortex built + Alpha I researched).
        /// Before this: all cash to eco expansion and tech placement.
        /// </summary>
        static bool CanProduceMilitary(Team team)
        {
            if (Planning.EcoPlanner.CurrentPhase != Planning.EcoPlanner.PlanPhase.Phase2_Expand)
                return false;
            int tier = 0;
            try { tier = team.TechnologyTier; } catch { }
            return tier >= 1;
        }

        // ================================================================
        // Army coordination — group + attack/defend decisions.
        // ================================================================
        static void ManageArmy(Team team, List<Unit> army, List<Threat> threats, List<Target> targets)
        {
            if (army.Count == 0) return;

            // 1) Defensive: if enemy inside base perimeter, recall all combat
            //    units to the closest own structure to that threat.
            if (threats.Count > 0)
            {
                var t = threats[0];   // closest threat (near own structure)
                MelonLogger.Msg($"[MIL] DEFEND threat at ({t.Pos.x:F0},{t.Pos.z:F0}) — recalling {army.Count} combat units");
                // TODO wire: SendMoveOrder(army, t.Pos) or SendAttackOrder(army, t.Unit)
                return;
            }

            // 2) Offensive: only attack if we've reached critical mass.
            //    Otherwise: hold at rally point (Nest position for MVP).
            if (army.Count < CriticalMassSize)
            {
                // Regroup at rally.
                Vector3 rally = FindNestPos(team);
                // TODO: only issue Move order if units are far from rally
                // and there's meaningful distance to close.
                return;
            }

            // 3) Attack top-priority target.
            if (targets.Count > 0)
            {
                var pick = targets[0];
                MelonLogger.Msg($"[MIL] ATTACK {pick.Kind} at ({pick.Pos.x:F0},{pick.Pos.z:F0}) with {army.Count} units " +
                                $"(SuppressCombat={SuppressCombat.Enabled})");
                // TODO wire: SendAttackOrder(army, pick.Pos)
                return;
            }
        }

        static List<Unit> FindCombatUnits(Team team)
        {
            var result = new List<Unit>();
            try
            {
                var units = team.Units;
                if (units == null) return result;
                for (int i = 0; i < units.Count; i++)
                {
                    var u = units[i];
                    if (u?.ObjectInfo == null || u.IsDestroyed) continue;
                    string n = u.ObjectInfo.DisplayName ?? "";
                    // Everything that isn't Shrimp or Queen is combat.
                    if (n == "Shrimp" || n == "Queen") continue;
                    result.Add(u);
                }
            }
            catch { }
            return result;
        }

        // ================================================================
        // Helpers
        // ================================================================
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

        static bool IsEnemyOf(Team us, Team other)
        {
            string usName = us.name ?? "";
            string otherName = other.name ?? "";
            // Alien vs Sol/Centauri — obvious enemy pairs.
            if (usName.Contains("Alien"))
                return otherName.Contains("Sol") || otherName.Contains("Cent");
            // For humans: Alien is always enemy; Sol vs Cent depends on gamemode.
            if (otherName.Contains("Alien")) return true;
            return usName != otherName;
        }

        internal static void ResetForNewRound()
        {
            _lastTickAt = 0f;
            _lastLogAt = 0f;
            _productionCatalogLogged = false;
            _productionFailReasonsLogged.Clear();
            _producerStructureCds.Clear();
            _lastStructurePlacementAt = 0f;
        }
    }
}
