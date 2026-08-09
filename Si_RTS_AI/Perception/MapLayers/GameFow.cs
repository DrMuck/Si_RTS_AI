using MelonLoader;
using Silica;
using UnityEngine;

namespace Si_RTS_AI.Perception.MapLayers
{
    /// <summary>
    /// THE GAME'S OWN FOG OF WAR, ASKED DIRECTLY.
    ///
    /// DrMuck: "the game has a fow layer by itself. are we tapping into this
    /// one?" We were not. FoWLayers reconstructs visibility by stamping disks
    /// of guessed radius (700m for structures, 500m for units) into our own
    /// 40m grid, and then everything downstream trusted that reconstruction as
    /// if it were sight.
    ///
    /// It was not sight, and the round of 2026-08-09 measured the gap exactly:
    ///
    ///     structs 0/380 in sight, HQ 0/20, explored 99.1%
    ///
    /// Zero of three hundred and eighty enemy buildings ever visible, twenty
    /// enemy headquarters among them, on a map our own layer claimed we had
    /// explored to 99.1% — while enemy UNITS passed the identical check in 95%
    /// of samples. A model that disagrees with itself that badly is not worth
    /// debugging when the real thing is one call away.
    ///
    /// FogOfWar.GetPositionVisible(team, position) is the engine's answer, per
    /// team, for the exact position we care about. It costs a point query where
    /// we already have a point — a few hundred per pass, against the tens of
    /// thousands of cells a rebuilt field costs — and it cannot drift from what
    /// the game actually shows a commander, which is the whole property we
    /// wanted and the one a reconstruction can never guarantee.
    ///
    /// FALLBACK IS DELIBERATELY ABSENT. If this throws we log it and report
    /// "not visible" rather than quietly reverting to the layer that produced
    /// 0/380 — a silent fallback to a broken model is how this stayed hidden
    /// for a fortnight.
    /// </summary>
    internal static class GameFow
    {
        static bool _warned;
        static int  _calls, _visible;

        /// <summary>Can this team see this world position right now, according
        /// to the game?</summary>
        internal static bool IsVisible(Team team, Vector3 world)
        {
            if (team == null) return false;
            _calls++;
            try
            {
                bool v = FogOfWar.GetPositionVisible(team, world);
                if (v) _visible++;
                return v;
            }
            catch (System.Exception ex)
            {
                if (!_warned)
                {
                    _warned = true;
                    MelonLogger.Warning(
                        "[FOW] the game's GetPositionVisible threw — perception is blind " +
                        "and will report nothing visible rather than fall back to the " +
                        "reconstruction that measured 0/380: " + ex.Message);
                }
                return false;
            }
        }

        /// <summary>Is the game's fog available at all? Answers the "did we wire
        /// it up" question in the log without needing a second round.</summary>
        internal static bool Available
        {
            get
            {
                try { return FogOfWar.Instance != null; }
                catch { return false; }
            }
        }

        internal static string Report()
        {
            string r = $"game-fow {(Available ? "live" : "MISSING")} " +
                       $"{_visible}/{_calls} visible";
            _calls = _visible = 0;
            return r;
        }

        internal static void ResetForNewRound()
        {
            _warned = false;
            _calls = _visible = 0;
        }
    }
}
