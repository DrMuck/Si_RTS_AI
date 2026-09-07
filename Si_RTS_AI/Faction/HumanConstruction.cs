using HarmonyLib;
using MelonLoader;
using Silica;
using Silica.AI;
using SilicaAdminMod;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Si_RTS_AI.Faction
{
    /// <summary>
    /// Human faction (Sol / Centauri) construction takeover.
    ///
    /// Two rules this slice:
    ///   R1: Refinery at balterium patches inside current HQ range
    ///       — HumanEcoLayers.RefineryPressure.ArgMax()
    ///   R2: Build ANOTHER Sol/Cent Headquarters at the highest-value uncovered balterium
    ///       cluster — cell must be within HQ→HQ range of an existing HQ AND outside
    ///       current refinery reach — HumanEcoLayers.HqExpansionValue.ArgMax()
    /// </summary>
    internal static class HumanConstruction
    {
        // 200m gives Sol a wider basin when the returned spot near balterium is
        // obstructed. Was 150m — Centauri worked, Sol sometimes couldn't find a spot.
        const float REFINERY_SEARCH_RADIUS = 200f;
        // Shrunk 250 → 80m to prevent the game's placement search from sliding the
        // HQ back to near the starter HQ. If the argmax cell doesn't have a valid
        // spot within 80m, the placement fails and we retry next tick — that's
        // preferable to landing a degenerate HQ inside the starter's refinery reach.
        // 80 → 150m: 80m was too tight — Sol's argmax cell couldn't find a valid
        // spot within it and spammed 375+ failed HQ placements at the same cell.
        // 150m still keeps the HQ close to the argmax pick (far from starter HQ).
        const float HQ_SEARCH_RADIUS       = 150f;
        // Bumped 2 → 4 so all starter balterium patches within HQ reach get a refinery
        // fired in the first tick instead of trickling across ticks. Extras dedup via
        // _pendingRef + HasStructureTypeNear checks.
        const int   MAX_REFINERY_PER_TICK  = 4;
        const int   MAX_HQ_PER_TICK        = 1;
        const int   PENDING_TIMEOUT_TICKS  = 40;
        const int   ABSOLUTE_MAX_REFINERIES = 12;
        const int   ABSOLUTE_MAX_HQS        = 5;

        // HqExpansionValue = BalteriumWeighted × distance_in_cells. So MIN_VALUE
        // roughly = "min balterium patches × min distance in cells". A value of 30
        // means e.g. 2 balterium at 15 cells (600m) away, or 3 balterium at 10 cells
        // (400m) away. Prevents firing an HQ on a marginal spot right next to base.
        const int   HQ_MIN_VALUE = 30;

        // HARD post-callback quality gate: reject any HQ placement that lands within
        // this radius of ANY existing HQ. Sized just above REFINERY_HQ_RADIUS (450m)
        // so a new HQ always brings its own uncovered refinery reach.
        //
        // Root cause of the "3 HQs too close together" bug: our ArgMax cell was far
        // from base, but the game's placement search kept sliding the actual landed
        // spot back toward the existing HQ (probably because of vision / team-coverage
        // fallback). Without a post-check we happily Constructed those degenerate HQs.
        const float HQ_MIN_DIST_FROM_OTHER_HQ = 550f;

        // Log a diagnostic snapshot every N override-on ticks so we can see WHY nothing
        // fires (no balterium discovered / no HQ mask / no argmax candidate / etc).
        const int DIAG_EVERY_N_TICKS = 5;

        static int _tickCounter;
        // Per-team CD cache — Sol/Centauri have DIFFERENT ConstructionData instances
        // for Refinery and their own HQ. Contains() is reference-based, so a shared
        // static CD would silently block the second faction from finding an anchor.
        static readonly Dictionary<Team, ConstructionData> _refineryCdByTeam = new Dictionary<Team, ConstructionData>();
        static readonly Dictionary<Team, ConstructionData> _hqCdByTeam       = new Dictionary<Team, ConstructionData>();
        static readonly Dictionary<Team, ConstructionData> _barracksCdByTeam = new Dictionary<Team, ConstructionData>();
        static readonly Dictionary<Team, ConstructionData> _researchCdByTeam = new Dictionary<Team, ConstructionData>();
        static readonly Dictionary<Team, ConstructionData> _siloCdByTeam     = new Dictionary<Team, ConstructionData>();
        // Team's own HQ display name — at runtime it's "Headquarters" for both Sol
        // and Cent (the ObjectInfo asset names differ but DisplayName is generic).
        static readonly Dictionary<Team, string> _ownHqNameByTeam = new Dictionary<Team, string>();

        static readonly Dictionary<Vector3, int> _pendingRef      = new Dictionary<Vector3, int>();
        static readonly Dictionary<Vector3, int> _pendingHq       = new Dictionary<Vector3, int>();
        // Fire-count-per-argmax-cell for HQ, so we back off after N failures at the
        // same target cell (see v0.7.41 backoff in FireHqExpansion).
        static readonly Dictionary<Vector3, int> _hqFireCountByCell = new Dictionary<Vector3, int>();
        static readonly Dictionary<Vector3, int> _pendingBarracks = new Dictionary<Vector3, int>();
        static readonly Dictionary<Vector3, int> _pendingResearch = new Dictionary<Vector3, int>();
        static readonly Dictionary<Vector3, int> _pendingSilo     = new Dictionary<Vector3, int>();
        internal static int SiloAttempts, SiloSuccesses, SiloFailures;
        // Eco buffer — Silo is buildable from HQ per production_tree. Build one per
        // 2 refineries so income doesn't cap at HQ storage limit.
        const int   SILOS_PER_REFINERY   = 1;   // 1 silo per refinery (aggressive; user wants eco maxed)
        const int   ABSOLUTE_MAX_SILOS   = 12;

        internal static int RefAttempts, RefSuccesses, RefFailures;
        internal static int HqAttempts, HqSuccesses, HqFailures;
        internal static int BarracksAttempts, BarracksSuccesses, BarracksFailures;
        internal static int ResearchAttempts, ResearchSuccesses, ResearchFailures;

        // Tech gates — don't build tech until eco is running.
        const int   TECH_MIN_REFINERIES        = 1;
        const float TECH_SEARCH_RADIUS         = 120f;
        const int   ABSOLUTE_MAX_BARRACKS      = 1;
        const int   ABSOLUTE_MAX_RESEARCH_FAC  = 1;

        [HarmonyPatch(typeof(AIConstructionHandler), nameof(AIConstructionHandler.Think))]
        static class Patch_Think
        {
            static bool Prefix(AIConstructionHandler __instance)
            {
                try { return HandleTick(__instance); }
                catch (Exception ex)
                {
                    MelonLogger.Warning("[RTSA/Human] Construction takeover threw: " + ex.Message);
                    return true;   // fall back to stock so game doesn't freeze on our bug
                }
            }
        }

        // Returns true = let stock run; false = we own this tick.
        static bool HandleTick(AIConstructionHandler h)
        {
            var team = h?.Commander?.Team;
            if (team == null) return true;

            // Sol OR Centauri, but NOT Alien (Alien has its own file).
            string tn = team.name ?? "";
            bool isHuman = tn.Contains("Sol") || tn.Contains("Centauri") || tn.Contains("Cent");
            if (!isHuman || tn.Contains("Alien")) return true;
            // Master switch — if our Human AI is disabled for this faction,
            // let stock run.
            if (!FactionControl.IsEnabled(team)) return true;

            // Opt-in via the same /rtsai override flag Alien uses.

            // Alien-eco-only soak mode: return FALSE here so Harmony skips
            // Silica's stock AIConstructionHandler.Think too. Returning true
            // meant "let stock take over", which is the opposite of what
            // suppression should do — humans kept building because the stock
            // planner was still running underneath us.
            if (SuppressHumanAI.Enabled) return false;

            // We own this tick.
            _tickCounter++;

            EnsureOwnHqName(team);
            EnsureConstructionData(h, team);
            // Runtime-derive REFINERY_HQ_RADIUS / HQ_TO_HQ_RADIUS / PRODUCTION_HQ_RADIUS
            // from ConstructionData.MaximumBaseStructureDistance. Cheap check — only
            // logs on change, and the balance mod applies its overrides during scene
            // setup before AI ticks start.
            Perception.MapLayers.HumanEcoLayers.RefreshRuntimeRadii(team);
            Perception.EcoRateSampler.Tick(team);
            // Feed the layer replay/telemetry cache. Alien path in AlienConstruction
            // controls the interval — this call is a no-op unless the shared interval
            // has elapsed, but populates Human layers under the SAME frame index so
            // the viewer sees them synchronized.
            Perception.MapLayers.LayerReplay.MaybeSnapshotHuman(_tickCounter, team);
            Perception.BcMetrics.TickHuman(team);
            PruneExpired(_pendingRef);
            PruneExpired(_pendingHq);
            PruneExpired(_pendingBarracks);
            PruneExpired(_pendingResearch);

            MaybeLogDiagnostics(team);

            // ORDER OF SPENDING. Refineries first until two stand, then the Barracks and
            // the Research Facility get first call on cash, then refineries again.
            // 16,500 sat idle at minute four (2026-09-07 21:45) while every credit
            // went to refinery searches.
            if (CountOwnedIncludingSites(team, "Refinery") >= 2) { FireTech(team); FireRefinery(team); }
            else { FireRefinery(team); FireTech(team); }
            FireHqExpansion(team);

            // Ride-along: queue Mark I → V research at every Research Facility.
            HumanTechResearcher.Tick(team);

            return false;   // we own the tick — skip stock Think
        }

        static void FireTech(Team team)
        {
            int refCount = CountOwnedIncludingSites(team, "Refinery");
            if (refCount < TECH_MIN_REFINERIES) return;   // wait for eco to start

            var hqPos = FindOwnHqPosition(team);
            if (hqPos.sqrMagnitude < 0.01f) return;   // no HQ yet

            // R3a — Barracks
            if (_barracksCdByTeam.TryGetValue(team, out var barracksCd))
            {
                int barracksCount = CountOwnedIncludingSites(team, "Barracks");
                if (barracksCount < ABSOLUTE_MAX_BARRACKS && !HasPendingNear(_pendingBarracks, hqPos, TECH_SEARCH_RADIUS))
                {
                    FireTechPlacement(team, barracksCd, "Barracks", hqPos, _pendingBarracks,
                        onSuccessCount: () => BarracksSuccesses++,
                        onFailCount:    () => BarracksFailures++,
                        onAttemptCount: () => BarracksAttempts++);
                }
            }

            // R3b — Research Facility (fires in parallel with Barracks placement)
            if (_researchCdByTeam.TryGetValue(team, out var researchCd))
            {
                int researchCount = CountOwnedIncludingSites(team, "Research Facility");
                if (researchCount < ABSOLUTE_MAX_RESEARCH_FAC && !HasPendingNear(_pendingResearch, hqPos, TECH_SEARCH_RADIUS))
                {
                    FireTechPlacement(team, researchCd, "Research Facility", hqPos, _pendingResearch,
                        onSuccessCount: () => ResearchSuccesses++,
                        onFailCount:    () => ResearchFailures++,
                        onAttemptCount: () => ResearchAttempts++);
                }
            }

            // R3c — Silo (eco buffer) DISABLED per user spec. Depletion via
            // EcoRateSampler handles overflow directly now.
            if (false)
            {
                int siloCount  = 0;
                int siloTarget = 0;
                // Ring-arrangement: place silos at fixed offsets around HQ so they
                // don't stack on top of each other.
                int idx = siloCount;   // next silo we need to add
                float angle = idx * 40f * Mathf.Deg2Rad;     // 40° per slot → 9 slots for 360°
                float radius = 80f + (idx / 9) * 40f;         // grow outward every full ring
                Vector3 spawnPos = new Vector3(
                    hqPos.x + Mathf.Cos(angle) * radius, hqPos.y,
                    hqPos.z + Mathf.Sin(angle) * radius);
                SiloAttempts++;
                try
                {
                    // Classname is the Unity prefab name (e.g. "Sol_Silo" / "Cent_Silo")
                    // — pulled from cd.ObjectInfo.name. Falls back to "Silo".
                    string classname = "Silo";
                    if (_siloCdByTeam.TryGetValue(team, out var siloCd) && siloCd?.ObjectInfo != null)
                    {
                        string n = siloCd.ObjectInfo.name;
                        if (!string.IsNullOrEmpty(n)) classname = n;
                    }
                    UnityEngine.GameObject go = null;   // spawning structures for free is a test cheat and is off
                    if (go != null)
                    {
                        SiloSuccesses++;
                        Si_RTS_AI.AppendToRound(
                            $"[H3] team={team.name} spawned=Silo classname={classname} at=({spawnPos.x:F0},{spawnPos.z:F0}) idx={idx}");
                    }
                    else
                    {
                        SiloFailures++;
                        Si_RTS_AI.AppendToRound(
                            $"[H3] team={team.name} spawn=Silo classname={classname} failed(null) at=({spawnPos.x:F0},{spawnPos.z:F0}) idx={idx}");
                    }
                }
                catch (Exception ex)
                {
                    SiloFailures++;
                    MelonLogger.Warning($"[RTSA/Human] SpawnAtLocation(Silo) threw: {ex.Message}");
                }
            }
        }

        static void FireTechPlacement(Team team, ConstructionData cd, string displayName, Vector3 nearHq,
            Dictionary<Vector3, int> pendingDict,
            Action onSuccessCount, Action onFailCount, Action onAttemptCount,
            bool freeConstruct = false)
        {
            // THROUGH THE EXECUTOR: reach-aware search, prerequisite and cash checked,
            // result logged. The 120 m / 8 s search here took nine minutes to land a
            // Research Facility (2026-09-07 20:12) and never landed a Barracks.
            pendingDict[nearHq] = _tickCounter;
            onAttemptCount();
            bool queued = HumanBuild.TryBuild(team, cd, nearHq, null);
            if (queued) onSuccessCount(); else { onFailCount(); pendingDict.Remove(nearHq); }
            Si_RTS_AI.AppendToRound($"[H3] team={team.name} fire={displayName} nearHq=({nearHq.x:F0},{nearHq.z:F0}) queued={queued}");
        }
        // Grant + restore team.TotalResources around Construct so the structure spawns
        // without deducting cost from the team's bank. Used for Silo (and Alien BC) —
        // storage buildings the user wants as "free buffers" for eco overflow.
        // TotalResources is a { get; } property with a private backing field, so we
        // reflect it once and cache the FieldInfo.
        static System.Reflection.FieldInfo? _totalResourcesField;
        static System.Reflection.FieldInfo? ResolveTotalResourcesField()
        {
            if (_totalResourcesField != null) return _totalResourcesField;
            try
            {
                var candidates = new[] { "<TotalResources>k__BackingField", "m_TotalResources", "_totalResources" };
                foreach (var name in candidates)
                {
                    var fi = typeof(Team).GetField(name,
                        System.Reflection.BindingFlags.Instance |
                        System.Reflection.BindingFlags.NonPublic |
                        System.Reflection.BindingFlags.Public);
                    if (fi != null && fi.FieldType == typeof(int))
                    { _totalResourcesField = fi; return fi; }
                }
            }
            catch { }
            return null;
        }

        static object ConstructFree(Structure? cs, ConstructionData cd, Vector3 pos, Quaternion rot, Team team)
        {
            if (cs == null) return "no-cs";
            var fi = ResolveTotalResourcesField();
            if (fi == null) { return cs.Construct(cd, pos, rot); } // fallback — no free

            // NO FREE CONSTRUCTION. This used to grant 999,999 cash around the
            // call for test buffers; a commander pays for what it builds.
            return cs.Construct(cd, pos, rot);
        }

        static float DistanceToNearestHq(Team team, Vector3 p)
        {
            float best = float.MaxValue;
            try
            {
                var structs = team.Structures;
                if (structs == null) return best;
                for (int i = 0; i < structs.Count; i++)
                {
                    var s = structs[i];
                    if (s?.ObjectInfo == null || s.IsDestroyed) continue;
                    if ((s.ObjectInfo.DisplayName ?? "") != "Headquarters") continue;
                    bool functional = false; try { functional = s.IsFunctional; } catch { }
                    if (!functional) continue;
                    float dx = s.transform.position.x - p.x, dz = s.transform.position.z - p.z;
                    float d = Mathf.Sqrt(dx * dx + dz * dz);
                    if (d < best) best = d;
                }
            }
            catch { }
            return best;
        }
        static float _lastHqFireAt = -999f;
        const float HQ_FIRE_INTERVAL_S = 60f;
        const float HQ_COVER_RESERVE_EFF = 4000f;
        static float _lastHqCoverLogAt = -999f;
        const float HQ_MAX_DRIFT_M     = 300f;
        static int CountOwned(Team team, string name)
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
                        if (!string.Equals(s.ObjectInfo.DisplayName, name, StringComparison.OrdinalIgnoreCase)) continue;
                        bool functional = false; try { functional = s.IsFunctional; } catch { }
                        if (functional) n++;
                    }
            }
            catch { }
            return n;
        }
        static Vector3 FindOwnHqPosition(Team team)
        {
            try
            {
                var structs = team.Structures;
                if (structs != null)
                    for (int i = 0; i < structs.Count; i++)
                    {
                        var s = structs[i];
                        if (s?.ObjectInfo == null || s.IsDestroyed) continue;
                        if (string.Equals(s.ObjectInfo.DisplayName, "Headquarters", StringComparison.OrdinalIgnoreCase))
                            return s.transform.position;
                    }
            }
            catch { }
            return Vector3.zero;
        }

        static bool HasPendingNear(Dictionary<Vector3, int> pending, Vector3 pos, float radius)
        {
            float r2 = radius * radius;
            foreach (var kv in pending)
            {
                float dx = kv.Key.x - pos.x, dz = kv.Key.z - pos.z;
                if (dx * dx + dz * dz < r2) return true;
            }
            return false;
        }

        static void MaybeLogDiagnostics(Team team)
        {
            if (_tickCounter % DIAG_EVERY_N_TICKS != 0) return;
            try
            {
                bool haveRefCd = _refineryCdByTeam.ContainsKey(team);
                bool haveHqCd  = _hqCdByTeam.ContainsKey(team);
                _ownHqNameByTeam.TryGetValue(team, out string? ownHq);

                // Get the actual list count (not the API return value — v0.7.12 showed
                // that return may be a resource amount, not a count). Print both.
                int apiReturn = 0;
                int actualPatchCount = 0;
                var firstPatchCenter = Vector3.zero;
                try
                {
                    var scratch = new List<ResourceArea>(8);
                    apiReturn = ResourceArea.GetKnownResourcesAreas(team, team.UsableResource, scratch, ignoreEmpty: true);
                    actualPatchCount = scratch.Count;
                    if (scratch.Count > 0 && scratch[0] != null) firstPatchCenter = scratch[0].SignalCenter;
                }
                catch { }

                // Balterium mask cell count.
                int balteriumMaskCells = 0;
                var balterium = Perception.MapLayers.HumanEcoLayers.GetBalteriumDiscovered(team);
                var bd = balterium.Data;
                for (int i = 0; i < bd.Length; i++) if (bd[i] != 0) balteriumMaskCells++;

                // HQ mask cell count.
                int hqMaskCells = 0;
                var hqMask = Perception.MapLayers.HumanEcoLayers.GetHqMask(team);
                var d = hqMask.Data;
                for (int i = 0; i < d.Length; i++) if (d[i] != 0) hqMaskCells++;

                // Overlap cell count.
                int overlap = 0;
                for (int i = 0; i < d.Length; i++) if (bd[i] != 0 && d[i] != 0) overlap++;

                var pressure = Perception.MapLayers.HumanEcoLayers.GetRefineryPressure(team);
                var (px, pz, pval) = pressure.ArgMax();

                int refCount = CountOwnedIncludingSites(team, "Refinery");
                int hqCount = 0;
                if (ownHq != null) hqCount = CountOwnedIncludingSites(team, ownHq);

                // Grid config for coordinate-mapping sanity check.
                MelonLogger.Msg(
                    $"[RTSA/Human/DIAG] team={team.name} tick={_tickCounter} " +
                    $"ownHq='{ownHq ?? "?"}' refCd={haveRefCd} hqCd={haveHqCd} " +
                    $"apiReturn={apiReturn} patches={actualPatchCount} firstPatch=({firstPatchCenter.x:F0},{firstPatchCenter.z:F0}) " +
                    $"balteriumMask={balteriumMaskCells} hqMask={hqMaskCells} overlap={overlap} " +
                    $"refPress=({px},{pz})={pval} " +
                    $"refCount={refCount} hqCount={hqCount} " +
                    $"grid=origin({Perception.MapLayers.GridWorld.OriginX:F0},{Perception.MapLayers.GridWorld.OriginZ:F0})+" +
                    $"{Perception.MapLayers.GridWorld.Width}x{Perception.MapLayers.GridWorld.Height}");
            }
            catch (Exception ex) { MelonLogger.Warning("[RTSA/Human] diag threw: " + ex.Message); }
        }

        // A refinery counts as "assigned" to a balterium patch if within this radius —
        // used to skip patches that already have a refinery. Sized to accommodate the
        // realistic distance refineries land from balterium (~170-190m in observed
        // rounds — balterium has a large physical no-build footprint).
        const float REFINERY_ASSIGNED_RADIUS = 200f;
        const float REFINERY_SERVE_RADIUS    = 300f;
        static readonly List<Vector3> _landedRef = new List<Vector3>();
        static bool LandedRefineryNear(Vector3 p, float r)
        {
            for (int i = 0; i < _landedRef.Count; i++)
            {
                float dx = _landedRef[i].x - p.x, dz = _landedRef[i].z - p.z;
                if (dx * dx + dz * dz <= r * r) return true;
            }
            return false;
        }
        // Post-callback quality gate — reject placements that landed too far from
        // the intended balterium. Sized larger than balterium's physical footprint
        // (~150m) but smaller than the typical distance between balterium patches
        // (~400m+), so it still catches cross-patch contamination.
        const float REFINERY_PLACEMENT_QUALITY_RADIUS = 260f;
        const float REFINERY_SEARCH_OFFSET_M = 130f;
        const float RAMP_EXIT_M = 30f;
        // Duplicate-refinery reject: minimum distance between two refineries.
        // Fixes the "2 refineries at 1 balterium" case where two nearby balteriums
        // both fire and their placements land next to each other. 250m keeps them
        // apart while allowing tight clustering when patches are far.
        const float REFINERY_MIN_DIST_BETWEEN = 250f;

        // The game accepts refinery rotations ONLY in 90° increments (per user).
        // The previous attempt at LookRotation-based facing silently failed Construct
        // because it fed arbitrary yaws — refinery never landed, refCount stayed 0.
        // Fix: snap the LookRotation yaw to nearest 90°, then optionally add
        // REFINERY_RAMP_QUARTER_TURNS × 90° if the ramp axis isn't on local forward.
        // Sweep 0, 1, 2, 3 if the ramp still doesn't face balterium.
        const int REFINERY_RAMP_QUARTER_TURNS = 0;

        static void FireRefinery(Team team)
        {
            if (!_refineryCdByTeam.TryGetValue(team, out var refineryCd)) return;
            var anchor = FindStructureThatCanBuild(team, refineryCd);
            if (anchor == null) return;

            int refCount = CountOwnedIncludingSites(team, "Refinery");
            if (refCount >= ABSOLUTE_MAX_REFINERIES) return;

            // Per-PATCH iteration (not per-cell argmax). Each known balterium in reach
            // of an HQ gets ITS OWN refinery, ramp facing the patch. This delivers the
            // "2-3 refineries at start, one per starter patch" behavior.
            var patches = new List<ResourceArea>(16);
            try { ResourceArea.GetKnownResourcesAreas(team, team.UsableResource, patches, ignoreEmpty: true); }
            catch (Exception ex) { MelonLogger.Warning("[RTSA/Human] balterium enum threw: " + ex.Message); return; }

            var hqMask = Perception.MapLayers.HumanEcoLayers.GetHqMask(team);

            // Sort patches ascending by distance to nearest HQ so starter patches
            // (closest to base) go up first.
            patches.Sort((a, b) =>
            {
                float da = a != null ? MinDistOwnedStructureByName(team, a.SignalCenter, "Headquarters") : float.MaxValue;
                float db = b != null ? MinDistOwnedStructureByName(team, b.SignalCenter, "Headquarters") : float.MaxValue;
                return da.CompareTo(db);
            });

            int fired = 0;
            foreach (var patch in patches)
            {
                if (patch == null) continue;
                // TWO REFINERIES, THEN THE RESEARCH FACILITY. Three at the start left
                // 1,500 and the tech building waited four minutes for income (DrMuck:
                // "tech up came in too late"). Starting cash is two refineries plus
                // the Research Facility plus a Barracks.
                int perTick = CountOwnedIncludingSites(team, "Research Facility") == 0 ? 2 : MAX_REFINERY_PER_TICK;
                if (fired >= perTick) break;
                if (refCount >= ABSOLUTE_MAX_REFINERIES) break;
                int refCost = 3000; try { refCost = refineryCd.ResourceCost; } catch { }
                if (team.TotalResources < refCost * (fired + 1)) break;   // what we cannot pay for, we do not ask for

                Vector3 balteriumPos = patch.SignalCenter;

                // Skip: patch already has a nearby refinery / already pending / outside HQ range.
                // A SPOT IS SERVED BY ANY REFINERY WITHIN 300 M, standing or landed this
                // round. The 200 m test missed refineries the search had set down 200-220 m
                // from the spot, so Naraka 21:17 got three refineries on one field and two
                // on each of two others (DrMuck: "the refinery at 2700,-2113 is too much").
                if (HasStructureTypeNear(team, "Refinery", balteriumPos, REFINERY_SERVE_RADIUS)) continue;
                if (LandedRefineryNear(balteriumPos, REFINERY_SERVE_RADIUS)) continue;
                if (_pendingRef.ContainsKey(balteriumPos)) continue;

                // ONLY SPOTS INSIDE THE REFINERY'S OWN REACH OF A HEADQUARTERS.
                // The HQ mask is the HQ-to-HQ range, so refineries chained out
                // 700-900 m on Naraka (2026-09-07 20:12). DrMuck: spots outside
                // the radius are long-distance harvesting targets, no refinery.
                float refReach = 600f;
                try { if (refineryCd.MaximumBaseStructureDistance > 0f) refReach = refineryCd.MaximumBaseStructureDistance; } catch { }
                if (DistanceToNearestHq(team, balteriumPos) > refReach) continue;
                // A FIELD THE GAME REPORTS AS TWO AREAS GETS ONE REFINERY: skip a
                // spot with another refinery request pending within 120 m.
                bool pendingNear = false;
                foreach (var pk in _pendingRef.Keys)
                {
                    float pdx = pk.x - balteriumPos.x, pdz = pk.z - balteriumPos.z;
                    if (pdx * pdx + pdz * pdz <= 120f * 120f) { pendingNear = true; break; }
                }
                if (pendingNear) continue;

                _pendingRef[balteriumPos] = _tickCounter;
                RefAttempts++;
                var closer = FindClosestStructureThatCanBuild(team, refineryCd, balteriumPos) ?? anchor;
                Vector3 firedFor = balteriumPos;
                // START THE SEARCH ON THE HEADQUARTERS SIDE OF THE FIELD. Started on
                // the field itself, the search walked outward across the resource
                // cells and set the refinery down 238 m away on the far side, which
                // the quality test then rejected, 119 times for the best spot on
                // Naraka (2026-09-07 21:17, DrMuck: "a refinery close to 2513,-2676
                // would have been much better"). Between the HQ and the field is
                // where the harvester's ramp wants to be anyway.
                Vector3 searchFrom = balteriumPos;
                {
                    Vector3 hqFor = FindOwnHqPosition(team);
                    Vector3 dirHq = hqFor - balteriumPos; dirHq.y = 0f;
                    if (dirHq.sqrMagnitude > 1f) searchFrom = balteriumPos + dirHq.normalized * REFINERY_SEARCH_OFFSET_M;
                }
                var cd = refineryCd;
                try
                {
                    // maxTime shrunk 40s → 8s: if the game's placement search can't
                    // find a valid spot in 8s, it won't in 40 either. Faster failure
                    // = faster retry on next Alien tick when units may have moved.
                    // THE SEARCH IS TOLD THE FACING. With the ramp yaw handed to the
                    // search, every position it returns is valid for that yaw, so the
                    // rotation sweep that used to accept a refinery facing away from
                    // its field (Naraka 21:17: yaw 0 with the patch due south) is only
                    // a last resort. Ramp faces the yaw direction; measured from
                    // DrMuck's own placements as commander (2026-09-07 21:33).
                    Vector3 toPatch0 = new Vector3(balteriumPos.x - searchFrom.x, 0f, balteriumPos.z - searchFrom.z);
                    float wantYaw = toPatch0.sqrMagnitude > 0.01f
                        ? Mathf.Round(Quaternion.LookRotation(toPatch0, Vector3.up).eulerAngles.y / 90f) * 90f + REFINERY_RAMP_QUARTER_TURNS * 90f
                        : 0f;
                    Quaternion wantRot = Quaternion.Euler(0f, wantYaw, 0f);
                    // THE SEARCH RADIUS IS THE BUILDING'S OWN BUILD RADIUS, read from the
                    // game: it is modded on DrMuck's servers, so no constant may stand in
                    // for it. The distance to the patch that a landing must respect is a
                    // separate, economic rule (REFINERY_PLACEMENT_QUALITY_RADIUS).
                    float refSearch = REFINERY_SEARCH_RADIUS;
                    try { if (refineryCd.MaximumBaseStructureDistance > 0f) refSearch = refineryCd.MaximumBaseStructureDistance; } catch { }
                    ConstructionPlacement.QueueFirstValidPlacementAroundPoint(
                        cd.ObjectPreviewSetup, team, closer, searchFrom,
                        cd.GridSnapXZ, cd.GridSnapY,
                        refSearch, Mathf.Clamp(refSearch / 20f, 6f, 40f), 300,
                        (cData, ct, cs, gotPos, gotRot) =>
                        {
                            // Quality gate: reject placements that landed too far from
                            // the target balterium. Otherwise the game happily piles
                            // 2-3 refineries near the first patch. On reject we clear
                            // the pending flag so a later tick can retry this patch.
                            float dxq = gotPos.x - firedFor.x, dzq = gotPos.z - firedFor.z;
                            if (dxq * dxq + dzq * dzq > REFINERY_PLACEMENT_QUALITY_RADIUS * REFINERY_PLACEMENT_QUALITY_RADIUS)
                            {
                                RefFailures++;
                                _pendingRef.Remove(firedFor);
                                Si_RTS_AI.AppendToRound(
                                    $"[H1] team={team.name} reject=Refinery-too-far patchAt=({firedFor.x:F0},{firedFor.z:F0}) " +
                                    $"landedAt=({gotPos.x:F0},{gotPos.z:F0}) dist={Mathf.Sqrt(dxq*dxq+dzq*dzq):F0}m");
                                return;   // don't Construct
                            }
                            // Duplicate reject: if landed spot is within REFINERY_MIN_DIST_BETWEEN
                            // of another refinery, we'd be doubling up on one balterium.
                            // Check on the LANDED position (not target) — target dedup already
                            // handled by HasStructureTypeNear on balterium above.
                            if (HasStructureTypeNear(team, "Refinery", gotPos, REFINERY_MIN_DIST_BETWEEN)
                                || LandedRefineryNear(gotPos, REFINERY_MIN_DIST_BETWEEN))
                            {
                                RefFailures++;
                                _pendingRef.Remove(firedFor);
                                Si_RTS_AI.AppendToRound(
                                    $"[H1] team={team.name} reject=Refinery-too-close-to-other patchAt=({firedFor.x:F0},{firedFor.z:F0}) " +
                                    $"landedAt=({gotPos.x:F0},{gotPos.z:F0})");
                                return;
                            }
                            // THE RAMP MUST OPEN TOWARD THE FIELD. A refinery at (2020,-1661)
                            // on Naraka 22:04 faced its field with the ramp cut off by the
                            // ground on that side; the harvester had to go round. The point
                            // thirty metres in front of the ramp has to reach the field on
                            // the graph, or this landing is treated as obstructed.
                            {
                                Vector3 toB = new Vector3(firedFor.x - gotPos.x, 0f, firedFor.z - gotPos.z);
                                float yawB = toB.sqrMagnitude > 0.01f
                                    ? Mathf.Round(Quaternion.LookRotation(toB, Vector3.up).eulerAngles.y / 90f) * 90f + REFINERY_RAMP_QUARTER_TURNS * 90f
                                    : wantYaw;
                                Vector3 rampPoint = gotPos + Quaternion.Euler(0f, yawB, 0f) * Vector3.forward * RAMP_EXIT_M;
                                bool rampOpen = true;
                                try { rampOpen = Perception.Reach.CanReach(Pathfinding.GraphMask.everything, rampPoint, firedFor); } catch { }
                                if (!rampOpen)
                                {
                                    RefFailures++;
                                    _pendingRef.Remove(firedFor);
                                    Si_RTS_AI.AppendToRound(
                                        $"[H1] team={team.name} reject=Refinery-ramp-cut-off patchAt=({firedFor.x:F0},{firedFor.z:F0}) " +
                                        $"landedAt=({gotPos.x:F0},{gotPos.z:F0}) yaw={yawB:F0}");
                                    return;
                                }
                            }
                            RefSuccesses++;
                            // Preferred yaw = ramp axis pointing at balterium, snapped
                            // to 90° (game requires this grid).
                            Vector3 toBalt = new Vector3(firedFor.x - gotPos.x, 0f, firedFor.z - gotPos.z);
                            float baseYaw = toBalt.sqrMagnitude > 0.01f
                                ? Mathf.Round(Quaternion.LookRotation(toBalt, Vector3.up).eulerAngles.y / 90f) * 90f
                                  + REFINERY_RAMP_QUARTER_TURNS * 90f
                                : wantYaw;

                            // Try 4 rotations at the returned position, then re-search
                            // NEW positions around it if all obstructed (a unit standing
                            // on the exact spot blocks every rotation). Each new search
                            // uses ConstructionPlacement.GetFirstValidPlacementAroundPoint
                            // (SYNC api discovered v0.7.29) with the failed position as
                            // seed and a widening exclusion radius.
                            string res = "no-call";
                            float winningYaw = baseYaw;
                            Vector3 winningPos = gotPos;
                            bool succeeded = TryConstructAllRotations(
                                cs, cData, gotPos, baseYaw, out winningYaw, out res);

                            if (!succeeded)
                            {
                                // Fallback: sync-search for a fresh valid pos around
                                // the failed spot, then retry 4 rotations there.
                                Vector3 altPos; Quaternion altRot;
                                for (float alt_radius = 60f; alt_radius <= 180f && !succeeded; alt_radius += 60f)
                                {
                                    bool found = false;
                                    try
                                    {
                                        found = ConstructionPlacement.GetFirstValidPlacementAroundPoint(
                                            cData.ObjectPreviewSetup, team, gotPos,
                                            cData.GridSnapXZ, cData.GridSnapY, alt_radius,
                                            out altPos, out altRot, cs);
                                    }
                                    catch { altPos = gotPos; altRot = gotRot; }
                                    if (!found) continue;
                                    // altPos differs from gotPos → try 4 rotations there.
                                    if (Vector3.SqrMagnitude(altPos - gotPos) < 1f) continue;
                                    succeeded = TryConstructAllRotations(
                                        cs, cData, altPos, baseYaw, out winningYaw, out res);
                                    if (succeeded) winningPos = altPos;
                                }
                            }

                            _landedRef.Add(gotPos);
                            Si_RTS_AI.AppendToRound(
                                $"[H1] team={team.name} constructed=Refinery atBalterium=({firedFor.x:F0},{firedFor.z:F0}) " +
                                $"landedAt=({winningPos.x:F0},{winningPos.z:F0}) yaw={winningYaw:F0} result={res}");
                        },
                        (cData, ct, cs) => { RefFailures++; _pendingRef.Remove(firedFor); },
                        false, true, wantRot);
                }
                catch (Exception ex) { MelonLogger.Warning("[RTSA/Human] refinery placement threw: " + ex.Message); }

                refCount++;
                fired++;
                Si_RTS_AI.AppendToRound(
                    $"[H1] team={team.name} fire=Refinery atBalterium=({balteriumPos.x:F0},{balteriumPos.z:F0}) " +
                    $"refCount={refCount}");
            }
        }

        // Try Construct at the given position with 4 rotations (preferred first,
        // then ±90°, then 180°). Returns true iff any of the 4 succeeded.
        // Also returns the winning yaw and the last result string (for logging).
        static bool TryConstructAllRotations(
            Structure? cs, ConstructionData cd, Vector3 pos, float baseYaw,
            out float winningYaw, out string lastRes)
        {
            float[] yawTries = { baseYaw, baseYaw + 90f, baseYaw - 90f, baseYaw + 180f };
            lastRes = "no-call";
            winningYaw = baseYaw;
            for (int yi = 0; yi < yawTries.Length; yi++)
            {
                var facing = Quaternion.Euler(0f, yawTries[yi], 0f);
                string thisRes = "no-call";
                try { if (cs != null) thisRes = cs.Construct(cd, pos, facing).ToString(); }
                catch (Exception cx) { thisRes = "throw:" + cx.Message; }
                if (thisRes == "Success")
                {
                    winningYaw = yawTries[yi];
                    lastRes = thisRes;
                    return true;
                }
                if (yi == 0) lastRes = thisRes;
            }
            return false;
        }

        // Distance to nearest owned HQ (completed structures + in-progress sites).
        // Used by the HQ post-callback quality gate to reject placements that got
        // snapped back near the starter HQ.
        static float MinDistToOwnHq(Team team, Vector3 pos)
        {
            float best = float.MaxValue;
            try
            {
                var structs = team.Structures;
                if (structs != null)
                    for (int i = 0; i < structs.Count; i++)
                    {
                        var s = structs[i];
                        if (s?.ObjectInfo == null || s.IsDestroyed) continue;
                        if (!string.Equals(s.ObjectInfo.DisplayName, "Headquarters", StringComparison.OrdinalIgnoreCase)) continue;
                        Vector3 sp = s.transform.position;
                        float dx = sp.x - pos.x, dz = sp.z - pos.z;
                        float d = Mathf.Sqrt(dx * dx + dz * dz);
                        if (d < best) best = d;
                    }
                var sites = ConstructionSite.ConstructionSites;
                if (sites != null)
                    for (int i = 0; i < sites.Count; i++)
                    {
                        var cs = sites[i];
                        if (cs == null || cs.IsDestroyed) continue;
                        if (cs.Team != team) continue;
                        if (cs.ObjectInfo == null) continue;
                        if (!string.Equals(cs.ObjectInfo.DisplayName, "Headquarters", StringComparison.OrdinalIgnoreCase)) continue;
                        Vector3 sp = cs.transform.position;
                        float dx = sp.x - pos.x, dz = sp.z - pos.z;
                        float d = Mathf.Sqrt(dx * dx + dz * dz);
                        if (d < best) best = d;
                    }
            }
            catch { }
            return best;
        }

        // Distance from a world position to the nearest owned structure of a given
        // display name. Used to sort balterium patches by proximity to team HQs.
        static float MinDistOwnedStructureByName(Team team, Vector3 pos, string displayName)
        {
            float best = float.MaxValue;
            try
            {
                var structs = team.Structures;
                if (structs != null)
                    for (int i = 0; i < structs.Count; i++)
                    {
                        var s = structs[i];
                        if (s?.ObjectInfo == null || s.IsDestroyed) continue;
                        if (!string.Equals(s.ObjectInfo.DisplayName, displayName, StringComparison.OrdinalIgnoreCase)) continue;
                        Vector3 sp = s.transform.position;
                        float dx = sp.x - pos.x, dz = sp.z - pos.z;
                        float d = Mathf.Sqrt(dx * dx + dz * dz);
                        if (d < best) best = d;
                    }
            }
            catch { }
            return best;
        }

        // Checks completed structures + construction sites for the display name.
        static bool HasStructureTypeNear(Team team, string displayName, Vector3 pos, float radius)
        {
            float r2 = radius * radius;
            try
            {
                var structs = team.Structures;
                if (structs != null)
                    for (int i = 0; i < structs.Count; i++)
                    {
                        var s = structs[i];
                        if (s?.ObjectInfo == null || s.IsDestroyed) continue;
                        if (!string.Equals(s.ObjectInfo.DisplayName, displayName, StringComparison.OrdinalIgnoreCase)) continue;
                        Vector3 sp = s.transform.position;
                        float dx = sp.x - pos.x, dz = sp.z - pos.z;
                        if (dx * dx + dz * dz < r2) return true;
                    }
            }
            catch { }
            try
            {
                var sites = ConstructionSite.ConstructionSites;
                if (sites != null)
                    for (int i = 0; i < sites.Count; i++)
                    {
                        var cs = sites[i];
                        if (cs == null || cs.IsDestroyed) continue;
                        if (cs.Team != team) continue;
                        if (cs.ObjectInfo == null) continue;
                        if (!string.Equals(cs.ObjectInfo.DisplayName, displayName, StringComparison.OrdinalIgnoreCase)) continue;
                        Vector3 sp = cs.transform.position;
                        float dx = sp.x - pos.x, dz = sp.z - pos.z;
                        if (dx * dx + dz * dz < r2) return true;
                    }
            }
            catch { }
            return false;
        }

        static void FireHqExpansion(Team team)
        {
            if (!_hqCdByTeam.TryGetValue(team, out var hqCd)) return;
            if (!_ownHqNameByTeam.TryGetValue(team, out var hqName)) return;
            var anchor = FindStructureThatCanBuild(team, hqCd);
            if (anchor == null) return;

            int hqCount = CountOwnedIncludingSites(team, hqName);
            if (hqCount >= ABSOLUTE_MAX_HQS) return;

            var expansion = Perception.MapLayers.HumanEcoLayers.GetEcoHqExpansionValue(team);
            // ONE HEADQUARTERS AT A TIME. Four went up in fifty seconds on Naraka 22:04,
            // three of them within 300 m of each other: no wait for the one in progress
            // and no spacing at the landing. One in flight, a minute between fires.
            if (CountOwnedIncludingSites(team, hqName) > CountOwned(team, hqName)) return;   // a site is in progress
            if (Time.time - _lastHqFireAt < HQ_FIRE_INTERVAL_S) return;
            // AN EXPANSION NEEDS AN ARMY TO STAND BESIDE IT. No reserve to spare, no HQ.
            float reserve = 0f; try { reserve = Mil.Forces.ReserveEff; } catch { }
            if (reserve < HQ_COVER_RESERVE_EFF)
            {
                if (Time.time - _lastHqCoverLogAt > 60f) { _lastHqCoverLogAt = Time.time; Si_RTS_AI.AppendToRound($"[H2] team={team.name} hold=Headquarters reserve {reserve:F0} eff cannot cover a site (needs {HQ_COVER_RESERVE_EFF:F0})"); }
                return;
            }
            int fired = 0;
            while (fired < 1 && hqCount < ABSOLUTE_MAX_HQS)
            {
                var (cx, cz, val) = expansion.ArgMax();
                if (cx < 0 || val < HQ_MIN_VALUE) break;

                Vector3 target = Perception.MapLayers.GridWorld.CellCenter(cx, cz);
                if (_pendingHq.ContainsKey(target))
                {
                    expansion.SubtractDiskAtWorld(target,
                        Perception.MapLayers.HumanEcoLayers.REFINERY_HQ_RADIUS,
                        Perception.MapLayers.HumanEcoLayers.REFINERY_PENALTY);
                    continue;
                }

                // Backoff: if we've fired at this exact argmax cell N times without
                // a Success, subtract the disk locally to force argmax to try
                // elsewhere. Prevents 400+ retries on the same cell that has no
                // valid spot (Sol case).
                _hqFireCountByCell.TryGetValue(target, out int prevCount);
                if (prevCount >= 3)
                {
                    expansion.SubtractDiskAtWorld(target,
                        Perception.MapLayers.HumanEcoLayers.HQ_TO_HQ_RADIUS,
                        Perception.MapLayers.HumanEcoLayers.REFINERY_PENALTY);
                    continue;
                }
                _hqFireCountByCell[target] = prevCount + 1;

                _pendingHq[target] = _tickCounter;
                HqAttempts++;
                Vector3 firedFor = target;
                // THROUGH THE EXECUTOR. The old call searched 150 m around a cell that
                // could lie beyond the HQ-to-HQ reach of every anchor and failed in
                // silence: sixteen fires and no Headquarters on Naraka 21:17 with
                // 46,000 cash and tier 5 (DrMuck: "Sol could have placed one or two
                // expansion HQs by now"). HumanBuild clamps to the reach, searches
                // the way the vanilla commander does and logs the answer.
                if (MinDistToOwnHq(team, target) < HQ_MIN_DIST_FROM_OTHER_HQ)
                {
                    HqFailures++; _pendingHq.Remove(firedFor);
                    Si_RTS_AI.AppendToRound($"[H2] team={team.name} reject=HQ-too-close targetCell=({target.x:F0},{target.z:F0})");
                }
                else if (HumanBuild.TryBuild(team, hqCd, target, null, HQ_MAX_DRIFT_M, HQ_MIN_DIST_FROM_OTHER_HQ)) { HqSuccesses++; _lastHqFireAt = Time.time; }
                else { HqFailures++; _pendingHq.Remove(firedFor); }

                hqCount++;
                fired++;

                Si_RTS_AI.AppendToRound(
                    $"[H2] team={team.name} fire={hqName} argmaxCell=({cx},{cz}) val={val} " +
                    $"atWorld=({target.x:F0},{target.z:F0}) hqCount={hqCount}");

                // Prevent picking cells that would be re-covered by this new HQ's refinery reach.
                expansion.SubtractDiskAtWorld(target,
                    Perception.MapLayers.HumanEcoLayers.REFINERY_HQ_RADIUS,
                    Perception.MapLayers.HumanEcoLayers.REFINERY_PENALTY);
            }
        }

        static void EnsureOwnHqName(Team team)
        {
            if (_ownHqNameByTeam.ContainsKey(team)) return;
            // Both Sol and Centauri HQs share the runtime display name "Headquarters"
            // (the ObjectInfo assets are "Sol Headquarters" / "Cent Headquarters" but
            // DisplayName is generic). Match on that.
            try
            {
                var structs = team.Structures;
                if (structs != null)
                    for (int i = 0; i < structs.Count; i++)
                    {
                        var s = structs[i];
                        if (s?.ObjectInfo == null) continue;
                        string n = s.ObjectInfo.DisplayName ?? "";
                        if (string.Equals(n, "Headquarters", StringComparison.OrdinalIgnoreCase))
                        { _ownHqNameByTeam[team] = n; return; }
                    }
            }
            catch { }
        }

        // ---- helpers ----

        static void EnsureConstructionData(AIConstructionHandler h, Team team)
        {
            bool haveRef  = _refineryCdByTeam.ContainsKey(team);
            bool haveHq   = _hqCdByTeam.ContainsKey(team);
            bool haveBar  = _barracksCdByTeam.ContainsKey(team);
            bool haveRes  = _researchCdByTeam.ContainsKey(team);
            bool haveSilo = _siloCdByTeam.ContainsKey(team);
            if (haveRef && haveHq && haveBar && haveRes && haveSilo) return;

            bool IsMyHq(string n) => string.Equals(n, "Headquarters", StringComparison.OrdinalIgnoreCase);

            void Sift(ConstructionData cd)
            {
                if (cd?.ObjectInfo == null) return;
                var n = cd.ObjectInfo.DisplayName;
                if (!haveRef  && string.Equals(n, "Refinery",          StringComparison.OrdinalIgnoreCase)) { _refineryCdByTeam[team] = cd; haveRef  = true; }
                if (!haveHq   && IsMyHq(n))                                                                { _hqCdByTeam[team]       = cd; haveHq   = true; }
                if (!haveBar  && string.Equals(n, "Barracks",          StringComparison.OrdinalIgnoreCase)) { _barracksCdByTeam[team] = cd; haveBar  = true; }
                if (!haveRes  && string.Equals(n, "Research Facility", StringComparison.OrdinalIgnoreCase)) { _researchCdByTeam[team] = cd; haveRes  = true; }
                if (!haveSilo && string.Equals(n, "Silo",              StringComparison.OrdinalIgnoreCase)) { _siloCdByTeam[team]     = cd; haveSilo = true; }
            }

            var list = h.BuildableStructuresAll;
            if (list != null)
                for (int i = 0; i < list.Count; i++)
                    Sift(list[i]);

            if (haveRef && haveHq && haveBar && haveRes && haveSilo) return;

            try
            {
                var structs = team.Structures;
                if (structs != null)
                    for (int i = 0; i < structs.Count; i++)
                    {
                        var s = structs[i];
                        if (s == null || s.ConstructionOptions == null) continue;
                        foreach (var opt in s.ConstructionOptions)
                            Sift(opt);
                    }
            }
            catch { }
        }

        static Structure? FindStructureThatCanBuild(Team team, ConstructionData targetCd)
        {
            try
            {
                var structs = team.Structures;
                if (structs == null) return null;
                for (int i = 0; i < structs.Count; i++)
                {
                    var s = structs[i];
                    if (s == null || s.IsDestroyed) continue;
                    if (s.ConstructionOptions != null && s.ConstructionOptions.Contains(targetCd))
                        return s;
                }
            }
            catch { }
            return null;
        }

        static Structure? FindClosestStructureThatCanBuild(Team team, ConstructionData targetCd, Vector3 targetPos)
        {
            Structure? best = null;
            float bestSq = float.MaxValue;
            try
            {
                var structs = team.Structures;
                if (structs != null)
                    for (int i = 0; i < structs.Count; i++)
                    {
                        var s = structs[i];
                        if (s == null || s.IsDestroyed) continue;
                        if (s.ConstructionOptions == null || !s.ConstructionOptions.Contains(targetCd)) continue;
                        Vector3 sp = s.transform.position;
                        float dx = sp.x - targetPos.x, dz = sp.z - targetPos.z;
                        float d2 = dx * dx + dz * dz;
                        if (d2 < bestSq) { bestSq = d2; best = s; }
                    }
            }
            catch { }
            return best;
        }

        static int CountOwnedIncludingSites(Team team, string displayName)
        {
            int n = 0;
            try
            {
                var structs = team.Structures;
                if (structs != null)
                    for (int i = 0; i < structs.Count; i++)
                    {
                        var s = structs[i];
                        if (s?.ObjectInfo == null) continue;
                        if (string.Equals(s.ObjectInfo.DisplayName, displayName, StringComparison.OrdinalIgnoreCase))
                            n++;
                    }
            }
            catch { }
            try
            {
                var sites = ConstructionSite.ConstructionSites;
                if (sites != null)
                    for (int i = 0; i < sites.Count; i++)
                    {
                        var cs = sites[i];
                        if (cs == null || cs.IsDestroyed) continue;
                        if (cs.Team != team) continue;
                        if (cs.ObjectInfo == null) continue;
                        if (string.Equals(cs.ObjectInfo.DisplayName, displayName, StringComparison.OrdinalIgnoreCase))
                            n++;
                    }
            }
            catch { }
            return n;
        }

        static void PruneExpired(Dictionary<Vector3, int> dict)
        {
            List<Vector3>? expired = null;
            foreach (var kv in dict)
                if (_tickCounter - kv.Value > PENDING_TIMEOUT_TICKS)
                {
                    if (expired == null) expired = new List<Vector3>();
                    expired.Add(kv.Key);
                }
            if (expired != null) foreach (var k in expired) dict.Remove(k);
        }

        // ---- Round wiring ----

        internal static void ResetForNewRound()
        {
            _tickCounter = 0;
            _pendingRef.Clear();
            _pendingHq.Clear();
            _hqFireCountByCell.Clear();
            _pendingBarracks.Clear();
            _pendingResearch.Clear();
            _pendingSilo.Clear();
            _refineryCdByTeam.Clear();
            _hqCdByTeam.Clear();
            _barracksCdByTeam.Clear();
            _researchCdByTeam.Clear();
            _siloCdByTeam.Clear();
            _ownHqNameByTeam.Clear();
            SiloAttempts = SiloSuccesses = SiloFailures = 0;
            RefAttempts = RefSuccesses = RefFailures = 0;
            HqAttempts = HqSuccesses = HqFailures = 0;
            BarracksAttempts = BarracksSuccesses = BarracksFailures = 0;
            _landedRef.Clear();
            ResearchAttempts = ResearchSuccesses = ResearchFailures = 0;
        }

        internal static string BuildRoundSummaryFragment()
        {
            if (RefAttempts == 0 && HqAttempts == 0 && BarracksAttempts == 0 && ResearchAttempts == 0 && SiloAttempts == 0) return "";
            return "--- Human construction (Refinery + HQ + Barracks + Research + Silo) ---\n" +
                   $"  Refinery fires: attempts={RefAttempts} success={RefSuccesses} fail={RefFailures}\n" +
                   $"  HQ fires:       attempts={HqAttempts}  success={HqSuccesses}  fail={HqFailures}\n" +
                   $"  Barracks fires: attempts={BarracksAttempts} success={BarracksSuccesses} fail={BarracksFailures}\n" +
                   $"  Research fires: attempts={ResearchAttempts} success={ResearchSuccesses} fail={ResearchFailures}\n" +
                   $"  Silo fires:     attempts={SiloAttempts} success={SiloSuccesses} fail={SiloFailures}\n";
        }
    }
}
