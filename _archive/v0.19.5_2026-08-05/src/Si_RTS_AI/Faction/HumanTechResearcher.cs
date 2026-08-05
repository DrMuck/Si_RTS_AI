using MelonLoader;
using Silica;
using System;
using System.Collections.Generic;

namespace Si_RTS_AI.Faction
{
    /// <summary>
    /// Queue tech-tier research (Mark I → V) at every owned Research Facility for
    /// managed Human teams. Analogous to AlienShrimpProducer's role for Shrimps.
    ///
    /// Why we stop at Mark V: HQ construction is typically gated at Mark IV/V. Going
    /// beyond spends credits on advanced units/turrets we don't yet build. Bump
    /// TECH_TARGET_MARK if a later slice wants full unlock.
    ///
    /// Strategy: sequential try. Each tick, per Research Facility, walk MARK_ORDER
    /// and call Construct() for the first mark it accepts (Success). The game itself
    /// rejects marks whose prereqs aren't met and marks already done — we don't need
    /// to track tier state.
    /// </summary>
    internal static class HumanTechResearcher
    {
        const int TECH_TARGET_MARK = 5;   // stop after Mark V

        // Tier names in order. Silica's Research Facility exposes these as
        // ConstructionOptions with DisplayName exactly "Mark I", "Mark II", ...
        static readonly string[] MARK_ORDER = { "Mark I", "Mark II", "Mark III", "Mark IV", "Mark V" };

        // Per-team lookup: mark name → ConstructionData. Cached lazily from any
        // Research Facility's ConstructionOptions the first time we see it.
        static readonly Dictionary<Team, Dictionary<string, ConstructionData>> _marksByTeam
            = new Dictionary<Team, Dictionary<string, ConstructionData>>();

        internal static int Queued;
        internal static int Skipped;

        internal static void Tick(Team team)
        {
            try { Run(team); }
            catch (Exception ex) { MelonLogger.Warning("[RTSA/Human] Tech researcher threw: " + ex.Message); }
        }

        static void Run(Team team)
        {
            EnsureMarkCds(team);
            if (!_marksByTeam.TryGetValue(team, out var marksMap) || marksMap.Count == 0) return;

            var structs = team.Structures;
            if (structs == null) return;

            for (int i = 0; i < structs.Count; i++)
            {
                var s = structs[i];
                if (s == null || s.ObjectInfo == null || s.IsDestroyed) continue;
                if (!string.Equals(s.ObjectInfo.DisplayName, "Research Facility", StringComparison.OrdinalIgnoreCase)) continue;

                // Only stack one research at a time — same money-management reasoning
                // as the Alien Cyst cap.
                int queueDepth = 0;
                try { queueDepth = s.ProductionQueue?.Count ?? 0; } catch { }
                if (queueDepth >= 1) { Skipped++; continue; }

                // Try each mark in order until one is accepted. Game rejects
                // already-researched (or prereq-not-met) with non-Success.
                for (int mi = 0; mi < MARK_ORDER.Length && mi < TECH_TARGET_MARK; mi++)
                {
                    if (!marksMap.TryGetValue(MARK_ORDER[mi], out var cd) || cd == null) continue;
                    try
                    {
                        var res = s.Construct(cd);
                        if (res == ProductionActionResult.Success)
                        {
                            Queued++;
                            break;
                        }
                    }
                    catch (Exception ex) { MelonLogger.Warning("[RTSA/Human] Research.Construct threw: " + ex.Message); }
                }
            }
        }

        static void EnsureMarkCds(Team team)
        {
            if (_marksByTeam.ContainsKey(team)) return;

            var map = new Dictionary<string, ConstructionData>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var structs = team.Structures;
                if (structs != null)
                    for (int i = 0; i < structs.Count; i++)
                    {
                        var s = structs[i];
                        if (s == null || s.ConstructionOptions == null) continue;
                        foreach (var opt in s.ConstructionOptions)
                        {
                            if (opt?.ObjectInfo == null) continue;
                            string n = opt.ObjectInfo.DisplayName ?? "";
                            for (int mi = 0; mi < MARK_ORDER.Length; mi++)
                                if (string.Equals(n, MARK_ORDER[mi], StringComparison.OrdinalIgnoreCase))
                                    map[MARK_ORDER[mi]] = opt;
                        }
                    }
            }
            catch { }

            // Only stash once we've found at least one mark — otherwise re-try next
            // tick after Research Facility gets built.
            if (map.Count > 0) _marksByTeam[team] = map;
        }

        internal static void ResetForNewRound()
        {
            Queued = 0;
            Skipped = 0;
            _marksByTeam.Clear();
        }

        internal static string BuildRoundSummaryFragment()
        {
            if (Queued == 0 && Skipped == 0) return "";
            return "--- Human tech research ---\n" +
                   $"  Marks queued this round:      {Queued}\n" +
                   $"  Skipped (queue full / fail):  {Skipped}\n";
        }
    }
}
