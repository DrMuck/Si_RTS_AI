using System;
using System.Collections.Generic;
using HarmonyLib;
using MelonLoader;
using Silica;
using UnityEngine;

namespace Si_RTS_AI.Perception
{
    /// <summary>
    /// WHAT EACH COMMANDER DECIDES, WITH TIME AND IDENTITY. Replays record what
    /// happened; this records what was asked for. Every Construct call on a
    /// structure — a placement, a unit queued, a research tier — is written to
    /// the round log as a [CMD] line with the round time, the team, whether a
    /// player or the AI asked, the player's name and Steam id, what, where, the
    /// yaw, and the game's answer. Unit orders get the same time and identity
    /// stamp (see AttributeSource in Si_RTS_AI.cs). Together with the replay
    /// this is the dataset for learning from high-Elo commanders: the Elo table
    /// is keyed by Steam id, so the two join.
    /// </summary>
    internal static class CommanderLog
    {
        struct Tag { public string Text; public float At; }
        static readonly Dictionary<int, Tag> _tags = new Dictionary<int, Tag>();
        const float TAG_TTL_S = 5f;
        internal static int Lines;

        internal static void ResetForNewRound() { _tags.Clear(); Lines = 0; }

        static float RoundSeconds()
        {
            try { return MapLayers.LayerReplay.CurrentRoundTime; } catch { return -1f; }
        }

        /// <summary>"who=Name steam=7656..." for the player commanding this team, cached 5 s; empty when the AI commands.</summary>
        internal static string CommanderTag(Team team)
        {
            if (team == null) return "";
            int key = team.GetInstanceID();
            float now = Time.time;
            if (_tags.TryGetValue(key, out var t) && now - t.At < TAG_TTL_S) return t.Text;
            string text = "";
            try
            {
                var gm = GameMode.CurrentGameMode as GameModeExt;
                var p = gm?.GetCommanderForTeam(team);
                if (p != null)
                {
                    string steam = "";
                    try { steam = p.PlayerID.ToString(); } catch { }
                    text = $"who={p.PlayerName ?? "?"} steam={steam}";
                }
            }
            catch { }
            _tags[key] = new Tag { Text = text, At = now };
            return text;
        }

        [HarmonyPatch(typeof(Structure), nameof(Structure.Construct),
                      new[] { typeof(ConstructionData), typeof(Vector3), typeof(Quaternion), typeof(bool) })]
        static class Patch_Construct
        {
            static void Postfix(Structure __instance, ConstructionData constructionData, Vector3 worldPosition,
                                Quaternion worldRotation, bool isClientRequest, ProductionActionResult __result)
            {
                try
                {
                    if (__instance == null || constructionData == null) return;
                    var team = __instance.Team;
                    string name = constructionData.ObjectInfo?.DisplayName ?? "?";
                    bool isUnit = false; try { isUnit = constructionData.IsUnit; } catch { }
                    bool isTech = false; try { isTech = constructionData.IsTechTier; } catch { }
                    string kind = isTech ? "tech" : isUnit ? "unit" : "structure";
                    bool playerCmd = isClientRequest;
                    try { if (!playerCmd && team != null) playerCmd = team.GetHasPlayerCommander(); } catch { }
                    string who = playerCmd ? CommanderTag(team) : "";
                    float yaw = 0f; try { yaw = worldRotation.eulerAngles.y; } catch { }
                    string at = kind == "structure" ? $" at=({worldPosition.x:F0},{worldPosition.z:F0}) yaw={yaw:F0}" : "";
                    Lines++;
                    Si_RTS_AI.AppendToRound(
                        $"[CMD] t={RoundSeconds():F0} team={team?.name} by={(playerCmd ? "player" : "ai")}{(who.Length > 0 ? " " + who : "")} " +
                        $"what={name} kind={kind}{at} from={__instance.ObjectInfo?.DisplayName} client={isClientRequest} result={__result}");
                }
                catch (Exception ex)
                {
                    if (Lines++ < 3) MelonLogger.Warning("[CMD] log threw: " + ex.Message);
                }
            }
        }
    }
}
