using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using MelonLoader;
using Silica;
using UnityEngine;
using Si_RTS_AI.Config;

namespace Si_RTS_AI.Faction
{
    /// <summary>
    /// PLACEMENT SEARCH BUDGET (v0.94.13): the server-wide limit on AI
    /// construction-placement searches, the vanilla commander's and ours.
    ///
    /// ConstructionPlacement.QueueFirstValidPlacementAroundPoint walks a square
    /// spiral of (2R+1)^2 cells x 4 rotations around the asked point at
    /// StepsPerSecond = max(ceil(num / maxTime), minStepsPerSecond). Each step is
    /// a main-thread raycast plus a structure-distance test against every
    /// structure on the map, and every frame a search with work waits for its
    /// raycast jobs. The callers ask for 600 m in 30-40 s (about 900 steps/s on
    /// the 15 m grid) or 120 m in 8 s on the 5 m alien grid (1 200 steps/s), and
    /// the 2026-11-03 perf rounds measured 300-530 ms per wall-second of the main
    /// thread in "placement storms" lasting 20-60 s (Silica_Performance/REPORT.md
    /// 2.4). minStepsPerSecond is only a floor.
    ///
    /// Rules, for every caller except the player's own placement preview
    /// (StrategyMode passes ignoreUnits AND useInputRotation; no AI does):
    ///  1. Step cap: an AI search runs at most placement.maxStepsPerSecond
    ///     (default 100) steps per second.
    ///  2. Concurrency cap by time-slicing: at most
    ///     placement.maxConcurrentSearches (default 2) AI searches step at once.
    ///     Every request starts at once (callbacks and the caller's state are
    ///     untouched); a search beyond the cap is PARKED (its GameObject set
    ///     inactive, which takes it out of ActiveConstructionPlacements without
    ///     pooling it) and the parked ones take turns in slices of
    ///     placement.sliceS (0.5 s), longest-waiting first. A search that lands on
    ///     its first rings finishes inside one slice; a long one keeps
    ///     progressing at full rate whenever it holds a slot.
    ///     Two designs were measured and dropped on 2026-11-03 (GreatErg, all
    ///     factions under the mod):
    ///       a. a FIFO of not-yet-started requests (the obvious concurrency cap):
    ///          two 50 s Sol searches held both slots while alien Node/Bio Cache
    ///          requests that resolve on ring 0 timed out in the queue (30 of 198
    ///          in 12 min), tripping the alien planner's back-off;
    ///       b. sharing a total step rate among all running searches: a search
    ///          stepping slower than the frame rate has frames with DoSteps < 1,
    ///          and on those the game does not clear its query lists, re-runs the
    ///          last batch and appends it again (ConstructionPlacement.cs
    ///          381-401, 468-483), so the cost per step grew from ~0.05 ms to
    ///          2.4 ms at 1-10 steps/s and the placement work rose to 100 ms/s.
    ///          A search must step at least about once per frame, which is why
    ///          slots are time-sliced instead.
    ///  3. Every request is answered: a search older than its limit (counted
    ///     from the request, parked time included) is ended through the game's
    ///     own failure path (RadialSteps = Radial). placement.maxSearchS (50 s)
    ///     for callers outside this mod: under vanilla's own 60 s "placement
    ///     never reported back" release (AIConstructionHandler), so a vanilla
    ///     commander never fires a second search while its first is alive.
    ///     placement.maxSearchOwnS (120 s) for Si_RTS_AI's own callers, which
    ///     hold their spot in flight until the search answers (HumanBuild,
    ///     AlienConstruction).
    ///
    /// Callbacks are wrapped, not replaced: the caller's success/failure code
    /// runs exactly as before, the wrapper only books the result. The game's
    /// three placement update methods are timed for the log (no control).
    /// With the mod switched off nothing acts and parked searches are released.
    /// </summary>
    internal static class PlacementBudget
    {
        // ---- config (placement.*), re-read every few seconds ----
        internal static int   MaxStepsPerSecond = 100;
        internal static int   MaxConcurrent     = 2;
        internal static float SliceS            = 0.5f;
        internal static float MaxSearchS        = 50f;
        internal static float MaxSearchOwnS     = 120f;
        static float _cfgReadAt = -999f;

        /// <summary>minStepsPerSecond our own call sites pass: the cap itself (the game's 300 floor would sit above it).</summary>
        internal static int StepsFloor => MaxStepsPerSecond > 0 ? Mathf.Min(MaxStepsPerSecond, 300) : 300;

