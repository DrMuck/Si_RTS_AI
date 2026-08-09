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

        // ---- THE FIELDS, NOT JUST THE POINTS --------------------------------
        //
        // DrMuck: "did we replace all relevant layers? also the one we used for
        // eco?" No, and eco was the one that mattered most. EcoPlanner gates
        // buildability on the explored layer — unexplored ground is unbuildable
        // ground — so a reconstruction that is too GENEROUS marks ground
        // explored that nobody ever looked at, and the planner commits to
        // building somewhere it has never seen. FoWLayers' own header records
        // that failure being observed on MonumentValley on 2026-08-01 and calls
        // it "the damaging direction"; the cause was the same guessed disks.
        //
        // The game keeps the real thing per team as a byte per fog pixel, so
        // there is nothing to approximate. Our grid is coarser than the fog
        // texture, so a cell takes the reading at its centre.
        //
        // Cached per tick: several planners ask for this each pass and the
        // answer cannot change between them within one frame.

        /// <summary>How stale a fog field may be. Explored only ever grows and
        /// active moves at walking pace, so a second is finer than any decision
        /// that reads it, and it keeps the per-cell sweep off the hot path.</summary>
        const float REFRESH_S = 2f;

        static byte[] _exBuf, _acBuf;

        static byte[] Rent(ref byte[] buf, int need)
        {
            if (buf == null || buf.Length < need) buf = new byte[need];
            return buf;
        }

        static readonly System.Collections.Generic.Dictionary<Team, (float at, LayerB ex, LayerB ac)>
            _cache = new System.Collections.Generic.Dictionary<Team, (float, LayerB, LayerB)>();

        /// <summary>
        /// THE ACTIVE FOG AS A MANAGED ARRAY, refreshed on the cache interval.
        ///
        /// Hot paths must read THIS, never IsVisible. The point query is honest
        /// and correct and costs an interop call, which is fine for a handful of
        /// positions a second and catastrophic for a few thousand — see the FPS
        /// numbers above. Stamp walks every enemy unit and structure every tick,
        /// so it reads the layer.
        /// </summary>
        internal static LayerB ActiveLayer(Team team)
        {
            return TryFill(team, out _, out var ac) ? ac : null;
        }

        internal static bool TryFill(Team team, out LayerB explored, out LayerB active)
        {
            explored = active = null;
            if (team == null) return false;
            if (_cache.TryGetValue(team, out var c) && Time.time - c.at < REFRESH_S)
            { explored = c.ex; active = c.ac; return true; }

            try
            {
                var fow = FogOfWar.Instance;
                if (fow == null) return false;
                var data = fow.GetTeamData(team);
                if (data == null) return false;

                // ONE INTEROP CALL, NOT TWENTY THOUSAND.
                //
                // Indexing a NativeArray from managed code crosses the Il2Cpp
                // boundary EVERY TIME, and this loop touched two of them per
                // cell over a 150x150 grid, per team, every second. Measured
                // cost of getting that wrong: mean server FPS 142 -> 50, worst
                // frames 4 fps, 1353 frames over 100ms against 32 before, and
                // 56 slow ticks from this mod against 2.
                //
                // CopyTo moves the whole buffer in one call and the sweep then
                // runs entirely on managed arrays.
                var exPix = Rent(ref _exBuf, data.ExploredPixels.Length);
                var acPix = Rent(ref _acBuf, data.ActivePixels.Length);
                data.ExploredPixels.CopyTo(exPix);
                data.ActivePixels.CopyTo(acPix);
                int pw = fow.PixelWidth, ph = fow.PixelHeight;
                if (pw <= 0 || ph <= 0) return false;

                var ex = c.ex ?? new LayerB();
                var ac = c.ac ?? new LayerB();
                ex.Clear(); ac.Clear();

                // World-to-pixel is an affine transform, so derive it ONCE
                // rather than paying an interop call per cell — the grid is
                // tens of thousands of cells and this runs several times a
                // second. Calibrated against the game's own conversion at two
                // points and abandoned entirely if it disagrees, because a
                // silently wrong mapping here would read the fog of the wrong
                // ground, which is a worse failure than being slow.
                Vector3 p0 = GridWorld.CellCenter(0, 0);
                Vector3 p1 = GridWorld.CellCenter(GridWorld.Width - 1, GridWorld.Height - 1);
                FogOfWar.GetRegionAtPosition(p0, out int x0, out int y0);
                FogOfWar.GetRegionAtPosition(p1, out int x1, out int y1);

                float sx = (p1.x - p0.x) == 0f ? 0f : (x1 - x0) / (p1.x - p0.x);
                float sz = (p1.z - p0.z) == 0f ? 0f : (y1 - y0) / (p1.z - p0.z);

                Vector3 pm = GridWorld.CellCenter(GridWorld.Width / 2, GridWorld.Height / 2);
                FogOfWar.GetRegionAtPosition(pm, out int xm, out int ym);
                int predX = Mathf.RoundToInt(x0 + (pm.x - p0.x) * sx);
                int predZ = Mathf.RoundToInt(y0 + (pm.z - p0.z) * sz);
                if (Mathf.Abs(predX - xm) > 1 || Mathf.Abs(predZ - ym) > 1)
                {
                    if (!_warnedFields)
                    {
                        _warnedFields = true;
                        MelonLogger.Warning(
                            "[FOW] world-to-pixel is not affine as assumed " +
                            $"(mid cell predicted ({predX},{predZ}), game says ({xm},{ym})) — " +
                            "not reading the fog texture rather than reading the wrong ground");
                    }
                    return false;
                }

                for (int cz = 0; cz < GridWorld.Height; cz++)
                {
                    float wz = GridWorld.CellCenter(0, cz).z;
                    int py = Mathf.RoundToInt(y0 + (wz - p0.z) * sz);
                    if (py < 0 || py >= ph) continue;
                    int row = py * pw;
                    for (int cx = 0; cx < GridWorld.Width; cx++)
                    {
                        float wx = GridWorld.CellCenter(cx, 0).x;
                        int px = Mathf.RoundToInt(x0 + (wx - p0.x) * sx);
                        if (px < 0 || px >= pw) continue;
                        int idx = row + px;
                        if (idx < exPix.Length && exPix[idx] != 0) ex.SetOne(cx, cz);
                        if (idx < acPix.Length && acPix[idx] != 0) ac.SetOne(cx, cz);
                    }
                }

                _cache[team] = (Time.time, ex, ac);
                explored = ex; active = ac;
                return true;
            }
            catch (System.Exception ex2)
            {
                if (!_warnedFields)
                {
                    _warnedFields = true;
                    MelonLogger.Warning(
                        "[FOW] could not read the game's fog pixels, falling back to the " +
                        "reconstruction for FIELD queries (point queries are unaffected): "
                        + ex2.Message);
                }
                return false;
            }
        }

        static bool _warnedFields;

        internal static bool FieldsAvailable => !_warnedFields;

        /// <summary>
        /// IS THE SERVER EVEN RUNNING FOG?
        ///
        /// A dedicated server has nothing to render, so fog can be switched off
        /// entirely — and then GetPositionVisible answers true everywhere and we
        /// quietly become omniscient. That is the opposite of the bug we just
        /// fixed and considerably worse: the bot would play well for reasons it
        /// could never justify from what a commander can see, and every
        /// conclusion drawn from the round would be worthless.
        ///
        /// So it is checked and stated rather than assumed. If this ever reads
        /// DISABLED, nothing measured that round means anything.
        /// </summary>
        internal static bool FogDisabled
        {
            get
            {
                try { return FogOfWar.FogOfWarDisabled; }
                catch { return false; }
            }
        }

        internal static string Report()
        {
            string r = $"game-fow {(Available ? "live" : "MISSING")} " +
                       $"{_visible}/{_calls} visible" +
                       (_warnedFields ? ", FIELDS FELL BACK to the reconstruction" : ", fields live") +
                       (FogDisabled ? "  ** FOG IS DISABLED SERVER-SIDE — WE ARE OMNISCIENT **" : "");
            _calls = _visible = 0;
            return r;
        }

        internal static void ResetForNewRound()
        {
            _warned = false;
            _warnedFields = false;
            _calls = _visible = 0;
            _cache.Clear();
        }
    }
}
