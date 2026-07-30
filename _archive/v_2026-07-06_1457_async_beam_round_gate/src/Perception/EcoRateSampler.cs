using HarmonyLib;
using MelonLoader;
using Silica;
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEngine;

namespace Si_RTS_AI.Perception
{
    /// <summary>
    /// Eco-rate observability. Samples each managed team's TotalResources on a
    /// wall-clock cadence and logs deltas to the round file. Used as a soak-test
    /// benchmark: "how much cash per second does this AI extract on this map spawn?"
    ///
    /// One line per team per interval:
    ///   [ECO] team=Sol t=45.0s cash=8500 delta=+320 rate=64/s peak=110/s cumulIncome=15400
    ///
    /// Tick() is called from every faction handler (HumanConstruction, AlienConstruction).
    /// Per-team state deduped by Team reference — first Tick() for a team in a round
    /// initializes the baseline.
    /// </summary>
    internal static class EcoRateSampler
    {
        // Sample every N seconds of real wall time. Kept short so a big income
        // burst can't skip past DRAIN_TRIGGER_PCT into game-side cap-clamping
        // (which silently discards income the sampler can't recover).
        const float SAMPLE_INTERVAL_S = 2f;

        // Auto-drain: if team is >75% full AND >=20k stored, drain down to 70%.
        // 20k floor deliberately protects early game — the user's spec is that
        // drain only kicks in once the team is meaningfully into mid-game, not
        // on starter resources. If a scenario needs to exercise drain with a
        // smaller setup, add BCs until team cap exceeds the 20k floor.
        const int   DRAIN_MIN_TOTAL       = 20000;
        const float DRAIN_TRIGGER_PCT     = 0.75f;
        const float DRAIN_TARGET_PCT      = 0.70f;

        // Reflection cache for the TotalResources backing field (see
        // HumanConstruction.ConstructFree for the field-name candidates).
        static System.Reflection.FieldInfo? _totalResourcesField;
        static System.Reflection.FieldInfo? ResolveTotalResourcesField()
        {
            if (_totalResourcesField != null) return _totalResourcesField;
            try
            {
                // The real backing field is m_StoredResources — the previous name list
                // ("<TotalResources>k__BackingField" etc.) never matched, so drain has
                // been silently no-op-ing since v0.7.5x. Confirmed by field enumeration
                // at runtime on the shipping SilicaCore.dll.
                foreach (var name in new[] { "m_StoredResources", "<TotalResources>k__BackingField", "m_TotalResources", "_totalResources" })
                {
                    var fi = typeof(Team).GetField(name,
                        System.Reflection.BindingFlags.Instance |
                        System.Reflection.BindingFlags.NonPublic |
                        System.Reflection.BindingFlags.Public);
                    if (fi != null && fi.FieldType == typeof(int)) { _totalResourcesField = fi; return fi; }
                }
            }
            catch { }
            return null;
        }

        // Snapshot cash at these round-elapsed times so soak runs across maps/AI
        // configs are directly comparable. Chosen to span "early eco" (60s),
        // "mid-eco stability" (300s), and "late eco / saturation" (900-1200s).
        static readonly int[] CheckpointTimesS = { 60, 120, 180, 300, 600, 900, 1200 };

        class TeamEco
        {
            public string Name = "?";
            public float LastSampleAt;
            public int   LastResources;
            public int   FirstResources;    // baseline for cumulative income
            public float FirstSampleAt;
            public int   TotalIncome;       // sum of positive deltas across the round
            public int   PeakRatePerSec;
            public readonly Dictionary<int, int> CashAtCheckpoint = new Dictionary<int, int>();
        }

        static readonly Dictionary<Team, TeamEco> _perTeam = new Dictionary<Team, TeamEco>();

        /// <summary>
        /// Total gross income earned by the team since round start, including any
        /// amount drained out of team storage (which IS income — we just moved it
        /// out to keep the eco flowing). Called by TelemetryServer for the
        /// browser viewer's cumulative-income line.
        /// </summary>
        internal static int GetCumulativeIncome(Team team)
        {
            if (team == null) return 0;
            return _perTeam.TryGetValue(team, out var e) ? e.TotalIncome : 0;
        }

