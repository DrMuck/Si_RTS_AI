using MelonLoader;
using Silica;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Si_RTS_AI.Perception
{
    /// <summary>
    /// Logs when each eco structure STARTS building, with round time.
    ///
    /// Exists because `[SPAWN_S]` was being read as completion when it is
    /// actually build START — which made the NarakaCity timings unreadable: a
    /// Cyst logged at t=104s had been requested at t=48s and spent the gap in
    /// the game's placement search, not building.
    ///
    ///     [BUILD] Bio Cache            start=52s at=(2645,1135)
    ///     [BUILD] Lesser Spawning Cyst start=55s at=(2686,1170)
    ///
    /// Completion is deliberately NOT logged. Two candidate signals were tried
    /// and both were wrong: ConstructionBuildUp01 lives on ConstructionData and
    /// is a static ratio, and IsFunctional returns true ~1s after start for
    /// every structure. Infer completion from start + the build time read off
    /// the CDs instead of trusting a number that isn't measuring anything.
    internal static class BuildTimeline
    {
        const float TICK_S = 1f;


        class Entry
        {
            public string Name = "";
            public Vector3 Pos;
            public float StartedAtRoundT;
            public bool  Reported;
        }

        static readonly Dictionary<Structure, Entry> _tracked = new Dictionary<Structure, Entry>();
        static float _lastTickAt;

        internal static void ResetForNewRound()
        {
            _tracked.Clear();
            _lastTickAt = 0f;
        }

        internal static void Tick(Team team)
        {
            if (team == null) return;
            if (!(team.name ?? "").Contains("Alien")) return;
            float now = Time.time;
            if (now - _lastTickAt < TICK_S) return;
            _lastTickAt = now;

            float roundT;
            try { roundT = MapLayers.LayerReplay.CurrentRoundTime; } catch { return; }

            try
            {
                var structs = team.Structures;
                if (structs == null) return;
                for (int i = 0; i < structs.Count; i++)
                {
                    var st = structs[i];
                    if (st == null || st.ObjectInfo == null || st.IsDestroyed) continue;
                    string name = st.ObjectInfo.DisplayName ?? "?";
                    // Only the eco structures whose ordering we reason about.
                    if (name != "Bio Cache" && name != "Lesser Spawning Cyst"
                        && name != "Node" && name != "Quantum Cortex") continue;

                    if (!_tracked.TryGetValue(st, out var e))
                    {
                        e = new Entry { Name = name, Pos = st.transform.position, StartedAtRoundT = roundT };
                        _tracked[st] = e;
                        Si_RTS_AI.AppendToRound(
                            $"[BUILD] {name} start={roundT:F0}s at=({e.Pos.x:F0},{e.Pos.z:F0})");
                        continue;
                    }
                    // NO done= LINE.
                    //
                    // IsFunctional reported true one second after every start —
                    // Bio Caches and Nodes alike, against real build times of
                    // 30s and 20s. So it is not a construction-complete signal
                    // on this build (ConstructionBuildUp01 was not either; it
                    // lives on ConstructionData and is a static ratio). Rather
                    // than emit "took=1s" and have it read as measurement, only
                    // the START is logged, which does come from the structure
                    // actually appearing and is trustworthy.
                    //
                    // Completion can be inferred where needed: start + the
                    // build time read from the CDs (BC 30s, Cyst 35s, Node 20s).
                    e.Reported = true;
                }
            }
            catch (Exception ex) { MelonLogger.Warning("[BUILD] threw: " + ex.Message); }
        }

        /// <summary>
        /// Is a Bio Cache near this spot COMPLETE?
        ///
        /// Judged by elapsed time since construction started, against the build
        /// time read off the ConstructionData — because neither property that
        /// looked like a completion flag actually is one. IsFunctional turns
        /// true about a second after a structure STARTS (which is why every
        /// logged "took=" was 1s), and ConstructionBuildUp01 is a static ratio
        /// on ConstructionData.
        ///
        /// Trusting IsFunctional made the planner fire Cysts ~30s before their
        /// Bio Cache existed. The game silently refused each one, but TryFire
        /// had already recorded the position — and its 45s dedup window then
        /// blocked the retry, which is the fixed offset in "Cyst requested t=48s,
        /// structure at t=106s".
        /// </summary>
        internal static bool BcCompleteNear(Vector3 pos, float radiusM, float roundT)
        {
            float r2 = radiusM * radiusM;
            foreach (var kv in _tracked)
            {
                var e = kv.Value;
                if (e.Name != "Bio Cache") continue;
                float dx = e.Pos.x - pos.x, dz = e.Pos.z - pos.z;
                if (dx * dx + dz * dz > r2) continue;
                if (roundT - e.StartedAtRoundT >= Planning.EcoSimulator.BC_BUILD_S) return true;
            }
            return false;
        }

    }
}
