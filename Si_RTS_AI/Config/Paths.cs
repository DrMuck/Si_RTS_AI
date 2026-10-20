using System.IO;

namespace Si_RTS_AI.Config
{
    /// <summary>
    /// EVERY FILE THE MOD TOUCHES, NAMED ONCE.
    ///
    /// Configuration and data live in UserData/cfg/RTSAI/ (configs/*.json, state.json,
    /// the unit prior, the doctrine, the composition table). Round logs stay in
    /// UserData/RTSA/ because the analysis tools under tools/ read them there.
    /// Files that used to sit loose in UserData/ are still found as a fallback,
    /// so an old server layout keeps working until it is migrated.
    /// </summary>
    internal static class Paths
    {
        public const string UserData = "UserData";

        /// <summary>UserData/cfg/RTSAI — the mod's own folder inside the server's cfg/
        /// folder. A pre-0.94.2 UserData/RTSAI folder is moved there once.</summary>
        public static readonly string Root      = ResolveRoot();
        /// <summary>UserData/cfg/RTSAI/configs — one json per selectable configuration.</summary>
        public static readonly string ConfigDir = Path.Combine(Root, "configs");
        /// <summary>UserData/cfg/RTSAI/state.json — the only file the mod WRITES:
        /// active config name plus the chat-set overrides (on/off, logging).</summary>
        public static readonly string StateFile = Path.Combine(Root, "state.json");
        /// <summary>UserData/RTSA — round logs, replays, benchmark rows.</summary>
        public static readonly string LogDir    = Path.Combine(UserData, "RTSA");

        /// <summary>The pre-0.94 single config file, read only for migration.</summary>
        public static readonly string LegacyConfig = Path.Combine(UserData, "rtsai.json");

        public static string ConfigFile(string name) => Path.Combine(ConfigDir, name + ".json");

        /// <summary>UserData/cfg/RTSAI/&lt;file&gt; when it exists, else the older
        /// UserData/RTSAI/&lt;file&gt; or UserData/&lt;file&gt; locations, else the new
        /// path (for error messages).</summary>
        public static string Data(string fileName)
        {
            string here = Path.Combine(Root, fileName);
            if (File.Exists(here)) return here;
            string old = Path.Combine(UserData, "RTSAI", fileName);
            if (File.Exists(old)) return old;
            old = Path.Combine(UserData, fileName);
            if (File.Exists(old)) return old;
            return here;
        }

        /// <summary>A file in Si_UnitBalance's config folder: UserData/cfg/UnitBalance/
        /// when it exists, else its pre-1.1 UserData/UnitBalance_cfg/ location.</summary>
        public static string UnitBalanceFile(string fileName)
        {
            string now = Path.Combine(UserData, "cfg", "UnitBalance", fileName);
            string old = Path.Combine(UserData, "UnitBalance_cfg", fileName);
            return File.Exists(now) || !File.Exists(old) ? now : old;
        }

        static string ResolveRoot()
        {
            string dir = Path.Combine(UserData, "cfg", "RTSAI");
            string legacy = Path.Combine(UserData, "RTSAI");
            try
            {
                if (!Directory.Exists(dir) && Directory.Exists(legacy))
                {
                    Directory.CreateDirectory(Path.Combine(UserData, "cfg"));
                    Directory.Move(legacy, dir);
                    MelonLoader.MelonLogger.Msg($"[RTSAI/CONFIG] moved {legacy} -> {dir}");
                }
            }
            catch (System.Exception ex)
            {
                MelonLoader.MelonLogger.Warning($"[RTSAI/CONFIG] could not move {legacy} -> {dir}: {ex.Message}");
            }
            return dir;
        }
    }
}
