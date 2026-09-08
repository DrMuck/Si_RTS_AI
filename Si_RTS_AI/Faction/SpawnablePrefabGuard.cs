using System;
using HarmonyLib;
using MelonLoader;
using UnityEngine;

namespace Si_RTS_AI.Faction
{
    /// <summary>
    /// A NULL IN AllNetworkedPrefabs MUST NOT TAKE ANOTHER MOD DOWN.
    ///
    /// GameDatabase.GetSpawnablePrefabIndex(string) walks
    /// Database.AllNetworkedPrefabs and reads .name off every entry:
    ///
    ///     if (!(Database.AllNetworkedPrefabs[i].name.ToLower() != prefabName.ToLower()))
    ///
    /// On Beta 0.9.47 that list holds a null entry - measured, one of them, and
    /// present before this mod touches anything - so the walk throws
    /// NullReferenceException on the first call. It surfaces in whatever asked:
    ///
    ///   [Admin Mod] Failed to run MP_Strategy::SetTeamVersusMode
    ///   Object reference not set to an instance of an object
    ///     at GameDatabase.GetSpawnablePrefabIndex (System.String prefabName)
    ///     at SilicaAdminMod.HelperMethods.SpawnAtLocation (...)
    ///     at Si_Resources.ResourceConfig+..._SetTeamVersusMode.Postfix (...)
    ///
    /// Si_Resources dies in its postfix, so the second team never gets its
    /// starting resources and the scene's values stand - the bug
    /// StartingResourcesGuard was written to paper over. This fixes the cause:
    /// the same lookup, skipping nulls, matched case-insensitively as before.
    ///
    /// The prefix replaces the method rather than wrapping it, because the throw
    /// happens inside the original loop and there is nothing to catch from
    /// outside without losing the answer.
    /// </summary>
    internal static class SpawnablePrefabGuard
    {
        static bool _loggedNulls;

        static bool Prefix(string prefabName, ref int __result)
        {
            __result = -1;
            try
            {
                var db = GameDatabase.Database;
                if (db == null || string.IsNullOrEmpty(prefabName)) return false;
                var list = db.AllNetworkedPrefabs;
                if (list == null) return false;

                int nulls = 0;
                for (int i = 0; i < list.Count; i++)
                {
                    var go = list[i];
                    if (go == null) { nulls++; continue; }          // the entry the game trips on
                    if (string.Equals(go.name, prefabName, StringComparison.OrdinalIgnoreCase))
                    {
                        __result = i;
                        break;
                    }
                }
                if (nulls > 0 && !_loggedNulls)
                {
                    _loggedNulls = true;
                    MelonLogger.Msg($"[PREFAB/GUARD] GameDatabase.AllNetworkedPrefabs holds {nulls} null entr{(nulls == 1 ? "y" : "ies")}; " +
                                     "GetSpawnablePrefabIndex(string) now skips them instead of throwing");
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning("[PREFAB/GUARD] lookup threw: " + ex.Message);
            }
            return false;   // never run the original: it is the thing that throws
        }

        internal static void Install(HarmonyLib.Harmony harmony)
        {
            try
            {
                var m = AccessTools.Method(typeof(GameDatabase), "GetSpawnablePrefabIndex", new[] { typeof(string) });
                if (m == null)
                {
                    MelonLogger.Msg("[PREFAB/GUARD] GameDatabase.GetSpawnablePrefabIndex(string) not present on this build");
                    return;
                }
                harmony.Patch(m, prefix: new HarmonyMethod(typeof(SpawnablePrefabGuard), nameof(Prefix)));
                MelonLogger.Msg("[PREFAB/GUARD] GetSpawnablePrefabIndex(string) guarded against null prefab entries");
            }
            catch (Exception ex) { MelonLogger.Warning("[PREFAB/GUARD] install threw: " + ex.Message); }
        }
    }
}
