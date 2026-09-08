using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using MelonLoader;
using Silica;

namespace Si_RTS_AI.Mil
{
    /// <summary>
    /// ONE MILITARY LAYER, ONE STATE PER TEAM. Intel, Objectives, Forces,
    /// production, defence siting, scouting, threat map, harvesters: all of it is
    /// static state written for a single seat. Rather than rewrite eleven
    /// thousand lines into instances, the static fields of those classes are
    /// swapped at the tick boundary: Use(team) saves the current team's values
    /// and installs the team's own. A team seen for the first time gets a fresh
    /// allocation — every collection a new instance of the same type (with its
    /// comparer), every other field its pristine value — and then each class's
    /// ResetForNewRound() runs on it, exactly as at round start.
    ///
    /// What the hooks need across teams — is this unit held by a force, is it a
    /// scout — is kept as unions rebuilt at every swap, so an order event for a
    /// Sol unit is answered correctly while the alien's state is installed.
    ///
    /// Cost: about 180 field reads and writes per swap, three swaps a second at
    /// most. Fields marked [ThreadStatic] and constants are left alone.
    /// </summary>
    internal static class MilContext
    {
        static readonly Type[] CLASSES =
        {
            typeof(Intel), typeof(Objectives), typeof(Forces), typeof(ProductionV3), typeof(SpirePlanner),
            typeof(QueenKeeper), typeof(Shadow),
            typeof(Perception.ThreatMap), typeof(Perception.QueenStatus), typeof(Perception.Utilisation),
            typeof(Perception.Reach), typeof(Perception.BcIncome),
            typeof(Planning.ScoutPlanner), typeof(Human.HarvesterManager),
            // ControlMap holds _ours/_theirs/_heldCells for ONE team. It was rebuilt
            // only for the alien ("OURS MEANS THE ALIEN'S"), so in a mod-vs-mod round
            // the human team's Intel.ControlGain and Utilisation.MapHeld read the
            // alien's ground. Per-team storage makes rebuilding it per team correct.
            typeof(Perception.ControlMap),
        };

        static FieldInfo[] _fields;
        static object[] _pristine;
        static readonly Dictionary<int, object[]> _snap = new Dictionary<int, object[]>();   // team instance id -> values
        static readonly Dictionary<int, Team> _teams = new Dictionary<int, Team>();
        static Team _current;
        static bool _ready, _readonlyOk = true, _loggedFail;
        internal static int Swaps;

        // unions for the hooks
        static readonly HashSet<Unit> _heldUnion = new HashSet<Unit>();
        static readonly HashSet<Unit> _scoutUnion = new HashSet<Unit>();
        static FieldInfo _heldField, _scoutField;

        internal static Team Current => _current;
        /// <summary>"[Sol] " while more than one team is under the mod, else "" (log lines stay as they were).</summary>
        internal static string Tag
        {
            get
            {
                if (_snap.Count <= 1 || _current == null) return "";
                if (_tagFor != _current) { _tagFor = _current; _tag = "[" + Short(_current.name) + "] "; }
                return _tag;
            }
        }
        static Team _tagFor; static string _tag = "";
        static string Short(string n)
        {
            n = n ?? "";
            if (n.StartsWith("Team_")) n = n.Substring(5);
            if (n.StartsWith("Human_")) n = n.Substring(6);
            return n;
        }
        internal static bool Enabled = true;

        // Switches and limits set from config or the console apply to every team;
        // they must not be reset to their compile-time value for a new team.
        static readonly HashSet<string> KNOBS = new HashSet<string> { "Enabled", "Execute", "MaxScouts", "Enforce" };
        static bool IsKnob(string name)
        {
            if (KNOBS.Contains(name)) return true;
            // auto-property backing field: <Name>k__BackingField
            if (name.Length > 2 && name[0] == '<') { int e = name.IndexOf('>'); if (e > 1 && KNOBS.Contains(name.Substring(1, e - 1))) return true; }
            return false;
        }

        static void Init()
        {
            if (_ready) return;
            var list = new List<FieldInfo>();
            foreach (var t in CLASSES)
            {
                foreach (var f in t.GetFields(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly))
                {
                    if (f.IsLiteral) continue;                                   // const
                    if (f.GetCustomAttribute<ThreadStaticAttribute>() != null) continue;
                    if (IsKnob(f.Name)) continue;                                // config, same for every team
                    list.Add(f);
                }
            }
            _fields = list.ToArray();
            _pristine = new object[_fields.Length];
            for (int i = 0; i < _fields.Length; i++) { try { _pristine[i] = _fields[i].GetValue(null); } catch { } }
            _heldField = typeof(Forces).GetField("_held", BindingFlags.Static | BindingFlags.NonPublic);
            _scoutField = typeof(Planning.ScoutPlanner).GetField("_scoutSet", BindingFlags.Static | BindingFlags.NonPublic);
            _ready = true;
            MelonLogger.Msg($"[MIL/CTX] per-team context over {_fields.Length} static fields of {CLASSES.Length} classes");
        }

        /// <summary>Called LAST in the round reset: the reset and freshly configured values become the template for further teams.</summary>
        internal static void ResetForNewRound()
        {
            Init();
            for (int i = 0; i < _fields.Length; i++) { try { _pristine[i] = _fields[i].GetValue(null); } catch { } }
            _snap.Clear(); _teams.Clear(); _current = null; Swaps = 0;
            _heldUnion.Clear(); _scoutUnion.Clear();
        }

