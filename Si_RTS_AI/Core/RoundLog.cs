using MelonLoader;
using System;
using System.IO;
using System.Text;

namespace Si_RTS_AI.Core
{
    /// <summary>
    /// THE PER-ROUND TEXT LOG: UserData/RTSA/round-&lt;stamp&gt;-&lt;map&gt;.log.
    ///
    /// One open file, flushed once a second — not an open per line.
    /// File.AppendAllText used to open, write and close the round log for every
    /// line, on the game thread. A NarakaCity round on the public server
    /// (2026-09-13 14:30) wrote 323,814 lines that way, inside Harmony postfixes
    /// on the game's own code paths, where the mod's budget meter cannot see
    /// them. The same session recorded 6,325 frame spikes over 200 ms. Lines go
    /// to a StreamWriter that stays open for the round and is flushed from the
    /// tick, so a line costs a memory copy and the disk sees one write per second.
    ///
    /// Off entirely when ModSwitches.RoundLog is false (or the mod is off): no
    /// file is created and Append is a no-op.
    /// </summary>
    internal static class RoundLog
    {
        static string _path = "";
        static StreamWriter _writer;
        static string _writerPath = "";
        static float _flushAt;
        const float FLUSH_S = 1f;

        /// <summary>Current round's file ("" before the first round).</summary>
        internal static string Path => _path;

        /// <summary>Start a new file for this scene and write its header.</summary>
        internal static void Begin(string sceneName)
        {
            Close();
            try { Directory.CreateDirectory(Config.Paths.LogDir); } catch { }
            _path = System.IO.Path.Combine(Config.Paths.LogDir, $"round-{DateTime.Now:yyyyMMdd_HHmmss}-{Safe(sceneName)}.log");
            Append($"# Si_RTS_AI observability log — scene={sceneName} startedAt={DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            Append("# categories: [AI] commander Think summary, [CMD] construct/queue/research request, [ORDER] unit order (attack/move/stop), [SPAWN_S] structure, [SPAWN_U] unit, [PILOT] player controlled-unit change, [TEAM] player team change, [CASH] cash sample");
            MelonLogger.Msg($"[RTSA] New round → logging to {_path}");
        }

        internal static void Append(string line)
        {
            if (string.IsNullOrEmpty(_path) || !Config.ModSwitches.RoundLog || !Config.ModSwitches.Enabled) return;
            try
            {
                if (_writer == null || !string.Equals(_writerPath, _path, StringComparison.Ordinal))
                {
                    Close();
                    _writer = new StreamWriter(_path, append: true, Encoding.UTF8, 1 << 16) { AutoFlush = false };
                    _writerPath = _path;
                }
                _writer.WriteLine(line);
            }
            catch (Exception ex) { MelonLogger.Warning($"[RTSA] round-log write failed: {ex.Message}"); }
        }

        /// <summary>Called from the tick: pushes the buffered log to disk about once a second.</summary>
        internal static void Flush(bool force = false)
        {
            try
            {
                if (_writer == null) return;
                float now = UnityEngine.Time.realtimeSinceStartup;
                if (!force && now - _flushAt < FLUSH_S) return;
                _flushAt = now;
                _writer.Flush();
            }
            catch (Exception ex) { MelonLogger.Warning($"[RTSA] round-log flush failed: {ex.Message}"); }
        }

        internal static void Close()
        {
            try { _writer?.Flush(); _writer?.Dispose(); } catch { }
            _writer = null; _writerPath = "";
        }

        /// <summary>
        /// THE CONSOLE LOG DIES WITH THE NEXT SERVER START. Every military line
        /// from the 2026-08-13 rounds was lost that way. Keep a copy beside the
        /// round log, taken while the round's lines are still in it.
        /// </summary>
        internal static void CopyMelonLog()
        {
            if (string.IsNullOrEmpty(_path) || !Config.ModSwitches.RoundLog) return;
            try
            {
                string src = System.IO.Path.Combine("MelonLoader", "Latest.log");
                if (!File.Exists(src)) return;
                string dst = System.IO.Path.ChangeExtension(_path, null) + ".melon.log";
                using (var fin = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var fout = new FileStream(dst, FileMode.Create, FileAccess.Write, FileShare.Read))
                    fin.CopyTo(fout);
            }
            catch (Exception ex) { MelonLogger.Warning("[RTSA] could not copy Latest.log: " + ex.Message); }
        }

        static string Safe(string s)
        {
            if (string.IsNullOrEmpty(s)) return "unknown";
            var sb = new StringBuilder(s.Length);
            foreach (var c in s)
                sb.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_');
            return sb.ToString();
        }
    }
}