        /// <summary>Upper bound on how long one of OUR requests can stay unanswered; our in-flight timeouts are sized on it.</summary>
        internal static float ResolveWithinS => MaxSearchOwnS > 0f ? MaxSearchOwnS + 5f : 600f;

        /// <summary>
        /// maxTime for a search of <paramref name="range"/> on <paramref name="grid"/> so that the game's
        /// num/maxTime lands at or below the step cap (never shorter than <paramref name="minTime"/>).
        /// </summary>
        internal static float MaxTimeFor(float range, float grid, float minTime)
        {
            if (grid <= 0.01f) return minTime;
            int r = Mathf.CeilToInt(range / grid);
            float num = (2f * r + 1f) * (2f * r + 1f) * 4f;
            int cap = MaxStepsPerSecond > 0 ? MaxStepsPerSecond : 300;
            return Mathf.Max(minTime, num / cap);
        }

        // ---- reflection ----
        static MethodInfo? _queueMethod;
        static int _iPreview = -1, _iTeam = -1, _iStruct = -1, _iPos = -1, _iGrid = -1, _iRange = -1,
                   _iMaxTime = -1, _iOk = -1, _iFail = -1, _iIgnoreUnits = -1, _iUseInputRot = -1;
        static AccessTools.FieldRef<ConstructionPlacement, int>?   _fSteps, _fRadialSteps, _fRadial;
        static AccessTools.FieldRef<ConstructionPlacement, bool>?  _fIgnoreUnits, _fUseInputRot;
        static AccessTools.FieldRef<ConstructionPlacement, float>? _fDoSteps;
        static bool _installed;
        static bool _reentry;

        // ---- state ----
        sealed class Req
        {
            public object[] Args = Array.Empty<object>();
            public float AskedAt;
            public string Name = "?", TeamName = "?", Origin = "?";
            public bool Own;          // asked by Si_RTS_AI itself
            public Vector3 Pos;
            public float Range, Grid, MaxTime;
            public bool InputRot;
            public Action<ConstructionData, Team, Structure, Vector3, Quaternion>? Ok;
            public Action<ConstructionData, Team, Structure>? Fail;
            public ConstructionPlacement? P;
            public float StartedAt = -1f;
            public int StepsTotal, RateAsked, Rate;
            public bool Done, Truncated;
            public bool Parked;
            public float ParkedAt, SliceStart, ParkedTotal;
            public int Parks;
        }

        static readonly List<Req> _live = new List<Req>();

        // ---- time measurement (log only) ----
        static long  _cpTicks;
        static float _sampleAt = -1f;
        static float _wMsSum, _wMsMax; static int _wMsN;

        // ---- stats (per summary window and per round) ----
        static int _wStarted, _wOk, _wFail, _wTrunc, _wClamped, _wMaxLive, _wMaxParked, _wParks;
        static float _wDurSum, _wParkSum; static int _wDurN;
        static float _summaryAt;
        internal static int RoundStarted, RoundOk, RoundFail, RoundTrunc, RoundClamped, RoundParks;

