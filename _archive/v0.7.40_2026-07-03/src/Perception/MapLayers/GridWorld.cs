using MelonLoader;
using UnityEngine;

namespace Si_RTS_AI.Perception.MapLayers
{
    /// <summary>
    /// Grid extent + coord transforms. Configured per-round from the active Unity
    /// Terrain so we don't waste memory on small maps and don't truncate on big ones.
    /// A fallback of ±3000 world is used if the terrain isn't queryable.
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

        /// <summary>
        /// Read the active Terrain's world position + size and set grid parameters
        /// accordingly. Rounds up so the grid fully covers the terrain footprint.
        /// If no terrain is found (menu scene, pre-round init), falls back to the
        /// default ±3000 extent.
        /// </summary>
        public static void ConfigureFromMap()
        {
            // Multi-tile maps (e.g. North Polar Cap) have several Terrain objects.
            // Terrain.activeTerrain returns just ONE of them — often the wrong corner,
            // which produces a grid offset far from where game entities actually live
            // (game world coords may span negative to positive, but a single tile's
            // transform is a positive corner). Union ALL terrains to get the true map
            // bounds.
            var terrains = Terrain.activeTerrains;
            if (terrains == null || terrains.Length == 0)
            {
                OriginX = -3000f; OriginZ = -3000f; Width = 150; Height = 150;
                MelonLogger.Msg($"[RTSA/Layers] No active terrain — using default grid {Width}×{Height} at ({OriginX},{OriginZ}).");
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
                MelonLogger.Msg($"[RTSA/Layers] Terrain tiles had no usable data — using default grid {Width}×{Height} at ({OriginX},{OriginZ}).");
                return;
            }

            OriginX = minX;
            OriginZ = minZ;
            Width   = Mathf.Max(1, Mathf.CeilToInt((maxX - minX) / CellSize));
            Height  = Mathf.Max(1, Mathf.CeilToInt((maxZ - minZ) / CellSize));
            MelonLogger.Msg($"[RTSA/Layers] Grid configured from {tileCount} tile(s): {Width}×{Height} @ {CellSize}m " +
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
