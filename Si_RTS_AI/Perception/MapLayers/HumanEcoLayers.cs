using MelonLoader;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Si_RTS_AI.Perception.MapLayers
{
    /// <summary>
    /// Human-eco layers (Sol / Centauri). Simpler than Alien: no node chain, coverage
    /// is granted directly by HQ / Outpost build radius, so we don't need dilated masks
    /// to model "reach". Layer set:
    ///
    ///   BalteriumDiscovered (LayerB) — 1 per cell with a known balterium patch
    ///   HqMask              (LayerB) — 1 per cell within HQ_REACH of any HQ or Outpost
    ///   RefineryPressure    (LayerI) — BalteriumDiscovered × HqMask, minus disks around refineries
    ///
    /// Refinery placement is `RefineryPressure.ArgMax()` → uncovered balterium in HQ reach.
    /// (HQ expansion / balterium-weighted map for future slices.)
    /// </summary>
    public static class HumanEcoLayers
    {
        // Three distinct radii relative to the HQ, ordered LARGEST → SMALLEST.
        // HQ→HQ is the widest because that's how strategic expansion works: a new HQ
        // can go far from the existing one to seize a new balterium cluster. Then
        // refineries fit inside a medium radius from any HQ, and production
        // buildings sit in the tightest radius. Balance mod may override
        // REFINERY_HQ_RADIUS upward; the constant here is a conservative lower bound.
        // Runtime-derived from Headquarters.ConstructionData.MaximumBaseStructureDistance.
        public static float HQ_TO_HQ_RADIUS = 2500f;      // Headquarters.MaxBaseStructDist
        // v0.7.44: RUNTIME-DERIVED from ConstructionData.MaximumBaseStructureDistance
        // (updated at scene load via RefreshRuntimeRadii). Fallback values here are used
        // only if the runtime read fails (e.g. before scene has any ConstructionData
        // available). Vanilla defaults are ~450 for refinery — the balance mod bumps
        // it to 1000. Reading at runtime means we stay correct regardless of mods.
        public static float REFINERY_HQ_RADIUS = 1000f;  // Refinery.MaxBaseStructDist
        public static float PRODUCTION_HQ_RADIUS = 250f;  // Barracks.MaxBaseStructDist

        public const float REFINERY_COVERAGE_METERS = 400f;
        public const int   REFINERY_PENALTY         = 9999;

        // For HQ expansion — the kernel is the effective "how far a candidate HQ can
        // harvest balterium via a refinery". A refinery may be placed anywhere inside
        // REFINERY_HQ_RADIUS of the HQ, and each refinery harvests balterium within
        // REFINERY_COVERAGE_METERS of ITSELF. So the max distance from HQ at which
        // a balterium patch is still economically reachable = both radii combined.
        // Previously this was just REFINERY_HQ_RADIUS, which UNDER-counted patches
        // in the outer ring and made eligible expansion cells look weak vs their
        // true eco potential.
        public static float HQ_EXPANSION_KERNEL_METERS => REFINERY_HQ_RADIUS + REFINERY_COVERAGE_METERS;

        public const int BALTERIUM_MARK = 1;

        static LayerB _balterium         = new LayerB();
        // Refinery-reach mask (HQs dilated by REFINERY_HQ_RADIUS). Refineries can be
        // placed anywhere inside this.
        static LayerB _hqMask            = new LayerB();
        // Minimum HQ-to-HQ separation mask (HQs dilated by MIN_HQ_HQ_SEPARATION_M).
        // A NEW HQ must land OUTSIDE this so its refinery coverage disk doesn't
        // overlap with the existing HQ's refinery coverage. Bigger than _hqMask.
        static LayerB _hqMinSepMask      = new LayerB();
        // HQ-to-HQ mask (HQs dilated by HQ_TO_HQ_RADIUS). NEW HQs can only be placed
        // inside this — every new HQ must chain-link to an existing one within this
        // tighter radius.
        static LayerB _hqBuildableMask   = new LayerB();
        static LayerI _pressure          = new LayerI();
        static LayerI _balteriumWeighted = new LayerI();
        static LayerI _ecoHqExpansionValue = new LayerI();
        // Distance (in cells) from each cell to the nearest existing HQ. Used to
        // weight EcoHqExpansionValue toward FAR cells — otherwise ArgMax keeps
        // picking the closest high-balterium cell and expansion HQs pile near the
        // starter HQ.
        static LayerI _distanceToHq      = new LayerI();
        static List<ResourceArea> _scratch = new List<ResourceArea>(16);

        static bool _balteriumDirty         = true;
        static bool _hqMaskDirty            = true;
        static bool _hqMinSepMaskDirty      = true;
        static bool _hqBuildableMaskDirty   = true;
        static bool _pressureDirty          = true;
        static bool _balteriumWeightedDirty = true;
        static bool _ecoHqExpansionDirty    = true;
        static bool _distanceToHqDirty      = true;

        /// <summary>
        /// Minimum HQ-to-HQ world distance in meters — set so that a new HQ's
        /// refinery reach can't overlap the existing HQ's refinery reach.
        /// 2× REFINERY_HQ_RADIUS gives clean separation (each HQ's refinery
        /// coverage disk just touches the other's).
        /// </summary>
        public static float MIN_HQ_HQ_SEPARATION_M => 2f * REFINERY_HQ_RADIUS;

        static int  _lastKnownBalteriumCount = -1;
        static Team _lastTeam;

        // ---- Accessors ----

        public static LayerB GetBalteriumDiscovered(Team team)
        {
            EnsureTeamCoherent(team);
            RefreshBalteriumDirtinessByPoll(team);
            if (_balteriumDirty) { RebuildBalterium(team); _balteriumDirty = false; _pressureDirty = true; }
            return _balterium;
        }

        public static LayerB GetHqMask(Team team)
        {
            EnsureTeamCoherent(team);
            if (_hqMaskDirty) { RebuildHqMask(team); _hqMaskDirty = false; _pressureDirty = true; _ecoHqExpansionDirty = true; }
            return _hqMask;
        }

        /// <summary>HQ→HQ range mask. Cells here can receive a NEW HQ placement.</summary>
        public static LayerB GetHqBuildableMask(Team team)
        {
            EnsureTeamCoherent(team);
            if (_hqBuildableMaskDirty) { RebuildHqBuildableMask(team); _hqBuildableMaskDirty = false; _ecoHqExpansionDirty = true; }
            return _hqBuildableMask;
        }

        /// <summary>Minimum HQ-to-HQ separation mask. A NEW HQ must land OUTSIDE
        /// this so its refinery coverage disk doesn't overlap the existing HQ's.</summary>
        public static LayerB GetHqMinSepMask(Team team)
        {
            EnsureTeamCoherent(team);
            if (_hqMinSepMaskDirty) { RebuildHqMinSepMask(team); _hqMinSepMaskDirty = false; _ecoHqExpansionDirty = true; }
            return _hqMinSepMask;
        }

        public static LayerI GetRefineryPressure(Team team)
        {
            var balt = GetBalteriumDiscovered(team);
            var mask = GetHqMask(team);
            if (_pressureDirty) { RebuildPressure(team, balt, mask); _pressureDirty = false; }
            return _pressure;
        }

        /// <summary>Balterium density weighted by an HQ-reach box kernel. Cell value =
        /// number of known balterium patches that would be inside HQ_EXPANSION_KERNEL_METERS
        /// if an HQ landed on this cell.</summary>
        public static LayerI GetBalteriumWeighted(Team team)
        {
            var balt = GetBalteriumDiscovered(team);
            if (_balteriumWeightedDirty)
            {
                // Convolve balterium mask with a FILLED DISK kernel of radius =
                // refinery build radius (in cells). Cell value = # balterium a
                // refinery from an HQ here would cover. Circle is correct because
                // the game's refinery build radius is round — a box kernel
                // over-counts on the diagonals.
                _balteriumWeighted.SumCircleFrom(balt,
                    GridWorld.MetersToCellsInt(HQ_EXPANSION_KERNEL_METERS));
                _balteriumWeightedDirty = false;
                _ecoHqExpansionDirty = true;
            }
            return _balteriumWeighted;
        }

        /// <summary>ECO next-HQ cells (a strategic-expansion variant will live alongside
        /// this later — this one is scored purely on balterium eco). Value at a cell
        /// = balterium the future HQ's refinery ring would reach × distance from any
        /// existing HQ. Eligible cells are:
        ///   - INSIDE HQ→HQ chain range of some existing HQ (buildable), AND
        ///   - OUTSIDE the MIN_HQ_HQ_SEPARATION_M disk of every existing HQ
        ///     (so the new HQ's refinery reach doesn't overlap the current one's).
        /// </summary>
        public static LayerI GetEcoHqExpansionValue(Team team)
        {
            var weighted   = GetBalteriumWeighted(team);
            var minSepMask = GetHqMinSepMask(team);
            var buildable  = GetHqBuildableMask(team);
            if (_distanceToHqDirty) { RebuildDistanceToHq(team); _distanceToHqDirty = false; _ecoHqExpansionDirty = true; }
            if (_ecoHqExpansionDirty)
            {
                RebuildEcoHqExpansion(weighted, minSepMask, buildable, _distanceToHq);
                _ecoHqExpansionDirty = false;
            }
            return _ecoHqExpansionValue;
        }

        // ---- Event hooks ----

        public static void OnHqChanged()
        {
            _hqMaskDirty = true;
            _hqMinSepMaskDirty = true;
            _hqBuildableMaskDirty = true;
            _distanceToHqDirty = true;
            _pressureDirty = true;
            _ecoHqExpansionDirty = true;
        }
        public static void OnRefineryChanged() { _pressureDirty = true; }
        public static void OnBalteriumDiscovered()
        {
            _balteriumDirty = true;
            _pressureDirty  = true;
            _balteriumWeightedDirty = true;
            _ecoHqExpansionDirty = true;
        }
        /// <summary>
        /// FRESH LAYERS FOR A TEAM TAKING THE STAGE.
        ///
        /// MilContext installs a team's state and then calls ResetForNewRound on
        /// every class it swaps, so this is where a second human team stops
        /// sharing the first one's grids. OnRoundReset only marks them dirty,
        /// which is right for a new round on the same team and wrong for a new
        /// team: the accessors hand back these very objects, so Sol and Centauri
        /// under the mod at once would read each other's masks.
        /// </summary>
        public static void ResetForNewRound()
        {
            _balterium = new LayerB();
            _hqMask = new LayerB();
            _hqMinSepMask = new LayerB();
            _hqBuildableMask = new LayerB();
            _pressure = new LayerI();
            _balteriumWeighted = new LayerI();
            _ecoHqExpansionValue = new LayerI();
            _distanceToHq = new LayerI();
            _scratch = new List<ResourceArea>(16);
            OnRoundReset();
        }

        public static void OnRoundReset()
        {
            _balteriumDirty = _hqMaskDirty = _hqMinSepMaskDirty = _hqBuildableMaskDirty = _pressureDirty = true;
            _balteriumWeightedDirty = _ecoHqExpansionDirty = _distanceToHqDirty = true;
            _lastKnownBalteriumCount = -1;
            _lastTeam = null;
        }

        /// <summary>
        /// Refresh the three per-structure radii from live ConstructionData at scene load.
        /// The balance mod overrides ConstructionData.MaximumBaseStructureDistance —
        /// reading it here means we always match the running config without recompiling.
        /// If a structure isn't found in any team's ConstructionOptions (round hasn't
        /// spawned team HQs yet), the previous value is kept.
        /// </summary>
        public static void RefreshRuntimeRadii(Team humanTeam)
        {
            try
            {
                float refDist = TryReadMaxBaseStructDist(humanTeam, "Refinery");
                float hqDist  = TryReadMaxBaseStructDist(humanTeam, "Headquarters");
                float prodDist= TryReadMaxBaseStructDist(humanTeam, "Barracks");
                if (refDist  > 0f && !Mathf.Approximately(refDist,  REFINERY_HQ_RADIUS))
                {
                    MelonLoader.MelonLogger.Msg($"[RTSA/Layers] REFINERY_HQ_RADIUS: {REFINERY_HQ_RADIUS:F0}m → {refDist:F0}m (from ConstructionData)");
                    REFINERY_HQ_RADIUS = refDist;
                }
                if (hqDist   > 0f && !Mathf.Approximately(hqDist,   HQ_TO_HQ_RADIUS))
                {
                    MelonLoader.MelonLogger.Msg($"[RTSA/Layers] HQ_TO_HQ_RADIUS:   {HQ_TO_HQ_RADIUS:F0}m → {hqDist:F0}m (from ConstructionData)");
                    HQ_TO_HQ_RADIUS = hqDist;
                }
                if (prodDist > 0f && !Mathf.Approximately(prodDist, PRODUCTION_HQ_RADIUS))
                {
                    MelonLoader.MelonLogger.Msg($"[RTSA/Layers] PRODUCTION_HQ_RADIUS: {PRODUCTION_HQ_RADIUS:F0}m → {prodDist:F0}m (from ConstructionData)");
                    PRODUCTION_HQ_RADIUS = prodDist;
                }
            }
            catch (Exception ex) { MelonLoader.MelonLogger.Warning($"[RTSA/Layers] RefreshRuntimeRadii threw: {ex.Message}"); }
        }

        static float TryReadMaxBaseStructDist(Team team, string displayName)
        {
            try
            {
                if (team?.Structures == null) return 0f;
                for (int i = 0; i < team.Structures.Count; i++)
                {
                    var s = team.Structures[i];
                    if (s?.ConstructionOptions == null) continue;
                    foreach (var cd in s.ConstructionOptions)
                    {
                        if (cd?.ObjectInfo == null) continue;
                        if (string.Equals(cd.ObjectInfo.DisplayName, displayName, StringComparison.OrdinalIgnoreCase))
                            return cd.MaximumBaseStructureDistance;
                    }
                }
            }
            catch { }
            return 0f;
        }

        public static void RefreshAllForSnapshot(Team team)
        {
            GetRefineryPressure(team);
            GetEcoHqExpansionValue(team);
        }

        // ---- Rebuilds ----

        static void RefreshBalteriumDirtinessByPoll(Team team)
        {
            try
            {
                _scratch.Clear();
                int count = ResourceArea.GetKnownResourcesAreas(team, team.UsableResource, _scratch, ignoreEmpty: true);
                if (count != _lastKnownBalteriumCount)
                {
                    _balteriumDirty = true;
                    _balteriumWeightedDirty = true;
                    _ecoHqExpansionDirty = true;
                    _lastKnownBalteriumCount = count;
                }
            }
            catch { }
        }

        static void RebuildEcoHqExpansion(LayerI weighted, LayerB minSepMask, LayerB buildableMask, LayerI distanceToHq)
        {
            var dst  = _ecoHqExpansionValue.Data;
            var wsrc = weighted.Data;
            var sm   = minSepMask.Data;
            var bm   = buildableMask.Data;
            var dt   = distanceToHq.Data;
            // A HEADQUARTERS AT THE MAP BORDER WASTES ITS RADIUS. Naraka 2026-09-07
            // 22:04: an expansion HQ landed at (-975,-3030), on the southern edge,
            // while a cell of similar value sat at (-1580,-2020) (DrMuck). A cell
            // within a refinery reach of the border keeps only the share of that
            // reach that lies inside the map, never less than a quarter.
            int w = GridWorld.Width, h = GridWorld.Height;
            float reachCells = Mathf.Max(1f, REFINERY_HQ_RADIUS / GridWorld.CellSize);
            for (int i = 0; i < dst.Length; i++)
            {
                // Candidate iff INSIDE HQ→HQ chain reach (bm=1) AND OUTSIDE the min-
                // separation ring of every existing HQ (sm=0). The old logic used
                // refinery-reach as the exclusion, but that lets a new HQ land right
                // at REFINERY_HQ_RADIUS + ε where refinery coverage would overlap.
                // Using MIN_HQ_HQ_SEPARATION_M (= 2× REFINERY_HQ_RADIUS) guarantees
                // clean, non-overlapping refinery zones per HQ.
                //
                // Value = balterium-in-reach × distance-to-nearest-HQ (in cells). The
                // distance factor makes ArgMax prefer FAR cells with good eco over
                // near cells with slightly better eco.
                int x = i % w, z = i / w;
                int dEdge = Mathf.Min(Mathf.Min(x, w - 1 - x), Mathf.Min(z, h - 1 - z));
                float edge = Mathf.Clamp(dEdge / reachCells, 0.25f, 1f);
                dst[i] = (bm[i] != 0 && sm[i] == 0) ? (int)(wsrc[i] * dt[i] * edge) : 0;
            }
        }

        static void RebuildDistanceToHq(Team team)
        {
            // Distance in CELLS from each cell to nearest HQ world position. O(W×H×NumHQs)
            // — cheap for typical maps (~100×100 grid, 1-3 HQs → 30k ops per rebuild,
            // and this only fires when hq mask changes.
            var d = _distanceToHq.Data;
            var hqPositions = new List<Vector3>(4);
            try
            {
                var structs = team.Structures;
                if (structs != null)
                    for (int i = 0; i < structs.Count; i++)
                    {
                        var s = structs[i];
                        if (s?.ObjectInfo == null || s.IsDestroyed) continue;
                        if (!string.Equals(s.ObjectInfo.DisplayName, "Headquarters", StringComparison.OrdinalIgnoreCase)) continue;
                        hqPositions.Add(s.transform.position);
                    }
            }
            catch { }

            int w = GridWorld.Width, h = GridWorld.Height;
            float cs = GridWorld.CellSize;
            if (hqPositions.Count == 0)
            {
                // No HQ known — distance uniformly large so BalteriumWeighted alone drives argmax.
                int fill = Mathf.Max(w, h);
                for (int i = 0; i < d.Length; i++) d[i] = fill;
                return;
            }

            for (int z = 0; z < h; z++)
            {
                float wz = GridWorld.OriginZ + (z + 0.5f) * cs;
                int rowBase = z * w;
                for (int x = 0; x < w; x++)
                {
                    float wx = GridWorld.OriginX + (x + 0.5f) * cs;
                    float bestSq = float.MaxValue;
                    for (int hi = 0; hi < hqPositions.Count; hi++)
                    {
                        var p = hqPositions[hi];
                        float dx = wx - p.x, dz = wz - p.z;
                        float d2 = dx * dx + dz * dz;
                        if (d2 < bestSq) bestSq = d2;
                    }
                    d[rowBase + x] = (int)(Mathf.Sqrt(bestSq) / cs);
                }
            }
        }

        static void RebuildBalterium(Team team)
        {
            _balterium.Clear();
            for (int i = 0; i < _scratch.Count; i++)
            {
                var patch = _scratch[i];
                if (patch != null) _balterium.SetOneAtWorld(patch.SignalCenter);
            }
        }

        // The three HQ masks are drawn as EUCLIDEAN disks around each HQ position,
        // NOT via LayerB.DilateInto (which uses a Chebyshev/square window). Game
        // placement rules are round — square dilation created straight cut-offs
        // in the eco expansion layer that didn't match where placement is actually
        // legal.
        static void FillHqDisks(Team team, LayerB into, float radiusMeters)
        {
            into.Clear();
            try
            {
                var structs = team.Structures;
                if (structs != null)
                    for (int i = 0; i < structs.Count; i++)
                    {
                        var s = structs[i];
                        if (s == null || s.ObjectInfo == null || s.IsDestroyed) continue;
                        if (!string.Equals(s.ObjectInfo.DisplayName, "Headquarters", StringComparison.OrdinalIgnoreCase)) continue;
                        into.SetDiskAtWorld(s.transform.position, radiusMeters);
                    }
            } catch { }
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
                        if (!string.Equals(cs.ObjectInfo.DisplayName, "Headquarters", StringComparison.OrdinalIgnoreCase)) continue;
                        into.SetDiskAtWorld(cs.transform.position, radiusMeters);
                    }
            } catch { }
        }

        static void RebuildHqMask(Team team)          => FillHqDisks(team, _hqMask,          REFINERY_HQ_RADIUS);
        static void RebuildHqMinSepMask(Team team)    => FillHqDisks(team, _hqMinSepMask,    MIN_HQ_HQ_SEPARATION_M);
        static void RebuildHqBuildableMask(Team team) => FillHqDisks(team, _hqBuildableMask, HQ_TO_HQ_RADIUS);

        static void RebuildPressure(Team team, LayerB balt, LayerB mask)
        {
            var pd = _pressure.Data;
            var bd = balt.Data;
            var md = mask.Data;
            for (int i = 0; i < pd.Length; i++)
                pd[i] = (bd[i] != 0 && md[i] != 0) ? BALTERIUM_MARK : 0;

            SubtractStructureCoverage(team, "Refinery",
                _pressure, REFINERY_COVERAGE_METERS, REFINERY_PENALTY);
        }

        static void EnsureTeamCoherent(Team team)
        {
            if (team != _lastTeam)
            {
                _balteriumDirty = _hqMaskDirty = _hqMinSepMaskDirty = _hqBuildableMaskDirty = _pressureDirty = true;
                _balteriumWeightedDirty = _ecoHqExpansionDirty = _distanceToHqDirty = true;
                _lastKnownBalteriumCount = -1;
                _lastTeam = team;
            }
        }

        // ---- helpers ----

        static void MarkStructurePositions(Team team, LayerB layer, params string[] displayNames)
        {
            try
            {
                var structs = team.Structures;
                if (structs != null)
                    for (int i = 0; i < structs.Count; i++)
                    {
                        var s = structs[i];
                        if (s == null || s.ObjectInfo == null || s.IsDestroyed) continue;
                        string n = s.ObjectInfo.DisplayName ?? "";
                        if (!MatchesAny(n, displayNames)) continue;
                        layer.SetOneAtWorld(s.transform.position);
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
                        string n = cs.ObjectInfo.DisplayName ?? "";
                        if (!MatchesAny(n, displayNames)) continue;
                        layer.SetOneAtWorld(cs.transform.position);
                    }
            }
            catch { }
        }

        static bool MatchesAny(string n, string[] names)
        {
            for (int j = 0; j < names.Length; j++)
                if (string.Equals(n, names[j], StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        static void SubtractStructureCoverage(Team team, string displayName, LayerI layer, float radiusMeters, int penalty)
        {
            try
            {
                var structs = team.Structures;
                if (structs != null)
                    for (int i = 0; i < structs.Count; i++)
                    {
                        var s = structs[i];
                        if (s == null || s.ObjectInfo == null || s.IsDestroyed) continue;
                        if (!string.Equals(s.ObjectInfo.DisplayName, displayName, StringComparison.OrdinalIgnoreCase)) continue;
                        layer.SubtractDiskAtWorld(s.transform.position, radiusMeters, penalty);
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
                        layer.SubtractDiskAtWorld(cs.transform.position, radiusMeters, penalty);
                    }
            }
            catch { }
        }
    }
}
