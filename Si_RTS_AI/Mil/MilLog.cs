using MelonLoader;
using System.Collections.Generic;
using UnityEngine;

namespace Si_RTS_AI.Mil
{
    /// <summary>
    /// EVERY MILITARY LINE GOES TO THE ROUND LOG AS WELL AS THE CONSOLE.
    ///
    /// The two military rounds of 2026-08-13 cannot be reviewed: [MISSION],
    /// [BATTALION], [MIL/PROD] and [DEFENCE] all logged through MelonLogger
    /// only, which lands in MelonLoader/Latest.log, and the next server start
    /// overwrote it. The round log survived and had none of them. So this is
    /// the one door for the layer, and it writes to both.
    ///
    /// Throttled variants exist because a planner that says the same thing every
    /// tick buries the line that changed.
    /// </summary>
    internal static class MilLog
    {
        static readonly Dictionary<string, float> _lastAt = new Dictionary<string, float>();
        static readonly HashSet<string> _once = new HashSet<string>();

        internal static void ResetForNewRound()
        {
            _lastAt.Clear();
            _once.Clear();
        }

        internal static void Msg(string line)
        {
            MelonLogger.Msg(line);
            try { Si_RTS_AI.AppendToRound(Stamp() + line); } catch { }
        }

        internal static void Warn(string line)
        {
            MelonLogger.Warning(line);
            try { Si_RTS_AI.AppendToRound(Stamp() + "WARN " + line); } catch { }
        }

        /// <summary>Round-log only — for lines a human reads later, not now.</summary>
        internal static void Quiet(string line)
        {
            try { Si_RTS_AI.AppendToRound(Stamp() + line); } catch { }
        }

        /// <summary>At most once per <paramref name="everyS"/> per key.</summary>
        internal static void Every(string key, float everyS, string line)
        {
            float now = Time.time;
            if (_lastAt.TryGetValue(key, out float at) && now - at < everyS) return;
            _lastAt[key] = now;
            Msg(line);
        }

        internal static void Once(string key, string line)
        {
            if (!_once.Add(key)) return;
            Msg(line);
        }

        static string Stamp()
        {
            float t = 0f;
            try { t = Perception.MapLayers.LayerReplay.CurrentRoundTime; } catch { }
            return $"[t={t:F0}s] ";
        }
    }
}