        internal static void Install(HarmonyLib.Harmony harmony)
        {
            try
            {
                _queueMethod = AccessTools.Method(typeof(ConstructionPlacement), "QueueFirstValidPlacementAroundPoint");
                if (_queueMethod == null) { MelonLogger.Warning("[PLACE/BUDGET] QueueFirstValidPlacementAroundPoint not found - budget inactive"); return; }
                var ps = _queueMethod.GetParameters();
                for (int i = 0; i < ps.Length; i++)
                {
                    switch (ps[i].Name)
                    {
                        case "constructionPreview": _iPreview = i; break;
                        case "callingTeam":         _iTeam = i; break;
                        case "callingStructure":    _iStruct = i; break;
                        case "initialPosition":     _iPos = i; break;
                        case "gridSizeXZ":          _iGrid = i; break;
                        case "range":               _iRange = i; break;
                        case "maxTime":             _iMaxTime = i; break;
                        case "callbackSuccess":     _iOk = i; break;
                        case "callbackFailed":      _iFail = i; break;
                        case "ignoreUnits":         _iIgnoreUnits = i; break;
                        case "useInputRotation":    _iUseInputRot = i; break;
                    }
                }
                if (_iOk < 0 || _iFail < 0 || _iIgnoreUnits < 0 || _iUseInputRot < 0 || _iPreview < 0 || _iTeam < 0)
                { MelonLogger.Warning("[PLACE/BUDGET] unexpected parameter list - budget inactive"); return; }

                _fSteps       = AccessTools.FieldRefAccess<ConstructionPlacement, int>("StepsPerSecond");
                _fRadialSteps = AccessTools.FieldRefAccess<ConstructionPlacement, int>("RadialSteps");
                _fRadial      = AccessTools.FieldRefAccess<ConstructionPlacement, int>("Radial");
                _fIgnoreUnits = AccessTools.FieldRefAccess<ConstructionPlacement, bool>("IgnoreUnits");
                _fUseInputRot = AccessTools.FieldRefAccess<ConstructionPlacement, bool>("UseInputRotation");
                _fDoSteps     = AccessTools.FieldRefAccess<ConstructionPlacement, float>("DoSteps");

                harmony.Patch(_queueMethod, prefix: new HarmonyMethod(typeof(PlacementBudget).GetMethod(nameof(Prefix), BindingFlags.Static | BindingFlags.NonPublic)));
                var tPre  = new HarmonyMethod(typeof(PlacementBudget).GetMethod(nameof(TimePrefix), BindingFlags.Static | BindingFlags.NonPublic));
                var tPost = new HarmonyMethod(typeof(PlacementBudget).GetMethod(nameof(TimePostfix), BindingFlags.Static | BindingFlags.NonPublic));
                foreach (var name in new[] { "UpdateAllActivePlacementsEnqueueSnappedRotation", "UpdateAllActivePlacementsEnqueueGroundSlopeCheck", "UpdateFinishAllActivePlacements" })
                {
                    var m = AccessTools.Method(typeof(ConstructionPlacement), name);
                    if (m != null) harmony.Patch(m, prefix: tPre, postfix: tPost);
                }
                _installed = true;
                ReadConfig(true);
                RtsLog.Msg($"[PLACE/BUDGET] installed on {ps.Length}-parameter QueueFirstValidPlacementAroundPoint: " +
                                $"maxStepsPerSecond {MaxStepsPerSecond}, maxConcurrentSearches {MaxConcurrent} (slices of {SliceS:F1}s), " +
                                $"maxSearchS {MaxSearchS:F0}, maxSearchOwnS {MaxSearchOwnS:F0}");
            }
            catch (Exception ex) { MelonLogger.Warning("[PLACE/BUDGET] install failed: " + ex); _installed = false; }
        }

        static void ReadConfig(bool force)
        {
            float now = Time.realtimeSinceStartup;
            if (!force && now - _cfgReadAt < 5f) return;
            _cfgReadAt = now;
            try
            {
                MaxStepsPerSecond = Mathf.Max(0, RtsaiConfig.Int("placement.maxStepsPerSecond", 100));
                MaxConcurrent     = Mathf.Max(0, RtsaiConfig.Int("placement.maxConcurrentSearches", 2));
                SliceS            = Mathf.Max(0.05f, RtsaiConfig.Float("placement.sliceS", 0.5f));
                MaxSearchS        = Mathf.Max(0f, RtsaiConfig.Float("placement.maxSearchS", 50f));
                MaxSearchOwnS     = Mathf.Max(0f, RtsaiConfig.Float("placement.maxSearchOwnS", 120f));
            }
            catch { }
        }

        static bool Acting => _installed && ModSwitches.Enabled;

        static bool IsPlayerArgs(object[] a)
        {
            try { return (bool)a[_iIgnoreUnits] && (bool)a[_iUseInputRot]; } catch { return false; }
        }

        static bool IsPlayer(ConstructionPlacement p)
        {
            try { return _fIgnoreUnits!(p) && _fUseInputRot!(p); } catch { return false; }
        }

        /// <summary>AI searches currently stepping (in the game's active list), ours or not.</summary>
        internal static int CountActiveAi()
        {
            int n = 0;
            var list = ConstructionPlacement.ActiveConstructionPlacements;
            for (int i = 0; i < list.Count; i++)
            {
                var p = list[i];
                if (p == null) continue;
                if (!IsPlayer(p)) n++;
            }
            return n;
        }

        static void TimePrefix(out long __state) { __state = System.Diagnostics.Stopwatch.GetTimestamp(); }
        static void TimePostfix(long __state) { _cpTicks += System.Diagnostics.Stopwatch.GetTimestamp() - __state; }

