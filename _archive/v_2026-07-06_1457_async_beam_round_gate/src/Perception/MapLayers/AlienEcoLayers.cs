using MelonLoader;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Si_RTS_AI.Perception.MapLayers
{
    /// <summary>
    /// Alien-eco layers with lazy rebuild.
    ///
    /// Dependency graph:
    ///
    ///   Biotics discovery ── BioticsDiscovered (LayerB)
    ///                             │
    ///                             ▼
    ///                       BioticsWeighted (LayerI)  ── box sum, cluster kernel
    ///                             │
    ///                             ▼
    ///   BC changes ─── BcMask (LayerB) ─────┐
    ///                                       ▼
    ///   Cyst changes ────────────► CystPressure (LayerI) = weighted × mask − cyst disks
    ///
    /// Rebuild rule: each layer has a dirty flag. On query (Get*), we check flags in
    /// dependency order and rebuild only the dirty ones. Event handlers set the right
    /// flags when the game state that feeds a layer changes. Biotics discovery isn't
    /// event-driven (it happens whenever a unit's FoW covers a patch) so we poll the
    /// known-biotics count on query and mark dirty if it changed.
    /// </summary>
    public static class AlienEcoLayers
    {
        public const float CLUSTER_KERNEL_METERS = 300f;
        public const float BC_REACH_METERS       = 250f;
        public const float CYST_COVERAGE_METERS  = 700f;
        public const int   CYST_PENALTY          = 9999;

        static readonly LayerB _biotics  = new LayerB();
        static readonly LayerB _bcMask   = new LayerB();
        static readonly LayerI _weighted = new LayerI();
        static readonly LayerI _pressure = new LayerI();
        static readonly List<ResourceArea> _bioticsScratch = new List<ResourceArea>(16);

        // Dirty flags — layer needs to be recomputed on next Get*.
        static bool _bioticsDirty  = true;
        static bool _bcMaskDirty   = true;
        static bool _weightedDirty = true;
        static bool _pressureDirty = true;

        // Poll cache for biotics-count change detection (discovery isn't event-driven).
        static int  _lastKnownBioticsCount = -1;
        // Team we last rebuilt for — if it changes (unlikely), invalidate everything.
        static Team _lastTeam;

        // ---- Accessors ----

        /// <summary>Returns current BioticsDiscovered, rebuilding if dirty.</summary>
        public static LayerB GetBioticsDiscovered(Team team)
        {
            EnsureTeamCoherent(team);
            RefreshBioticsDirtinessByPoll(team);
            if (_bioticsDirty) { RebuildBiotics(team); _bioticsDirty = false; _weightedDirty = true; _pressureDirty = true; }
            return _biotics;
        }

        /// <summary>Returns current BcMask, rebuilding if dirty.</summary>
        public static LayerB GetBcMask(Team team)
        {
            EnsureTeamCoherent(team);
            if (_bcMaskDirty) { RebuildBcMask(team); _bcMaskDirty = false; _pressureDirty = true; }
            return _bcMask;
        }

        public static LayerI GetBioticsWeighted(Team team)
        {
            GetBioticsDiscovered(team);   // ensures biotics is fresh
            if (_weightedDirty) { RebuildWeighted(); _weightedDirty = false; _pressureDirty = true; }
            return _weighted;
        }

        public static LayerI GetCystPressure(Team team)
        {
            var weighted = GetBioticsWeighted(team);   // recurses through biotics
            var mask     = GetBcMask(team);            // ensures BC mask fresh
            if (_pressureDirty) { RebuildPressure(team, weighted, mask); _pressureDirty = false; }
            return _pressure;
        }

        // ---- Event hooks (called from Si_RTS_AI on GameEvents subscriptions) ----

        public static void OnBcChanged()       { _bcMaskDirty   = true; _pressureDirty = true; }
        public static void OnCystChanged()     { _pressureDirty = true; }
        public static void OnRoundReset()
        {
            _bioticsDirty = _bcMaskDirty = _weightedDirty = _pressureDirty = true;
            _lastKnownBioticsCount = -1;
            _lastTeam = null;
        }

        // Snapshot hook — the replay uses this to grab a consistent set of layers at a
        // given tick. All getters share the same team so all layers get rebuilt-if-dirty.
        public static void RefreshAllForSnapshot(Team team)
        {
            GetCystPressure(team);   // cascades through the whole graph
        }

        // ---- Rebuilds ----

        static void RefreshBioticsDirtinessByPoll(Team team)
        {
            // GetKnownResourcesAreas is cheap (allocation into a reused list). We poll
            // the count — if it changed, the biotics layer is dirty. This is the one
            // signal we can't get via structure events since discovery = FoW × patch.
            try
            {
                _bioticsScratch.Clear();
                int count = ResourceArea.GetKnownResourcesAreas(team, team.UsableResource, _bioticsScratch, ignoreEmpty: true);
                if (count != _lastKnownBioticsCount)
                {
                    _bioticsDirty = true;
                    _lastKnownBioticsCount = count;
                }
            }
            catch { }
        }

        static void RebuildBiotics(Team team)
        {
            _biotics.Clear();
            for (int i = 0; i < _bioticsScratch.Count; i++)
            {
                var patch = _bioticsScratch[i];
                if (patch != null) _biotics.SetOneAtWorld(patch.SignalCenter);
            }
        }

        static void RebuildBcMask(Team team)
        {
            _bcMask.Clear();
            MarkStructurePositions(team, "Bio Cache", _bcMask);
            _bcMask.DilateInto(GridWorld.MetersToCellsInt(BC_REACH_METERS), _bcMask);
        }

        static void RebuildWeighted()
        {
            // Circle kernel — biocache/cyst reach is a round area, box kernel over-counts
            // on the diagonals of the cluster. Edge cells may be partially included
            // (staircase), which is fine given the ~40m grid resolution.
            _weighted.SumCircleFrom(_biotics, GridWorld.MetersToCellsInt(CLUSTER_KERNEL_METERS));
        }

        static void RebuildPressure(Team team, LayerI weighted, LayerB mask)
        {
            Array.Copy(weighted.Data, _pressure.Data, weighted.Data.Length);
            _pressure.MaskInPlace(mask);
            SubtractStructureCoverage(team, "Lesser Spawning Cyst",
                _pressure, CYST_COVERAGE_METERS, CYST_PENALTY);
        }

        static void EnsureTeamCoherent(Team team)
        {
            if (team != _lastTeam)
            {
                _bioticsDirty = _bcMaskDirty = _weightedDirty = _pressureDirty = true;
                _lastKnownBioticsCount = -1;
                _lastTeam = team;
            }
        }

        // ---- Structure iteration helpers (same as before) ----

        static void MarkStructurePositions(Team team, string displayName, LayerB layer)
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
                        if (!string.Equals(cs.ObjectInfo.DisplayName, displayName, StringComparison.OrdinalIgnoreCase)) continue;
                        layer.SetOneAtWorld(cs.transform.position);
                    }
            }
            catch { }
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
