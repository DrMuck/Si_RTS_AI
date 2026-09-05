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
            GuardLateUpdate(harmony);
        }

        // ONE PLAYER IN A VEHICLE SEAT BREAKS THE WHOLE SERVER'S LATE UPDATE.
        //
        // Public round 2026-09-05 22:03: a player sat down in a vehicle
        // compartment and from that frame on the server threw a
        // NullReferenceException 60,000 times in twenty minutes from
        // CharacterAnimator.OnInCompartmentAnimatorLateUpdate, called by
        // Soldier.OnOrderedLateUpdate, called by Soldier.OrderedLateUpdateAll,
        // called by OrderedUpdateManager.LateUpdate. That manager loops its
        // callbacks with no try/catch, so the exception ended the loop every
        // frame, and everything registered below the soldiers' priority of 100
        // never ran again while the player stayed seated: the construction
        // placement finisher (priority 0 — no Bio Cache could be placed, the
        // eco planner reported "held", income fell to zero and 38 of 56 shrimps
        // walked to patches with no Bio Cache), the network transform late
        // update (-1000 — clients saw units circle), the aim updates, and the
        // late-update finish (-1000001).
        //
        // The null comes from UnitAnimator.Awake, which Si_ServerOptimizer
        // disables on the dedicated server: Awake calls OnAnimatorInit, and
        // CharacterAnimator's OnAnimatorInit is where DefaultAnimator is taken
        // from the Animator. Without it the compartment override is built on
        // nothing and the first use dereferences it. So the game has no bug
        // here without the optimizer, and the optimizer has no bug here without
        // a seated player; together they take the server down a level. Two
        // guards:
        //
        //   1. A prefix on CharacterAnimator.OnInCompartmentAnimatorLateUpdate
        //      that skips the method when the Animator or DefaultAnimator is
        //      null. Nothing is lost: there is no animator to drive.
        //   2. A finalizer on Soldier.OnOrderedLateUpdate that swallows any
        //      exception, counts it, and reports once a minute, so that no
        //      single soldier can end the late-update list for everyone again.
        internal static long LateUpdateExceptions, AnimatorSkips;
        static float _lastExcLogAt;
        static Func<object, object> _animatorGetter, _defaultAnimatorGetter;

        static void GuardLateUpdate(HarmonyLib.Harmony harmony)
        {
            if (!Planning.RtsaiConfig.Bool("guardLateUpdate", true)) { MelonLogger.Msg("[RTSA/SRV] late-update guard off"); return; }
            try
            {
                var ca = AccessTools.TypeByName("CharacterAnimator");
                var m = ca == null ? null : AccessTools.Method(ca, "OnInCompartmentAnimatorLateUpdate");
                if (m != null)
                {
                    var f = AccessTools.Field(ca, "Animator") ?? AccessTools.Field(ca, "m_Animator");
                    var pr = AccessTools.Property(ca, "Animator");
                    if (f != null) _animatorGetter = o => f.GetValue(o);
                    else if (pr != null) _animatorGetter = o => pr.GetValue(o);
                    var fd = AccessTools.Field(ca, "DefaultAnimator");
                    if (fd != null) _defaultAnimatorGetter = o => fd.GetValue(o);
                    if (_animatorGetter != null)
                    {
                        harmony.Patch(m, prefix: new HarmonyMethod(typeof(ServerPatches).GetMethod(nameof(CompartmentAnimatorPrefix), BindingFlags.Static | BindingFlags.NonPublic)));
                        MelonLogger.Msg("[RTSA/SRV] compartment animator guarded (skips when the Animator or DefaultAnimator is null)");
                    }
                    else MelonLogger.Warning("[RTSA/SRV] CharacterAnimator has no Animator member to test; prefix not applied");
                }
                else MelonLogger.Warning("[RTSA/SRV] CharacterAnimator.OnInCompartmentAnimatorLateUpdate not found");

                var sm = AccessTools.Method(typeof(Soldier), "OnOrderedLateUpdate");
                if (sm != null)
                {
                    harmony.Patch(sm, finalizer: new HarmonyMethod(typeof(ServerPatches).GetMethod(nameof(SoldierLateUpdateFinalizer), BindingFlags.Static | BindingFlags.NonPublic)));
                    MelonLogger.Msg("[RTSA/SRV] Soldier.OnOrderedLateUpdate exceptions are contained");
                }
                else MelonLogger.Warning("[RTSA/SRV] Soldier.OnOrderedLateUpdate not found");
            }
            catch (Exception ex) { MelonLogger.Warning("[RTSA/SRV] late-update guard failed: " + ex.Message); }
        }

        static bool CompartmentAnimatorPrefix(object __instance)
        {
            try
            {
                var anim = _animatorGetter(__instance) as UnityEngine.Object;
                if (anim == null) { AnimatorSkips++; return false; }
                if (_defaultAnimatorGetter != null && (_defaultAnimatorGetter(__instance) as UnityEngine.Object) == null)
                { AnimatorSkips++; return false; }
            }
            catch { }
            return true;
        }

        static Exception SoldierLateUpdateFinalizer(Exception __exception)
        {
            if (__exception == null) return null;
            LateUpdateExceptions++;
            float now = Time.time;
            if (now - _lastExcLogAt > 60f)
            {
                _lastExcLogAt = now;
                MelonLogger.Warning($"[RTSA/SRV] contained {LateUpdateExceptions} exception(s) in Soldier.OnOrderedLateUpdate so far; latest: {__exception.GetType().Name}: {__exception.Message}");
            }
            return null;
        }

        internal static void ResetForNewRound() { _lastNoAt.Clear(); Skipped = 0; Evaluated = 0; LateUpdateExceptions = 0; AnimatorSkips = 0; }

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