        internal static void Tick(Team team)
        {
            if (team == null) return;
            try
            {
                if (!_perTeam.TryGetValue(team, out var eco))
                {
                    eco = new TeamEco { Name = team.name ?? "?" };
                    _perTeam[team] = eco;
                    int cash0 = TryGetResources(team);
                    eco.FirstResources = cash0;
                    eco.LastResources  = cash0;
                    eco.FirstSampleAt  = Time.time;
                    eco.LastSampleAt   = Time.time;
                    return;
                }

                float now = Time.time;
                if (now - eco.LastSampleAt < SAMPLE_INTERVAL_S) return;

                // Ordering matters: sample PRE-drain so `delta` is the honest
                // income earned since the last sample, then drain, then save
                // POST-drain cash as the new baseline. The drained amount IS
                // income (we just moved it out of storage), so it counts too.
                int cashPre    = TryGetResources(team);
                float dt       = now - eco.LastSampleAt;
                int delta      = cashPre - eco.LastResources;
                int rate       = dt > 0.01f ? Mathf.RoundToInt(delta / dt) : 0;
                if (rate > eco.PeakRatePerSec) eco.PeakRatePerSec = rate;
                if (delta > 0) eco.TotalIncome += delta;

                int drainedNow = TryDrain(team);
                if (drainedNow > 0) eco.TotalIncome += drainedNow;
                int cashPost   = drainedNow > 0 ? TryGetResources(team) : cashPre;

                float elapsed = now - eco.FirstSampleAt;

                // Checkpoint recording — first sample past each threshold captures
                // cumulIncome at that elapsed time. Store cumulIncome instead of raw
                // cash so the drain doesn't affect the checkpoint value (raw cash
                // fluctuates with drain, cumulIncome is monotonic).
                foreach (int cp in CheckpointTimesS)
                {
                    if (elapsed >= cp && !eco.CashAtCheckpoint.ContainsKey(cp))
                        eco.CashAtCheckpoint[cp] = eco.TotalIncome;
                }

                Si_RTS_AI.AppendToRound(
                    $"[ECO] team={eco.Name} t={elapsed:F0}s cash={cashPost} " +
                    $"delta={(delta >= 0 ? "+" : "")}{delta} rate={rate}/s " +
                    $"peak={eco.PeakRatePerSec}/s cumulIncome={eco.TotalIncome}" +
                    (drainedNow > 0 ? $" drained={drainedNow}" : ""));

                eco.LastResources = cashPost;
                eco.LastSampleAt  = now;
            }
            catch (Exception ex) { MelonLogger.Warning($"[ECO] Sampler threw: {ex.Message}"); }
        }

        static int TryGetResources(Team team)
        {
            try { return team.TotalResources; } catch { return 0; }
        }

        // Cache the resource-mutation method chosen the first time we successfully
        // change TotalResources. Il2Cpp interop hides these from C# reflection —
        // HarmonyLib.AccessTools uses different lookup paths that reach the native
        // side. We try SetResources → set_TotalResources → AddResources in that
        // order.
        static MethodInfo? _drainMi;
        static string?     _drainName;
        static bool        _drainDiagLogged;

        // Event-based drain — only fires when both conditions met:
        //   1. team.TotalResources >= DRAIN_MIN_TOTAL (20k floor — don't hurt eco early)
        //   2. team.TotalResources >= DRAIN_TRIGGER_PCT of ResourceCapacity
        // On trigger, drains down to DRAIN_TARGET_PCT of ResourceCapacity (or a
        // safe floor). Returns the amount actually drained so the caller can add
        // it back to cumulative-income tracking.
        //
        // Previous versions of this method wrote to the m_StoredResources field
        // via plain reflection SetValue. That silently did nothing on Il2Cpp
        // types — the write hit the C# shadow while the getter read from the
        // native side. Rewritten to route through AccessTools.Method + Invoke,
        // which crosses the Il2Cpp bridge correctly.
        static int TryDrain(Team team)
        {
            try
            {
                int cash = team.TotalResources;
                if (cash < DRAIN_MIN_TOTAL) return 0;
                int cap;
                try { cap = team.ResourceCapacity; } catch { return 0; }
                if (cap <= 0) return 0;
                int trigger = (int)(cap * DRAIN_TRIGGER_PCT);
                if (cash < trigger) return 0;

                int target = Mathf.Max(DRAIN_MIN_TOTAL, (int)(cap * DRAIN_TARGET_PCT));
                if (target >= cash) return 0;

                int drained = cash - target;
                string usedPath;
                // First-choice: invoke the same debug-console command a player types
                // manually ("resources <team> <delta>"). This bypasses every Il2Cpp
                // interop trap — the command runs on the game's own dispatch path.
                if (TryConsoleResourcesCommand(team, -drained, out usedPath))
                {
                    // ok
                }
                else if (!InvokeResourceSetter(team, target, out usedPath))
                {
                    // Last-ditch: reflection field write. Almost certainly a no-op
                    // in Il2Cpp but preserved as a documented fallback.
                    var fi = ResolveTotalResourcesField();
                    if (fi != null) fi.SetValue(team, target);
                    usedPath = "field-fallback:m_StoredResources";
                }

                int after = team.TotalResources;
                if (!_drainDiagLogged)
                {
                    _drainDiagLogged = true;
                    MelonLogger.Msg($"[ECO] drain first-attempt team={team.name} {cash}→{after} (wanted {target}) via {usedPath}");
                }
                Si_RTS_AI.AppendToRound(
                    $"[ECO] drain team={team.name} cash={cash}→{after} capacity={cap} drained={cash-after}");
                return cash - after;
            }
            catch (Exception ex) { MelonLogger.Warning($"[ECO] TryDrain threw: {ex.Message}"); return 0; }
        }

