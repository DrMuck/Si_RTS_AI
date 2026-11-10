using MelonLoader;

namespace Si_RTS_AI
{
    /// <summary>
    /// PLANNER DIAGNOSTICS ARE OPT-IN. The console is for what a person watching
    /// the server needs to see; the round log is for reviewing a round that was
    /// set up to be reviewed.
    ///
    /// Every planner wrote its diagnostics through MelonLogger, so the MelonLoader
    /// console scrolled a dozen lines a second — lag spikes, shadow bands, intel
    /// summaries, node manager counts — and the lines that mattered (a round
    /// starting, a config switched, an error) went by unseen. DrMuck, 2026-11-05:
    /// "the rts ai mod is a bit crazy in terms of logging to melonloader console"
    /// and then "can we disable planner logging by default?" A served round wrote
    /// 70,000 such lines (7 MB) nobody would read.
    ///
    /// One door. `log.planner` picks the level: `off` (default) drops every
    /// diagnostic line; `file` writes them to the round log only; `console`
    /// mirrors them to the MelonLoader console as well (the pre-0.94.14
    /// behaviour). Warnings keep going through MelonLogger.Warning unfiltered: a
    /// thrown exception is never chatter. Lifecycle lines — mod loaded, round
    /// started, config switched, chat replies — bypass this class on purpose.
    /// The observation streams of the round log ([CMD] [CASH] [ORDER] [SPAWN_*],
    /// the commanderLog key) and the jsonl datasets are not diagnostics and are
    /// not gated here.
    /// </summary>
    internal static class RtsLog
    {
        internal enum PlannerLevel { Off, File, Console }

        internal static PlannerLevel Level = PlannerLevel.Off;

        internal static bool Enabled => Level != PlannerLevel.Off;
        internal static bool Verbose => Level == PlannerLevel.Console;

        internal static PlannerLevel Parse(string s, PlannerLevel fallback)
        {
            switch ((s ?? "").Trim().ToLowerInvariant())
            {
                case "off": case "false": case "none": return PlannerLevel.Off;
                case "file": case "round": case "quiet": return PlannerLevel.File;
                case "console": case "verbose": case "on": return PlannerLevel.Console;
                default: return fallback;
            }
        }

        internal static string Name => Level == PlannerLevel.Off ? "off" : Level == PlannerLevel.File ? "file" : "console";

        internal static void Msg(string line)
        {
            if (Level == PlannerLevel.Off) return;
            if (Level == PlannerLevel.Console) MelonLogger.Msg(line);
            try { Core.RoundLog.Append(Stamp() + line); } catch { }
        }

        /// <summary>Console only, gated — for callers that write the round log themselves.</summary>
        internal static void Console(string line)
        {
            if (Level == PlannerLevel.Console) MelonLogger.Msg(line);
        }

        static string Stamp()
        {
            float t = 0f;
            try { t = Perception.MapLayers.LayerReplay.CurrentRoundTime; } catch { }
            return $"[t={t:F0}s] ";
        }
    }
}
