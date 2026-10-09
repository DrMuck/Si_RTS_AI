using HarmonyLib;
using MelonLoader;
using Silica;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Si_RTS_AI.Perception
{
    /// <summary>
    /// Observes construction: when each site STARTS, when it finishes BUILD-UP
    /// (the point it becomes usable as an anchor / prerequisite), and when it
    /// COMPLETES. All three are measured from the game's own ConstructionSite,
    /// never predicted.
    ///
    ///     [BUILD] Bio Cache start=21s at=(2645,1135)
    ///     [BUILD] Bio Cache up=41s at=(2645,1135) progress=20.0/30.0s
    ///     [BUILD] Bio Cache done=51s at=(2645,1135) took=30.0s (site said 30.0s)
    ///
    /// Why measured rather than computed. Two earlier designs were wrong:
    ///
    ///  * team.Structures does not contain a structure under construction — it
    ///    appears carrying its finished DisplayName only when construction
    ///    ENDS. So the first tick that saw it was the completion tick, and the
    ///    field once named StartedAtRoundT held a completion time. Adding a
    ///    build time on top double-counted: NarakaCity v0.8.31, Bio Caches
    ///    ordered roundT=21s, listed at 52s, gate opened at 72s.
    ///  * Deriving readiness as "order time + BuildUpTime read off the CDs"
    ///    fixes that arithmetic but is still arithmetic. It assumes the CD
    ///    values are the ones the game actually uses (a balance mod may rewrite
    ///    them) and that wall-clock tracks build progress (it does not when
    ///    server FPS drops).
    ///
    /// ConstructionSite carries the real numbers per instance: ProgressTime,
    /// ProgressTime01, TotalConstructionTime, ConstructionBuildUp01. ProgressTime
    /// is the game's own accumulated build time, so a stalled server slows the
    /// observation exactly as much as it slows the build. These are the live
    /// per-site properties — the identically named ConstructionBuildUp01 on
    /// ConstructionData is a static ratio, and was the earlier dead end.
    ///
    /// IsFunctional is not used and should not be: it turns true about a second
    /// after a structure starts, which is why every logged "took=" was 1s.
    /// </summary>
    internal static class BuildTimeline
    {
        const float TICK_S = 0.5f;

        class Site
        {
            public string Name = "";
            public Vector3 Pos;
            public float StartRoundT;
            public float BuiltUpRoundT = -1f;    // observed; -1 until it happens
            public float DoneRoundT = -1f;
            public float TotalS;                 // from the site instance
            public float BuildUpFrac = 1f;
            public float LastProgressS;
            public ConstructionSite Ref;
        }

        static readonly List<Site> _sites = new List<Site>(32);

        /// <summary>Structures seen complete in team.Structures — the fallback
        /// for anything whose site we never observed: pre-placed bases, the
        /// Nest, or a site started before this mod began ticking.</summary>
        static readonly Dictionary<Structure, Vector3> _finished = new Dictionary<Structure, Vector3>();

        /// <summary>One line per structure type, so a modded build time shows up
        /// in the log instead of silently shifting every gate.</summary>
        static readonly HashSet<string> _paramsLogged = new HashSet<string>();

        static float _lastTickAt;
        static int _hookCalls;

        internal static void ResetForNewRound()
        {
            _sites.Clear();
            _finished.Clear();
            _paramsLogged.Clear();
            _lastTickAt = 0f;
            _hookCalls = 0;
        }

        // ---- start: the game hands us the site the moment it is created ----

        /// <summary>Report at round start whether the target methods resolved,
        /// so a silently-unapplied patch is visible rather than looking like a
        /// game that never creates construction sites.</summary>
        internal static void ReportHookState()
        {
            try
            {
                var m1 = AccessTools.Method(typeof(ConstructionSiteManager),
                                            nameof(ConstructionSiteManager.RequestConstructionSite));
                var m2 = AccessTools.Method(typeof(Structure),
                                            nameof(Structure.OnConstructionSiteDeinitialized));
                MelonLogger.Msg($"[BUILD/HOOK] RequestConstructionSite resolved={m1 != null} " +
                                $"OnConstructionSiteDeinitialized resolved={m2 != null} " +
                                $"patched={(m1 != null && HarmonyLib.Harmony.GetPatchInfo(m1) != null)}");
            }
            catch (Exception ex) { MelonLogger.Warning("[BUILD/HOOK] report threw: " + ex.Message); }
        }

        [HarmonyPatch(typeof(ConstructionSiteManager), nameof(ConstructionSiteManager.RequestConstructionSite))]
        static class Patch_RequestConstructionSite
        {
            static void Postfix(ConstructionSite __result)
            { if (!Config.ModSwitches.Enabled) return;
                // Unconditional probe: this hook has produced no output at all
                // on the dedicated server, and the two candidate explanations —
                // never called, or called with names IsWatched rejects — need
                // different fixes. Log the raw name for the first few calls.
                try
                {
                    if (_hookCalls < 20)
                    {
                        _hookCalls++;
                        string raw = __result?.ConstructionData?.ObjectInfo?.DisplayName ?? "<null cd>";
                        MelonLogger.Msg($"[BUILD/HOOK] RequestConstructionSite #{_hookCalls} " +
                                        $"result={(__result == null ? "null" : "ok")} name='{raw}'");
                    }
                }
                catch (Exception ex) { MelonLogger.Warning("[BUILD/HOOK] probe threw: " + ex.Message); }

                try { NoteSiteCreated(__result); }
                catch (Exception ex) { MelonLogger.Warning("[BUILD] start hook threw: " + ex.Message); }
            }
        }

        static void NoteSiteCreated(ConstructionSite site)
        {
            if (site == null) return;
            var cd = site.ConstructionData;
            string name = cd?.ObjectInfo?.DisplayName ?? "?";
            if (!IsWatched(name)) return;

            float roundT;
            try { roundT = MapLayers.LayerReplay.CurrentRoundTime; } catch { return; }

            var s = new Site
            {
                Name = name,
                Pos = site.transform.position,
                StartRoundT = roundT,
                Ref = site,
            };
            try
            {
                s.TotalS = site.TotalConstructionTime;
                s.BuildUpFrac = site.ConstructionBuildUp01;
            }
            catch { }
            _sites.Add(s);

            Si_RTS_AI.AppendToRound(
                $"[BUILD] {name} start={roundT:F0}s at=({s.Pos.x:F0},{s.Pos.z:F0})");

            if (_paramsLogged.Add(name))
            {
                // What the game is really using this round. A gap against the
                // startup CD read means a balance mod moved the numbers.
                Si_RTS_AI.AppendToRound(
                    $"[BUILD/PARAM] {name} total={s.TotalS:F1}s buildUp01={s.BuildUpFrac:F3} " +
                    $"=> usable at {s.TotalS * s.BuildUpFrac:F1}s");
            }
        }

        // ---- finish: authoritative, from the game's own teardown ----

        [HarmonyPatch(typeof(Structure), nameof(Structure.OnConstructionSiteDeinitialized))]
        static class Patch_SiteDeinitialized
        {
            static void Postfix(ConstructionSite __0, bool __1)
            { if (!Config.ModSwitches.Enabled) return;
                try { NoteSiteGone(__0, __1); }
                catch (Exception ex) { MelonLogger.Warning("[BUILD] done hook threw: " + ex.Message); }
            }
        }

        static void NoteSiteGone(ConstructionSite site, bool completed)
        {
            if (site == null) return;
            for (int i = 0; i < _sites.Count; i++)
            {
                if (!ReferenceEquals(_sites[i].Ref, site)) continue;
                Finish(_sites[i], completed);
                return;
            }
        }

        static void Finish(Site s, bool completed)
        {
            if (s.DoneRoundT >= 0f) return;
            float roundT;
            try { roundT = MapLayers.LayerReplay.CurrentRoundTime; } catch { return; }
            s.DoneRoundT = roundT;
            s.Ref = null;
            if (!completed)
            {
                Si_RTS_AI.AppendToRound(
                    $"[BUILD] {s.Name} CANCELLED={roundT:F0}s at=({s.Pos.x:F0},{s.Pos.z:F0})");
                // A cancelled site must not answer "yes" to readiness queries.
                s.BuiltUpRoundT = -1f;
                _sites.Remove(s);
                return;
            }
            // Build-up may never have been sampled if the whole build fit inside
            // one poll interval; completion implies it.
            if (s.BuiltUpRoundT < 0f) s.BuiltUpRoundT = roundT;
            Si_RTS_AI.AppendToRound(
                $"[BUILD] {s.Name} done={roundT:F0}s at=({s.Pos.x:F0},{s.Pos.z:F0}) " +
                $"took={roundT - s.StartRoundT:F1}s (site said {s.TotalS:F1}s)");
        }

        // ---- progress poll: catches the build-up boundary ----

        internal static void Tick(Team team)
        {
            float roundT;
            try { roundT = MapLayers.LayerReplay.CurrentRoundTime; } catch { return; }

            // The finished-structure scan is PER TEAM and must run on every
            // call. Throttling above it meant only whichever team ticked first
            // in a cycle got scanned — a human team — so the Alien fallback
            // never populated and no Cyst fired for a whole round.
            ScanFinished(team, roundT);

            float now = Time.time;
            if (now - _lastTickAt < TICK_S) return;
            _lastTickAt = now;

            for (int i = _sites.Count - 1; i >= 0; i--)
            {
                var s = _sites[i];
                if (s.DoneRoundT >= 0f)
                {
                    // Keep completed sites as the readiness record, but not
                    // for the whole round.
                    if (roundT - s.DoneRoundT > 900f) _sites.RemoveAt(i);
                    continue;
                }
                var site = s.Ref;
                if (site == null) { Finish(s, true); continue; }

                float progressS, total, upFrac;
                try
                {
                    if (site.IsDestroyed) { Finish(s, true); continue; }
                    progressS = site.ProgressTime;
                    total     = site.TotalConstructionTime;
                    upFrac    = site.ConstructionBuildUp01;
                }
                catch { Finish(s, true); continue; }   // Il2Cpp object went away

                s.LastProgressS = progressS;
                if (total > 0f) s.TotalS = total;
                s.BuildUpFrac = upFrac;

                if (s.BuiltUpRoundT < 0f && progressS >= s.TotalS * s.BuildUpFrac)
                {
                    s.BuiltUpRoundT = roundT;
                    Si_RTS_AI.AppendToRound(
                        $"[BUILD] {s.Name} up={roundT:F0}s at=({s.Pos.x:F0},{s.Pos.z:F0}) " +
                        $"progress={progressS:F1}/{s.TotalS:F1}s");
                }
            }

        }

        /// <summary>
        /// Record structures seen COMPLETE. A structure enters team.Structures
        /// under its finished DisplayName only when construction ends, so first
        /// sight here is a completion event — never a start.
        /// </summary>
        static void ScanFinished(Team team, float roundT)
        {
            if (team == null) return;
            try
            {
                var structs = team.Structures;
                if (structs == null) return;
                for (int i = 0; i < structs.Count; i++)
                {
                    var st = structs[i];
                    if (st == null || st.ObjectInfo == null || st.IsDestroyed) continue;
                    string name = st.ObjectInfo.DisplayName ?? "";
                    if (!IsWatched(name)) continue;
                    if (_finished.ContainsKey(st)) continue;
                    var pos = st.transform.position;
                    _finished[st] = pos;
                    if ((team.name ?? "").Contains("Alien"))
                        Si_RTS_AI.AppendToRound(
                            $"[BUILD] {name} complete={roundT:F0}s at=({pos.x:F0},{pos.z:F0})");
                }
            }
            catch (Exception ex) { MelonLogger.Warning("[BUILD] scan threw: " + ex.Message); }
        }

        static bool IsWatched(string name)
        {
            return name == "Bio Cache" || name == "Lesser Spawning Cyst"
                || name == "Node" || name == "Quantum Cortex" || name == "Nest";
        }

        // ---- queries ----

        /// <summary>
        /// Has a structure of this name near here finished BUILD-UP — i.e. is it
        /// usable as an anchor or prerequisite now?
        ///
        /// Measured: either an observed site has passed its build-up fraction,
        /// or a finished structure of that name is standing there.
        /// </summary>
        internal static bool IsBuiltUpNear(string name, Vector3 pos, float radiusM)
        {
            return IsBuiltUpNear(name, pos, radiusM, out _);
        }

        /// <summary>
        /// As above, plus which signal answered: "site" (observed build-up),
        /// "complete" (standing structure), "order" (elapsed since the accepted
        /// Construct), or "none".
        ///
        /// The order path is a deliberate last resort, not the plan. It is the
        /// only one that assumes anything — that the CD build time is what the
        /// game uses and that wall-clock tracks build progress — but without it
        /// a silent failure in the observation layer means no Cyst is ever
        /// placed, which is exactly what v0.8.33 did for a full round.
        /// </summary>
        internal static bool IsBuiltUpNear(string name, Vector3 pos, float radiusM, out string via)
        {
            via = "none";
            float r2 = radiusM * radiusM;
            for (int i = 0; i < _sites.Count; i++)
            {
                var s = _sites[i];
                if (s.Name != name || s.BuiltUpRoundT < 0f) continue;
                float dx = s.Pos.x - pos.x, dz = s.Pos.z - pos.z;
                if (dx * dx + dz * dz <= r2) { via = "site"; return true; }
            }
            foreach (var kv in _finished)
            {
                var st = kv.Key;
                if (st == null || st.IsDestroyed) continue;
                if ((st.ObjectInfo?.DisplayName ?? "") != name) continue;
                float dx = kv.Value.x - pos.x, dz = kv.Value.z - pos.z;
                if (dx * dx + dz * dz <= r2) { via = "complete"; return true; }
            }

            float buildUpS = MeasuredBuildUpS(name);
            if (buildUpS > 0f
                && Faction.AlienConstruction.SecondsSinceOrderedNear(name, pos, radiusM) >= buildUpS)
            { via = "order"; return true; }

            return false;
        }

        /// <summary>
        /// Seconds from start to usable for this structure type. Prefers what a
        /// site of this type actually reported this round; falls back to the
        /// startup CD read only if none has been observed yet.
        /// </summary>
        internal static float MeasuredBuildUpS(string name)
        {
            for (int i = 0; i < _sites.Count; i++)
            {
                var s = _sites[i];
                if (s.Name == name && s.TotalS > 0f) return s.TotalS * s.BuildUpFrac;
            }
            if (name == "Bio Cache") return Planning.EcoSimulator.BC_BUILD_S;
            if (name == "Lesser Spawning Cyst") return Planning.EcoSimulator.CYST_BUILD_S;
            if (name == "Node") return Planning.EcoSimulator.NODE_BUILD_S;
            return 0f;
        }

        /// <summary>
        /// Is a structure of this name near here FULLY COMPLETE?
        ///
        /// Distinct from IsBuiltUpNear: a Cyst needs its Bio Cache finished, not
        /// merely built up. Measured on NarakaCity v0.8.34 — Bio Caches ordered
        /// at 12:57:38, Cyst attempts at +20s (the build-up boundary) all came
        /// back UnmetPrerequisite, and the first Success was at +30s, the full
        /// TotalConstructionTime. Gating on build-up cost eight refused orders
        /// and ~10s per Cyst.
        /// </summary>
        internal static bool IsCompleteNear(string name, Vector3 pos, float radiusM, out string via)
        {
            via = "none";
            float r2 = radiusM * radiusM;
            for (int i = 0; i < _sites.Count; i++)
            {
                var s = _sites[i];
                if (s.Name != name || s.DoneRoundT < 0f) continue;
                float dx = s.Pos.x - pos.x, dz = s.Pos.z - pos.z;
                if (dx * dx + dz * dz <= r2) { via = "site"; return true; }
            }
            foreach (var kv in _finished)
            {
                var st = kv.Key;
                if (st == null || st.IsDestroyed) continue;
                if ((st.ObjectInfo?.DisplayName ?? "") != name) continue;
                float dx = kv.Value.x - pos.x, dz = kv.Value.z - pos.z;
                if (dx * dx + dz * dz <= r2) { via = "complete"; return true; }
            }

            float totalS = MeasuredTotalS(name);
            if (totalS > 0f
                && Faction.AlienConstruction.SecondsSinceOrderedNear(name, pos, radiusM) >= totalS)
            { via = "order"; return true; }

            return false;
        }

        /// <summary>Start-to-fully-complete for this type, preferring what a
        /// site of this type reported this round.</summary>
        internal static float MeasuredTotalS(string name)
        {
            for (int i = 0; i < _sites.Count; i++)
            {
                var s = _sites[i];
                if (s.Name == name && s.TotalS > 0f) return s.TotalS;
            }
            // No site observed yet: the build-up time read off the CDs, scaled
            // by the ratio the CDs also give. BC is 20s up / 30s total.
            float up = MeasuredBuildUpS(name);
            return up > 0f ? up * 1.5f : 0f;
        }

        /// <summary>
        /// Is ANY structure of this name finished anywhere? The Cyst
        /// prerequisite is global — one finished Bio Cache on the map allows
        /// Cysts everywhere — so a per-site test only added delay.
        /// </summary>
        internal static bool AnyComplete(string name)
        {
            for (int i = 0; i < _sites.Count; i++)
                if (_sites[i].Name == name && _sites[i].DoneRoundT >= 0f) return true;
            foreach (var kv in _finished)
            {
                var st = kv.Key;
                if (st == null || st.IsDestroyed) continue;
                if ((st.ObjectInfo?.DisplayName ?? "") == name) return true;
            }
            return false;
        }

        /// <summary>Is a Bio Cache near this spot usable as a Cyst anchor?</summary>
        internal static bool BcCompleteNear(Vector3 pos, float radiusM, float roundT)
        {
            return IsCompleteNear("Bio Cache", pos, radiusM, out _);
        }

        /// <summary>
        /// Is a site of this name under construction near here and not yet
        /// done? Lets the planner wait for one rather than order a second.
        /// </summary>
        internal static bool IsBuildingNear(string name, Vector3 pos, float radiusM)
        {
            float r2 = radiusM * radiusM;
            for (int i = 0; i < _sites.Count; i++)
            {
                var s = _sites[i];
                if (s.Name != name || s.DoneRoundT >= 0f) continue;
                float dx = s.Pos.x - pos.x, dz = s.Pos.z - pos.z;
                if (dx * dx + dz * dz <= r2) return true;
            }
            return false;
        }
    }
}
