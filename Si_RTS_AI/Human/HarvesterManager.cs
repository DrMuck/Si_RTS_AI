using System;
using System.Collections.Generic;
using MelonLoader;
using Silica;
using Silica.AI;
using UnityEngine;

namespace Si_RTS_AI.Human
{
    /// <summary>
    /// ONE HARVESTER PER BALTERIUM SPOT. DrMuck's rules (2026-09-07):
    ///   - a spot is paired with one harvester; two on one spot only when every
    ///     non-empty spot already has one;
    ///   - a refinery built next to a spot means its harvester works THAT spot
    ///     first (the spot nearest a refinery ranks first for a free harvester);
    ///   - when a spot is mined out its harvester moves on to a mid- or
    ///     long-range spot nobody is assigned to;
    ///   - the assignment table is the record: spot -> harvester; an empty spot
    ///     loses its assignment, a dead harvester frees its spot.
    /// The game's own harvester agent keeps the mechanics (dock, deposit, return);
    /// this only tells it which area to work, through the agent's Harvest call,
    /// and re-orders only when the agent's current area differs from the table.
    /// </summary>
    internal static class HarvesterManager
    {
        const float TICK_S       = 5f;
        const float NEAR_REF_M   = 250f;     // a spot this close to a refinery is "the refinery's spot"
        // A refinery only works balterium within its own coverage; past that a
        // harvester has nowhere to deliver. Sharing a covered patch (3000) must be
        // cheaper than walking to an uncovered one, or harvesters wander and die.
        const float REFINERY_COVERAGE_M = 400f;
        const float UNSERVED_PENALTY    = 4000f;
        const float REORDER_S    = 20f;      // do not repeat the same order more often than this
        const int   LOG_CAP      = 60;

        sealed class Slot { public ResourceArea Area; public Unit Harvester; public float Since; public float LastOrderAt; }
        static Dictionary<int, Slot> _bySpot = new Dictionary<int, Slot>();       // area instance id -> slot
        static Dictionary<int, int>  _byHarvester = new Dictionary<int, int>();   // harvester instance id -> area id
        static float _lastTickAt, _lastSummaryAt;
        static int _assignments, _moves, _logged;
        internal static int Assignments => _assignments;
        static Team _team;

        internal static bool Active(Team team) => team != null && _team != null && ReferenceEquals(team, _team);

        internal static void ResetForNewRound()
        {
            _bySpot.Clear(); _byHarvester.Clear(); _lastTickAt = _lastSummaryAt = 0f;
            _assignments = _moves = _logged = 0; _team = null;
        }

        internal static void Tick(Team team)
        {
            if (team == null) return;
            float now = Time.time;
            if (now - _lastTickAt < TICK_S) return;
            _lastTickAt = now; _team = team;
            try { Run(team, now); }
            catch (Exception ex) { if (_logged++ < LOG_CAP) MelonLogger.Warning("[HARV] threw: " + ex.Message); }
        }

