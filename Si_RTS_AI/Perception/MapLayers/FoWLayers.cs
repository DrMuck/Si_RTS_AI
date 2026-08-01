using MelonLoader;
using Silica;
using System;
using System.Collections.Generic;

namespace Si_RTS_AI.Perception.MapLayers
{
    /// <summary>
    /// Fog-of-War layers computed from entity positions. Two per team:
    ///   active   — cells currently within vision of any owned unit/structure
    ///   explored — cumulative union of active since round start (passive memory)
    ///
    /// This is an AI-side FoW approximation. Silica also exposes a native FoW
    /// texture via <c>FogOfWar.GetTeamFogTexture(team)</c>, but reading it
    /// requires Il2Cpp interop and matches what the RENDER shows, not what the
    /// AI planner can reason about. Computing from unit positions gives us a
    /// deterministic per-team knowledge model that lines up with how the AI
    /// consumes the world.
    ///
    /// Vision radii are conservative constants — Silica doesn't cleanly expose
    /// a per-unit VisionRange field. Good enough for eco planning; if we later
    /// need pixel-exact match with the game FoW we'll switch to the texture.
    /// </summary>
    public static class FoWLayers
    {
        // Rough vision radii. Structures see farther than units on average;
        // harvesters see less than combat units. These are AI-planner constants,
        // not game-truth values.
        const float STRUCT_VISION_M = 700f;
        const float UNIT_VISION_M   = 500f;
        const float HARV_VISION_M   = 300f;

        static readonly Dictionary<Team, LayerB> _active   = new Dictionary<Team, LayerB>();
        static readonly Dictionary<Team, LayerB> _explored = new Dictionary<Team, LayerB>();

        static (LayerB a, LayerB e) EnsureLayers(Team team)
        {
            if (!_active.TryGetValue(team, out var a))   { a = new LayerB(); _active[team]   = a; }
            if (!_explored.TryGetValue(team, out var e)) { e = new LayerB(); _explored[team] = e; }
            return (a, e);
        }

        /// <summary>
        /// How far this object actually sees, from the game.
        ///
        /// The constants below are guesses, and a guess that is too GENEROUS is
        /// the damaging direction: ground gets marked explored that was never
        /// seen, so the planner believes it has already looked somewhere it has
        /// not, and biotics sitting there stay unknown while the map appears
        /// surveyed. Reported on MonumentValley 2026-08-01 — much of the map
        /// showing as explored while biotics inside it were missing.
        ///
        /// FogOfWarViewDistance is the per-object truth and follows any balance
        /// mod. The constants remain only as a fallback for objects that do not
        /// report one.
        /// </summary>
        static float VisionOf(BaseGameObject o, float fallbackM)
        {
            try
            {
                float v = o.FogOfWarViewDistance;
                if (v > 1f) return v;
            }
            catch { }
            return fallbackM;
        }

        static void RebuildActive(Team team, LayerB active)
        {
            active.Clear();
            if (team == null) return;

            try
            {
                var structs = team.Structures;
                if (structs != null)
                    for (int i = 0; i < structs.Count; i++)
                    {
                        var s = structs[i];
                        if (s == null || s.ObjectInfo == null || s.IsDestroyed) continue;
                        active.SetDiskAtWorld(s.transform.position, VisionOf(s, STRUCT_VISION_M));
                    }
            }
            catch { }

            try
            {
                var units = team.Units;
                if (units != null)
                    for (int i = 0; i < units.Count; i++)
                    {
                        var u = units[i];
                        if (u == null || u.ObjectInfo == null || u.IsDestroyed) continue;
                        string name = u.ObjectInfo.DisplayName ?? "";
                        float fallback = (name == "Harvester" || name == "HoverHarvester"
                                          || name == "HeavyHarvester") ? HARV_VISION_M : UNIT_VISION_M;
                        float r = VisionOf(u, fallback);
                        active.SetDiskAtWorld(u.transform.position, r);
                    }
            }
            catch { }
        }

        public static LayerB GetActive(Team team)
        {
            var (a, e) = EnsureLayers(team);
            RebuildActive(team, a);
            e.OrFrom(a);   // cheap, and keeps explored consistent with the just-rebuilt active
            return a;
        }

        public static LayerB GetExplored(Team team)
        {
            // Rebuild active + OR into explored, then return explored.
            GetActive(team);
            return _explored[team];
        }

        public static void OnRoundReset()
        {
            // Clear both dictionaries — grid dimensions may have changed on the
            // new map, so keeping stale byte[] would cause size mismatches.
            _active.Clear();
            _explored.Clear();
        }
    }
}
