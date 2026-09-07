using MelonLoader;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Si_RTS_AI.Mil
{
    /// <summary>
    /// WHAT 2,619 RECORDED GAMES SAY WE SHOULD BE DOING, AND WHAT WE ARE DOING.
    ///
    /// This module decides nothing and orders nothing. It computes the two
    /// numbers the military v2 spec is built on and logs them beside what the
    /// live layer is actually doing, so the difference can be read off a round
    /// instead of guessed at. Turning it on cannot change a game.
    ///
    /// THE FIRST NUMBER IS EFFECTIVE FORCE, not cash. `mil_doctrine.json`
    /// carries a fitted multiplier per unit — a Behemoth is worth about twice
    /// its price, a Firebug about half — measured with AI on both sides,
    /// because that is the only mode this bot is ever in. Effective force is
    /// SUM(count * cost * w), and a unit the fit never priced counts at 1.0:
    /// unmeasured means unknown, never bad.
    ///
    /// THE SECOND IS WHICH BAND THE FIGHT IS IN. Across 1,043 decisive AI-vs-AI
    /// engagements, P(the stronger side wins) by force ratio runs:
    ///
    ///     parity (under 1.25x)   57%      and only 8% of fights land here
    ///     1.25 - 2x              78%
    ///     2 - 4x                 92%
    ///     over 4x                89%      no better than 2-4x
    ///
    /// Two things follow, and neither needs a model to act on. A fight at
    /// parity is close to a coin flip that NOTHING predicts — not cash, not
    /// composition, not the fitted kernel — so the right move is to decline it.
    /// And past 4x the curve is flat, so the surplus is doing nothing and
    /// belongs on another objective. That is the whole commitment rule, and it
    /// is measured rather than tuned.
    ///
    /// The live layer instead uses `military.pushMargin`, a single 1.5x
    /// constant, which sits inside the band the data says is a coin flip. This
    /// logs both verdicts side by side. If the doctrine is right, rounds will
    /// show pushes committed at ratios that then lost.
    ///
    /// THE TRAJECTORY is the third block: what a top-quartile human commander
    /// had built by each minute, so "we are behind on Greater Cysts at fifteen
    /// minutes" becomes a line in the log rather than something DrMuck has to
    /// notice in a replay. It is a yardstick and not a script — Elo is the
    /// commander's final rating, so it says what good players build and not
    /// what caused them to win.
    ///
    /// ERA MATTERS AND THE FILE SAYS WHICH ONE IT IS. Alien Greater Cysts by
    /// minute ten went from 0.41 in March 2026 to 1.75 in June after the tier
    /// requirement dropped from 2 to 1. A doctrine file with no era stamp is one
    /// nobody can date, and a stale one looks exactly like a fresh one — so the
    /// era is logged every time the file is read.
    ///
    /// Missing file means every part falls back: raw cash for force, no bands,
    /// no trajectory. The mod must never refuse to run because a data file it
    /// only reports against is absent.
    /// </summary>
    internal static class Doctrine
    {
        const string PATH = "UserData/mil_doctrine.json";

        static readonly Dictionary<string, float> _value =
            new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);

        /// <summary>name -> (minute -> mean cumulative a good commander had).</summary>
        static readonly Dictionary<string, SortedList<int, float>> _trajectory =
            new Dictionary<string, SortedList<int, float>>(StringComparer.OrdinalIgnoreCase);

        internal static bool Loaded { get; private set; }
        internal static float RefuseBelow   { get; private set; } = 1.25f;
        internal static float CommitAt      { get; private set; } = 2.0f;
        internal static float WastefulAbove { get; private set; } = 4.0f;
        /// <summary>
        /// THE HQ IS PRICED ON WHAT DEFENDS IT. DrMuck's replays (2026-09-07):
        /// "army clumping from mid game, a huge army that could pressure the
        /// enemy; attrition only makes sense at parity, push once our army
        /// outweighs theirs." The planner priced the HQ at twice the WHOLE enemy
        /// army estimate, so it waited at home for a 2:1 edge over everything Sol
        /// owned. Now the defence is the local strength plus what can reinforce,
        /// floored at a share of the enemy army, and the commit ratio for the win
        /// condition is its own knob.
        /// </summary>
        internal static float HqCommitAt    { get; private set; } = 1.5f;
        internal static float HqArmyShare   { get; private set; } = 0.5f;
        /// <summary>Slope of the win curve, P(A wins) = sigmoid(K * ln(effA/effB)). From the AI-vs-AI fit.</summary>
        internal static float K { get; private set; } = 1.437f;
        /// <summary>Exchange exponent: loss ratio scales as forceRatio^-Beta. 0.60 AI-vs-AI (MIL_V2 s4).</summary>
        internal static float Beta { get; private set; } = 0.60f;
        static string _era = "unknown";
        static DateTime _lastWrite;

        // ---- loading ----------------------------------------------------------

        internal static void Reload()
        {
            try
            {
                if (!File.Exists(PATH))
                {
                    if (Loaded)
                        MelonLogger.Msg("[MIL/DOC] mil_doctrine.json removed — " +
                                        "force reads raw cash, no bands, no trajectory");
                    _value.Clear(); _trajectory.Clear(); Loaded = false;
                    return;
                }
                var stamp = File.GetLastWriteTimeUtc(PATH);
                if (Loaded && stamp == _lastWrite) return;
                _lastWrite = stamp;

                var root = JObject.Parse(File.ReadAllText(PATH));
                _value.Clear(); _trajectory.Clear();

                var uv = root["unitValue"] as JObject;
                if (uv != null)
                    foreach (var p in uv.Properties()) _value[p.Name] = (float)p.Value;

                var cm = root["commit"] as JObject;
                if (cm != null)
                {
                    RefuseBelow   = (float)(cm["refuseBelow"]   ?? RefuseBelow);
                    CommitAt      = (float)(cm["commitAt"]      ?? CommitAt);
                    WastefulAbove = (float)(cm["wastefulAbove"] ?? WastefulAbove);
                    HqCommitAt    = (float)(cm["hqCommitAt"]    ?? HqCommitAt);
                    HqArmyShare   = (float)(cm["hqArmyShare"]   ?? HqArmyShare);
                }

                var tr = root["trajectory"] as JObject;
                if (tr != null)
                    foreach (var p in tr.Properties())
                    {
                        var o = p.Value as JObject;
                        if (o == null) continue;
                        var pts = new SortedList<int, float>();
                        foreach (var m in o.Properties())
                            if (int.TryParse(m.Name, out int min)) pts[min] = (float)m.Value;
                        if (pts.Count > 0) _trajectory[p.Name] = pts;
                    }

                var era = root["era"] as JObject;
                _era = era == null ? "unstamped"
                     : $"trajectory>={era["trajectorySince"]}, values>={era["unitValueSince"]}";
                var src = root["source"] as JObject;
                if (src?["k"] != null) K = (float)src["k"];
                if (src?["beta"] != null) Beta = (float)src["beta"];

                Loaded = _value.Count > 0 || _trajectory.Count > 0;
                MelonLogger.Msg($"[MIL/DOC] loaded {PATH}: {_value.Count} unit values, " +
                                $"{_trajectory.Count} trajectories, era {_era}" +
                                (src?["fights"] != null
                                    ? $" | fitted on {src["fights"]} fights, " +
                                      $"cv {src["cv_accuracy"]} vs cash {src["cv_baseline"]}"
                                    : "") +
                                $" | bands refuse<{RefuseBelow:F2} commit>={CommitAt:F2} " +
                                $"wasteful>{WastefulAbove:F2}");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[MIL/DOC] {PATH} could not be read ({ex.Message}) — " +
                                    "force reads raw cash");
                _value.Clear(); _trajectory.Clear(); Loaded = false;
            }
        }

        // ---- the two numbers --------------------------------------------------

        /// <summary>Multiplier on a unit's cash price. 1.0 when unmeasured,
        /// which is the honest default: the fit not having priced a chassis is
        /// not evidence against it.</summary>
        internal static float ValueOf(string unitName) =>
            _value.TryGetValue(unitName ?? "", out var w) ? w : 1f;

        /// <summary>
        /// Which band a force ratio falls in, and what to do about it.
        /// `ratio` is ours over theirs in EFFECTIVE cash.
        /// </summary>
        internal enum Band { Outnumbered, CoinFlip, Favourable, Commit, Wasteful }

        internal static Band Classify(float ratio)
        {
            if (ratio < 1f / WastefulAbove) return Band.Outnumbered;
            if (ratio < RefuseBelow)        return Band.CoinFlip;
            if (ratio < CommitAt)           return Band.Favourable;
            if (ratio <= WastefulAbove)     return Band.Commit;
            return Band.Wasteful;
        }

        internal static string Advice(Band b)
        {
            switch (b)
            {
                case Band.Outnumbered: return "disengage";
                case Band.CoinFlip:    return "REFUSE — nothing predicts this";
                case Band.Favourable:  return "hold for more";
                case Band.Commit:      return "commit";
                default:               return "surplus — split it off";
            }
        }

        /// <summary>What a top-quartile commander had at this minute, linearly
        /// interpolated between checkpoints. NaN when we have no curve.</summary>
        internal static float TargetAt(string name, float minutes)
        {
            if (!_trajectory.TryGetValue(name ?? "", out var pts) || pts.Count == 0)
                return float.NaN;
            if (minutes <= pts.Keys[0]) return pts.Values[0];
            for (int i = 1; i < pts.Count; i++)
            {
                if (minutes > pts.Keys[i]) continue;
                float t0 = pts.Keys[i - 1], t1 = pts.Keys[i];
                float v0 = pts.Values[i - 1], v1 = pts.Values[i];
                float f = t1 > t0 ? (minutes - t0) / (t1 - t0) : 0f;
                return v0 + f * (v1 - v0);
            }
            return pts.Values[pts.Count - 1];
        }

        internal static IEnumerable<string> TrajectoryNames() => _trajectory.Keys;
    }
}
