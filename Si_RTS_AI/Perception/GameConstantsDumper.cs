using MelonLoader;
using Silica;
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;

namespace Si_RTS_AI.Perception
{
    /// <summary>
    /// One-shot runtime dump of every readable field/property on the game types
    /// we base the analytical economic model on:
    ///
    ///   - Shrimp / Harvester   (Unit + ObjectInfo)
    ///   - Bio Cache / Refinery (Structure + ObjectInfo + ConstructionData)
    ///   - Cyst / Node          (ConstructionData)
    ///   - ResourceArea         (a live biotics/balterium patch)
    ///
    /// Written to UserData/RTSA/game_constants.dump — human-readable. Purpose:
    /// verify that runtime numbers match either vanilla or the installed balance
    /// mod, and identify the exact field names to bake into GameConstants (which
    /// the utility functions will read from). Any divergence between what we
    /// expect and what runtime reports gets flagged in the summary so we don't
    /// silently plan against wrong constants.
    ///
    /// Fires ONCE per scene, from AlienConstruction after ConstructionData is
    /// resolved (so bc/cyst/node CDs are populated) and there's at least one
    /// Shrimp on the team.
    /// </summary>
    internal static class GameConstantsDumper
    {
        // Track sections we've already dumped this scene. Each section is guarded
        // independently so BC/Cyst/Shrimp/etc. get dumped as soon as they exist,
        // not on the first tick when most are still null.
        static readonly HashSet<string> _dumpedSections = new HashSet<string>();

        internal static void ResetForNewRound() { _dumpedSections.Clear(); }