        // ------------------------------------------------------------------
        // Harmony prefix: every call to QueueFirstValidPlacementAroundPoint.
        // ------------------------------------------------------------------
        static bool Prefix(object[] __args)
        {
            if (_reentry || !Acting) return true;
            try
            {
                if (IsPlayerArgs(__args)) return true;
                Start(MakeReq(__args));
                return false;
            }
            catch (TargetInvocationException tie) when (tie.InnerException != null)
            {
                // The game's own method threw: the caller sees the same exception it always did.
                throw tie.InnerException;
            }
            catch (Exception ex)
            {
                // Our bookkeeping failed before the original ran: let the game do it unbudgeted.
                MelonLogger.Warning("[PLACE/BUDGET] prefix threw, passing the call through: " + ex.Message);
                return true;
            }
        }

        static Req MakeReq(object[] args)
        {
            var r = new Req { Args = (object[])args.Clone(), AskedAt = Time.time };
            if (_iPos >= 0 && args[_iPos] is Vector3 v) r.Pos = v;
            if (_iGrid >= 0 && args[_iGrid] is float g) r.Grid = g;
            if (_iRange >= 0 && args[_iRange] is float rg) r.Range = rg;
            if (_iMaxTime >= 0 && args[_iMaxTime] is float mt) r.MaxTime = mt;
            try { r.InputRot = (bool)args[_iUseInputRot]; } catch { }
            try { r.Name = (args[_iPreview] as ConstructionPreview)?.ConstructionData?.ObjectInfo?.DisplayName ?? "?"; } catch { }
            try { var t = args[_iTeam] as Team; r.TeamName = t != null ? t.name : "?"; } catch { }
            r.Origin = CallerName(out r.Own);
            r.Ok   = args[_iOk] as Action<ConstructionData, Team, Structure, Vector3, Quaternion>;
            r.Fail = args[_iFail] as Action<ConstructionData, Team, Structure>;
            var req = r;
            r.Args[_iOk] = new Action<ConstructionData, Team, Structure, Vector3, Quaternion>((cd, t, s, pos, rot) =>
            {
                Finish(req, true, pos);
                req.Ok?.Invoke(cd, t, s, pos, rot);
            });
            r.Args[_iFail] = new Action<ConstructionData, Team, Structure>((cd, t, s) =>
            {
                Finish(req, false, Vector3.zero);
                req.Fail?.Invoke(cd, t, s);
            });
            return r;
        }

        /// <summary>The first frame outside the game's placement code and Harmony: who asked.</summary>
        static string CallerName(out bool own)
        {
            own = false;
            try
            {
                var st = new System.Diagnostics.StackTrace(2, false);
                for (int i = 0; i < st.FrameCount && i < 8; i++)
                {
                    var m = st.GetFrame(i)?.GetMethod();
                    var t = m?.DeclaringType;
                    if (t == null) continue;
                    while (t.DeclaringType != null) t = t.DeclaringType;   // lambdas: <>c__DisplayClass -> owner
                    if (t == typeof(ConstructionPlacement) || t == typeof(PlacementBudget)) continue;
                    string n = t.Name;
                    if (n.StartsWith("DMD") || n.Contains("Harmony") || n.Contains("Patch")) continue;
                    own = (t.Namespace ?? "").StartsWith("Si_RTS_AI", StringComparison.Ordinal);
                    return n;
                }
            }
            catch { }
            return "?";
        }

