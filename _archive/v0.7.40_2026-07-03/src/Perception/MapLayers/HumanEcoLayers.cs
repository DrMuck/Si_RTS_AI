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
        public const float HQ_TO_HQ_RADIUS      = 900f;   // HQ → HQ  (widest — strategic expansion reach)
        public const float REFINERY_HQ_RADIUS   = 450f;   // Refinery → HQ (medium)
        public const float PRODUCTION_HQ_RADIUS = 250f;   // Barracks/Factory → HQ (tightest)

        public const float REFINERY_COVERAGE_METERS = 400f;
        public const int   REFINERY_PENALTY         = 9999;

        // For HQ expansion — cluster kernel matches the refinery reach so a new HQ's
        // "value" = # known balterium a refinery from that new HQ could cover.
        public const float HQ_EXPANSION_KERNEL_METERS = REFINERY_HQ_RADIUS;

        public const int BALTERIUM_MARK = 1;

        static readonly LayerB _balterium         = new LayerB();
        // Refinery-reach mask (HQs dilated by REFINERY_HQ_RADIUS). Refineries can be
        // placed anywhere inside this.
        static readonly LayerB _hqMask            = new LayerB();
        // HQ-to-HQ mask (HQs dilated by HQ_TO_HQ_RADIUS). NEW HQs can only be placed
        // inside this — every new HQ must chain-link to an existing one within this
        // tighter radius.
        static readonly LayerB _hqBuildableMask   = new LayerB();
        static readonly LayerI _pressure          = new LayerI();
        static readonly LayerI _balteriumWeighted = new LayerI();
        static readonly LayerI _hqExpansionValue  = new LayerI();
        // Distance (in cells) from each cell to the nearest existing HQ. Used to
        // weight HqExpansionValue toward FAR cells — otherwise ArgMax keeps picking
        // the closest high-balterium cell and expansion HQs pile near the starter HQ.
        static readonly LayerI _distanceToHq      = new LayerI();
        static readonly List<ResourceArea> _scratch = new List<ResourceArea>(16);

        static bool _balteriumDirty         = true;
        static bool _hqMaskDirty            = true;
        static bool _hqBuildableMaskDirty   = true;
        static bool _pressureDirty          = true;
        static bool _balteriumWeightedDirty = true;
        static bool _hqExpansionDirty       = true;
        static bool _distanceToHqDirty      = true;

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
            if (_hqMaskDirty) { RebuildHqMask(team); _hqMaskDirty = false; _pressureDirty = true; _hqExpansionDirty = true; }
            return _hqMask;
        }

        /// <summary>HQ→HQ range mask. Cells here can receive a NEW HQ placement.</summary>
        public static LayerB GetHqBuildableMask(Team team)
        {
            EnsureTeamCoherent(team);
            if (_hqBuildableMaskDirty) { RebuildHqBuildableMask(team); _hqBuildableMaskDirty = false; _hqExpansionDirty = true; }
            return _hqBuildableMask;
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
                _hqExpansionDirty = true;
            }
            return _balteriumWeighted;
        }

        /// <summary>Best next-HQ cells: high balterium density, WITHIN HQ→HQ range of an
        /// existing HQ (so we can actually place there), AND outside current refinery
        /// reach (so the new HQ brings new balterium into reach).</summary>
        public static LayerI GetHqExpansionValue(Team team)
        {
            var weighted     = GetBalteriumWeighted(team);
            var refineryMask = GetHqMask(team);
            var buildable    = GetHqBuildableMask(team);
            if (_distanceToHqDirty) { RebuildDistanceToHq(team); _distanceToHqDirty = false; _hqExpansionDirty = true; }
            if (_hqExpansionDirty)
            {
                RebuildHqExpansion(weighted, refineryMask, buildable, _distanceToHq);
                _hqExpansionDirty = false;
            }
            return _hqExpansionValue;
        }

        // ---- Event hooks ----

        public static void OnHqChanged()
        {
            _hqMaskDirty = true;
            _hqBuildableMaskDirty = true;
            _distanceToHqDirty = true;
            _pressureDirty = true;
            _hqExpansionDirty = true;
        }
        public static void OnRefineryChanged() { _pressureDirty = true; }
        public static void OnBalteriumDiscovered()
        {
            _balteriumDirty = true;
            _pressureDirty  = true;
            _balteriumWeightedDirty = true;
            _hqExpansionDirty = true;
        }
        public static void OnRoundReset()
        {
            _balteriumDirty = _hqMaskDirty = _hqBuildableMaskDirty = _pressureDirty = true;
            _balteriumWeightedDirty = _hqExpansionDirty = _distanceToHqDirty = true;
            _lastKnownBalteriumCount = -1;
            _lastTeam = null;
        }

        public static void RefreshAllForSnapshot(Team team)
        {
            GetRefineryPressure(team);
            GetHqExpansionValue(team);
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
                    _hqExpansionDirty = true;
                    _lastKnownBalteriumCount = count;
                }
            }
            catch { }
        }

        static void RebuildHqExpansion(LayerI weighted, LayerB refineryMask, LayerB buildableMask, LayerI distanceToHq)
        {
            var dst  = _hqExpansionValue.Data;
            var wsrc = weighted.Data;
            var rm   = refineryMask.Data;
            var bm   = buildableMask.Data;
            var dt   = distanceToHq.Data;
            for (int i = 0; i < dst.Length; i++)
            {
                // Candidate iff INSIDE HQ→HQ reach AND OUTSIDE current refinery reach.
                // Value = balterium-in-reach × distance-to-nearest-HQ (in cells). The
                // distance factor makes the argmax prefer FAR cells with good eco over
                // near cells with slightly better eco — matches the user's "HQs as far
                // apart as possible, weighted by resource optima" spec.
                dst[i] = (bm[i] != 0 && rm[i] == 0) ? wsrc[i] * dt[i] : 0;
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

        static void RebuildHqMask(Team team)
        {
            // Runtime DisplayName for both Sol and Centauri HQs is the generic
            // "Headquarters" (ObjectInfo asset names differ but DisplayName does not).
            _hqMask.Clear();
            MarkStructurePositions(team, _hqMask, "Headquarters");
            _hqMask.DilateInto(GridWorld.MetersToCellsInt(REFINERY_HQ_RADIUS), _hqMask);
        }

        static void RebuildHqBuildableMask(Team team)
        {
            _hqBuildableMask.Clear();
            MarkStructurePositions(team, _hqBuildableMask, "Headquarters");
            _hqBuildableMask.DilateInto(GridWorld.MetersToCellsInt(HQ_TO_HQ_RADIUS), _hqBuildableMask);
        }

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
                _balteriumDirty = _hqMaskDirty = _hqBuildableMaskDirty = _pressureDirty = true;
                _balteriumWeightedDirty = _hqExpansionDirty = _distanceToHqDirty = true;
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
