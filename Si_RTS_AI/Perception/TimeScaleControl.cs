using MelonLoader;
using System;
using UnityEngine;

namespace Si_RTS_AI.Perception
{
    /// <summary>
    /// Runs the simulation faster than real time, for soak throughput — and,
    /// more importantly, MEASURES WHETHER IT ACTUALLY DID.
    ///
    /// Asking for 2x does not give 2x. Time.timeScale multiplies the clock the
    /// game advances per frame; if the server cannot compute those frames fast
    /// enough, game time falls behind the multiple and every round after that
    /// is a DIFFERENT GAME rather than a faster one — units step further per
    /// frame, placement searches resolve against staler state, and nothing is
    /// comparable to the 1x baseline. A speed-up that silently degrades is worse
    /// than no speed-up, so this reports the achieved rate and says plainly when
    /// it is short.
    ///
    /// `timeScale` in rtsai.json, default 1. Applied per frame because the game
    /// resets the clock on scene load.
    /// </summary>
    internal static class TimeScaleControl
    {
        static float _fixedDeltaAt1 = -1f;
        static float _applied = 1f;

        // Achieved-rate meter: game seconds against wall seconds.
        static float _lastGameT;
        static DateTime _lastWall = DateTime.MinValue;
        static float _lastReportAt;
        static bool _warned;

        internal static void ResetForNewRound()
        {
            _lastWall = DateTime.MinValue;
            _lastReportAt = 0f;
            _warned = false;
        }

        internal static void Tick()
        {
            float want = Mathf.Clamp(Planning.RtsaiConfig.Float("timeScale", 1f), 0.1f, 8f);

            if (_fixedDeltaAt1 < 0f) _fixedDeltaAt1 = Time.fixedDeltaTime;

            if (!Mathf.Approximately(Time.timeScale, want))
            {
                Time.timeScale = want;
                // Physics keeps the same number of steps per GAME second, so
                // movement and collision behave as they do at 1x. Leaving it
                // alone would halve the steps per game second at 2x, which is a
                // behaviour change dressed up as a speed setting.
                Time.fixedDeltaTime = _fixedDeltaAt1 * want;
                if (!Mathf.Approximately(_applied, want))
                {
                    _applied = want;
                    MelonLogger.Msg($"[RTSA/TIME] timeScale={want:F2} " +
                                    $"(fixedDelta {_fixedDeltaAt1:F4} -> {Time.fixedDeltaTime:F4}). " +
                                    (want > 1f
                                        ? $"A 25 game-minute round should finish in {25f / want:F1} wall-minutes — " +
                                          "watch the achieved rate below before trusting any result."
                                        : "Real time."));
                }
            }

            // ---- achieved rate ----
            float nowGame = Time.time;
            var nowWall = DateTime.UtcNow;
            if (_lastWall == DateTime.MinValue) { _lastGameT = nowGame; _lastWall = nowWall; return; }

            if (nowGame - _lastReportAt < REPORT_EVERY_GAME_S) return;
            _lastReportAt = nowGame;

            double wallS = (nowWall - _lastWall).TotalSeconds;
            float gameS = nowGame - _lastGameT;
            _lastGameT = nowGame; _lastWall = nowWall;
            if (wallS < 0.5) return;

            float achieved = (float)(gameS / wallS);
            bool short_ = want > 1.05f && achieved < want * KEEPING_UP_FRACTION;
            if (short_ || Mathf.Abs(achieved - want) > 0.15f)
                MelonLogger.Msg($"[RTSA/TIME] achieved {achieved:F2}x of {want:F2}x requested" +
                                (short_ ? "  — SERVER IS NOT KEEPING UP; this round is not comparable "
                                        + "to 1x rounds and the timeScale should be lowered"
                                        : ""));
            if (short_ && !_warned)
            {
                _warned = true;
                MelonLogger.Warning($"[RTSA/TIME] simulation is running at {achieved:F2}x against a " +
                                    $"requested {want:F2}x — game time is falling behind, so units " +
                                    "move further per frame than at 1x and results diverge from the " +
                                    "baseline. Lower timeScale in rtsai.json.");
            }
        }

        /// <summary>Achieved rate this far below the request means the box is the
        /// bottleneck rather than the setting.</summary>
        const float KEEPING_UP_FRACTION = 0.9f;

        const float REPORT_EVERY_GAME_S = 60f;
    }
}