        /// <summary>Run the game's method now; clamp the new search's rate; park it if the slots are taken.</summary>
        static void Start(Req r)
        {
            var list = ConstructionPlacement.ActiveConstructionPlacements;
            int before = list.Count;
            _reentry = true;
            try { _queueMethod!.Invoke(null, r.Args); }
            finally { _reentry = false; }

            r.StartedAt = Time.time;
            r.SliceStart = r.StartedAt;
            ConstructionPlacement? p = null;
            if (list.Count == before + 1) p = list[list.Count - 1];
            r.P = p;
            int activeOthers = CountActiveAi() - (p != null ? 1 : 0);
            if (p != null)
            {
                int rs = _fRadialSteps!(p);
                r.StepsTotal = (2 * rs + 1) * (2 * rs + 1) * (r.InputRot ? 1 : 4);
                r.RateAsked = _fSteps!(p);
                r.Rate = r.RateAsked;
                if (MaxStepsPerSecond > 0 && r.Rate > MaxStepsPerSecond)
                {
                    _fSteps(p) = MaxStepsPerSecond;
                    r.Rate = MaxStepsPerSecond;
                    _wClamped++; RoundClamped++;
                }
                _live.Add(r);
                // The new search is the LAST element of the active list, so taking it out is safe even
                // when this runs inside the game's reverse loop in UpdateFinishAllActivePlacements
                // (a placement callback that asks for the next structure).
                if (MaxConcurrent > 0 && activeOthers >= MaxConcurrent) Park(r);
            }
            else ClampAll();   // could not tell which instance is ours: clamp every AI search (idempotent)

            _wStarted++; RoundStarted++;
            int live = _live.Count;
            if (live > _wMaxLive) _wMaxLive = live;
            Si_RTS_AI.AppendToRound(
                $"[PLACE/BUDGET] start team={r.TeamName} {r.Name} by={r.Origin}{(r.Own ? "" : "(foreign)")} at=({r.Pos.x:F0},{r.Pos.z:F0}) range={r.Range:F0}m grid={r.Grid:F0}m " +
                $"rot={(r.InputRot ? "input" : "x4")} steps={r.StepsTotal} rate={r.RateAsked}->{r.Rate}/s maxTime={r.MaxTime:F1}s " +
                $"stepping={activeOthers + (r.Parked ? 0 : 1)} parked={(r.Parked ? "yes" : "no")} live={live}");
        }

        static void Park(Req r)
        {
            if (r.Parked || r.P == null || r.Done) return;
            try { r.P.gameObject.SetActive(false); } catch { return; }
            r.Parked = true; r.ParkedAt = Time.time; r.Parks++;
            _wParks++; RoundParks++;
        }

        static void Unpark(Req r)
        {
            if (!r.Parked || r.P == null) return;
            float now = Time.time;
            r.ParkedTotal += now - r.ParkedAt;
            _wParkSum += now - r.ParkedAt;
            r.Parked = false; r.SliceStart = now;
            try
            {
                // Step on the first frame back: the game clears a search's query lists only on a
                // frame where it steps, and would otherwise re-run the batch from before parking.
                if (_fDoSteps!(r.P) < 1f) _fDoSteps(r.P) = 1f;
                r.P.gameObject.SetActive(true);
            }
            catch { }
        }

        static void ClampAll()
        {
            if (MaxStepsPerSecond <= 0) return;
            var list = ConstructionPlacement.ActiveConstructionPlacements;
            for (int i = 0; i < list.Count; i++)
            {
                var p = list[i];
                if (p == null || IsPlayer(p)) continue;
                if (_fSteps!(p) > MaxStepsPerSecond) { _fSteps(p) = MaxStepsPerSecond; _wClamped++; RoundClamped++; }
            }
        }

        static void Finish(Req r, bool ok, Vector3 landed)
        {
            if (r.Done) return;
            r.Done = true;
            float now = Time.time;
            float dur = r.StartedAt >= 0f ? now - r.StartedAt : 0f;
            if (ok) { _wOk++; RoundOk++; }
            else if (r.Truncated) { _wTrunc++; RoundTrunc++; }
            else { _wFail++; RoundFail++; }
            _wDurSum += dur; _wDurN++;
            int ring = -1, rings = -1;
            try { if (r.P != null) { ring = _fRadial!(r.P); rings = _fRadialSteps!(r.P); } } catch { }
            string res = ok ? "ok" : r.Truncated ? "truncated" : "fail";
            float drift = ok ? Vector2.Distance(new Vector2(landed.x, landed.z), new Vector2(r.Pos.x, r.Pos.z)) : 0f;
            Si_RTS_AI.AppendToRound(
                $"[PLACE/BUDGET] done team={r.TeamName} {r.Name} by={r.Origin} result={res} search={dur:F1}s parked={r.ParkedTotal:F1}s/{r.Parks}x " +
                $"ring={ring}/{rings} grid={r.Grid:F0}m{(ok ? $" landed={drift:F0}m" : "")}");
        }