        static bool IsHarvester(Unit u)
        {
            try
            {
                string n = u?.ObjectInfo?.DisplayName ?? "";
                return n.IndexOf("Harvester", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch { return false; }
        }

        static void Run(Team team, float now)
        {
            // live harvesters and known non-empty spots
            var harvesters = new List<Unit>(16);
            var units = team.Units;
            if (units != null)
                for (int i = 0; i < units.Count; i++)
                {
                    var u = units[i];
                    if (u == null || u.IsDestroyed || !IsHarvester(u)) continue;
                    harvesters.Add(u);
                }
            var spots = new List<ResourceArea>(32);
            try { ResourceArea.GetKnownResourcesAreas(team, team.UsableResource, spots, ignoreEmpty: true); } catch { }
            if (spots.Count == 0 || harvesters.Count == 0) return;
            var refineries = new List<Vector3>(16);
            var hqs = new List<Vector3>(4);
            try
            {
                var structs = team.Structures;
                if (structs != null)
                    for (int i = 0; i < structs.Count; i++)
                    {
                        var s = structs[i];
                        if (s?.ObjectInfo == null || s.IsDestroyed) continue;
                        string n = s.ObjectInfo.DisplayName ?? "";
                        if (n == "Refinery") refineries.Add(s.transform.position);
                        else if (n == "Headquarters") hqs.Add(s.transform.position);
                    }
            }
            catch { }

            // 1. drop assignments whose spot is empty or gone, or whose harvester is gone
            var liveSpotIds = new HashSet<int>();
            for (int i = 0; i < spots.Count; i++) liveSpotIds.Add(spots[i].GetInstanceID());
            var liveHarvIds = new HashSet<int>();
            for (int i = 0; i < harvesters.Count; i++) liveHarvIds.Add(harvesters[i].GetInstanceID());
            var drop = new List<int>();
            foreach (var kv in _bySpot)
            {
                var slot = kv.Value;
                bool spotDead = !liveSpotIds.Contains(kv.Key) || slot.Area == null || slot.Area.IsEmpty;
                bool harvDead = slot.Harvester == null || slot.Harvester.IsDestroyed || !liveHarvIds.Contains(slot.Harvester.GetInstanceID());
                if (spotDead || harvDead) drop.Add(kv.Key);
            }
            for (int i = 0; i < drop.Count; i++)
            {
                var slot = _bySpot[drop[i]];
                if (slot.Harvester != null) _byHarvester.Remove(slot.Harvester.GetInstanceID());
                _bySpot.Remove(drop[i]);
                if (_logged++ < LOG_CAP)
                    Si_RTS_AI.AppendToRound($"[HARV] t={RoundS():F0} spot ({slot.Area?.SignalCenter.x:F0},{slot.Area?.SignalCenter.z:F0}) released ({(slot.Area == null || slot.Area.IsEmpty ? "mined out" : "harvester gone")})");
            }

            // 2. every harvester without a live spot gets one
            for (int h = 0; h < harvesters.Count; h++)
            {
                var u = harvesters[h];
                int hid = u.GetInstanceID();
                if (_byHarvester.TryGetValue(hid, out int spotId) && _bySpot.ContainsKey(spotId)) continue;
                _byHarvester.Remove(hid);
                Vector3 hp = u.transform.position;
                ResourceArea best = null; float bestScore = float.MaxValue; bool bestShared = false;
                for (int s = 0; s < spots.Count; s++)
                {
                    var a = spots[s];
                    if (a == null || a.IsEmpty) continue;
                    bool taken = _bySpot.ContainsKey(a.GetInstanceID());
                    Vector3 c = a.SignalCenter;
                    float dHarv = Dist2D(hp, c);
                    float dRef = float.MaxValue;
                    for (int r = 0; r < refineries.Count; r++) dRef = Mathf.Min(dRef, Dist2D(refineries[r], c));
                    // A HARVESTER NEEDS A REFINERY TO DELIVER TO. DrMuck: "a refinery
                    // comes with a harvester, so that can be used also for a harvester
                    // production near a balterium patch" - the pairing is refinery to
                    // patch, and a spot beyond a refinery's own coverage has nothing to
                    // deposit into.
                    //
                    // The old weights had it backwards: sharing a served spot cost
                    // +3000 while walking to an UNSERVED spot cost at most +1600, so a
                    // harvester crossed the map rather than double up at home. Naraka
                    // 2026-09-09 10:23: assignments at (-2054,-1846) and (2893,-1684),
                    // eleven "harvester gone" releases against twelve "mined out", and
                    // Centauri lost five of nine harvesters while losing one refinery.
                    // It is also DrMuck's G7 case - a refinery with balterium beside it
                    // whose harvester had been sent somewhere else entirely.
                    //
                    // So: unserved ground costs more than sharing. Doubling up on a
                    // covered patch still earns; a lone trip across the map earns
                    // nothing and usually ends with a dead harvester.
                    bool served = dRef <= REFINERY_COVERAGE_M;
                    float score = dHarv
                                + (dRef <= NEAR_REF_M ? 0f : 600f)
                                + Mathf.Min(dRef, 2000f) * 0.5f
                                + (taken ? 3000f : 0f)
                                + (served ? 0f : UNSERVED_PENALTY);
                    if (score < bestScore) { bestScore = score; best = a; bestShared = taken; }
                }
                if (best == null) continue;
                int aid = best.GetInstanceID();
                if (!bestShared)
                    _bySpot[aid] = new Slot { Area = best, Harvester = u, Since = now };
                _byHarvester[hid] = aid;
                _assignments++;
                if (_logged++ < LOG_CAP)
                    Si_RTS_AI.AppendToRound($"[HARV] t={RoundS():F0} harvester #{hid} -> spot ({best.SignalCenter.x:F0},{best.SignalCenter.z:F0}) {best.ResourceAmountCurrent} left{(bestShared ? " (shared: every spot taken)" : "")}");
            }

            // 3. steer: only when the agent is working a different area
            for (int h = 0; h < harvesters.Count; h++)
            {
                var u = harvesters[h];
                if (!_byHarvester.TryGetValue(u.GetInstanceID(), out int aid)) continue;
                ResourceArea area = null;
                if (_bySpot.TryGetValue(aid, out var slot)) area = slot.Area;
                else for (int s = 0; s < spots.Count; s++) if (spots[s].GetInstanceID() == aid) { area = spots[s]; break; }
                if (area == null || area.IsEmpty) continue;
                var agent = u.AIAgent as AIHarvesterAgent;
                if (agent == null) continue;
                ResourceArea current = null; try { current = agent.CurrentHarvestArea; } catch { }
                if (current != null && ReferenceEquals(current, area)) continue;
                if (slot != null && now - slot.LastOrderAt < REORDER_S) continue;
                if (slot != null) slot.LastOrderAt = now;
                try
                {
                    Vector3 cell = area.SignalCenter;
                    try { cell = area.GetNearestResourceCell(u.transform.position); } catch { }
#if GAME_MAIN
                    agent.Harvest(area, cell, AgentMoveSpeed.Fast);
#else
                    agent.Harvest(area, cell, AgentMoveSpeed.Fast, true);
#endif
                    _moves++;
                }
                catch (Exception ex) { if (_logged++ < LOG_CAP) MelonLogger.Warning("[HARV] Harvest order threw: " + ex.Message); }
            }

            if (now - _lastSummaryAt >= 60f)
            {
                _lastSummaryAt = now;
                int shared = 0; foreach (var kv in _byHarvester) { int c = 0; foreach (var kv2 in _byHarvester) if (kv2.Value == kv.Value) c++; if (c > 1) shared++; }
                Si_RTS_AI.AppendToRound($"[HARV] t={RoundS():F0} harvesters {harvesters.Count} spots {spots.Count} paired {_bySpot.Count} sharing {shared} orders {_moves}");
            }
        }

        static float Dist2D(Vector3 a, Vector3 b) { float dx = a.x - b.x, dz = a.z - b.z; return Mathf.Sqrt(dx * dx + dz * dz); }
        static float RoundS() { try { return Perception.MapLayers.LayerReplay.CurrentRoundTime; } catch { return -1f; } }

        internal static string Summary() =>
            _assignments == 0 ? "" : $"--- Harvester manager ---\n  assignments {_assignments}, harvest orders {_moves}, spots paired at end {_bySpot.Count}\n";
    }
}
