using System;
using System.Collections.Generic;
using MelonLoader;
using Silica;

namespace Si_RTS_AI.Faction
{
    /// <summary>
    /// TECH TIERS FOR A HUMAN TEAM UNDER THE MOD. Every structure of ours that
    /// offers tier upgrades (the Research Facility) queues the lowest tier the
    /// game accepts, one at a time, up to TECH_TARGET_TIER. Tiers are found by
    /// the ConstructionData's IsTechTier flag and ordered by TechnologyTier, so
    /// the display names ("Mark I" in one build, something else in another) do
    /// not matter: the first Sol round of 2026-09-07 sat on 24,500 cash with
    /// no factory because the name list never matched and nothing researched.
    ///
    /// The Ultra Heavy Factory needs tier 7 and a second Headquarters tier 5,
    /// so the target is the whole ladder; the game rejects tiers whose
    /// prerequisites are unmet or that are already done, and the broker's cash
    /// floor keeps research from starving refineries.
    /// </summary>
    internal static class HumanTechResearcher
    {
        const int TECH_TARGET_TIER = 7;
        const float RETRY_S = 15f;

        static readonly Dictionary<Team, List<ConstructionData>> _tiersByTeam = new Dictionary<Team, List<ConstructionData>>();
        static readonly Dictionary<Team, float> _lastTryAt = new Dictionary<Team, float>();
        internal static int Queued;
        internal static int Skipped;
        static bool _logged;

        internal static void Tick(Team team)
        {
            try { Run(team); }
            catch (Exception ex) { MelonLogger.Warning("[RTSA/Human] Tech researcher threw: " + ex.Message); }
        }

        static void Run(Team team)
        {
            if (team == null) return;
            float now = UnityEngine.Time.time;
            if (_lastTryAt.TryGetValue(team, out float last) && now - last < RETRY_S) return;
            _lastTryAt[team] = now;
            EnsureTiers(team);
            if (!_tiersByTeam.TryGetValue(team, out var tiers) || tiers.Count == 0) return;
            var structs = team.Structures;
            if (structs == null) return;
            for (int i = 0; i < structs.Count; i++)
            {
                var s = structs[i];
                if (s == null || s.ObjectInfo == null || s.IsDestroyed) continue;
                bool functional = false; try { functional = s.IsFunctional; } catch { }
                if (!functional) continue;
                var opts = s.ConstructionOptions;
                if (opts == null) continue;
                bool offersAny = false;
                for (int k = 0; k < tiers.Count; k++) if (opts.Contains(tiers[k])) { offersAny = true; break; }
                if (!offersAny) continue;
                int queueDepth = 0;
                try { queueDepth = s.ProductionQueue?.Count ?? 0; } catch { }
                if (queueDepth >= 1) { Skipped++; continue; }
                // ONLY THE NEXT TIER. Trying every tier each pass fired Mark I..VIII
                // every 15 s (about 120 UnmetPrerequisite lines per tier per round,
                // 2026-09-08): tiers at or below the team's tier are done, tiers
                // above the next one are refused by the game.
                int have = 0; try { have = team.TechnologyTier; } catch { }
                for (int k = 0; k < tiers.Count; k++)
                {
                    var cd = tiers[k];
                    if (!opts.Contains(cd)) continue;
                    int tier = -1; try { tier = cd.TechnologyTier; } catch { }
                    if (tier <= have) continue;
                    if (tier > have + 1) break;
                    if (tier > TECH_TARGET_TIER) break;
                    int cost = 0; try { cost = cd.ResourceCost; } catch { }
                    if (cost > 0 && team.TotalResources < cost) break;
                    try
                    {
                        var res = s.Construct(cd);
                        if (res == ProductionActionResult.Success)
                        {
                            Queued++;
                            MelonLogger.Msg($"[RTSA/Human] {team.name} researching {cd.ObjectInfo?.DisplayName} (tier {tier}, cost {cost}) at {s.ObjectInfo.DisplayName}");
                            break;
                        }
                    }
                    catch (Exception ex) { MelonLogger.Warning("[RTSA/Human] Research.Construct threw: " + ex.Message); }
                }
            }
        }

        static void EnsureTiers(Team team)
        {
            if (_tiersByTeam.ContainsKey(team)) return;
            var list = new List<ConstructionData>();
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
                            bool it = false; try { it = opt.IsTechTier; } catch { }
                            if (it && !list.Contains(opt)) list.Add(opt);
                        }
                    }
            }
            catch { }
            if (list.Count == 0) return;
            list.Sort((a, b) => a.TechnologyTier.CompareTo(b.TechnologyTier));
            _tiersByTeam[team] = list;
            if (!_logged)
            {
                _logged = true;
                var sb = new System.Text.StringBuilder("[RTSA/Human] tech ladder:");
                foreach (var cd in list) sb.Append(' ').Append(cd.ObjectInfo?.DisplayName).Append("(T").Append(cd.TechnologyTier).Append(')');
                MelonLogger.Msg(sb.ToString());
            }
        }

        internal static void ResetForNewRound()
        {
            Queued = 0; Skipped = 0; _tiersByTeam.Clear(); _lastTryAt.Clear(); _logged = false;
        }

        internal static string BuildRoundSummaryFragment()
        {
            if (Queued == 0 && Skipped == 0) return "";
            return "--- Human tech research ---\n" +
                   $"  tiers queued: {Queued}, ticks skipped (queue busy): {Skipped}\n";
        }
    }
}