        internal static void MaybeDumpAlien(
            Team alienTeam,
            ConstructionData? bcCd,
            ConstructionData? cystCd,
            ConstructionData? nodeCd)
        {
            if (alienTeam == null) return;
            // Resolve each candidate up front — cheap, and lets us know whether
            // anything new is available before we bother touching the file.
            var shrimp    = FindUnitByDisplayName(alienTeam, "Shrimp");
            var harvester = FindUnitByDisplayName(alienTeam, "Harvester")
                         ?? FindHumanHarvesterAcrossTeams();
            var bc        = FindStructureByDisplayName(alienTeam, "Bio Cache");
            var nest      = FindStructureByDisplayName(alienTeam, "Nest");
            var cyst      = FindStructureByDisplayName(alienTeam, "Lesser Spawning Cyst");
            var node      = FindStructureByDisplayName(alienTeam, "Node");
            ResourceArea? patch = null;
            try
            {
                var all = ResourceArea.AllResourceAreas;
                if (all != null)
                    for (int i = 0; i < all.Count; i++)
                    {
                        var ra = all[i];
                        if (ra != null && !ra.IsEmpty) { patch = ra; break; }
                    }
            } catch { }

            // The child components we care about aren't referenced by strongly-
            // typed API (ResourceHolder / ResourceDepositPoint / ConstructionOption).
            // Fetch them via reflection so we don't have to compile against those types.
            object? shrimpHolder = FirstFromEnumerable(shrimp,    "ResourceHolders");
            object? bcHolder     = FirstFromEnumerable(bc,        "ResourceHolders");
            object? bcDeposit    = FirstFromEnumerable(bc,        "ResourceDepositPoints");
            object? nestDeposit  = FirstFromEnumerable(nest,      "ResourceDepositPoints");
            object? shrimpProdCd = FindConstructionOption(nest,   "Shrimp");
            object? shrimpProdOI = GetProp(shrimpProdCd, "ObjectInfo");

            // Build up only the sections that are (a) freshly available and (b) not yet dumped.
            var sb = new StringBuilder();
            int newSections = 0;
            newSections += TryAdd(sb, "SHRIMP.Unit",                       shrimp);
            newSections += TryAdd(sb, "SHRIMP.ObjectInfo",                 shrimp?.ObjectInfo);
            newSections += TryAdd(sb, "SHRIMP.ResourceHolder[0]",          shrimpHolder);
            newSections += TryAdd(sb, "SHRIMP.ProdConstructionData",       shrimpProdCd);
            newSections += TryAdd(sb, "SHRIMP.ProdCD.ObjectInfo",          shrimpProdOI);
            newSections += TryAdd(sb, "HARVESTER.Unit",                    harvester);
            newSections += TryAdd(sb, "HARVESTER.ObjectInfo",              harvester?.ObjectInfo);
            newSections += TryAdd(sb, "BIOCACHE.Structure",                bc);
            newSections += TryAdd(sb, "BIOCACHE.ObjectInfo",               bc?.ObjectInfo);
            newSections += TryAdd(sb, "BIOCACHE.ResourceHolder[0]",        bcHolder);
            newSections += TryAdd(sb, "BIOCACHE.DepositPoint[0]",          bcDeposit);
            newSections += TryAdd(sb, "NEST.Structure",                    nest);
            newSections += TryAdd(sb, "NEST.ObjectInfo",                   nest?.ObjectInfo);
            newSections += TryAdd(sb, "NEST.DepositPoint[0]",              nestDeposit);
            newSections += TryAdd(sb, "CYST.Structure",                    cyst);
            newSections += TryAdd(sb, "NODE.Structure",                    node);
            newSections += TryAdd(sb, "BC.ConstructionData",               bcCd);
            newSections += TryAdd(sb, "BC.CD.ObjectInfo",                  bcCd?.ObjectInfo);
            newSections += TryAdd(sb, "CYST.ConstructionData",             cystCd);
            newSections += TryAdd(sb, "CYST.CD.ObjectInfo",                cystCd?.ObjectInfo);
            newSections += TryAdd(sb, "NODE.ConstructionData",             nodeCd);
            newSections += TryAdd(sb, "NODE.CD.ObjectInfo",                nodeCd?.ObjectInfo);
            newSections += TryAdd(sb, "BIOTICS.ResourceArea",              patch);

            if (newSections == 0) return;

            try
            {
                string dir = Config.Paths.LogDir;
                Directory.CreateDirectory(dir);
                string path = Path.Combine(dir, "game_constants.dump");
                bool exists = File.Exists(path);
                using (var f = new StreamWriter(path, append: exists))
                {
                    if (!exists)
                    {
                        f.WriteLine("=== Si_RTS_AI game_constants.dump ===");
                        f.WriteLine($"Written at {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                        f.WriteLine($"Map: {MapLayers.LayerReplay.CurrentMap}");
                        f.WriteLine();
                        f.WriteLine("Sections are appended AS THEY BECOME AVAILABLE at runtime — an");
                        f.WriteLine("early tick will only capture Nest/ObjectInfo, later ticks add");
                        f.WriteLine("Shrimp/BC/Cyst/Node as they get built. Purpose: cross-check");
                        f.WriteLine("runtime values against vanilla + balance-mod expectations.");
                        f.WriteLine();
                    }
                    f.Write(sb.ToString());
                }
                MelonLogger.Msg($"[CONSTS] Appended {newSections} new section(s) to {path}. Total dumped so far: {_dumpedSections.Count}.");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[CONSTS] Dump threw: {ex.Message}");
            }
        }

        static int TryAdd(StringBuilder sb, string label, object? obj)
        {
            if (obj == null) return 0;
            if (_dumpedSections.Contains(label)) return 0;
            DumpMember(sb, label, obj);
            _dumpedSections.Add(label);
            return 1;
        }

        // Fetch the first element of an IEnumerable-typed property/field on `obj`.
        // Used to reach child components (ResourceHolders, DepositPoints, etc.)
        // that the mod's strongly-typed API doesn't expose but exist at runtime.
        static object? FirstFromEnumerable(object? obj, string memberName)
        {
            if (obj == null) return null;
            try
            {
                var t = obj.GetType();
                object? val = null;
                var prop = t.GetProperty(memberName,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (prop != null) val = prop.GetValue(obj);
                if (val == null)
                {
                    var fld = t.GetField(memberName,
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (fld != null) val = fld.GetValue(obj);
                }
                if (val is System.Collections.IEnumerable enu)
                {
                    foreach (var item in enu) return item;
                }
            } catch { }
            return null;
        }

        // Read a plain (single-value) property or field by name via reflection.
        static object? GetProp(object? obj, string memberName)
        {
            if (obj == null) return null;
            try
            {
                var t = obj.GetType();
                var prop = t.GetProperty(memberName,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (prop != null) return prop.GetValue(obj);
                var fld = t.GetField(memberName,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (fld != null) return fld.GetValue(obj);
            } catch { }
            return null;
        }

        // Walk a Structure's ConstructionOptions (list of ConstructionData) looking
        // for one whose target ObjectInfo.DisplayName matches. That's where Shrimp
        // production data lives (it's an option on Nest / Cyst).
        static object? FindConstructionOption(object? structure, string displayName)
        {
            if (structure == null) return null;
            try
            {
                var opts = GetProp(structure, "ConstructionOptions") as System.Collections.IEnumerable;
                if (opts == null) return null;
                foreach (var opt in opts)
                {
                    if (opt == null) continue;
                    var oi = GetProp(opt, "ObjectInfo");
                    if (oi == null) continue;
                    var dn = GetProp(oi, "DisplayName") as string;
                    if (string.Equals(dn, displayName, StringComparison.OrdinalIgnoreCase))
                        return opt;
                }
            } catch { }
            return null;
        }

        // ---- Reflection helpers ----

        static void DumpMember(StringBuilder sb, string label, object? obj)
        {
            sb.AppendLine($"---- {label} ----");
            if (obj == null) { sb.AppendLine("(null)").AppendLine(); return; }
            var t = obj.GetType();
            sb.AppendLine($"# runtime type: {t.FullName}");

            var lines = new List<string>();

            foreach (var f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                string val;
                try { val = FormatValue(f.GetValue(obj)); }
                catch (Exception ex) { val = $"<threw:{ex.Message}>"; }
                lines.Add($"  F {f.FieldType.Name,-24} {f.Name} = {val}");
            }
            foreach (var p in t.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (!p.CanRead) continue;
                // Skip indexers — they need arguments we don't have.
                if (p.GetIndexParameters().Length > 0) continue;
                string val;
                try { val = FormatValue(p.GetValue(obj)); }
                catch (Exception ex) { val = $"<threw:{ex.Message}>"; }
                lines.Add($"  P {p.PropertyType.Name,-24} {p.Name} = {val}");
            }

            lines.Sort(StringComparer.OrdinalIgnoreCase);
            foreach (var l in lines) sb.AppendLine(l);
            sb.AppendLine();
        }

        static string FormatValue(object? v)
        {
            if (v == null) return "null";
            if (v is float f)   return f.ToString("G6", System.Globalization.CultureInfo.InvariantCulture);
            if (v is double d)  return d.ToString("G8", System.Globalization.CultureInfo.InvariantCulture);
            if (v is UnityEngine.Vector3 vec)
                return $"({vec.x:F1},{vec.y:F1},{vec.z:F1})";
            if (v is System.Collections.ICollection col) return $"<{v.GetType().Name} Count={col.Count}>";
            // Trim overly long values so the file stays readable.
            string s = v.ToString() ?? "?";
            if (s.Length > 120) s = s.Substring(0, 117) + "...";
            return s;
        }

        static Unit? FindUnitByDisplayName(Team team, string displayName)
        {
            if (team == null) return null;
            try
            {
                var units = team.Units;
                if (units == null) return null;
                for (int i = 0; i < units.Count; i++)
                {
                    var u = units[i];
                    if (u == null || u.ObjectInfo == null || u.IsDestroyed) continue;
                    if (string.Equals(u.ObjectInfo.DisplayName, displayName, StringComparison.OrdinalIgnoreCase))
                        return u;
                }
            } catch { }
            return null;
        }

        static Structure? FindStructureByDisplayName(Team team, string displayName)
        {
            if (team == null) return null;
            try
            {
                var s = team.Structures;
                if (s == null) return null;
                for (int i = 0; i < s.Count; i++)
                {
                    var st = s[i];
                    if (st == null || st.ObjectInfo == null || st.IsDestroyed) continue;
                    if (string.Equals(st.ObjectInfo.DisplayName, displayName, StringComparison.OrdinalIgnoreCase))
                        return st;
                }
            } catch { }
            return null;
        }

        static Unit? FindHumanHarvesterAcrossTeams()
        {
            try
            {
                foreach (var kv in Silica.AI.AIManager.Commanders)
                {
                    var team = kv.Key;
                    if (team == null) continue;
                    string tn = team.name ?? "";
                    if (tn.Contains("Alien")) continue;
                    var h = FindUnitByDisplayName(team, "Harvester");
                    if (h != null) return h;
                }
            } catch { }
            return null;
        }
    }
}