        // Try each candidate resource-setter method. On first success, cache the
        // MethodInfo so we don't re-resolve every drain call.
        static bool InvokeResourceSetter(Team team, int value, out string usedPath)
        {
            usedPath = "";
            if (_drainMi != null)
            {
                try
                {
                    object arg = _drainName == "AddResources" ? (object)(value - team.TotalResources) : (object)value;
                    _drainMi.Invoke(team, new object[] { arg });
                    usedPath = _drainName ?? "cached";
                    return team.TotalResources == value;
                }
                catch { }
            }

            // Broaden the search — no strict signature. Some Il2Cpp interop methods
            // don't match AccessTools when we pin the parameter list. Match by
            // NAME first, then filter for a single-int parameter list at call time.
            var candidates = new List<(string name, bool isDelta)>
            {
                ("SetResources",       false),
                ("set_TotalResources", false),
                ("AddResources",       true),
                ("SetTotalResources",  false),
                ("GiveResources",      true),
                ("SpendResources",     true),   // negative delta semantics
            };

            foreach (var (name, isDelta) in candidates)
            {
                foreach (var mi in typeof(Team).GetMethods(
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    if (mi.Name != name) continue;
                    var pars = mi.GetParameters();
                    if (pars.Length != 1) continue;
                    if (pars[0].ParameterType != typeof(int)) continue;
                    try
                    {
                        int arg = isDelta ? (value - team.TotalResources) : value;
                        // For SpendResources we usually want a POSITIVE amount to spend.
                        if (name == "SpendResources") arg = team.TotalResources - value;
                        mi.Invoke(team, new object[] { arg });
                        if (team.TotalResources == value)
                        {
                            _drainMi   = mi;
                            _drainName = name;
                            usedPath   = name;
                            return true;
                        }
                    }
                    catch { }
                }
            }

            // Enumerate the actual methods once so we can eyeball what IS accessible.
            if (!_methodsEnumerated)
            {
                _methodsEnumerated = true;
                var seen = new List<string>();
                foreach (var mi in typeof(Team).GetMethods(
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    var pars = mi.GetParameters();
                    string sig = pars.Length == 0 ? "()" :
                        "(" + string.Join(", ", System.Array.ConvertAll(pars, p => p.ParameterType.Name)) + ")";
                    // Focus on int-taking methods and property setters that might be resource-related.
                    if (pars.Length == 1 && pars[0].ParameterType == typeof(int))
                        seen.Add($"[int] {mi.Name}{sig} -> {mi.ReturnType.Name}");
                    else if (mi.Name.StartsWith("set_") || mi.Name.Contains("Resource") ||
                             mi.Name.Contains("Set") || mi.Name.Contains("Add") || mi.Name.Contains("Give") ||
                             mi.Name.Contains("Spend"))
                        seen.Add($"[oth] {mi.Name}{sig} -> {mi.ReturnType.Name}");
                }
                seen.Sort();
                MelonLogger.Msg("[ECO/DIAG] Team methods reflection can see (filtered):");
                foreach (var s in seen) MelonLogger.Msg($"  {s}");
            }
            return false;
        }

        static bool _methodsEnumerated;

        // ==== Console-command drain path ====
        // The game's "resources <team> <delta>" debug console command already
        // does the exact mutation we want, and it runs through the game's own
        // dispatch path — no Il2Cpp interop bridge to fight. If we can find
        // the console executor via AccessTools.TypeByName + AccessTools.Method,
        // invoking it is far more reliable than trying to reflect Team's setter.
        static bool _consoleResolved;
        static Type? _consoleType;
        static MethodInfo? _consoleExec;
        static bool _consoleIsStatic;

        /// <summary>
        /// Public helper: mutate a team's resources by `delta` (negative to deduct)
        /// via the game's own "resources &lt;team&gt; &lt;delta&gt;" console command.
        /// Sidesteps Il2Cpp reflection issues that make direct SetValue / GetMethod
        /// paths silently no-op. Returns true iff cash actually changed. Callable
        /// from anywhere that needs to move a team's cash (drain, human-suppress).
        /// </summary>
        internal static bool TryMutateTeamResources(Team team, int delta, out string usedPath)
            => TryConsoleResourcesCommand(team, delta, out usedPath);

        static bool TryConsoleResourcesCommand(Team team, int delta, out string usedPath)
        {
            usedPath = "";
            if (!_consoleResolved)
            {
                _consoleResolved = true;
                foreach (var name in new[] {
                    "DebugTools.DebugConsole",       // sibling of DebugConsoleHandler in same namespace
                    "DebugConsole",
                    "Silica.DebugConsole",
                    "DebugTools.DebugConsoleHandler",
                    "DebugConsoleHandler", "Silica.DebugConsoleHandler",
                })
                {
                    _consoleType = AccessTools.TypeByName(name);
                    if (_consoleType == null) continue;
                    // Confirm this type has a TryExecuteCommand method; otherwise keep looking.
                    bool hasExec = false;
                    foreach (var mi in _consoleType.GetMethods(
                        BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
                    {
                        if (mi.Name == "TryExecuteCommand" || mi.Name == "ExecuteCommand") { hasExec = true; break; }
                    }
                    if (hasExec) { break; }
                    MelonLogger.Msg($"[ECO] Console: {_consoleType.FullName} has no exec method — continuing search.");
                    _consoleType = null;
                }
                if (_consoleType == null)
                {
                    MelonLogger.Warning("[ECO] Console executor: no DebugConsoleHandler / DebugConsole type found via AccessTools.TypeByName.");
                    return false;
                }
                // Signature filter used to check pars[0]==typeof(string), but Il2Cpp
                // interop types (Il2CppSystem.String, Il2CppInterop wrapper types)
                // don't equal that. Enumerate ALL methods on the class named
                // TryExecuteCommand / ExecuteCommand and log their signatures once
                // so we can see what the actual first-parameter type is.
                var candidates = new List<MethodInfo>();
                var allSigs    = new List<string>();
                foreach (var mi in _consoleType.GetMethods(
                    BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    var pars = mi.GetParameters();
                    string sig = string.Join(", ", System.Array.ConvertAll(pars, p => p.ParameterType.FullName ?? p.ParameterType.Name));
                    string line = $"    {(mi.IsStatic ? "static " : "       ")}{mi.ReturnType.Name} {mi.Name}({sig})";
                    allSigs.Add(line);
                    if (mi.Name == "TryExecuteCommand" || mi.Name == "ExecuteCommand")
                        candidates.Add(mi);
                }

                // Prefer the shortest-arg TryExecuteCommand variant (usually the entrypoint).
                candidates.Sort((a, b) => a.GetParameters().Length.CompareTo(b.GetParameters().Length));
                if (candidates.Count > 0)
                {
                    _consoleExec = candidates[0];
                    _consoleIsStatic = _consoleExec.IsStatic;
                    MelonLogger.Msg($"[ECO] Console executor selected: {_consoleType.FullName}.{_consoleExec.Name}(...) static={_consoleIsStatic} params={_consoleExec.GetParameters().Length}");
                }
                else
                {
                    MelonLogger.Warning($"[ECO] Console executor: {_consoleType.FullName} has no TryExecuteCommand/ExecuteCommand method. All methods:");
                    foreach (var s in allSigs) MelonLogger.Msg(s);
                    return false;
                }
            }
            if (_consoleExec == null || _consoleType == null) return false;

            // Try several team-name flavours since the console command might want
            // "alien" but team.name is "Team_Alien".
            string[] teamNameCandidates = new[]
            {
                team.TeamShortName ?? "",
                team.TeamName      ?? "",
                team.name          ?? "",
            };

            foreach (var teamName in teamNameCandidates)
            {
                if (string.IsNullOrEmpty(teamName)) continue;
                string cmd = $"resources {teamName} {delta}";
                int before = team.TotalResources;
                object? target = _consoleIsStatic ? null : ResolveConsoleInstance();
                try
                {
                    // Fill remaining params with defaults if any (bool acceptSubcommands, Player caller etc.)
                    var pars = _consoleExec.GetParameters();
                    var args = new object?[pars.Length];
                    args[0] = cmd;
                    for (int i = 1; i < pars.Length; i++) args[i] = Type.Missing;
                    _consoleExec.Invoke(target, args);
                }
                catch (Exception ex)
                {
                    MelonLogger.Warning($"[ECO] TryExecuteCommand('{cmd}') threw: {ex.Message}");
                    continue;
                }
                int after = team.TotalResources;
                if (after != before)
                {
                    usedPath = $"console:'{cmd}' (Δ {before}→{after})";
                    return true;
                }
            }
            return false;
        }

        // Some console handler classes are singleton MonoBehaviours; try Instance / Current statics.
        static object? ResolveConsoleInstance()
        {
            if (_consoleType == null) return null;
            foreach (var name in new[] { "Instance", "Current", "instance", "s_Instance" })
            {
                var pi = _consoleType.GetProperty(name,
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                if (pi != null) try { var v = pi.GetValue(null); if (v != null) return v; } catch { }
                var fi = _consoleType.GetField(name,
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                if (fi != null) try { var v = fi.GetValue(null); if (v != null) return v; } catch { }
            }
            return null;
        }

        internal static void ResetForNewRound()
        {
            _perTeam.Clear();
        }

        internal static string BuildRoundSummaryFragment()
        {
            if (_perTeam.Count == 0) return "";
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("--- Eco rate summary ---");
            foreach (var kv in _perTeam)
            {
                var e = kv.Value;
                float totalTime = Mathf.Max(1f, Time.time - e.FirstSampleAt);
                float avgRate   = e.TotalIncome / totalTime;
                sb.AppendLine($"  {e.Name}: cumulIncome={e.TotalIncome} over {totalTime:F0}s " +
                              $"avg={avgRate:F1}/s peak={e.PeakRatePerSec}/s finalCash={e.LastResources}");
            }
            return sb.ToString();
        }

        /// <summary>
        /// Append one JSON line per team to UserData/RTSA/benchmarks.jsonl at
        /// round end. Structured so a Python soak-analysis script can group by
        /// (map, configId) and diff cumulIncome@checkpoint across runs without
        /// scraping the free-form round log. Called from TestHarness.ForceEndRound.
        /// </summary>
        internal static void WriteBenchmarkLines(string mapName, string configId, float roundElapsedS)
        {
            if (_perTeam.Count == 0) return;
            try
            {
                string dir = Path.Combine("UserData", "RTSA");
                Directory.CreateDirectory(dir);
                string path = Path.Combine(dir, "benchmarks.jsonl");
                string ts = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss");

                using (var sw = new StreamWriter(path, append: true))
                {
                    foreach (var kv in _perTeam)
                    {
                        var e = kv.Value;
                        float avgRate = e.TotalIncome / Mathf.Max(1f, roundElapsedS);
                        var chk = new StringBuilder();
                        chk.Append('{');
                        bool first = true;
                        foreach (var pair in e.CashAtCheckpoint)
                        {
                            if (!first) chk.Append(',');
                            chk.Append('"').Append(pair.Key).Append("\":").Append(pair.Value);
                            first = false;
                        }
                        chk.Append('}');

                        sw.WriteLine(
                            "{" +
                            $"\"ts\":\"{ts}\"," +
                            $"\"map\":\"{Esc(mapName)}\"," +
                            $"\"configId\":\"{Esc(configId)}\"," +
                            $"\"team\":\"{Esc(e.Name)}\"," +
                            $"\"elapsedS\":{roundElapsedS:F1}," +
                            $"\"cumulIncome\":{e.TotalIncome}," +
                            $"\"peakRatePerSec\":{e.PeakRatePerSec}," +
                            $"\"avgRatePerSec\":{avgRate:F2}," +
                            $"\"finalCash\":{e.LastResources}," +
                            $"\"checkpoints\":{chk}" +
                            "}");
                    }
                }
                MelonLogger.Msg($"[ECO] benchmark: appended {_perTeam.Count} team rows to {path} (map={mapName} elapsed={roundElapsedS:F0}s)");
            }
            catch (Exception ex) { MelonLogger.Warning($"[ECO] WriteBenchmarkLines threw: {ex.Message}"); }
        }

        static string Esc(string s) => (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"");
    }
}
