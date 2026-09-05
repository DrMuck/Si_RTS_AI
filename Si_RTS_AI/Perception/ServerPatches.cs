using HarmonyLib;
using MelonLoader;
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace Si_RTS_AI.Perception
{
    /// <summary>
    /// SERVER-SIDE RELIEF FOR THE ONE GAME LOOP THAT SCALES BADLY.
    ///
    /// Round eight, minute 13, 267 units, 160 structures, 34 construction
    /// sites: ConstructionSite.Update took 3.7 s of a 20 s window, more than
    /// every unit agent on the map together, and it grows with sites times
    /// structures. Decompiled 0.9.46: a site whose progress is still zero
    /// calls GetFriendlyStructureNearby EVERY FRAME, and that walks every
    /// structure of every team, rotating eight corners of a bounding box per
    /// structure, to ask whether a functional anchor is within reach. A chain
    /// of queued nodes is exactly a row of sites at zero progress, so the
    /// alien expansion pays this cost squared.
    ///
    /// The answer to that question changes when a structure finishes or is
    /// placed, not sixty times a second. So the result is remembered per site
    /// for siteAnchorCheckS (0.5 s) and the original runs only when the memory
    /// is stale. A site starts at most half a second later than it would have.
    /// A yes is never cached: once it is yes the game sets progress above zero
    /// and never asks again.
    ///
    /// The developer's fix would be the same throttle, or an event on structure
    /// completion, plus a spatial index instead of the full structure list.
    /// </summary>
    internal static class ServerPatches
    {
        static bool _done;
        static float _holdS;
        static readonly Dictionary<int, float> _lastNoAt = new Dictionary<int, float>();
        internal static long Skipped, Evaluated;

        internal static void Init(HarmonyLib.Harmony harmony)
        {
            if (_done) return;
            _done = true;
            _holdS = Planning.RtsaiConfig.Float("siteAnchorCheckS", 0.5f);
            if (_holdS <= 0f) { MelonLogger.Msg("[RTSA/SRV] site anchor-check throttle off"); return; }
            try
            {
                var m = AccessTools.Method(typeof(ConstructionSite), "GetFriendlyStructureNearby");
                if (m == null) { MelonLogger.Warning("[RTSA/SRV] ConstructionSite.GetFriendlyStructureNearby not found"); return; }
                harmony.Patch(m, prefix: new HarmonyMethod(typeof(ServerPatches).GetMethod(nameof(AnchorCheckPrefix), BindingFlags.Static | BindingFlags.NonPublic)));
                MelonLogger.Msg($"[RTSA/SRV] site anchor-check throttled to every {_holdS:F1}s per site");
            }
            catch (Exception ex) { MelonLogger.Warning("[RTSA/SRV] patch failed: " + ex.Message); }
        }

        internal static void ResetForNewRound() { _lastNoAt.Clear(); Skipped = 0; Evaluated = 0; }

        // Returning false skips the original and leaves __result as set here.
        static bool AnchorCheckPrefix(ConstructionSite __instance, ref bool __result)
        {
            try
            {
                int key = __instance.GetInstanceID();
                float now = Time.time;
                if (_lastNoAt.TryGetValue(key, out float at) && now - at < _holdS)
                {
                    __result = false;
                    Skipped++;
                    return false;
                }
                // Let the original run; remember the time. If it answers yes the
                // site progresses and this is never asked again for that site,
                // so a remembered time only ever gates a repeated no.
                _lastNoAt[key] = now;
                Evaluated++;
                if (_lastNoAt.Count > 4096) _lastNoAt.Clear();
            }
            catch { }
            return true;
        }
    }
}