        /// <summary>Install this team's state (saving the current team's first). No-op when it is already installed.</summary>
        internal static void Use(Team team)
        {
            if (!Enabled || team == null) return;
            Init();
            if (_current != null && ReferenceEquals(_current, team)) return;
            try
            {
                // save the outgoing team
                if (_current != null)
                {
                    int cid = _current.GetInstanceID();
                    if (!_snap.TryGetValue(cid, out var cur)) { cur = new object[_fields.Length]; _snap[cid] = cur; _teams[cid] = _current; }
                    for (int i = 0; i < _fields.Length; i++) cur[i] = _fields[i].GetValue(null);
                }
                int id = team.GetInstanceID();
                if (_snap.TryGetValue(id, out var vals))
                {
                    Install(vals);
                }
                else if (_current == null)
                {
                    // the state in memory belongs to nobody yet: it becomes this team's
                    vals = new object[_fields.Length];
                    for (int i = 0; i < _fields.Length; i++) vals[i] = _fields[i].GetValue(null);
                    _snap[id] = vals; _teams[id] = team;
                }
                else
                {
                    vals = Fresh();
                    _snap[id] = vals; _teams[id] = team;
                    Install(vals);
                    foreach (var t in CLASSES)
                    {
                        try { t.GetMethod("ResetForNewRound", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)?.Invoke(null, null); }
                        catch (Exception ex) { MelonLogger.Warning($"[MIL/CTX] {t.Name}.ResetForNewRound threw for {team.name}: {ex.InnerException?.Message ?? ex.Message}"); }
                    }
                    for (int i = 0; i < _fields.Length; i++) vals[i] = _fields[i].GetValue(null);
                    MelonLogger.Msg($"[MIL/CTX] fresh military state for {team.name}");
                }
                _current = team;
                Swaps++;
                RebuildUnions();
            }
            catch (Exception ex)
            {
                if (!_loggedFail) { _loggedFail = true; MelonLogger.Warning("[MIL/CTX] swap threw: " + ex.Message); }
            }
        }

        static void Install(object[] vals)
        {
            for (int i = 0; i < _fields.Length; i++)
            {
                try { _fields[i].SetValue(null, vals[i]); }
                catch (Exception ex)
                {
                    if (_readonlyOk) { _readonlyOk = false; MelonLogger.Warning($"[MIL/CTX] cannot set {_fields[i].DeclaringType.Name}.{_fields[i].Name}: {ex.Message} — per-team state is NOT reliable on this runtime"); }
                }
            }
        }

        /// <summary>A new set of values: collections as new instances (same comparer), arrays of the same length, everything else pristine.</summary>
        static object[] Fresh()
        {
            var vals = new object[_fields.Length];
            for (int i = 0; i < _fields.Length; i++)
            {
                var f = _fields[i]; object p = _pristine[i];
                object v = p;
                try
                {
                    var ft = f.FieldType;
                    if (p is Array arr) v = Array.CreateInstance(ft.GetElementType(), arr.Length);
                    else if (p is IDictionary dict) v = NewCollection(p.GetType(), dict);
                    else if (p is IEnumerable && !(p is string)) v = NewCollection(p.GetType(), p);
                    else if (p != null && !ft.IsValueType && !(p is string) && !(p is Delegate) && !(p is Team) && !(p is UnityEngine.Object))
                        v = ft.GetConstructor(Type.EmptyTypes) != null ? Activator.CreateInstance(ft) : null;
                }
                catch { v = null; }
                vals[i] = v;
            }
            return vals;
        }

        static object NewCollection(Type t, object like)
        {
            // Dictionary<,> and HashSet<> carry a comparer worth keeping (OrdinalIgnoreCase keys).
            var cmp = t.GetProperty("Comparer")?.GetValue(like, null);
            if (cmp != null)
            {
                var ctor = t.GetConstructor(new[] { cmp.GetType().GetInterfaces()[0] }) ;
                foreach (var c in t.GetConstructors())
                {
                    var ps = c.GetParameters();
                    if (ps.Length == 1 && ps[0].ParameterType.IsInstanceOfType(cmp)) return c.Invoke(new[] { cmp });
                }
            }
            return Activator.CreateInstance(t);
        }

        static void RebuildUnions()
        {
            _heldUnion.Clear(); _scoutUnion.Clear();
            int hi = Array.IndexOf(_fields, _heldField), si = Array.IndexOf(_fields, _scoutField);
            foreach (var kv in _snap)
            {
                if (hi >= 0 && kv.Value[hi] is IEnumerable<Unit> h) foreach (var u in h) if (u != null) _heldUnion.Add(u);
                if (si >= 0 && kv.Value[si] is IEnumerable<Unit> s) foreach (var u in s) if (u != null) _scoutUnion.Add(u);
            }
            // the installed team's live sets
            try { if (_heldField?.GetValue(null) is IEnumerable<Unit> h2) foreach (var u in h2) if (u != null) _heldUnion.Add(u); } catch { }
            try { if (_scoutField?.GetValue(null) is IEnumerable<Unit> s2) foreach (var u in s2) if (u != null) _scoutUnion.Add(u); } catch { }
        }

        /// <summary>Held by any team's forces (hook-time, team-independent).</summary>
        internal static bool AnyHeld(Unit u)
        {
            if (u == null) return false;
            if (_snap.Count <= 1) { try { return _heldField?.GetValue(null) is HashSet<Unit> h && h.Contains(u); } catch { return false; } }
            return _heldUnion.Contains(u);
        }
        internal static bool AnyScout(Unit u)
        {
            if (u == null) return false;
            if (_snap.Count <= 1) { try { return _scoutField?.GetValue(null) is HashSet<Unit> s && s.Contains(u); } catch { return false; } }
            return _scoutUnion.Contains(u);
        }

        internal static string Summary() => _snap.Count <= 1 ? "" : $"  military context: {_snap.Count} teams, {Swaps} swaps, readonly-ok {_readonlyOk}\n";
    }
}
