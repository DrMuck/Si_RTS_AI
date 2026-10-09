using MelonLoader;
using System;
using System.IO;

namespace Si_RTS_AI.Perception
{
    /// <summary>
    /// Samples server FPS every second and writes to fps_YYYYMMDD_HHMMSS.csv
    /// in UserData/RTSA/. Also captures per-second peak dt (max frame time
    /// in that second) so we can spot bursty stalls that the EMA smooths out.
    /// Companion to eco_sim/plot_fps.py for post-round FPS trajectory plots.
    /// </summary>
    internal static class FpsSampler
    {
        static StreamWriter _writer;
        static string _path;
        static float _lastSampleAt;
        static float _peakDtInWindow;
        static int _framesInWindow;
        static float _roundStart;

        internal static void ResetForNewRound()
        {
            try { _writer?.Flush(); _writer?.Close(); } catch { }
            _writer = null;
            _path = null;
            _lastSampleAt = 0f;
            _peakDtInWindow = 0f;
            _framesInWindow = 0;
            _roundStart = UnityEngine.Time.time;
        }

        /// <summary>Called every frame from OnUpdate. Aggregates per-second stats.</summary>
        internal static void OnFrame(float dt, float smoothedFps)
        {
            if (dt > _peakDtInWindow) _peakDtInWindow = dt;
            _framesInWindow++;

            float now = UnityEngine.Time.time;
            if (now - _lastSampleAt < 1f) return;
            _lastSampleAt = now;

            EnsureCsv();
            if (_writer == null) return;

            float roundT = now - _roundStart;
            try
            {
                _writer.WriteLine(
                    $"{roundT.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)}," +
                    $"{smoothedFps.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)}," +
                    $"{(_peakDtInWindow * 1000f).ToString("F1", System.Globalization.CultureInfo.InvariantCulture)}," +
                    $"{_framesInWindow}");
                _writer.Flush();
            }
            catch (Exception ex) { MelonLogger.Warning("[FPS] write threw: " + ex.Message); }

            _peakDtInWindow = 0f;
            _framesInWindow = 0;
        }

        static void EnsureCsv()
        {
            if (_writer != null) return;
            try
            {
                string dir = Config.Paths.LogDir;
                Directory.CreateDirectory(dir);
                string ts = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                _path = Path.Combine(dir, $"fps_{ts}.csv");
                _writer = new StreamWriter(_path, append: false);
                _writer.WriteLine("t_sec,smoothed_fps,peak_dt_ms,frames_in_window");
                _writer.Flush();
                MelonLogger.Msg($"[FPS] logging to {_path}");
            }
            catch (Exception ex) { MelonLogger.Warning("[FPS] EnsureCsv threw: " + ex.Message); }
        }
    }
}
