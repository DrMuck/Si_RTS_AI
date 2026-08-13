using MelonLoader;
using Si_RTS_AI.Planning;
using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace Si_RTS_AI.Mil
{
    /// <summary>
    /// THE MEASURED DOCTRINE, RUNNING BESIDE THE LIVE LAYER AND TOUCHING NOTHING.
    ///
    /// Every previous military change went in as behaviour and was judged from a
    /// replay the next morning. `MILITARY_MODEL` section 11 wrote the rule that
    /// should have prevented that — ship it logging-only, read the log, THEN turn
    /// it on — and it was broken repeatedly the same week. This is that rule
    /// applied first rather than promised.
    ///
    /// So: no orders, no production, no state anything else reads. Three lines a
    /// round-minute, answering three questions we currently cannot answer without
    /// watching a game.
    ///
    ///   [MIL/SHADOW force]  what our army is worth in EFFECTIVE cash rather
    ///                       than sticker price, and how that compares to the
    ///                       enemy estimate the live layer is already using.
    ///
    ///   [MIL/SHADOW band]   which force-ratio band that puts us in, what the
    ///                       measured doctrine says to do, and what
    ///                       `military.pushMargin` says instead. Those two
    ///                       disagree by construction: pushMargin is 1.5, which
    ///                       sits inside the range the archive says is a coin
    ///                       flip, so a round where the bot commits at 1.5x and
    ///                       loses is the doctrine being right.
    ///
    ///   [MIL/SHADOW build]  what a top-quartile commander had built by this
    ///                       minute against what we have. Only structures whose
    ///                       count is a decision we make; the largest shortfall
    ///                       first, because that is the one worth arguing about.
    ///
    /// WHY THE BUILD LINE MATTERS MORE THAN IT LOOKS. The 255-Shockers-to-41-
    /// Behemoths round was diagnosed in `MilitaryProduction` as a producer
    /// ceiling — one Greater Spawning Cyst cannot make more Behemoths than that,
    /// whatever the unit prior prefers. The fix was a throughput heuristic with
    /// no target to check itself against. This is the target: a good commander
    /// has 1.3 Greater Cysts at ten minutes and 4.2 at twenty. If the heuristic
    /// lands somewhere else, the log now says so on the minute rather than in a
    /// post-mortem.
    ///
    /// Cost: one pass over our own units and structures every 30s.
    /// </summary>
    internal static class Shadow
    {
        /// <summary>Master switch, `mil.shadow` in rtsai.json. Default ON —
        /// it is inert by construction, and the reason to have it is that
        /// nobody remembers to turn on a diagnostic before the round they
        /// needed it.</summary>
        internal static bool Enabled { get; private set; } = true;

        const float TICK_S = 30f;
        static float _nextAt;

        /// <summary>Structures worth reporting a shortfall on, in the order a
        /// reader cares about them. Everything else in the trajectory file is
        /// context rather than a decision this layer makes.</summary>
        static readonly string[] Watch =
        {
            "Greater Spawning Cyst", "Lesser Spawning Cyst",
            "Grand Spawning Cyst", "Colossal Spawning Cyst",
            "Quantum Cortex", "Hive Spire", "Thorn Spire",
        };

        internal static void Configure()
        {
            Enabled = RtsaiConfig.Bool("mil.shadow", true);
            Doctrine.Reload();
            MelonLogger.Msg($"[MIL/SHADOW] {(Enabled ? "on" : "off")} — " +
                            "observes only, issues no orders and builds nothing");
        }

        internal static void ResetForNewRound() => _nextAt = 0f;

        internal static void Tick(Team team)
        {
            if (!Enabled || team == null) return;
            float now = Time.time;
            if (now < _nextAt) return;
            _nextAt = now + TICK_S;

            Doctrine.Reload();                       // picks up an edited file live

            float minutes = 0f;
            try { minutes = Perception.MapLayers.LayerReplay.CurrentRoundTime / 60f; }
            catch { }
            if (minutes <= 0.1f) return;             // no clock yet, nothing to say

            ReportForce(team, minutes);
            ReportBuild(team, minutes);
        }

        // ---- force and band ---------------------------------------------------

        static void ReportForce(Team team, float minutes)
        {
            int rawCash = 0;
            float effective = 0f;
            var byUnit = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var units = team.Units;
                if (units != null)
                    for (int i = 0; i < units.Count; i++)
                    {
                        var u = units[i];
                        if (u?.ObjectInfo == null || u.IsDestroyed) continue;
                        string n = u.ObjectInfo.DisplayName ?? "";
                        // Same exclusions MissionPlanner.MeasureArmy uses, so the
                        // two numbers are comparable rather than merely similar.
                        if (n == "Shrimp" || n == "Queen") continue;
                        int cost = Perception.UnitValues.CostOf(n);
                        rawCash   += cost;
                        effective += cost * Doctrine.ValueOf(n);
                        byUnit[n] = byUnit.TryGetValue(n, out int c) ? c + 1 : 1;
                    }
            }
            catch { }

            if (rawCash <= 0) return;

            // READ THREATMAP DIRECTLY, NOT MissionPlanner.EnemyEstimate.
            //
            // They are the same number — MeasureEnemy() assigns
            // ThreatMap.TotalValue verbatim — but they are not equally
            // available. MissionPlanner.Tick returns immediately unless
            // military.enabled is true, so its EnemyEstimate reads 0 on exactly
            // the rounds this module exists to describe: eco soaks with the
            // military layer switched off. The band line would have gone
            // silently missing and looked like "nothing seen" rather than like
            // a wiring fault.
            //
            // ThreatMap.Observe/Tick run before the FactionControl gate and are
            // unconditional, so this works whatever the military layer is doing.
            //
            // THEIRS IS NOT CONVERTED to effective cash. We have no per-unit
            // breakdown of what we can see, so there is nothing to apply a
            // multiplier to. Reporting ours-effective over theirs-raw would
            // flatter us by exactly the amount our own multipliers exceed 1.
            // Both sides are RAW for the band; effective is printed alongside
            // so the gap is visible.
            int theirs = 0;
            try { theirs = Mathf.Max(0, Perception.ThreatMap.TotalValue); } catch { }
            float ratio = theirs > 0 ? (float)rawCash / theirs : float.PositiveInfinity;
            var band = Doctrine.Classify(ratio);

            float lift = effective / Mathf.Max(1, rawCash);
            string ratioTxt = theirs > 0 ? $"{ratio:F2}x" : "n/a (nothing seen)";

            string mix = "";
            try { mix = Perception.ThreatMap.EnemyMixSummary() ?? ""; } catch { }
            MelonLogger.Msg($"[MIL/SHADOW force] t={minutes:F1}m ours={rawCash} cash " +
                            $"(effective {effective:F0}, x{lift:F2}) theirs~{theirs} " +
                            $"| ratio {ratioTxt}" +
                            (mix.Length > 0 ? $" | seen: {mix}" : ""));

            if (theirs <= 0)
            {
                // Not a fault: it means nothing enemy is inside the threat field
                // yet. Said explicitly so a quiet log is not mistaken for a
                // module that failed to start.
                MelonLogger.Msg("[MIL/SHADOW band]  no enemy value in the threat " +
                                "field yet — no ratio to band");
                return;
            }

            // The live rule and the measured one, side by side. PushMargin is a
            // requirement multiplier on their estimate; the doctrine is a band.
            // WASTEFUL IS NOT AGREEMENT, and reading it as such hid the main
            // finding of the first played round. The first version counted
            // Wasteful as "would commit", so 31 of 73 ticks printed "agree"
            // while the doctrine was actually saying something pushMargin has
            // no opinion about: you are past the point where more force buys
            // anything, send the surplus somewhere else. The bot sat at 4x-10x
            // for the last third of that round and never split off, which is
            // the saturation result showing up live — and the log called it
            // agreement.
            float need = MilitaryConfig.PushMargin;
            bool liveWouldCommit = ratio >= need;
            string verdict;
            if (band == Doctrine.Band.Wasteful)
                verdict = liveWouldCommit
                    ? "DIVERGES — live says commit, doctrine says the surplus is idle"
                    : "doctrine says split the surplus, live holds";
            else
            {
                bool docWouldCommit = band == Doctrine.Band.Commit;
                verdict = liveWouldCommit == docWouldCommit
                    ? "agree"
                    : (liveWouldCommit ? "LIVE COMMITS, doctrine would not"
                                       : "doctrine would commit, live holds");
            }

            MelonLogger.Msg($"[MIL/SHADOW band]  {band} -> {Doctrine.Advice(band)} " +
                            $"| pushMargin {need:F2}x says {(liveWouldCommit ? "commit" : "hold")} " +
                            $"| {verdict}");
        }

        // ---- build trajectory -------------------------------------------------

        static void ReportBuild(Team team, float minutes)
        {
            if (!Doctrine.Loaded) return;

            var have = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var st = team.Structures;
                if (st != null)
                    for (int i = 0; i < st.Count; i++)
                    {
                        var s = st[i];
                        if (s?.ObjectInfo == null || s.IsDestroyed) continue;
                        string n = s.ObjectInfo.DisplayName ?? "";
                        have[n] = have.TryGetValue(n, out int c) ? c + 1 : 1;
                    }
            }
            catch { }

            var sb = new StringBuilder();
            string worstName = null;
            float worstGap = 0f;
            for (int i = 0; i < Watch.Length; i++)
            {
                string n = Watch[i];
                float target = Doctrine.TargetAt(n, minutes);
                if (float.IsNaN(target)) continue;
                int mine = have.TryGetValue(n, out int c) ? c : 0;
                float gap = target - mine;
                if (sb.Length > 0) sb.Append("  ");
                sb.Append($"{Short(n)} {mine}/{target:F1}");
                if (gap > worstGap) { worstGap = gap; worstName = n; }
            }
            if (sb.Length == 0) return;

            string tail = worstName != null && worstGap >= 1f
                ? $" | BEHIND on {worstName} by {worstGap:F1}"
                : " | on trajectory";
            MelonLogger.Msg($"[MIL/SHADOW build] t={minutes:F1}m {sb}{tail}");
        }

        /// <summary>Cyst names are long and the line has seven of them.</summary>
        static string Short(string n)
        {
            switch (n)
            {
                case "Lesser Spawning Cyst":    return "Lesser";
                case "Greater Spawning Cyst":   return "Greater";
                case "Grand Spawning Cyst":     return "Grand";
                case "Colossal Spawning Cyst":  return "Colossal";
                case "Quantum Cortex":          return "Cortex";
                case "Hive Spire":              return "Hive";
                case "Thorn Spire":             return "Thorn";
                default:                        return n;
            }
        }
    }
}
