using MelonLoader;
using Newtonsoft.Json.Linq;
using System;
using System.IO;
using System.Linq;

namespace Si_RTS_AI.Planning
{
    /// <summary>
    /// UserData/rtsai.json — settings that can be changed WHILE THE SERVER RUNS.
    ///
    /// MelonPreferences rewrites its file from memory on shutdown, so an edit
    /// made during a session is silently reverted; every configuration change so
    /// far has meant stopping the server. That cost this project a whole night
    /// once — an unset A/B cycle that could not be corrected without a restart,
    /// so 25 rounds ran the same configuration.
    ///
    /// This file is READ, never written. It is re-read at the start of every
    /// round, so editing it mid-session takes effect on the next map load
    /// without touching the process. Anything absent falls back to the
    /// MelonPreferences value, so it stays optional: no file, no change.
    ///
    /// Kept deliberately small. It is for settings worth changing between
    /// rounds, not a mirror of every constant in the mod.
    /// </summary>
    internal static class RtsaiConfig
    {
        const string PATH = "UserData/rtsai.json";

        static JObject _root;
        static DateTime _lastWrite;

        /// <summary>How a bridge is judged to be worth building.</summary>
        internal enum BridgeMode
        {
            /// <summary>Value = road removed BETWEEN the two ends, measured over
            /// the built network. Prefers big loops, which is what closes a ring
            /// around the base. Default since v0.26.</summary>
            Loop,
            /// <summary>Value = shortening of the road HOME to the Nest, the
            /// v0.25 rule. Prefers short cuts near the base and will not build a
            /// loop between two equally distant branches — kept because it is a
            /// genuinely different objective, not merely an older attempt: when
            /// the network is one long line, the road home IS the thing worth
            /// shortening.</summary>
            Shortcut,
            /// <summary>Plan them, build none.</summary>
            Off,
        }

        internal static void Reload()
        {
            try
            {
                if (!File.Exists(PATH))
                {
                    if (_root != null) MelonLogger.Msg("[RTSAI/JSON] rtsai.json removed — preferences apply");
                    _root = null;
                    return;
                }
                var stamp = File.GetLastWriteTimeUtc(PATH);
                if (_root != null && stamp == _lastWrite) return;
                _lastWrite = stamp;
                _root = JObject.Parse(File.ReadAllText(PATH));
                MelonLogger.Msg($"[RTSAI/JSON] loaded {PATH}: " +
                                $"bridgeMode={BridgeModeOrDefault(BridgeMode.Loop)} " +
                                $"(keys: {string.Join(",", _root.Properties().Select(p => p.Name))})");
            }
            catch (Exception ex)
            {
                // A malformed file must not take the round with it — say so and
                // carry on with preferences.
                MelonLogger.Warning($"[RTSAI/JSON] {PATH} could not be read ({ex.Message}) — " +
                                    "preferences apply unchanged");
                _root = null;
            }
        }

        /// <summary>Set by the A/B rig when an arm names a bridge rule. An arm
        /// outranks the file: during a soak the rig is the thing deciding, and a
        /// file edit that silently changed one arm mid-run would poison the
        /// comparison rather than the round. Cleared by any non-bridge arm.</summary>
        internal static BridgeMode? ArmBridgeMode;

        /// <summary>True once the A/B rig has applied an arm this round. The two
        /// experiment knobs the rig owns — worker cap and producer density —
        /// ignore the file while it is set, so an edit made to steer a live
        /// server cannot quietly rewrite one arm of a running comparison. The
        /// warning below says which keys were skipped rather than skipping them
        /// in silence.</summary>
        internal static bool ArmActive;

        static bool _warnedArmClash;

        /// <summary>Reads <paramref name="key"/> unless the rig owns it.</summary>
        internal static int IntUnlessArm(string key, int fallback)
        {
            if (!ArmActive) return Int(key, fallback);
            if (_root?[key] != null && !_warnedArmClash)
            {
                _warnedArmClash = true;
                MelonLogger.Warning($"[RTSAI/JSON] '{key}' in rtsai.json is ignored while the " +
                                    "A/B rig is driving arms — the arm sets it. " +
                                    "HeadlessTest_ConfigCycle=off to hand control back.");
            }
            return fallback;
        }

        internal static BridgeMode BridgeModeOrDefault(BridgeMode fallback)
        {
            if (ArmBridgeMode.HasValue) return ArmBridgeMode.Value;
            string s = Str("bridgeMode", null);
            if (string.IsNullOrEmpty(s)) return fallback;
            switch (s.Trim().ToLowerInvariant())
            {
                case "loop":     return BridgeMode.Loop;
                case "shortcut": return BridgeMode.Shortcut;
                case "off":      return BridgeMode.Off;
                default:
                    MelonLogger.Warning($"[RTSAI/JSON] bridgeMode='{s}' not recognised " +
                                        "(loop|shortcut|off) — using " + fallback);
                    return fallback;
            }
        }

        internal static int Int(string key, int fallback)
        {
            var t = _root?[key];
            try { return t != null ? t.Value<int>() : fallback; } catch { return fallback; }
        }

        internal static float Float(string key, float fallback)
        {
            var t = _root?[key];
            try { return t != null ? t.Value<float>() : fallback; } catch { return fallback; }
        }

        internal static bool Bool(string key, bool fallback)
        {
            var t = _root?[key];
            try { return t != null ? t.Value<bool>() : fallback; } catch { return fallback; }
        }

        internal static string Str(string key, string fallback)
        {
            var t = _root?[key];
            try { return t != null ? t.Value<string>() : fallback; } catch { return fallback; }
        }
    }
}
