using HarmonyLib;
using MelonLoader;
using System;
using System.Collections.Generic;
using System.Reflection;

namespace Si_RTS_AI.Perception
{
    /// <summary>
    /// PROBES INTO THE GAME'S OWN FRAME, FOR THE ROUND WHERE THE FPS SINKS.
    ///
    /// DrMuck, 2026-09-05: "Need to understand in depth what influences the fps
    /// drop most significantly." The budget line says our mod is under one
    /// percent of the wall clock, and Si_ModPerfMonitor times only methods that
    /// some mod has patched. So this patches the suspects with a prefix that
    /// does nothing, which puts them on the profiler's list, and times the
    /// three that matter most itself:
    ///
    ///   GameManager.SimulatePhysics — the game steps physics in a while loop
    ///     with NO cap on steps per frame (decompiled 0.9.46): a frame that
    ///     runs long owes more fixed steps next frame, each step runs every
    ///     agent's OrderedFixedUpdate, and the loop never drops a step. That
    ///     is the shape of a death spiral, and the fixed-steps-per-frame count
    ///     is the test for it.
    ///   OrderedUpdateManager.CallFixedUpdate — everything the game does per
    ///     fixed step besides Physics.Simulate itself.
    ///   OrderedUpdateManager.Update — everything the game does per frame in
    ///     its ordered update list (agents, structures, sensors).
    ///
    /// Extra probes come from rtsai.json "perfProbes": "Type:Method,Type:Method".
    /// The game's classes mostly sit in the global namespace; a bare type name
    /// is searched across all loaded assemblies.
    /// </summary>
    internal static class PerfProbes
    {
        static bool _done;
        internal static int    FixedStepsThisFrame, FixedStepsMaxPerFrame;
        internal static long   FixedStepsTotal;
        internal static double PhysicsMs, FixedMs, OrderedUpdateMs;
        static readonly System.Diagnostics.Stopwatch _swPhys = new System.Diagnostics.Stopwatch();
        static readonly System.Diagnostics.Stopwatch _swFixed = new System.Diagnostics.Stopwatch();
        static readonly System.Diagnostics.Stopwatch _swUpd = new System.Diagnostics.Stopwatch();

        internal static void Init(HarmonyLib.Harmony harmony)
        {
            if (_done) return;
            _done = true;
            if (harmony == null) return;
            var attached = new List<string>();

            if (Planning.RtsaiConfig.Bool("perfTimers", true))
            {
                Timed(harmony, "GameManager", "SimulatePhysics", nameof(PhysPre), nameof(PhysPost), attached);
                Timed(harmony, "OrderedUpdateManager", "CallFixedUpdate", nameof(FixedPre), nameof(FixedPost), attached);
                Timed(harmony, "OrderedUpdateManager", "Update", nameof(UpdPre), nameof(UpdPost), attached);
            }

            string list = Planning.RtsaiConfig.Str("perfProbes", "");
            foreach (var raw in list.Split(','))
            {
                var spec = raw.Trim();
                if (spec.Length == 0) continue;
                int colon = spec.LastIndexOf(':');
                if (colon <= 0) { MelonLogger.Warning("[RTSA/PROBE] bad probe spec: " + spec); continue; }
                string typeName = spec.Substring(0, colon), method = spec.Substring(colon + 1);
                try
                {
                    var m = FindMethod(typeName, method);
                    if (m == null) { MelonLogger.Warning("[RTSA/PROBE] not found: " + spec); continue; }
                    harmony.Patch(m, prefix: new HarmonyMethod(typeof(PerfProbes).GetMethod(nameof(EmptyPrefix), BindingFlags.Static | BindingFlags.NonPublic)));
                    attached.Add(spec);
                }
                catch (Exception ex) { MelonLogger.Warning("[RTSA/PROBE] " + spec + ": " + ex.Message); }
            }
            MelonLogger.Msg("[RTSA/PROBE] attached " + attached.Count + ": " + string.Join(", ", attached));
        }

        static void Timed(HarmonyLib.Harmony harmony, string type, string method, string pre, string post, List<string> attached)
        {
            try
            {
                var m = FindMethod(type, method);
                if (m == null) { MelonLogger.Warning("[RTSA/PROBE] not found: " + type + ":" + method); return; }
                harmony.Patch(m,
                    prefix:  new HarmonyMethod(typeof(PerfProbes).GetMethod(pre,  BindingFlags.Static | BindingFlags.NonPublic)),
                    postfix: new HarmonyMethod(typeof(PerfProbes).GetMethod(post, BindingFlags.Static | BindingFlags.NonPublic)));
                attached.Add(type + ":" + method + " (timed)");
            }
            catch (Exception ex) { MelonLogger.Warning("[RTSA/PROBE] " + type + ":" + method + ": " + ex.Message); }
        }

        static MethodInfo FindMethod(string typeName, string method)
        {
            var t = FindType(typeName);
            if (t == null) return null;
            var flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            MethodInfo best = null;
            foreach (var m in t.GetMethods(flags))
            {
                if (m.Name != method) continue;
                if (best == null || m.GetParameters().Length < best.GetParameters().Length) best = m;
            }
            return best;
        }

        static Type FindType(string name)
        {
            var t = AccessTools.TypeByName(name);
            if (t != null) return t;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try { types = asm.GetTypes(); } catch { continue; }
                for (int i = 0; i < types.Length; i++)
                    if (types[i].Name == name || types[i].FullName == name) return types[i];
            }
            return null;
        }

        static void EmptyPrefix() { }

        static void PhysPre()  { _swPhys.Restart(); }
        static void PhysPost() { _swPhys.Stop(); PhysicsMs += _swPhys.Elapsed.TotalMilliseconds; }
        static void FixedPre() { FixedStepsThisFrame++; FixedStepsTotal++; _swFixed.Restart(); }
        static void FixedPost(){ _swFixed.Stop(); FixedMs += _swFixed.Elapsed.TotalMilliseconds; }
        static void UpdPre()   { _swUpd.Restart(); }
        static void UpdPost()  { _swUpd.Stop(); OrderedUpdateMs += _swUpd.Elapsed.TotalMilliseconds; }

        /// <summary>Call once per frame from the main loop.</summary>
        internal static void OnFrame()
        {
            if (FixedStepsThisFrame > FixedStepsMaxPerFrame) FixedStepsMaxPerFrame = FixedStepsThisFrame;
            FixedStepsThisFrame = 0;
        }

        /// <summary>The last minute in one clause, and the counters reset.</summary>
        internal static string TakeMinute()
        {
            string s = $"physics {PhysicsMs:F0} ms in {FixedStepsTotal} fixed steps (max {FixedStepsMaxPerFrame}/frame, " +
                       $"ordered-fixed {FixedMs:F0} ms), ordered-update {OrderedUpdateMs:F0} ms";
            PhysicsMs = 0; FixedMs = 0; OrderedUpdateMs = 0; FixedStepsTotal = 0; FixedStepsMaxPerFrame = 0;
            return s;
        }
    }
}
