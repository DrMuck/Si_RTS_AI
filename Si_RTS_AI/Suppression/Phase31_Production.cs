using HarmonyLib;
using MelonLoader;
using Silica.AI;
using Si_RTS_AI.Perception;
using Si_RTS_AI.Strategic;
using System;
using System.Collections.Generic;

namespace Si_RTS_AI.Suppression
{
    /// <summary>
    /// Phase 3.1 — soft take-over of the AI's unit-production decision.
    ///
    /// We POSTFIX `AIUnitHandler.GetBestUnitToBuildForPreset(...)` — the internal method
    /// stock AI calls each Think() tick to pick which unit to queue next in the ONE
    /// factory the preset is targeting. We inspect stock's choice and compare with what
    /// our CompositionPlanner would have picked.
    ///
    /// Two modes:
    ///   1. Observation only (default) — log both choices to the round file, don't
    ///      change stock behavior. Lets us verify our planner is picking reasonable
    ///      units before we cede control.
    ///   2. Override on (Cfg.OverrideProduction = true, per team) — replace stock's
    ///      __result with our pick IF ours is present in the buildOptions list AND
    ///      the composition target says we still need more of it.
    ///
    /// Chat command: /rtsai override <teamName> on|off
    /// </summary>
    internal static class Phase31_Production
    {
        // Per-team override flag. Not in a persistent config yet — MVP.
        internal static readonly Dictionary<Team, bool> OverrideByTeam = new Dictionary<Team, bool>();
        /// <summary>Write one [P31] line per stock production decision. Off by default: the tally in the round summary is the useful output, the per-sample lines were 44% of a 29 MB round log.</summary>
        internal static bool SampleLog = false;

        // Per-team counters so we can summarize agreements/differences.
        internal class DecisionTally
        {
            public int SamplesObserved;
            public int Agreements;             // stock == ours
            public int Differed;               // stock != ours (and ours was buildable)
            public int OursNotBuildable;       // our pick wasn't in stock's buildOptions
            public int OverridesApplied;       // times we replaced __result
        }
        internal static readonly Dictionary<Team, DecisionTally> Tallies = new Dictionary<Team, DecisionTally>();

        internal static void ResetForNewRound()
        {
            Tallies.Clear();
            // Preserve OverrideByTeam — that's an admin config, not per-round.
        }

        [HarmonyPatch(typeof(AIUnitHandler), nameof(AIUnitHandler.GetBestUnitToBuildForPreset))]
        static class Patch_GetBestUnitToBuildForPreset
        {
            static void Postfix(
                AIUnitHandler __instance,
                AIGroupPreset preset,
                List<ConstructionData> buildOptions,
                ref ConstructionData __result)
            { if (!Config.ModSwitches.Enabled) return;
                try
                {
                    var team = __instance?.Commander?.Team;
                    if (team == null) return;

                    // Phase 3.1 scope now covers Sol AND Alien (Centauri stays stock).
                    // Sol: composition targets human unit types (Rifleman, Heavy, Sniper, tanks, air).
                    // Alien: composition targets shrimps (harvest floor scaled ~10-15 per biotics).
                    string teamName = team.name ?? "";
                    if (!teamName.Contains("Sol") && !teamName.Contains("Alien")) return;

                    // OBSERVE ONLY WHEN SOMEONE IS LOOKING. This postfix ran on every
                    // stock production decision - 143,643 times in one public
                    // NarakaCity round (2026-09-13 14:30) - rebuilding a team state and
                    // running the composition planner each time, then writing a line,
                    // for an override that was off for every team all round. With
                    // the override off and the sample log off there is nothing to do
                    // but count, and the tally already lives in the round summary.
                    bool overrideOn = OverrideByTeam.TryGetValue(team, out bool ov) && ov;
                    if (!overrideOn && !SampleLog)
                    {
                        GetTally(team).SamplesObserved++;
                        return;
                    }

                    var stockPickName = SafeDisplayName(__result);

                    // Build a lightweight TeamState from what we know so far.
                    var state = BuildState(team);
                    state.Phase = CompositionPlanner.EstimatePhase(state);
                    var target = CompositionPlanner.For(state);

                    // Pick our preferred: first entry from Priority whose current UnitCount < Desired
                    // AND which is present in stock's buildOptions (i.e. we have the tech + factory).
                    string? ourPickName = null;
                    string? ourRationale = null;
                    foreach (var entry in target.Priority)
                    {
                        state.UnitCount.TryGetValue(entry.UnitDisplayName, out int have);
                        if (have >= entry.Desired) continue;
                        if (!IsInBuildOptions(entry.UnitDisplayName, buildOptions)) continue;
                        ourPickName = entry.UnitDisplayName;
                        ourRationale = entry.Rationale + $" (have {have}/{entry.Desired})";
                        break;
                    }

                    var tally = GetTally(team);
                    tally.SamplesObserved++;
                    bool oursIsBuildable = ourPickName != null;
                    bool agree = ourPickName != null && ourPickName == stockPickName;
                    if (agree) tally.Agreements++;
                    else if (!oursIsBuildable) tally.OursNotBuildable++;
                    else tally.Differed++;

                    // Observation log (goes to round file via main mod's AppendToRound).
                    if (SampleLog) Si_RTS_AI.AppendToRound(
                        $"[P31] team={teamName} preset={SafePresetName(preset)} " +
                        $"stock={stockPickName ?? "-"} ours={(ourPickName ?? "-")} " +
                        $"agree={agree} rationale={(ourRationale ?? "-")}");

                    // Override, if enabled for this team and our pick is buildable.
                    if (oursIsBuildable && !agree && overrideOn)
                    {
                        var replacement = FindBuildOptionByName(ourPickName!, buildOptions);
                        if (replacement != null)
                        {
                            __result = replacement;
                            tally.OverridesApplied++;
                        }
                    }
                }
                catch (Exception ex)
                {
                    MelonLogger.Warning("[RTSA/P31] Postfix threw: " + ex.Message);
                }
            }
        }

