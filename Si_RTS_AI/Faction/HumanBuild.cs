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
        // THE GAME'S OWN SEARCH GEOMETRY. The vanilla commander clamps the asked
        // point to the anchor's build reach, searches a range of 1.2 x reach plus
        // the distance asked (80..600 m) and gives the search reach/20 seconds
        // (6..40). A flat 120 m / 8 s found no spot for a Barracks anywhere in
        // the first Sol rounds (2026-09-07 19:55) while nine refineries landed.
        const float DEFAULT_REACH_M = 500f;
        const int   SEARCH_STEPS    = 300;
        const float DEDUPE_M       = 60f;
        const float DEDUPE_S       = 45f;

        struct Pending { public string Name; public Vector3 At; public float When; }
        static readonly List<Pending> _pending = new List<Pending>();
        static readonly Dictionary<string, int> _refusals = new Dictionary<string, int>();
        static readonly HashSet<string> _geomLogged = new HashSet<string>();
        internal static int Fired, Landed, Failed;

        internal static void ResetForNewRound()
        {
            _pending.Clear(); _refusals.Clear(); _geomLogged.Clear(); _landed.Clear(); Fired = Landed = Failed = 0;
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
        struct Landed { public string Name; public Vector3 At; }
        static readonly List<Landed> _landed = new List<Landed>();
        static bool LandedNear(string name, Vector3 p, float r)
        {
            for (int i = 0; i < _landed.Count; i++)
            {
                if (_landed[i].Name != name) continue;
                float dx = _landed[i].At.x - p.x, dz = _landed[i].At.z - p.z;
                if (dx * dx + dz * dz <= r * r) return true;
            }
            return false;
        }
        static bool StandingNear(Team team, string name, Vector3 p, float r)
        {
            try
            {
                var structs = team.Structures;
                if (structs == null) return false;
                for (int i = 0; i < structs.Count; i++)
                {
                    var s = structs[i];
                    if (s?.ObjectInfo == null || s.IsDestroyed) continue;
                    if ((s.ObjectInfo.DisplayName ?? "") != name) continue;
                    float dx = s.transform.position.x - p.x, dz = s.transform.position.z - p.z;
                    if (dx * dx + dz * dz <= r * r) return true;
                }
            }
            catch { }
            return false;
        }

        /// <summary>
        /// Ask the game to place <paramref name="cd"/> near <paramref name="pos"/>; true when a search was queued.
        /// <paramref name="maxDriftM"/> rejects a landing farther than that from the asked point;
        /// <paramref name="sameTypeSpacingM"/> rejects a landing that close to another structure of the same
        /// name, standing or landed this round. Four Headquarters went up within 300 m of each other on
        /// Naraka 22:04 (DrMuck) because neither test existed and the search drifted 560 m from its cell.
        /// </summary>
        internal static bool TryBuild(Team team, ConstructionData cd, Vector3 pos, Quaternion? rot,
                                      float maxDriftM = -1f, float sameTypeSpacingM = -1f)
        {
            if (team == null || cd == null) return false;
            string name = cd.ObjectInfo?.DisplayName ?? "?";
            if (PendingNear(name, pos)) { Refuse(name, "pending nearby"); return false; }
            var anchor = FindAnchor(team, cd, pos);
            if (anchor == null) { Refuse(name, "no structure can build it"); return false; }
            // TIER AND OBJECTS, CHECKED THE WAY THE GAME CHECKS A CLIENT. The
            // server-side Construct skips its own prerequisite test when the
            // caller counts as game master, which a dedicated server does; a
            // Heavy Factory (tier 4) went up at tier 0 in the 20:12 Sol round.
            bool prereq = true;
            try { prereq = team.GetHasPrerequisitesForConstruction(cd, true, EConstructionCheckTechTier.Full); } catch { }
            int minTier = -1, teamTier = 0;
            try { minTier = cd.MinimumTeamTier; } catch { }
            try { teamTier = team.TechnologyTier; } catch { }
            if (minTier > -1 && teamTier < minTier) prereq = false;
            if (!prereq) { Refuse(name, $"prerequisite (needs tier {minTier}, team tier {teamTier})"); return false; }
            int cost = 0; try { cost = cd.ResourceCost; } catch { }
            if (cost > 0 && team.TotalResources < cost) { Refuse(name, $"cash {team.TotalResources} < {cost}"); return false; }
            var preview = cd.ObjectPreviewSetup;
            if (preview == null) { Refuse(name, "no preview"); return false; }
            float reach = DEFAULT_REACH_M;
            try { if (cd.MaximumBaseStructureDistance > 0f) reach = cd.MaximumBaseStructureDistance; } catch { }
            Vector3 anchorPos = anchor.transform.position;
            Vector3 toward = pos - anchorPos; toward.y = 0f;
            float dist = toward.magnitude;
            if (dist > reach && dist > 0.01f) pos = anchorPos + toward / dist * reach;
            float range = Mathf.Clamp(reach * 1.2f + Mathf.Min(dist, reach), 80f, 600f);
            float maxTime = Mathf.Clamp(range / 20f, 6f, 40f);
            if (!_geomLogged.Contains(name))
            {
                _geomLogged.Add(name);
                bool placeable = false; try { placeable = cd.Placeable; } catch { }
                MelonLogger.Msg($"[HUMAN/BUILD] {name}: reach {reach:F0}m snap {cd.GridSnapXZ}/{cd.GridSnapY} placeable {placeable} cost {cost} anchor {anchor.ObjectInfo?.DisplayName} range {range:F0}m time {maxTime:F0}s minTier {minTier} teamTier {teamTier}");
            }
            _pending.Add(new Pending { Name = name, At = pos, When = Time.time });
            Fired++;
            Vector3 asked = pos;
            try
            {
                ConstructionPlacement.QueueFirstValidPlacementAroundPoint(
                    preview, team, anchor, pos, cd.GridSnapXZ, cd.GridSnapY,
                    range, maxTime, SEARCH_STEPS,
                    (cData, cbTeam, cbStruct, gotPos, gotRot) =>
                    {
                        string res = "void";
                        float drift = Vector2.Distance(new Vector2(gotPos.x, gotPos.z), new Vector2(asked.x, asked.z));
                        if (maxDriftM > 0f && drift > maxDriftM)
                        {
                            Failed++;
                            Refuse(name, $"landing drifted {drift:F0}m from the asked point (limit {maxDriftM:F0}m)");
                            return;
                        }
                        if (sameTypeSpacingM > 0f && (StandingNear(cbTeam, name, gotPos, sameTypeSpacingM) || LandedNear(name, gotPos, sameTypeSpacingM)))
                        {
                            Failed++;
                            Refuse(name, $"another {name} within {sameTypeSpacingM:F0}m of the landing");
                            return;
                        }
                        try
                        {
                            if (cbStruct != null && !cbStruct.IsDestroyed)
                                res = cbStruct.Construct(cData, gotPos, gotRot).ToString();
                        }
                        catch (Exception ex) { res = "threw " + ex.Message; }
                        if (res == "Success") { Landed++; _landed.Add(new Landed { Name = name, At = gotPos }); } else Failed++;
                        MelonLogger.Msg($"[HUMAN/BUILD] {cbTeam?.name} {name} at ({gotPos.x:F0},{gotPos.z:F0}) asked ({asked.x:F0},{asked.z:F0}) " +
                                        $"anchor {cbStruct?.ObjectInfo?.DisplayName} rot {(rot.HasValue ? "given" : "search")} result {res} cash {cbTeam?.TotalResources}");
                    },
                    (cData, cbTeam, cbStruct) =>
                    {
                        Failed++;
                        Refuse(name, $"no valid spot within {range:F0}m of ({asked.x:F0},{asked.z:F0})");
                    },
                    false, rot.HasValue, rot ?? Quaternion.identity);
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
