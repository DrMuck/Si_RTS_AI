using System;
using System.Collections.Generic;
using MelonLoader;
using Silica;
using UnityEngine;

namespace Si_RTS_AI.Faction
{
    /// <summary>
    /// HUMAN PLACEMENT EXECUTOR (Sol / Centauri). Given a structure and a point,
    /// find the structure of ours that may build it, check the prerequisites the
    /// game will check, run the game's own placement search around the point,
    /// and construct with the rotation asked for (refineries face their patch)
    /// or the one the search returns. No cash tricks: the cost is paid.
    ///
    /// Requests are de-duplicated per structure name within DEDUPE_M for
    /// DEDUPE_S, so a planner asking every tick does not stack sites.
    /// </summary>
    internal static class HumanBuild
    {
        const float SEARCH_RANGE_M = 120f;
        const float SEARCH_MAX_S   = 8f;
        const int   SEARCH_STEPS   = 300;
        const float DEDUPE_M       = 60f;
        const float DEDUPE_S       = 45f;

        struct Pending { public string Name; public Vector3 At; public float When; }
        static readonly List<Pending> _pending = new List<Pending>();
        static readonly Dictionary<string, int> _refusals = new Dictionary<string, int>();
        internal static int Fired, Landed, Failed;

        internal static void ResetForNewRound()
        {
            _pending.Clear(); _refusals.Clear(); Fired = Landed = Failed = 0;
        }

        static bool PendingNear(string name, Vector3 at)
        {
            float now = Time.time;
            for (int i = _pending.Count - 1; i >= 0; i--)
            {
                if (now - _pending[i].When > DEDUPE_S) { _pending.RemoveAt(i); continue; }
                if (_pending[i].Name != name) continue;
                float dx = _pending[i].At.x - at.x, dz = _pending[i].At.z - at.z;
                if (dx * dx + dz * dz <= DEDUPE_M * DEDUPE_M) return true;
            }
            return false;
        }

        static void Refuse(string name, string why)
        {
            string key = name + ":" + why;
            _refusals.TryGetValue(key, out int n); _refusals[key] = n + 1;
            if (n < 5 || n % 50 == 0) MelonLogger.Msg($"[HUMAN/BUILD] {name} refused: {why} (#{n + 1})");
        }

        internal static Structure FindAnchor(Team team, ConstructionData cd, Vector3 near)
        {
            Structure best = null; float bestSq = float.MaxValue;
            try
            {
                var structs = team.Structures;
                if (structs == null) return null;
                for (int i = 0; i < structs.Count; i++)
                {
                    var s = structs[i];
                    if (s == null || s.IsDestroyed) continue;
                    bool functional = false; try { functional = s.IsFunctional; } catch { }
                    if (!functional) continue;
                    var opts = s.ConstructionOptions;
                    if (opts == null || !opts.Contains(cd)) continue;
                    float dx = s.transform.position.x - near.x, dz = s.transform.position.z - near.z;
                    float d = dx * dx + dz * dz;
                    if (d < bestSq) { bestSq = d; best = s; }
                }
            }
            catch { }
            return best;
        }

        /// <summary>Ask the game to place <paramref name="cd"/> near <paramref name="pos"/>; true when a search was queued.</summary>
        internal static bool TryBuild(Team team, ConstructionData cd, Vector3 pos, Quaternion? rot)
        {
            if (team == null || cd == null) return false;
            string name = cd.ObjectInfo?.DisplayName ?? "?";
            if (PendingNear(name, pos)) { Refuse(name, "pending nearby"); return false; }
            var anchor = FindAnchor(team, cd, pos);
            if (anchor == null) { Refuse(name, "no structure can build it"); return false; }
            bool prereq = true;
            try { prereq = anchor.HasPrerequisitesForConstruction(cd); } catch { }
            if (!prereq) { Refuse(name, "prerequisite"); return false; }
            int cost = 0; try { cost = cd.ResourceCost; } catch { }
            if (cost > 0 && team.TotalResources < cost) { Refuse(name, $"cash {team.TotalResources} < {cost}"); return false; }
            var preview = cd.ObjectPreviewSetup;
            if (preview == null) { Refuse(name, "no preview"); return false; }
            _pending.Add(new Pending { Name = name, At = pos, When = Time.time });
            Fired++;
            Vector3 asked = pos;
            try
            {
                ConstructionPlacement.QueueFirstValidPlacementAroundPoint(
                    preview, team, anchor, pos, cd.GridSnapXZ, cd.GridSnapY,
                    SEARCH_RANGE_M, SEARCH_MAX_S, SEARCH_STEPS,
                    (cData, cbTeam, cbStruct, gotPos, gotRot) =>
                    {
                        string res = "void";
                        try
                        {
                            if (cbStruct != null && !cbStruct.IsDestroyed)
                                res = cbStruct.Construct(cData, gotPos, rot ?? gotRot).ToString();
                        }
                        catch (Exception ex) { res = "threw " + ex.Message; }
                        if (res == "Success") Landed++; else Failed++;
                        MelonLogger.Msg($"[HUMAN/BUILD] {cbTeam?.name} {name} at ({gotPos.x:F0},{gotPos.z:F0}) asked ({asked.x:F0},{asked.z:F0}) " +
                                        $"anchor {cbStruct?.ObjectInfo?.DisplayName} rot {(rot.HasValue ? "given" : "search")} result {res} cash {cbTeam?.TotalResources}");
                    },
                    (cData, cbTeam, cbStruct) =>
                    {
                        Failed++;
                        Refuse(name, $"no valid spot within {SEARCH_RANGE_M:F0}m of ({asked.x:F0},{asked.z:F0})");
                    });
            }
            catch (Exception ex)
            {
                MelonLogger.Warning("[HUMAN/BUILD] placement search threw: " + ex.Message);
                return false;
            }
            return true;
        }

        internal static string Summary() => Fired == 0 ? "" : $"  human builds: fired {Fired}, landed {Landed}, failed {Failed}\n";
    }
}
