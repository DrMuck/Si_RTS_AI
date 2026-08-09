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
    /// SUPERSEDED — THIS IS NOW A FALLBACK ONLY. See GameFow: every consumer
    /// takes the game's real per-team fog, and this disk reconstruction runs
    /// only if those pixels cannot be read. It measured 0 of 380 enemy
    /// structures visible on a map it claimed was 99.1% explored, so it is not
    /// to be trusted with anything and its use is announced in the log.
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

        /// <summary>
        /// WHAT THE LAST REBUILD ACTUALLY MANAGED TO SEE WITH.
        ///
        /// The round of 2026-08-09 reported "structs 0/380 in sight, HQ 0/20"
        /// — not a few enemy buildings missed, every single one, all round,
        /// with twenty enemy headquarters standing in the open. Enemy UNITS
        /// registered normally through the identical check, and the rejected
        /// cell index was in bounds and sane.
        ///
        /// Seen near our own base but never at theirs is the signature of
        /// vision coming from STRUCTURES ONLY: our Nest and nodes light up our
        /// own ground, and the twenty scouts contribute nothing. Both loops
        /// below sat behind a bare catch{}, so a throw on team.Units would
        /// produce exactly that and say nothing about it for the whole round.
        ///
        /// These counters make the next round answer it outright instead of
        /// leaving it to inference.
        /// </summary>
        public static int LastStructSources { get; private set; }
        public static int LastUnitSources   { get; private set; }
        public static string LastFailure    { get; private set; }
        static bool _loggedFailure;

        static void RebuildActive(Team team, LayerB active)
        {
            active.Clear();
            if (team == null) return;
            int nStruct = 0, nUnit = 0;

            try
            {
                var structs = team.Structures;
                if (structs != null)
                    for (int i = 0; i < structs.Count; i++)
                    {
                        var s = structs[i];
                        if (s == null || s.ObjectInfo == null || s.IsDestroyed) continue;
                        active.SetDiskAtWorld(s.transform.position, VisionOf(s, STRUCT_VISION_M));
                        nStruct++;
                    }
            }
            catch (System.Exception ex) { Note("structures: " + ex.Message); }

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
                        nUnit++;
                    }
            }
            catch (System.Exception ex) { Note("units: " + ex.Message); }

            LastStructSources = nStruct;
            LastUnitSources   = nUnit;
        }

        /// <summary>A vision source that throws is not a detail to swallow — it
        /// blinds the whole planner. Logged once so it cannot spam, kept so the
        /// sight report can carry it.</summary>
        static void Note(string what)
        {
            LastFailure = what;
            if (_loggedFailure) return;
            _loggedFailure = true;
            MelonLoader.MelonLogger.Warning(
                "[FOW] a vision source threw and every later pass will be blind to it — " + what);
        }

        // THE GAME'S ANSWER FIRST, ALWAYS. The disk reconstruction below stays
        // only for the case where the fog pixels cannot be read at all — it is
        // the model that reported 99.1% explored while never once seeing an
        // enemy building, so it is a last resort and says so in the log.
        public static LayerB GetActive(Team team)
        {
            if (GameFow.TryFill(team, out _, out var gameActive) && gameActive != null)
            {
                var (_, e0) = EnsureLayers(team);
                e0.OrFrom(gameActive);
                return gameActive;
            }
            var (a, e) = EnsureLayers(team);
            RebuildActive(team, a);
            e.OrFrom(a);
            return a;
        }

        public static LayerB GetExplored(Team team)
        {
            if (GameFow.TryFill(team, out var gameExplored, out _) && gameExplored != null)
                return gameExplored;
            GetActive(team);
            return _explored[team];
        }

        public static void OnRoundReset()
        {
            // Clear both dictionaries — grid dimensions may have changed on the
            // new map, so keeping stale byte[] would cause size mismatches.
            _active.Clear();
            _explored.Clear();
            LastStructSources = LastUnitSources = 0;
            LastFailure = null;
            _loggedFailure = false;
        }
    }
}
