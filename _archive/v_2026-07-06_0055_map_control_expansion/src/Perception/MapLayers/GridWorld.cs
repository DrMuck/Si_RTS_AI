using MelonLoader;
using System.Collections.Generic;
using UnityEngine;

namespace Si_RTS_AI.Perception.MapLayers
{
    /// <summary>
    /// Grid extent + coord transforms. Sized to the PLAYABLE map area — same
    /// convention as MapReplay's per-map extent table — so we don't waste
    /// memory on off-map terrain margin and layer visualizations match what
    /// entities can actually reach.
    ///
    /// For known maps, world is [-extent, +extent] on both axes; for unknown
    /// maps we fall back to the Unity Terrain union bounds.
    ///
    /// All layers size themselves from GridWorld.CellCount — call ConfigureFromMap()
    /// on scene load BEFORE any layer is queried.
    /// </summary>
    public static class GridWorld
    {
        public const float CellSize = 40f;   // constant — tuning this is a schema change

        public static float OriginX  { get; private set; } = -3000f;
        public static float OriginZ  { get; private set; } = -3000f;
        public static int   Width    { get; private set; } = 150;
        public static int   Height   { get; private set; } = 150;
        public static int   CellCount => Width * Height;

        // Half-extent per map, in meters (world x, z ∈ [-extent, +extent]).
        // Kept in sync with MapReplay's MAP_WORLD_EXTENTS so replay tooling and
        // AI planning agree on where the playable area is.
        static readonly Dictionary<string, int> MapExtents = new Dictionary<string, int>
        {
            { "Badlands",          3000 },
            { "BlackIsle",         1000 },
            { "Citadel",           1500 },
            { "CombatDome",         500 },
            { "CrimsonPeak",       2048 },
            { "CrystalChasm",      1500 },
            { "GreatErg",          3000 },
            { "IndustrialQuarter", 2000 },
            { "MonumentValley",    3000 },
            { "NarakaCity",        3000 },
            { "NorthPolarCap",     2048 },
            { "PowerStation",       500 },
            { "ProvingGrounds",    1500 },
            { "RiftBasin",         1500 },
            { "RiftBasin_TD",      1500 },
            { "SandboxTest",        500 },
            { "SmallStrategyTest",  500 },
            { "TheMaw",            1500 },
            { "WhisperingPlains",  2048 },
        };

        /// <summary>
        /// Prefer the per-map playable extent (matches MapReplay). If we don't
        /// know the map, union the Unity terrain bounds. Last resort: ±3000.
        /// </summary>
        public static void ConfigureFromMap(string mapName = null)
        {
            if (!string.IsNullOrEmpty(mapName) && MapExtents.TryGetValue(mapName, out int extent))
            {
                OriginX = -extent;
                OriginZ = -extent;
                Width   = Mathf.Max(1, Mathf.CeilToInt(2f * extent / CellSize));
                Height  = Width;
                MelonLogger.Msg($"[RTSA/Layers] Grid configured from playable extent for '{mapName}': " +
                                $"{Width}×{Height} @ {CellSize}m origin=({OriginX:F0},{OriginZ:F0}) " +
                                $"size=({2*extent},{2*extent}) → {CellCount} cells.");
                return;
            }

            // Fallback — union all terrain tiles.
            var terrains = Terrain.activeTerrains;
            if (terrains == null || terrains.Length == 0)
            {
                OriginX = -3000f; OriginZ = -3000f; Width = 150; Height = 150;
                MelonLogger.Msg($"[RTSA/Layers] Unknown map '{mapName}' and no terrain — default grid " +
                                $"{Width}×{Height} at ({OriginX},{OriginZ}).");
                return;
            }

            float minX = float.PositiveInfinity, minZ = float.PositiveInfinity;
            float maxX = float.NegativeInfinity, maxZ = float.NegativeInfinity;
            int tileCount = 0;
            for (int i = 0; i < terrains.Length; i++)
            {
                var t = terrains[i];
                if (t == null || t.terrainData == null) continue;
                var p = t.transform.position;
                var s = t.terrainData.size;
                if (p.x < minX) minX = p.x;
                if (p.z < minZ) minZ = p.z;
                if (p.x + s.x > maxX) maxX = p.x + s.x;
                if (p.z + s.z > maxZ) maxZ = p.z + s.z;
                tileCount++;
            }

            if (tileCount == 0 || minX >= maxX || minZ >= maxZ)
            {
                OriginX = -3000f; OriginZ = -3000f; Width = 150; Height = 150;
                MelonLogger.Msg($"[RTSA/Layers] Terrain tiles had no usable data — default grid " +
                                $"{Width}×{Height} at ({OriginX},{OriginZ}).");
                return;
            }

            OriginX = minX;
            OriginZ = minZ;
            Width   = Mathf.Max(1, Mathf.CeilToInt((maxX - minX) / CellSize));
            Height  = Mathf.Max(1, Mathf.CeilToInt((maxZ - minZ) / CellSize));
            MelonLogger.Msg($"[RTSA/Layers] Grid configured from {tileCount} terrain tile(s) (unknown map " +
                            $"'{mapName}'): {Width}×{Height} @ {CellSize}m " +
                            $"origin=({OriginX:F0},{OriginZ:F0}) size=({maxX-minX:F0},{maxZ-minZ:F0}) → {CellCount} cells.");
        }

        public static int CellX(float worldX)
        {
            int x = (int)((worldX - OriginX) / CellSize);
            return x < 0 ? 0 : x >= Width  ? Width  - 1 : x;
        }
        public static int CellZ(float worldZ)
        {
            int z = (int)((worldZ - OriginZ) / CellSize);
            return z < 0 ? 0 : z >= Height ? Height - 1 : z;
        }

        public static Vector3 CellCenter(int cx, int cz) =>
            new Vector3(OriginX + (cx + 0.5f) * CellSize, 0f, OriginZ + (cz + 0.5f) * CellSize);

        public static float MetersToCells(float meters) => meters / CellSize;
        public static int   MetersToCellsInt(float meters) => Mathf.Max(1, Mathf.RoundToInt(meters / CellSize));
    }
}
