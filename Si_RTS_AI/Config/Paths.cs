using System.IO;

namespace Si_RTS_AI.Config
{
    /// <summary>
    /// EVERY FILE THE MOD TOUCHES, NAMED ONCE.
    ///
    /// Configuration and data live in UserData/RTSAI/ (configs/*.json, state.json,
    /// the unit prior, the doctrine, the composition table). Round logs stay in
    /// UserData/RTSA/ because the analysis tools under tools/ read them there.
    /// Files that used to sit loose in UserData/ are still found as a fallback,
    /// so an old server layout keeps working until it is migrated.
    /// </summary>
    internal static class Paths
    {
        public const string UserData = "UserData";

        /// <summary>UserData/RTSAI — the mod's own folder.</summary>
        public static readonly string Root      = Path.Combine(UserData, "RTSAI");
        /// <summary>UserData/RTSAI/configs — one json per selectable configuration.</summary>
        public static readonly string ConfigDir = Path.Combine(Root, "configs");
        /// <summary>UserData/RTSAI/state.json — the only file the mod WRITES:
        /// active config name plus the chat-set overrides (on/off, logging).</summary>
        public static readonly string StateFile = Path.Combine(Root, "state.json");
        /// <summary>UserData/RTSA — round logs, replays, benchmark rows.</summary>
        public static readonly string LogDir    = Path.Combine(UserData, "RTSA");

        /// <summary>The pre-0.94 single config file, read only for migration.</summary>
        public static readonly string LegacyConfig = Path.Combine(UserData, "rtsai.json");

        public static string ConfigFile(string name) => Path.Combine(ConfigDir, name + ".json");

        /// <summary>UserData/RTSAI/&lt;file&gt; when it exists, else the old
        /// UserData/&lt;file&gt; location, else the new path (for error messages).</summary>
        public static string Data(string fileName)
        {
            string here = Path.Combine(Root, fileName);
            if (File.Exists(here)) return here;
            string old = Path.Combine(UserData, fileName);
            if (File.Exists(old)) return old;
            return here;
        }
    }
}
