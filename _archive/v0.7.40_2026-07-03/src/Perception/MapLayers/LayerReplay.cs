using MelonLoader;
using System;
using System.IO;
using UnityEngine;

namespace Si_RTS_AI.Perception.MapLayers
{
    /// <summary>
    /// Snapshots layer state to disk periodically so a replay viewer can animate the
    /// AI's spatial model over the course of a round.
    ///
    /// Each frame's binary format (little-endian, sized by native layer type):
    ///
    ///   uint32  magic       ('RTLF' = 0x52544C46)
    ///   uint8   typeCode    (1=byte, 4=int32)
    ///   int32   tickNo
    ///   float32 roundTime   (seconds since round start)
    ///   int32   width
    ///   int32   height
    ///   T[w*h]  data        (row-major, T sized by typeCode)
    ///
    /// Layout on disk:
    ///   UserData/RTSA/layer-replay/round-YYYYMMDD_HHmmss-MapName/
    ///     manifest.txt
    ///     biotics_discovered/frame-00000.bin    (byte data)
    ///     bc_mask/frame-00000.bin               (byte data)
    ///     biotics_weighted/frame-00000.bin      (int data)
    ///     cyst_pressure/frame-00000.bin         (int data)
    ///     …/frames.txt                          (CSV: frameIdx,tickNo,roundTimeSec)
    /// </summary>
    public static class LayerReplay
    {
        const float SNAPSHOT_INTERVAL_SEC = 5f;
        const uint  MAGIC     = 0x52544C46;   // 'RTLF'
        const byte  TYPE_BYTE = 1;
        const byte  TYPE_INT  = 4;

        static string _roundDir = "";
        static int    _frameIndex;
        static float  _lastSnapshotTime = -1f;
        static float  _roundStartTime;

        public static void OnNewRound(string mapName)
        {
            try
            {
                _frameIndex = 0;
                _lastSnapshotTime = -SNAPSHOT_INTERVAL_SEC;
                _roundStartTime = Time.time;

                var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                _roundDir = Path.Combine("UserData", "RTSA", "layer-replay",
                    $"round-{stamp}-{Safe(mapName)}");
                Directory.CreateDirectory(_roundDir);

                var m = new System.Text.StringBuilder();
                m.Append("Si_RTS_AI layer replay\n");
                m.Append("map = ").Append(mapName ?? "?").Append('\n');
                m.Append("started = ").Append(DateTime.Now.ToString("O")).Append('\n');
                m.Append("gridWidth = ").Append(GridWorld.Width).Append('\n');
                m.Append("gridHeight = ").Append(GridWorld.Height).Append('\n');
                m.Append("cellSize = ").Append(GridWorld.CellSize.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append('\n');
                m.Append("originX = ").Append(GridWorld.OriginX.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append('\n');
                m.Append("originZ = ").Append(GridWorld.OriginZ.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append('\n');
                m.Append("layers = biotics_discovered:byte,bc_mask:byte,biotics_weighted:int,cyst_pressure:int\n");
                m.Append("snapshotIntervalSec = ").Append(SNAPSHOT_INTERVAL_SEC).Append('\n');
                m.Append("frameFormat = uint32 magic; uint8 typeCode; int32 tickNo; float32 roundTime; int32 w; int32 h; T[w*h] data\n");
                File.WriteAllText(Path.Combine(_roundDir, "manifest.txt"), m.ToString());

                MelonLogger.Msg($"[RTSA/Layers] Recording replay to {_roundDir}");
            }
            catch (Exception ex) { MelonLogger.Warning("[RTSA/Layers] Replay init threw: " + ex.Message); _roundDir = ""; }
        }

        public static void MaybeSnapshot(int tickNo, Team team)
        {
            if (string.IsNullOrEmpty(_roundDir)) return;
            float now = Time.time;
            if (now - _lastSnapshotTime < SNAPSHOT_INTERVAL_SEC) return;
            _lastSnapshotTime = now;
            float roundTime = now - _roundStartTime;

            try
            {
                // Force all layers through the lazy-rebuild path so we snapshot a
                // consistent set. If nothing was dirty, this is a no-op internally.
                AlienEcoLayers.RefreshAllForSnapshot(team);

                WriteByteFrame("biotics_discovered", AlienEcoLayers.GetBioticsDiscovered(team), tickNo, roundTime);
                WriteByteFrame("bc_mask",            AlienEcoLayers.GetBcMask(team),            tickNo, roundTime);
                WriteIntFrame ("biotics_weighted",   AlienEcoLayers.GetBioticsWeighted(team),   tickNo, roundTime);
                WriteIntFrame ("cyst_pressure",      AlienEcoLayers.GetCystPressure(team),      tickNo, roundTime);
                _frameIndex++;
            }
            catch (Exception ex) { MelonLogger.Warning("[RTSA/Layers] Snapshot threw: " + ex.Message); }
        }

        static void WriteByteFrame(string layerName, LayerB layer, int tickNo, float roundTime)
        {
            var (path, idx) = OpenFrame(layerName);
            using (var fs = File.Create(path))
            using (var bw = new BinaryWriter(fs))
            {
                bw.Write(MAGIC);
                bw.Write(TYPE_BYTE);
                bw.Write(tickNo);
                bw.Write(roundTime);
                bw.Write(GridWorld.Width);
                bw.Write(GridWorld.Height);
                bw.Write(layer.Data);
            }
            File.AppendAllText(idx, $"{_frameIndex},{tickNo},{roundTime:F2}\n");
        }

        static void WriteIntFrame(string layerName, LayerI layer, int tickNo, float roundTime)
        {
            var (path, idx) = OpenFrame(layerName);
            using (var fs = File.Create(path))
            using (var bw = new BinaryWriter(fs))
            {
                bw.Write(MAGIC);
                bw.Write(TYPE_INT);
                bw.Write(tickNo);
                bw.Write(roundTime);
                bw.Write(GridWorld.Width);
                bw.Write(GridWorld.Height);
                // Bulk copy int32[] → byte[] and write once.
                byte[] bytes = new byte[layer.Data.Length * sizeof(int)];
                Buffer.BlockCopy(layer.Data, 0, bytes, 0, bytes.Length);
                bw.Write(bytes);
            }
            File.AppendAllText(idx, $"{_frameIndex},{tickNo},{roundTime:F2}\n");
        }

        static (string framePath, string idxPath) OpenFrame(string layerName)
        {
            var layerDir = Path.Combine(_roundDir, layerName);
            Directory.CreateDirectory(layerDir);
            return (Path.Combine(layerDir, $"frame-{_frameIndex:D5}.bin"),
                    Path.Combine(layerDir, "frames.txt"));
        }

        static string Safe(string s)
        {
            if (string.IsNullOrEmpty(s)) return "unknown";
            var sb = new System.Text.StringBuilder(s.Length);
            foreach (var c in s)
                sb.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_');
            return sb.ToString();
        }
    }
}
