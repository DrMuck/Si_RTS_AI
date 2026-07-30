using HarmonyLib;
using MelonLoader;
using Silica;
using Silica.AI;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Si_RTS_AI.Faction
{
    /// <summary>
    /// v0.7.52 — Human harvester controller. Owns Sol/Centauri harvester ROUTING
    /// (direct move orders), while leaving stock harvest/deposit MECHANICS untouched.
    ///
    /// Four capabilities:
    ///   1. Closest-balterium-to-refinery assignment on first sighting.
    ///   2. Anti-clumping — spread harvesters across available patches.
    ///   3. Queue-vs-travel — divert to alt refinery when home queue is too long.
    ///   4. Stuck detection + force-reassign after 45s of no delivery + no motion.
    ///
    /// Ride-along: hooks AIConstructionHandler.Think **Postfix** so it runs after
    /// HumanConstruction's Prefix returns false (Postfixes still fire even when
    /// the Prefix suppresses the original). Only touches units with
    /// ObjectInfo.DisplayName == "Harvester" and only for teams where
    /// Suppression.Phase31_Production.OverrideByTeam[team] == true.
    ///
    /// ---------------------------------------------------------------------------
    /// API PROBE FINDINGS (compile-verified against SilicaCore.dll v0.9.16)
    /// ---------------------------------------------------------------------------
    ///   SHIPPED:
    ///     Unit.transform.position          — Vector3 (position, existing pattern)
    ///     Unit.IsMoving                    — bool
    ///     Unit.CurrentSpeed                — float
    ///     Unit.CurrentTarget               — object (Target-flavored)
    ///     Unit.Target                      — Target
    ///     Unit.ObjectInfo.DisplayName      — string ("Harvester")
    ///     Unit.OnMoveOrder(Vector3, AgentMoveSpeed) — void  ← primary control lever
    ///     Unit.OnStopOrder(AgentMoveSpeed, bool, bool) — 3-arg (not used here)
    ///     Unit.OnAttackOrder(Target, Vector3, AgentMoveSpeed, bool, bool, bool)
    ///     Team.Units                       — List&lt;Unit&gt;
    ///     Team.Structures                  — List&lt;Structure&gt;
    ///     Team.TotalResources              — int (for delivery-rate cross-check)
    ///     ResourceArea.SignalCenter        — Vector3
    ///     ResourceArea.ResourceType        — enum
    ///     ResourceArea.IsEmpty             — bool
    ///     ResourceArea.GetKnownResourcesAreas(team, resource, list, ignoreEmpty)
    ///     AgentMoveSpeed.Fast              — enum
    ///     AgentMoveSpeed.Normal            — enum
    ///     AIRequest.Harvest(AIUnitHandler, int priority, ResourceArea) — commander queue inject
    ///     AIGroup.OnMoveOrder(Vector3, AgentMoveSpeed)
    ///     AIGroup.Units / Task / Type / Preset / IsForHarvest
    ///     EAITaskType.Harvest              — enum value exists
    ///     Suppression.Phase31_Production.OverrideByTeam — gate
    ///
    ///   NOT FOUND (workarounds applied):
    ///     Unit.Position                    — use unit.transform.position
    ///     Unit.LinearVelocity / Velocity   — use IsMoving + position deltas
    ///     Unit.MoveTo(Vector3)             — use OnMoveOrder instead
    ///     Unit.CargoAmount / IsCarryingResource / HarvestState
    ///                                      — infer state via position + motion FSM
    ///     Unit.AssignedTask / CurrentOrder — no direct read; track our own assignment
    ///     AIRequest.Deposit                — doesn't exist as static factory
    ///     EAIRequestType.Harvest / .Deposit — enum values not in EAIRequestType
    ///     ResourceArea.CurrentAmount / MaximumAmount — no per-patch amount read;
    ///                                      use IsEmpty + ignoreEmpty=true filter
    ///     Structure.IsResourceDropoff / IsRefinery / QueueLength / etc.
    ///                                      — no first-class refinery flags; match
    ///                                      by ObjectInfo.DisplayName == "Refinery"
    ///                                      and infer queue by "stationary harvesters
    ///                                      within N meters of refinery" heuristic
    ///     AIGroup.OnHarvestOrder(ResourceArea) — no direct group-level harvest issue;
    ///                                      OnMoveOrder to patch position + rely on
    ///                                      stock harvest-on-proximity engagement
    /// ---------------------------------------------------------------------------
    /// </summary>
    internal static class HumanHarvesterController
    {
        // ---- tuning constants (top-of-file per spec) ----
        const string HARVESTER_NAME = "Harvester";
        const string REFINERY_NAME  = "Refinery";

        // Assignment / dedup radii
        const float PATCH_CLAIM_RADIUS_M   = 60f;    // harvester within this of a patch = actively harvesting it
        const float REFINERY_ARRIVE_RADIUS_M = 60f;  // harvester within this of a refinery = at dropoff
        const float REFINERY_QUEUE_RADIUS_M  = 55f;  // stationary within this counts as queued
        const float REPLACE_MOVE_ORDER_MIN_INTERVAL_S = 6f; // don't spam reissue

        // Rule 4 — stuck detection
        const float STUCK_TIMEOUT_S              = 45f;
        const float STUCK_POS_DELTA_M            = 4f;  // moved less than this over the window = "no motion"

        // Rule 3 — queue-vs-travel
        // Simple heuristic per spec: if queue_wait_estimate > time_to_travel_to_farther_refinery: travel
        const float TYPICAL_HARVESTER_SPEED_M_PER_S = 20f;   // ballpark
        const float UNLOAD_TIME_PER_HARVESTER_S     = 6f;    // per-slot wait cost
        const int   MIN_QUEUE_TO_DIVERT             = 2;     // don't divert against a queue of 1

        // Tick throttle — we ride along AIConstructionHandler.Think which is called
        // once per stock Think interval (~3s at vanilla). One controller pass per
        // tick per team is enough.
        const int LOG_STATE_EVERY_N_TICKS = 5;

        // ---- state ----
        static int _tickCounter;

        class HarvesterState
        {
            public Unit Unit = null!;
            public Vector3 HomeRefineryPos;      // sticky home (nearest refinery at first sighting)
            public Vector3 AssignedPatchPos;     // last patch we told it to go to (zero = unassigned)
            public Vector3 LastPos;
            public float   LastMoveObservedAt;   // wall-clock, updated when position changes >= STUCK_POS_DELTA_M
            public float   LastAssignmentAt;
            public float   LastDeliveryAt;
            public int     Deliveries;
            public string  MacroState = "?";     // OUTBOUND / LOADING / RETURNING / UNLOADING / IDLE
            public string  PrevMacroState = "?"; // for edge-triggered delivery counting
            public bool    HasBeenAssigned;      // rule-3 divert only after first refinery ROUND-TRIP
            public int     Diverts;              // count divert events per harvester
            public int     StuckEvents;
        }
        static readonly Dictionary<Unit, HarvesterState> _byUnit = new Dictionary<Unit, HarvesterState>();

        // Round counters
        internal static int TotalHarvestersSeen;
        internal static int InitialAssignments;
        internal static int Reassignments;
        internal static int StuckEvents;
        internal static int Diverts;
        internal static int Deliveries;

        // ---- Harmony ride-along (Postfix — HumanConstruction Prefix returns false;
        // Postfix still fires) ----
        [HarmonyPatch(typeof(AIConstructionHandler), nameof(AIConstructionHandler.Think))]
        static class Patch_Think_Postfix
        {
            static void Postfix(AIConstructionHandler __instance)
            {
                try { Tick(__instance?.Commander?.Team); }
                catch (Exception ex) { MelonLogger.Warning("[RTSA/HARV] threw: " + ex.Message); }
            }
        }

        static void Tick(Team? team)
        {
            if (team == null) return;
            string tn = team.name ?? "";
            bool isHuman = (tn.Contains("Sol") || tn.Contains("Centauri") || tn.Contains("Cent")) && !tn.Contains("Alien");
            if (!isHuman) return;
            if (!Suppression.Phase31_Production.OverrideByTeam.TryGetValue(team, out bool ov) || !ov) return;

            _tickCounter++;
            float now = Time.time;

            // Gather refineries + patches once per tick.
            var refineries = CollectRefineries(team);
            if (refineries.Count == 0) return;   // no refineries yet → nothing sensible to do

            var patches = new List<ResourceArea>(16);
            try { ResourceArea.GetKnownResourcesAreas(team, team.UsableResource, patches, ignoreEmpty: true); }
            catch { /* first-tick failures are expected before scene is ready */ }

            // Walk team.Units once — pick harvesters + drop stale entries.
            var currentHarvesters = new List<Unit>(8);
            var units = team.Units;
            if (units != null)
            {
                for (int i = 0; i < units.Count; i++)
                {
                    var u = units[i];
                    if (u?.ObjectInfo == null) continue;
                    if (!string.Equals(u.ObjectInfo.DisplayName, HARVESTER_NAME, StringComparison.OrdinalIgnoreCase)) continue;
                    currentHarvesters.Add(u);
                }
            }

            // Cleanup dictionary entries for units that no longer exist / changed team.
            List<Unit>? gone = null;
            foreach (var kv in _byUnit)
            {
                if (kv.Key == null || kv.Key.Team != team) { }   // may belong to another human team → leave alone
                else if (!currentHarvesters.Contains(kv.Key))
                {
                    if (gone == null) gone = new List<Unit>();
                    gone.Add(kv.Key);
                }
            }
            if (gone != null) foreach (var g in gone) _byUnit.Remove(g);

            // For anti-clumping: count harvesters we've assigned per patch.
            var claimsByPatch = new Dictionary<Vector3, int>();
            foreach (var kv in _byUnit)
            {
                if (kv.Value.AssignedPatchPos.sqrMagnitude < 0.01f) continue;
                Vector3 pos = SnapPatchKey(kv.Value.AssignedPatchPos);
                claimsByPatch[pos] = claimsByPatch.TryGetValue(pos, out int c) ? c + 1 : 1;
            }

            // Refinery queues: harvesters currently at each refinery (stationary within radius).
            var queueByRef = ComputeRefineryQueues(currentHarvesters, refineries);

            // Per-harvester step.
            for (int i = 0; i < currentHarvesters.Count; i++)
            {
                var u = currentHarvesters[i];
                if (!_byUnit.TryGetValue(u, out var st))
                {
                    st = new HarvesterState { Unit = u };
                    st.LastPos = u.transform.position;
                    st.LastMoveObservedAt = now;
                    st.HomeRefineryPos = FindClosestRefinery(u.transform.position, refineries);
                    _byUnit[u] = st;
                    TotalHarvestersSeen++;

                    // Rule 1: assign to closest AVAILABLE patch to home refinery.
                    if (TryInitialAssign(st, patches, claimsByPatch, now, team))
                        InitialAssignments++;
                    continue;
                }

                // Position tracking for motion + stuck detection.
                Vector3 pNow = u.transform.position;
                Vector3 pOld = st.LastPos;
                float dx = pNow.x - pOld.x, dz = pNow.z - pOld.z;
                float moved = Mathf.Sqrt(dx * dx + dz * dz);
                if (moved >= STUCK_POS_DELTA_M) { st.LastMoveObservedAt = now; st.LastPos = pNow; }

                // Macro-state derivation (used for delivery accounting + rule triggers).
                st.PrevMacroState = st.MacroState;
                st.MacroState = ClassifyMacroState(u, pNow, refineries, patches);

                // Delivery counting — edge trigger: entering UNLOADING from RETURNING.
                if (st.MacroState == "UNLOADING" && st.PrevMacroState != "UNLOADING")
                {
                    st.Deliveries++; st.LastDeliveryAt = now;
                    Deliveries++;
                }

                // Rule 2 — anti-clumping. Trigger only when: harvester is in OUTBOUND
                // state (heading to a patch), its claimed patch already has >=2
                // claimants, AND there's an unclaimed patch available.
                if (st.MacroState == "OUTBOUND" && !st.AssignedPatchPos.Equals(Vector3.zero))
                {
                    Vector3 key = SnapPatchKey(st.AssignedPatchPos);
                    if (claimsByPatch.TryGetValue(key, out int nClaimed) && nClaimed >= 2)
                    {
                        if (TryFindUnclaimedPatch(patches, claimsByPatch, st.HomeRefineryPos, out var better)
                            && CanReissue(st, now))
                        {
                            IssueMoveOrder(u, better.SignalCenter, AgentMoveSpeed.Fast);
                            // Update claims: subtract 1 from old key, add 1 to new.
                            claimsByPatch[key] = nClaimed - 1;
                            Vector3 newKey = SnapPatchKey(better.SignalCenter);
                            claimsByPatch[newKey] = claimsByPatch.TryGetValue(newKey, out int c2) ? c2 + 1 : 1;
                            st.AssignedPatchPos = better.SignalCenter;
                            st.LastAssignmentAt = now;
                            Reassignments++;
                            Si_RTS_AI.AppendToRound(
                                $"[H4] team={team.name} reassign=anti-clump " +
                                $"unitAt=({pNow.x:F0},{pNow.z:F0}) newPatchAt=({better.SignalCenter.x:F0},{better.SignalCenter.z:F0})");
                        }
                    }
                }

                // Rule 3 — queue-vs-travel. Trigger when the harvester is en route
                // to its home refinery (RETURNING) and the queue at home is long
                // enough that another refinery is a better bet.
                if (st.MacroState == "RETURNING" && st.HasBeenAssigned && CanReissue(st, now))
                {
                    Structure? homeRef = FindRefineryAt(st.HomeRefineryPos, refineries);
                    if (homeRef != null && queueByRef.TryGetValue(homeRef, out int qHome) && qHome >= MIN_QUEUE_TO_DIVERT)
                    {
                        Structure? altBest = null; float altBestScore = float.MaxValue;
                        for (int r = 0; r < refineries.Count; r++)
                        {
                            var alt = refineries[r];
                            if (alt == homeRef) continue;
                            Vector3 altPos = alt.transform.position;
                            float distToAlt = Distance2D(pNow, altPos);
                            float travelExtra = Distance2D(pNow, altPos) - Distance2D(pNow, st.HomeRefineryPos);
                            int altQueue = queueByRef.TryGetValue(alt, out int q) ? q : 0;
                            float altWait  = altQueue * UNLOAD_TIME_PER_HARVESTER_S;
                            float altTotal = Mathf.Max(0f, travelExtra / TYPICAL_HARVESTER_SPEED_M_PER_S) + altWait;
                            if (altTotal < altBestScore) { altBestScore = altTotal; altBest = alt; }
                        }
                        float homeWait = qHome * UNLOAD_TIME_PER_HARVESTER_S;
                        if (altBest != null && altBestScore < homeWait)
                        {
                            IssueMoveOrder(u, altBest.transform.position, AgentMoveSpeed.Fast);
                            st.Diverts++;
                            Diverts++;
                            st.LastAssignmentAt = now;
                            Si_RTS_AI.AppendToRound(
                                $"[H4] team={team.name} divert=queue-vs-travel " +
                                $"unitAt=({pNow.x:F0},{pNow.z:F0}) fromHome=({st.HomeRefineryPos.x:F0},{st.HomeRefineryPos.z:F0}) " +
                                $"toAlt=({altBest.transform.position.x:F0},{altBest.transform.position.z:F0}) " +
                                $"homeQueue={qHome} altScore={altBestScore:F1}s homeWait={homeWait:F1}s");
                        }
                    }
                }

                // Rule 4 — stuck detection + force reassign.
                bool noMotion = (now - st.LastMoveObservedAt) > STUCK_TIMEOUT_S;
                bool noDelivery = (st.LastDeliveryAt <= 0f) ? (now - (st.LastAssignmentAt > 0 ? st.LastAssignmentAt : 0f)) > STUCK_TIMEOUT_S
                                                            : (now - st.LastDeliveryAt) > STUCK_TIMEOUT_S;
                if (noMotion && noDelivery && !u.IsMoving)
                {
                    // Force reassign to nearest available patch to CURRENT position
                    // (not home — harvester may be lost far from base).
                    if (TryFindClosestUnclaimedPatch(patches, claimsByPatch, pNow, out var patch))
                    {
                        // Free the old claim.
                        if (!st.AssignedPatchPos.Equals(Vector3.zero))
                        {
                            Vector3 oldKey = SnapPatchKey(st.AssignedPatchPos);
                            if (claimsByPatch.TryGetValue(oldKey, out int oc) && oc > 0) claimsByPatch[oldKey] = oc - 1;
                        }
                        IssueMoveOrder(u, patch.SignalCenter, AgentMoveSpeed.Fast);
                        Vector3 newKey = SnapPatchKey(patch.SignalCenter);
                        claimsByPatch[newKey] = claimsByPatch.TryGetValue(newKey, out int nc) ? nc + 1 : 1;
                        st.AssignedPatchPos = patch.SignalCenter;
                        st.LastAssignmentAt = now;
                        st.LastMoveObservedAt = now;     // give it a fresh window
                        st.StuckEvents++;
                        StuckEvents++;
                        Si_RTS_AI.AppendToRound(
                            $"[H4] team={team.name} STUCK unitAt=({pNow.x:F0},{pNow.z:F0}) " +
                            $"lastDelivery={st.LastDeliveryAt:F0}s deliveries={st.Deliveries} " +
                            $"→ reassigned patchAt=({patch.SignalCenter.x:F0},{patch.SignalCenter.z:F0})");
                    }
                }
                st.HasBeenAssigned |= (st.Deliveries > 0) || !st.AssignedPatchPos.Equals(Vector3.zero);
            }

            // Periodic state dump for grepping.
            if (_tickCounter % LOG_STATE_EVERY_N_TICKS == 0)
            {
                int liveH = currentHarvesters.Count;
                float roundT = Mathf.Max(1f, Time.time);
                float avgRate = Deliveries / roundT;
                Si_RTS_AI.AppendToRound(
                    $"[H4] team={team.name} tick={_tickCounter} harvesters={liveH} deliveries={Deliveries} " +
                    $"avgRate={avgRate:F2}/s reassigns={Reassignments} diverts={Diverts} stuck={StuckEvents}");
            }
        }

        // ---- assignment helpers ----

        static bool TryInitialAssign(HarvesterState st, List<ResourceArea> patches,
            Dictionary<Vector3, int> claimsByPatch, float now, Team team)
        {
            if (patches == null || patches.Count == 0) return false;

            // Prefer patch with the fewest existing claimants that is closest to home refinery.
            // Two-pass: (a) unclaimed patches, (b) any patch.
            if (TryFindUnclaimedPatch(patches, claimsByPatch, st.HomeRefineryPos, out var patch)
                || TryFindClosestUnclaimedPatch(patches, claimsByPatch, st.HomeRefineryPos, out patch))
            {
                IssueMoveOrder(st.Unit, patch.SignalCenter, AgentMoveSpeed.Fast);
                Vector3 key = SnapPatchKey(patch.SignalCenter);
                claimsByPatch[key] = claimsByPatch.TryGetValue(key, out int c) ? c + 1 : 1;
                st.AssignedPatchPos = patch.SignalCenter;
                st.LastAssignmentAt = now;
                Si_RTS_AI.AppendToRound(
                    $"[H4] team={team.name} initial-assign unitAt=({st.LastPos.x:F0},{st.LastPos.z:F0}) " +
                    $"→ patchAt=({patch.SignalCenter.x:F0},{patch.SignalCenter.z:F0}) " +
                    $"homeRef=({st.HomeRefineryPos.x:F0},{st.HomeRefineryPos.z:F0})");
                return true;
            }
            return false;
        }

        static bool TryFindUnclaimedPatch(List<ResourceArea> patches,
            Dictionary<Vector3, int> claimsByPatch, Vector3 nearPos, out ResourceArea best)
        {
            best = null!;
            float bestDist = float.MaxValue;
            for (int i = 0; i < patches.Count; i++)
            {
                var p = patches[i];
                if (p == null) continue;
                Vector3 key = SnapPatchKey(p.SignalCenter);
                if (claimsByPatch.TryGetValue(key, out int c) && c > 0) continue;
                float d = Distance2D(p.SignalCenter, nearPos);
                if (d < bestDist) { bestDist = d; best = p; }
            }
            return best != null;
        }

        static bool TryFindClosestUnclaimedPatch(List<ResourceArea> patches,
            Dictionary<Vector3, int> claimsByPatch, Vector3 nearPos, out ResourceArea best)
        {
            // Fallback when all patches are claimed — pick the closest, ignore claims.
            best = null!;
            float bestDist = float.MaxValue;
            for (int i = 0; i < patches.Count; i++)
            {
                var p = patches[i];
                if (p == null) continue;
                float d = Distance2D(p.SignalCenter, nearPos);
                if (d < bestDist) { bestDist = d; best = p; }
            }
            return best != null;
        }

        // ---- classification ----

        static string ClassifyMacroState(Unit u, Vector3 pos, List<Structure> refineries, List<ResourceArea> patches)
        {
            bool atRef = false;
            for (int i = 0; i < refineries.Count; i++)
                if (Distance2D(pos, refineries[i].transform.position) < REFINERY_ARRIVE_RADIUS_M) { atRef = true; break; }

            bool atPatch = false;
            if (patches != null)
                for (int i = 0; i < patches.Count; i++)
                {
                    if (patches[i] == null) continue;
                    if (Distance2D(pos, patches[i].SignalCenter) < PATCH_CLAIM_RADIUS_M) { atPatch = true; break; }
                }

            if (atRef && !u.IsMoving) return "UNLOADING";
            if (atPatch && !u.IsMoving) return "LOADING";
            if (!u.IsMoving) return "IDLE";
            // Moving. Direction unknown — pick RETURNING if closer to a refinery than any patch, else OUTBOUND.
            float dRef = float.MaxValue;
            for (int i = 0; i < refineries.Count; i++)
                dRef = Mathf.Min(dRef, Distance2D(pos, refineries[i].transform.position));
            float dPatch = float.MaxValue;
            if (patches != null)
                for (int i = 0; i < patches.Count; i++)
                {
                    if (patches[i] == null) continue;
                    dPatch = Mathf.Min(dPatch, Distance2D(pos, patches[i].SignalCenter));
                }
            return dRef < dPatch ? "RETURNING" : "OUTBOUND";
        }

        // ---- misc helpers ----

        static Dictionary<Structure, int> ComputeRefineryQueues(List<Unit> harvesters, List<Structure> refineries)
        {
            var d = new Dictionary<Structure, int>();
            for (int r = 0; r < refineries.Count; r++)
            {
                var s = refineries[r]; if (s == null) continue;
                Vector3 sp = s.transform.position;
                int q = 0;
                for (int h = 0; h < harvesters.Count; h++)
                {
                    var u = harvesters[h]; if (u == null) continue;
                    if (u.IsMoving) continue;
                    if (Distance2D(u.transform.position, sp) < REFINERY_QUEUE_RADIUS_M) q++;
                }
                d[s] = q;
            }
            return d;
        }

        static List<Structure> CollectRefineries(Team team)
        {
            var list = new List<Structure>(8);
            var structs = team.Structures;
            if (structs == null) return list;
            for (int i = 0; i < structs.Count; i++)
            {
                var s = structs[i];
                if (s?.ObjectInfo == null || s.IsDestroyed) continue;
                if (string.Equals(s.ObjectInfo.DisplayName, REFINERY_NAME, StringComparison.OrdinalIgnoreCase))
                    list.Add(s);
            }
            return list;
        }

        static Vector3 FindClosestRefinery(Vector3 pos, List<Structure> refineries)
        {
            if (refineries.Count == 0) return Vector3.zero;
            Vector3 best = refineries[0].transform.position;
            float bestDist = Distance2D(pos, best);
            for (int i = 1; i < refineries.Count; i++)
            {
                Vector3 rp = refineries[i].transform.position;
                float d = Distance2D(pos, rp);
                if (d < bestDist) { bestDist = d; best = rp; }
            }
            return best;
        }

        static Structure? FindRefineryAt(Vector3 pos, List<Structure> refineries)
        {
            for (int i = 0; i < refineries.Count; i++)
            {
                var s = refineries[i]; if (s == null) continue;
                if (Distance2D(s.transform.position, pos) < 5f) return s;
            }
            return null;
        }

        static bool CanReissue(HarvesterState st, float now)
            => (now - st.LastAssignmentAt) >= REPLACE_MOVE_ORDER_MIN_INTERVAL_S;

        static void IssueMoveOrder(Unit u, Vector3 pos, AgentMoveSpeed speed)
        {
            try { u.OnMoveOrder(pos, speed); }
            catch (Exception ex) { MelonLogger.Warning("[RTSA/HARV] OnMoveOrder threw: " + ex.Message); }
        }

        static float Distance2D(Vector3 a, Vector3 b)
        {
            float dx = a.x - b.x, dz = a.z - b.z;
            return Mathf.Sqrt(dx * dx + dz * dz);
        }

        // Snap patch position to a coarse grid for dedup keys — Vector3-exact keys
        // miss due to Y precision & minor jitter, same pattern used elsewhere in
        // the mod (see AlienConstruction.HasPendingCystNear).
        static Vector3 SnapPatchKey(Vector3 p) => new Vector3(Mathf.Round(p.x / 5f) * 5f, 0f, Mathf.Round(p.z / 5f) * 5f);

        // ---- round wiring ----

        internal static void ResetForNewRound()
        {
            _tickCounter = 0;
            _byUnit.Clear();
            TotalHarvestersSeen = 0;
            InitialAssignments = 0;
            Reassignments = 0;
            StuckEvents = 0;
            Diverts = 0;
            Deliveries = 0;
        }

        internal static string BuildRoundSummaryFragment()
        {
            if (TotalHarvestersSeen == 0 && Deliveries == 0 && InitialAssignments == 0) return "";
            float roundT = Mathf.Max(1f, Time.time);
            float avgRate = Deliveries / roundT;
            return "--- Human harvester controller (v0.7.52) ---\n" +
                   $"  Harvesters seen this round:     {TotalHarvestersSeen}\n" +
                   $"  Initial assignments (rule 1):   {InitialAssignments}\n" +
                   $"  Reassignments (rule 2 anti-clump): {Reassignments}\n" +
                   $"  Diverts (rule 3 queue-vs-travel):  {Diverts}\n" +
                   $"  Stuck events (rule 4):          {StuckEvents}\n" +
                   $"  Total deliveries:               {Deliveries}\n" +
                   $"  Avg delivery rate:              {avgRate:F2} /s\n";
        }
    }
}
