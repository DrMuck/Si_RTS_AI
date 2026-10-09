using MelonLoader;
using Newtonsoft.Json.Linq;
using System;
using System.IO;
using System.Linq;

namespace Si_RTS_AI.Config
{
    /// <summary>
    /// THE ACTIVE CONFIGURATION, AS A KEY/VALUE READER.
    ///
    /// One json file under UserData/RTSAI/configs/ is active at a time (see
    /// ConfigStore). This class reads it and nothing else: every setting in the
    /// mod — switches, harness, economy, military — comes through Int/Float/
    /// Bool/Str here, with the default in the call. There are no
    /// MelonPreferences entries any more: MelonLoader rewrites that file from
    /// memory on shutdown, so an edit made during a session was silently
    /// reverted, which cost whole nights of soak rounds.
    ///
    /// The file is READ, never written. It is re-read at the start of every
    /// round (and when the chat menu selects another config), so editing it
    /// takes effect on the next map load with the server still up. Keys may be
    /// dotted: "military.enabled" reads { "military": { "enabled": true } }.
    /// </summary>
    internal static class RtsaiConfig
    {
        static JObject _root;
        static string _loadedPath = "";
        static DateTime _lastWrite;

        /// <summary>Path of the file the current values came from ("" = none).</summary>
        internal static string LoadedPath => _loadedPath;
        internal static bool HasFile => _root != null;

        /// <summary>How a bridge is judged to be worth building.</summary>
        internal enum BridgeMode
        {
            /// <summary>Value = road removed BETWEEN the two ends, measured over
            /// the built network. Prefers big loops, which is what closes a ring
            /// around the base. Default since v0.26.</summary>
            Loop,
            /// <summary>Value = shortening of the road HOME to the Nest, the
            /// v0.25 rule. Prefers short cuts near the base and will not build a
            /// loop between two equally distant branches.</summary>
            Shortcut,
            /// <summary>Plan them, build none.</summary>
            Off,
        }

        /// <summary>Re-read the active config if its path or timestamp changed.</summary>
        internal static void Reload(bool force = false)
        {
            string path = ConfigStore.ActiveConfigPath;
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                {
                    if (_root != null) MelonLogger.Warning($"[RTSAI/CONFIG] active config '{path}' is gone — defaults apply");
                    _root = null; _loadedPath = "";
                    return;
                }
                var stamp = File.GetLastWriteTimeUtc(path);
                bool samePath = string.Equals(path, _loadedPath, StringComparison.OrdinalIgnoreCase);
                if (!force && _root != null && samePath && stamp == _lastWrite) return;
                _lastWrite = stamp;
                _root = JObject.Parse(File.ReadAllText(path));
                _loadedPath = path;
                MelonLogger.Msg($"[RTSAI/CONFIG] loaded {path}: enabled={Bool("enabled", true)} " +
                                $"factions(alien={Bool("factions.alien", true)} sol={Bool("factions.sol", false)} cent={Bool("factions.centauri", false)}) " +
                                $"testMode={Bool("testMode", false)} military.enabled={Bool("military.enabled", false)} " +
                                $"(keys: {string.Join(",", _root.Properties().Select(p => p.Name).Where(n => !n.StartsWith("_")))})");
            }
            catch (Exception ex)
            {
                // A malformed file must not take the round with it — say so and
                // carry on with whatever was loaded before, or defaults.
                MelonLogger.Warning($"[RTSAI/CONFIG] {path} could not be read ({ex.Message}) — " +
                                    (_root != null ? "previous values stay in effect" : "defaults apply"));
            }
        }

        // ---- A/B rig ownership ---------------------------------------------

        /// <summary>Set by the A/B rig when an arm names a bridge rule. An arm
        /// outranks the file: during a soak the rig is the thing deciding, and a
        /// file edit that silently changed one arm mid-run would poison the
        /// comparison rather than the round. Cleared by any non-bridge arm.</summary>
        internal static BridgeMode? ArmBridgeMode;

        /// <summary>True once the A/B rig has applied an arm this round. The two
        /// experiment knobs the rig owns — worker cap and producer density —
        /// ignore the file while it is set.</summary>
        internal static bool ArmActive;
        internal static int? ArmWorkerCapPerBioCache;
        internal static int? ArmProducerPerSites;

        static bool _warnedArmClash;

        /// <summary>Reads <paramref name="key"/> unless the rig owns it.</summary>
        internal static int IntUnlessArm(string key, int fallback)
        {
            if (!ArmActive) return Int(key, fallback);
            if (Node(key) != null && !_warnedArmClash)
            {
                _warnedArmClash = true;
                MelonLogger.Warning($"[RTSAI/CONFIG] '{key}' in the config is ignored while the " +
                                    "A/B rig is driving arms — the arm sets it. " +
                                    "Set configCycle to \"off\" to hand control back.");
            }
            if (key == "workerCapPerBioCache" && ArmWorkerCapPerBioCache.HasValue) return ArmWorkerCapPerBioCache.Value;
            if (key == "producerPerSites"     && ArmProducerPerSites.HasValue)     return ArmProducerPerSites.Value;
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
                    MelonLogger.Warning($"[RTSAI/CONFIG] bridgeMode='{s}' not recognised " +
                                        "(loop|shortcut|off) — using " + fallback);
                    return fallback;
            }
        }

        // ---- readers ---------------------------------------------------------

        /// <summary>Resolves a key, which may be dotted. Flat keys are unaffected.</summary>
        static JToken Node(string key)
        {
            if (_root == null || string.IsNullOrEmpty(key)) return null;
            if (key.IndexOf('.') < 0) return _root[key];
            JToken t = _root;
            foreach (var part in key.Split('.'))
            {
                if (t == null) return null;
                t = t[part];
            }
            return t;
        }

        internal static bool Has(string key) => Node(key) != null;

        internal static int Int(string key, int fallback)
        {
            var t = Node(key);
            try { return t != null ? t.Value<int>() : fallback; } catch { return fallback; }
        }

        internal static float Float(string key, float fallback)
        {
            var t = Node(key);
            try { return t != null ? t.Value<float>() : fallback; } catch { return fallback; }
        }

        internal static bool Bool(string key, bool fallback)
        {
            var t = Node(key);
            try { return t != null ? t.Value<bool>() : fallback; } catch { return fallback; }
        }

        internal static string Str(string key, string fallback)
        {
            var t = Node(key);
            try { return t != null ? t.Value<string>() : fallback; } catch { return fallback; }
        }
    }
}
