using MelonLoader;
using Silica;
using System;
using System.Collections.Generic;
using System.Reflection;

namespace Si_RTS_AI.Perception
{
    /// <summary>
    /// WHAT A UNIT COSTS IN CAP, ASKED RATHER THAN ASSUMED.
    ///
    /// Silica has TWO unit caps — `UnitCapType` is Primary or Secondary — and each
    /// unit carries a `UnitCapValue` weight against its cap. DrMuck, 2026-08-05:
    /// "There is a unit cap for smaller and larger units! And the unitbalance Mod
    /// disables shrimps counting to the lesser unit cap. Military units count to
    /// the unit cap."
    ///
    /// That is a BALANCE-MOD CHOICE, not a property of the game, and the design
    /// downstream of it changes completely:
    ///
    ///   weight 0  — workers are free. Economy and army do not compete for
    ///               population at all, only for cash.
    ///   weight >0 — every shrimp is an army slot not taken, and the two have to
    ///               be arbitrated.
    ///
    /// I asserted the second from a hardcoded 185 that turned out to be OUR OWN
    /// performance constant, and was wrong about it. So this reads the live
    /// ConstructionData instead — the same rule the eco layer already follows for
    /// costs and structure ranges, and for the same reason: a mod can change it,
    /// and a number in our source cannot know that.
    ///
    /// Also probes the Team object for whatever cap totals it exposes and logs
    /// what it finds, because where the per-team LIMIT lives is still an open
    /// question and one round's log will answer it.
    /// </summary>
    internal static class UnitCaps
    {
        internal struct Cost { public string CapType; public int Weight; }

        static readonly Dictionary<string, Cost> _byName =
            new Dictionary<string, Cost>(StringComparer.OrdinalIgnoreCase);
        static bool _resolved, _probedTeam;

        internal static void ResetForNewRound()
        {
            _byName.Clear();
            _resolved = false;
            _probedTeam = false;
        }

        /// <summary>Cap weight for a unit, or 0 if it is free / unknown.</summary>
        internal static int WeightOf(string unitName) =>
            _byName.TryGetValue(unitName ?? "", out var c) ? c.Weight : 0;

        /// <summary>Which cap it draws on — "Primary", "Secondary", "None".</summary>
        internal static string CapTypeOf(string unitName) =>
            _byName.TryGetValue(unitName ?? "", out var c) ? c.CapType : "None";

        /// <summary>
        /// Do workers compete with the army for population under THIS config?
        /// The whole eco/military population question is this one boolean, so it
        /// is answered from the game rather than assumed in either direction.
        /// </summary>
        internal static bool WorkersConsumeCap => WeightOf("Shrimp") > 0;

        /// <summary>
        /// Resolve once per round from everything this team can build. Cheap and
        /// idempotent; call it from any per-team tick.
        /// </summary>
        internal static void Resolve(Team team)
        {
            if (_resolved || team == null) return;
            try
            {
                var structs = team.Structures;
                if (structs == null || structs.Count == 0) return;

                for (int i = 0; i < structs.Count; i++)
                {
                    var s = structs[i];
                    if (s == null || s.IsDestroyed) continue;
                    var opts = s.ConstructionOptions;
                    if (opts == null) continue;
                    foreach (var cd in opts)
                    {
                        if (cd == null) continue;
                        string name = null;
                        try { name = cd.ObjectInfo?.DisplayName; } catch { }
                        if (string.IsNullOrEmpty(name) || _byName.ContainsKey(name)) continue;
                        _byName[name] = new Cost
                        {
                            CapType = ReadString(cd, "UnitCapType") ?? "None",
                            Weight  = ReadInt(cd, "UnitCapValue"),
                        };
                    }
                }
                if (_byName.Count == 0) return;
                _resolved = true;

                var sb = new System.Text.StringBuilder("[UNITCAP] resolved from game: ");
                foreach (var kv in _byName)
                {
                    if (kv.Value.Weight == 0 && kv.Value.CapType == "None") continue;
                    sb.Append(kv.Key).Append('=').Append(kv.Value.CapType)
                      .Append('/').Append(kv.Value.Weight).Append(' ');
                }
                sb.Append("| workersConsumeCap=").Append(WorkersConsumeCap)
                  .Append(WorkersConsumeCap
                      ? "  (economy and army compete for population)"
                      : "  (workers are free — the contention is cash only)");
                MelonLogger.Msg(sb.ToString());

                ProbeTeamTotals(team);
            }
            catch (Exception ex) { MelonLogger.Warning("[UNITCAP] resolve threw: " + ex.Message); }
        }

        /// <summary>
        /// Where does the per-team LIMIT live? Unknown, and guessing a property
        /// name would be another assumption of the kind that caused this file to
        /// exist. Log every plausibly-named member once and read the answer off a
        /// round instead.
        /// </summary>
        static void ProbeTeamTotals(Team team)
        {
            if (_probedTeam) return;
            _probedTeam = true;
            try
            {
                var t = team.GetType();
                var found = new List<string>();
                foreach (var p in t.GetProperties(BindingFlags.Instance | BindingFlags.Public))
                {
                    if (p.Name.IndexOf("cap", StringComparison.OrdinalIgnoreCase) < 0 &&
                        p.Name.IndexOf("unit", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    object v = null;
                    try { v = p.GetValue(team); } catch { }
                    if (v is int || v is float) found.Add($"{p.Name}={v}");
                }
                MelonLogger.Msg("[UNITCAP] team cap-ish members: " +
                                (found.Count > 0 ? string.Join(" ", found) : "none found"));
            }
            catch { }
        }

        // Reflected because these live on ConstructionData in a form the typed
        // API does not expose cleanly under Il2Cpp interop, same as elsewhere.
        static string ReadString(object o, string member)
        {
            var v = Read(o, member);
            return v?.ToString();
        }

        static int ReadInt(object o, string member)
        {
            var v = Read(o, member);
            try { return v == null ? 0 : Convert.ToInt32(v); } catch { return 0; }
        }

        static object Read(object o, string member)
        {
            try
            {
                var t = o.GetType();
                var p = t.GetProperty(member, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (p != null) return p.GetValue(o);
                var f = t.GetField(member, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                return f?.GetValue(o);
            }
            catch { return null; }
        }
    }
}