        // ---- Helpers ----

        static TeamState BuildState(Team team)
        {
            var p2 = Phase2.Get(team);
            var s = new TeamState
            {
                Team = team,
                TeamName = team.name ?? "?",
                Faction = FactionFromTeamName(team.name ?? ""),
                StructuresOwned = System.Math.Max(0, p2.StructuresSpawned - p2.StructuresLost),
                UnitsOwned      = System.Math.Max(0, p2.UnitsSpawned - p2.UnitsLost),
                UnitCount = new Dictionary<string, int>(),
                StructureCount = new Dictionary<string, int>(),
                LastMissingResources = 0
            };
            // Copy built-but-not-lost counts as a rough owned-count. For MVP we use
            // (built - lost by name) as the approximation.
            foreach (var kv in p2.UnitBuiltByName)
            {
                p2.UnitLostByName.TryGetValue(kv.Key, out int lost);
                s.UnitCount[kv.Key] = System.Math.Max(0, kv.Value - lost);
            }
            foreach (var kv in p2.StructureBuiltByName)
            {
                p2.StructureLostByName.TryGetValue(kv.Key, out int lost);
                s.StructureCount[kv.Key] = System.Math.Max(0, kv.Value - lost);
            }
            return s;
        }

        static string FactionFromTeamName(string teamName)
        {
            if (teamName.Contains("Sol"))      return "Sol";
            if (teamName.Contains("Centauri")) return "Centauri";
            if (teamName.Contains("Alien"))    return "Alien";
            return "?";
        }

        static string SafeDisplayName(ConstructionData? cd)
        {
            try { return cd?.ObjectInfo?.DisplayName ?? "-"; } catch { return "?"; }
        }

        static string SafePresetName(AIGroupPreset? p)
        {
            try { return p != null ? p.Type.ToString() : "-"; } catch { return "?"; }
        }

        static bool IsInBuildOptions(string displayName, List<ConstructionData> options)
        {
            if (options == null) return false;
            for (int i = 0; i < options.Count; i++)
            {
                var o = options[i];
                if (o?.ObjectInfo != null && string.Equals(o.ObjectInfo.DisplayName, displayName, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        static ConstructionData? FindBuildOptionByName(string displayName, List<ConstructionData> options)
        {
            for (int i = 0; i < options.Count; i++)
            {
                var o = options[i];
                if (o?.ObjectInfo != null && string.Equals(o.ObjectInfo.DisplayName, displayName, StringComparison.OrdinalIgnoreCase))
                    return o;
            }
            return null;
        }

        static DecisionTally GetTally(Team team)
        {
            if (!Tallies.TryGetValue(team, out var t))
            {
                t = new DecisionTally();
                Tallies[team] = t;
            }
            return t;
        }

        // Called from Phase2.BuildRoundSummary to append our own bit to the round summary.
        internal static string BuildRoundSummaryFragment()
        {
            if (Tallies.Count == 0) return "";
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("--- Phase 3.1 (unit-production decision agreement) ---");
            foreach (var kv in Tallies)
            {
                var t = kv.Value;
                bool ov = OverrideByTeam.TryGetValue(kv.Key, out bool o) && o;
                sb.AppendLine($"  {kv.Key?.name ?? "?"}: samples={t.SamplesObserved} agree={t.Agreements} differ={t.Differed} " +
                              $"oursNotBuildable={t.OursNotBuildable} overridesApplied={t.OverridesApplied} overrideEnabled={ov}");
            }
            return sb.ToString();
        }
    }
}
