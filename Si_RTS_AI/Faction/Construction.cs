using System;
using Silica;
using UnityEngine;

namespace Si_RTS_AI.Faction
{
    /// <summary>
    /// ONE DOOR FOR EVERY PLACEMENT. The military layer asks for a structure at
    /// a point; which executor answers depends on the team. Aliens go through
    /// AlienConstruction (anchor reach, Queen, blueprint dedupe); Sol and
    /// Centauri through HumanBuild (HQ build radius, rotation, exit sides).
    /// </summary>
    internal static class Construction
    {
        internal static bool IsHuman(Team team)
        {
            string n = team?.name ?? "";
            return n.IndexOf("Sol", StringComparison.OrdinalIgnoreCase) >= 0
                || n.IndexOf("Cent", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        internal static bool TryBuild(Team team, ConstructionData cd, Vector3 pos)
        {
            if (team == null || cd == null) return false;
            if (IsHuman(team)) return HumanBuild.TryBuild(team, cd, pos, null);
            return AlienConstruction.TryBuildStructureByCd(team, cd, pos);
        }

        internal static bool TryBuild(Team team, ConstructionData cd, Vector3 pos, Quaternion rot)
        {
            if (team == null || cd == null) return false;
            if (IsHuman(team)) return HumanBuild.TryBuild(team, cd, pos, rot);
            return AlienConstruction.TryBuildStructureByCd(team, cd, pos);
        }
    }
}
