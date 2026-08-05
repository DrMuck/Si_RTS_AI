using Silica;

namespace Si_RTS_AI.Faction
{
    /// <summary>
    /// Per-faction master switch for Si_RTS_AI. When a faction is disabled,
    /// our mod skips its planners + construction handlers and lets stock
    /// AI (vanilla Silica AIConstructionHandler) run for that team.
    ///
    /// Set from TestHarness via the RTSAI_Alien / RTSAI_Sol / RTSAI_Centauri
    /// preferences. Also mutable at runtime via /rtsai enable &lt;faction&gt; on|off.
    /// </summary>
    internal static class FactionControl
    {
        public static bool AlienEnabled    = true;
        public static bool SolEnabled      = false;
        public static bool CentauriEnabled = false;

        /// <summary>
        /// True if our mod should manage this team. False → stock AI runs
        /// unopposed for that team.
        /// </summary>
        public static bool IsEnabled(Team team)
        {
            if (team == null) return false;
            return IsEnabled(team.name);
        }

        public static bool IsEnabled(string teamName)
        {
            if (string.IsNullOrEmpty(teamName)) return false;
            if (teamName.Contains("Alien"))    return AlienEnabled;
            if (teamName.Contains("Sol"))      return SolEnabled;
            if (teamName.Contains("Cent"))     return CentauriEnabled;
            return false;
        }

        /// <summary>Set the flag by faction key ("alien"/"sol"/"centauri").</summary>
        public static bool TrySetByKey(string factionKey, bool enabled, out string canonicalName)
        {
            canonicalName = "";
            if (string.IsNullOrEmpty(factionKey)) return false;
            factionKey = factionKey.ToLowerInvariant();
            if (factionKey == "alien" || factionKey == "aliens")
            {
                AlienEnabled = enabled;
                canonicalName = "Alien";
                return true;
            }
            if (factionKey == "sol")
            {
                SolEnabled = enabled;
                canonicalName = "Sol";
                return true;
            }
            if (factionKey == "cent" || factionKey == "centauri")
            {
                CentauriEnabled = enabled;
                canonicalName = "Centauri";
                return true;
            }
            return false;
        }
    }
}