        // ------------------------------------------------------------------
        // Per frame, from Si_RTS_AI.OnUpdate (also while the mod is off).
        // ------------------------------------------------------------------
        internal static void Pump()
        {
            if (!_installed) return;
            if (!ModSwitches.Enabled)
            {
                if (_live.Count > 0) ReleaseAll("mod switched off");
                return;
            }
            ReadConfig(false);
            float now = Time.time;
            SampleTime();

            // Forget finished or destroyed searches; end the over-age ones through the game's failure path.
            int parked = 0;
            for (int i = _live.Count - 1; i >= 0; i--)
            {
                var r = _live[i];
                if (r.Done || r.P == null) { _live.RemoveAt(i); continue; }
                float limit = r.Own ? MaxSearchOwnS : MaxSearchS;
                if (limit > 0f && !r.Truncated && now - r.AskedAt > limit)
                {
                    r.Truncated = true;
                    try { _fRadialSteps!(r.P) = Mathf.Max(0, _fRadial!(r.P)); } catch { }
                    if (r.Parked) Unpark(r);   // it must step once to report the failure
                }
                if (r.Parked) parked++;
            }
            if (parked > _wMaxParked) _wMaxParked = parked;

            if (MaxConcurrent <= 0)
            {
                if (parked > 0) foreach (var r in _live) if (r.Parked) Unpark(r);
            }
            else if (parked > 0)
            {
                // Free slots go to the longest-waiting parked searches.
                int stepping = CountActiveAi();
                while (stepping < MaxConcurrent)
                {
                    var next = LongestParked();
                    if (next == null) break;
                    Unpark(next); stepping++;
                }
                // Slices: a search that has stepped for SliceS while others wait gives its slot to the longest-waiting.
                for (int i = 0; i < _live.Count; i++)
                {
                    var r = _live[i];
                    if (r.Parked || r.Done || r.Truncated || now - r.SliceStart < SliceS) continue;
                    var next = LongestParked();
                    if (next == null) break;
                    Park(r);
                    Unpark(next);
                }
            }

            if (now - _summaryAt >= 60f)
            {
                if (_wStarted + _wOk + _wFail + _wTrunc > 0)
                    RtsLog.Msg($"[PLACE/BUDGET] last {now - _summaryAt:F0}s: started {_wStarted}, ok {_wOk} fail {_wFail} truncated {_wTrunc}; " +
                                    $"avg search {(_wDurN > 0 ? _wDurSum / _wDurN : 0f):F1}s; max live {_wMaxLive}, max parked {_wMaxParked}, parks {_wParks} ({_wParkSum:F0}s parked in total); " +
                                    $"asked above cap {_wClamped}; placement work {(_wMsN > 0 ? _wMsSum / _wMsN : 0f):F1} ms/s avg, {_wMsMax:F0} max " +
                                    $"(cap {MaxStepsPerSecond}/search x {MaxConcurrent})");
                _summaryAt = now;
                _wStarted = _wOk = _wFail = _wTrunc = _wClamped = _wMaxLive = _wMaxParked = _wParks = 0;
                _wDurSum = _wParkSum = 0f; _wDurN = 0;
                _wMsSum = _wMsMax = 0f; _wMsN = 0;
            }
        }

        static Req? LongestParked()
        {
            Req? best = null;
            for (int i = 0; i < _live.Count; i++)
            {
                var r = _live[i];
                if (!r.Parked || r.Done || r.P == null) continue;
                if (best == null || r.ParkedAt < best.ParkedAt) best = r;
            }
            return best;
        }

        /// <summary>Once a second: ms of main thread spent in the game's placement updates (for the log).</summary>
        static void SampleTime()
        {
            float now = Time.realtimeSinceStartup;
            if (_sampleAt < 0f) { _sampleAt = now; _cpTicks = 0; return; }
            float win = now - _sampleAt;
            if (win < 1f) return;
            float msPerS = _cpTicks * 1000f / System.Diagnostics.Stopwatch.Frequency / win;
            _wMsSum += msPerS; _wMsN++; if (msPerS > _wMsMax) _wMsMax = msPerS;
            _sampleAt = now; _cpTicks = 0;
        }

        static void ReleaseAll(string why)
        {
            int n = 0;
            foreach (var r in _live) if (r.Parked && r.P != null) { Unpark(r); n++; }
            _live.Clear();
            if (n > 0) RtsLog.Msg($"[PLACE/BUDGET] {why}: released {n} parked search(es)");
        }

        /// <summary>New round or map: forget the old round's searches (the scene took their objects).</summary>
        internal static void ResetForNewRound()
        {
            _live.Clear();
            RoundStarted = RoundOk = RoundFail = RoundTrunc = RoundClamped = RoundParks = 0;
            _summaryAt = Time.time;
            ReadConfig(true);
        }

        internal static string Summary() => RoundStarted == 0 ? "" :
            $"  placement budget: started {RoundStarted}, ok {RoundOk}, fail {RoundFail}, truncated {RoundTrunc}, parks {RoundParks}, rate clamped {RoundClamped}\n";
    }
}
