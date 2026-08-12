using MelonLoader;
using Silica;
using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Si_RTS_AI.Perception.MapLayers
{
    /// <summary>
    /// Snapshots layer state to disk periodically AND caches the latest snapshot in
    /// memory so the telemetry HTTP endpoint can serve it live. Together with the
    /// standalone browser viewer, this lets us visualize all AI perception layers
    /// live during a soak run and scrub through historical rounds afterward.
    ///
    /// v0.7.56 changes:
    ///  - Per-team subdirs: <roundDir>/<teamNameSafe>/<layerName>/frame-*.bin
    ///  - Human layers included (was Alien-only): balterium_discovered, hq_mask,
    ///    hq_buildable_mask, balterium_weighted, refinery_pressure, hq_expansion_value,
    ///    distance_to_hq
    ///  - Latest byte-array snapshot cached per (team,layer) for TelemetryServer
    ///  - Catalog exposed so the viewer knows what layers are available
    ///
    /// Frame binary format (unchanged):
    ///   uint32 magic ('RTLF') | uint8 typeCode (1=byte,4=int) | int32 tickNo |
    ///   float32 roundTime | int32 w | int32 h | T[w*h] data
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
        // 0 = round hasn't started yet. Set when MissionState flips to STARTED,
        // NOT at scene load — the pre-round lobby/countdown can sit at INIT for
        // minutes and used to be billed to t, so every timeline (build order,
        // opener gates, eco curves, kill log) was offset by however long the
        // lobby happened to last.
        static float  _roundStartTime;
        static float  _sceneLoadedAt;
        static bool   _roundStarted;

        // Only used when MissionState is unreadable (non-MP_Strategy mode, or the
        // game object isn't up yet). Never fires while we CAN read the state and it
        // simply says INIT — a real server waiting for players must not start the
        // clock early.
        const float UNREADABLE_FALLBACK_SEC = 60f;

        // ---- In-memory catalog for TelemetryServer ----
        public sealed class CatalogEntry
        {
            public string TeamName = "";
            public string LayerName = "";
            public string TypeCode = "byte";   // "byte" | "int"
            public int    Width;
            public int    Height;
            public float  OriginX;
            public float  OriginZ;
            public float  CellSize;
            public int    LastTick;
            public float  LastRoundTime;
        }

        static readonly Dictionary<string, CatalogEntry> _catalog = new Dictionary<string, CatalogEntry>();
        static readonly Dictionary<string, byte[]>       _cache   = new Dictionary<string, byte[]>();
        static readonly object _cacheLock = new object();

        public static string CurrentMap { get; private set; } = "";

        /// <summary>
        /// Seconds since the round actually STARTED (0 during the pre-round lobby
        /// and countdown). This is the mod-wide round clock — build timeline,
        /// opener/scout gates, eco samplers, combat log and the telemetry viewer
        /// all read it, so they stay on one shared t.
        /// </summary>
        public static float  CurrentRoundTime => _roundStarted ? Time.time - _roundStartTime : 0f;

        /// <summary>False while the map is loaded but the round hasn't begun.</summary>
        public static bool   RoundStarted => _roundStarted;

        static string KeyOf(string team, string layer) => team + "/" + layer;

        public static IReadOnlyDictionary<string, CatalogEntry> CatalogSnapshot()
        {
            lock (_cacheLock) return new Dictionary<string, CatalogEntry>(_catalog);
        }

        public static byte[]? GetCachedBytes(string team, string layer)
        {
            lock (_cacheLock)
            {
                if (_cache.TryGetValue(KeyOf(team, layer), out var b)) return b;
                return null;
            }
        }

        // ================================================================

        public static void OnNewRound(string mapName)
        {
            try
            {
                _frameIndex = 0;
                _lastSnapshotTime = -SNAPSHOT_INTERVAL_SEC;
                // Arm, don't start: TickRoundClock() starts t when the round does.
                _roundStartTime = 0f;
                _roundStarted   = false;
                _sceneLoadedAt  = Time.time;
                CurrentMap = mapName ?? "?";

                lock (_cacheLock)
                {
                    _catalog.Clear();
                    _cache.Clear();
                }

                var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                _roundDir = Path.Combine("UserData", "RTSA", "layer-replay",
                    $"round-{stamp}-{Safe(mapName)}");
                Directory.CreateDirectory(_roundDir);

                var m = new System.Text.StringBuilder();
                m.Append("Si_RTS_AI layer replay v2\n");
                m.Append("map = ").Append(mapName ?? "?").Append('\n');
                m.Append("started = ").Append(DateTime.Now.ToString("O")).Append('\n');
                m.Append("gridWidth = ").Append(GridWorld.Width).Append('\n');
                m.Append("gridHeight = ").Append(GridWorld.Height).Append('\n');
                m.Append("cellSize = ").Append(GridWorld.CellSize.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append('\n');
                m.Append("originX = ").Append(GridWorld.OriginX.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append('\n');
                m.Append("originZ = ").Append(GridWorld.OriginZ.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append('\n');
                m.Append("snapshotIntervalSec = ").Append(SNAPSHOT_INTERVAL_SEC).Append('\n');
                m.Append("layout = <team>/<layer>/frame-NNNNN.bin + frames.txt\n");
                m.Append("frameFormat = uint32 magic; uint8 typeCode; int32 tickNo; float32 roundTime; int32 w; int32 h; T[w*h] data\n");
                File.WriteAllText(Path.Combine(_roundDir, "manifest.txt"), m.ToString());

                MelonLogger.Msg($"[RTSA/Layers] Recording replay to {_roundDir}");
            }
            catch (Exception ex) { MelonLogger.Warning("[RTSA/Layers] Replay init threw: " + ex.Message); _roundDir = ""; }
        }

        /// <summary>
        /// Called every frame from OnUpdate. Starts the round clock the first frame
        /// the game reports the round as running. Costs one branch once the clock is
        /// running, so it's fine on the frame path.
        /// </summary>
        public static void TickRoundClock()
        {
            if (_roundStarted) return;

            bool readable = false;
            bool active   = false;
            try
            {
                var gm = GameMode.CurrentGameMode;
                if (gm != null)
                {
                    // MP_Strategy is the RTS mode we care about; MissionState is the
                    // authoritative INIT -> STARTED -> ENDED signal (the same one the
                    // test harness watches). Other modes only expose GameOngoing.
                    var mp = gm as MP_Strategy;
                    readable = true;
                    active = mp != null
                        ? mp.MissionState == GameModeExt.EMissionState.STARTED
                        : gm.GameOngoing;
                }
            }
            catch { readable = false; }

            if (readable && active)
            {
                MarkRoundStarted("MissionState=STARTED");
                return;
            }

            if (!readable && _sceneLoadedAt > 0f &&
                Time.time - _sceneLoadedAt >= UNREADABLE_FALLBACK_SEC)
            {
                MarkRoundStarted($"fallback — mission state unreadable for {UNREADABLE_FALLBACK_SEC:F0}s");
            }
        }

        static void MarkRoundStarted(string reason)
        {
            _roundStarted   = true;
            _roundStartTime = Time.time;
            float lobby = _sceneLoadedAt > 0f ? Time.time - _sceneLoadedAt : 0f;
            MelonLogger.Msg($"[RTSA/Layers] Round clock t=0 ({reason}); {lobby:F1}s of pre-round excluded.");
        }

        public static void MaybeSnapshot(int tickNo, Team team)
        {
            if (team == null) return;
            if (string.IsNullOrEmpty(_roundDir)) return;
            float now = Time.time;
            if (now - _lastSnapshotTime < SNAPSHOT_INTERVAL_SEC) return;
            _lastSnapshotTime = now;
            float roundTime = CurrentRoundTime;

            string teamName = SafeTeamName(team);

            try
            {
                AlienEcoLayers.RefreshAllForSnapshot(team);

                WriteByteFrame(teamName, "biotics_discovered", AlienEcoLayers.GetBioticsDiscovered(team), tickNo, roundTime);
                WriteByteFrame(teamName, "bc_mask",            AlienEcoLayers.GetBcMask(team),            tickNo, roundTime);
                WriteIntFrame (teamName, "biotics_weighted",   AlienEcoLayers.GetBioticsWeighted(team),   tickNo, roundTime);
                WriteIntFrame (teamName, "cyst_pressure",      AlienEcoLayers.GetCystPressure(team),      tickNo, roundTime);
                WriteIntFrame (teamName, "shrimp_density",    AlienEcoLayers.GetShrimpDensity(team),     tickNo, roundTime);
                WriteByteFrame(teamName, "fow_active",         FoWLayers.GetActive(team),                 tickNo, roundTime);
                WriteByteFrame(teamName, "fow_explored",       FoWLayers.GetExplored(team),               tickNo, roundTime);
                // Enemy pressure and enemy cash, so a replay shows what the
                // military layer was reacting to and not only what it did.
                WriteIntFrame (teamName, "enemy_pressure",     ThreatMap.PressureLayer(),                 tickNo, roundTime);
                WriteIntFrame (teamName, "enemy_value",        ThreatMap.ValueLayer(),                    tickNo, roundTime);
                _frameIndex++;
            }
            catch (Exception ex) { MelonLogger.Warning("[RTSA/Layers] Snapshot threw: " + ex.Message); }
        }

        public static void MaybeSnapshotHuman(int tickNo, Team team)
        {
            if (team == null) return;
            if (string.IsNullOrEmpty(_roundDir)) return;
            float now = Time.time;
            // Human path shares SNAPSHOT_INTERVAL_SEC with Alien via _lastSnapshotTime.
            // BUG (2026-07-08): the comment said "Alien path already ran" but there
            // was no actual gate — so this ran every 1Hz tick × 2 Human teams,
            // producing sustained 60-80ms frame stalls (layer= dominated LAG log).
            // Fix: gate on "did Alien snap this SAME tick?" — _lastSnapshotTime
            // is only bumped inside MaybeSnapshot when the interval elapsed, so
            // requiring now == _lastSnapshotTime aligns Human snapshots with Alien.
            if (now - _lastSnapshotTime > 0.001f) return;   // Alien didn't snap this tick — skip
            float roundTime = CurrentRoundTime;
            string teamName = SafeTeamName(team);

            try
            {
                HumanEcoLayers.RefreshAllForSnapshot(team);

                WriteByteFrame(teamName, "balterium_discovered",  HumanEcoLayers.GetBalteriumDiscovered(team),  tickNo, roundTime);
                WriteByteFrame(teamName, "hq_mask",               HumanEcoLayers.GetHqMask(team),               tickNo, roundTime);
                WriteByteFrame(teamName, "hq_min_sep_mask",       HumanEcoLayers.GetHqMinSepMask(team),         tickNo, roundTime);
                WriteByteFrame(teamName, "hq_buildable_mask",     HumanEcoLayers.GetHqBuildableMask(team),      tickNo, roundTime);
                WriteIntFrame (teamName, "balterium_weighted",    HumanEcoLayers.GetBalteriumWeighted(team),    tickNo, roundTime);
                WriteIntFrame (teamName, "refinery_pressure",     HumanEcoLayers.GetRefineryPressure(team),     tickNo, roundTime);
                WriteIntFrame (teamName, "eco_hq_expansion_value",HumanEcoLayers.GetEcoHqExpansionValue(team),  tickNo, roundTime);
                WriteByteFrame(teamName, "fow_active",            FoWLayers.GetActive(team),                    tickNo, roundTime);
                WriteByteFrame(teamName, "fow_explored",          FoWLayers.GetExplored(team),                  tickNo, roundTime);
            }
            catch (Exception ex) { MelonLogger.Warning("[RTSA/Layers/Human] Snapshot threw: " + ex.Message); }
        }

        // ================================================================

        // Deferred write queue — user 2026-07-08: "small pause timer between
        // each layer fetch could help a bit." Instead of doing ~15 file writes
        // synchronously every 5s (~15-22ms burst), we serialize the payload
        // upfront then drain ONE write per frame via DrainPending() called
        // from OnUpdate. Spreads the disk I/O across ~15-25 frames = <2ms
        // per frame instead of a single 15-22ms burst.
        struct Pending
        {
            public string   Team;
            public string   Layer;
            public byte[]   Payload;
            public string   Type;      // "byte" or "int"
            public int      TickNo;
            public float    RoundTime;
        }
        static readonly System.Collections.Generic.Queue<Pending> _pending
            = new System.Collections.Generic.Queue<Pending>();

        public static void DrainPending()
        {
            if (_pending.Count == 0) return;
            var p = _pending.Dequeue();
            try
            {
                UpdateCatalog(p.Team, p.Layer, p.Type, p.TickNo, p.RoundTime);
                CacheBytes(p.Team, p.Layer, p.Payload);
                var (path, idx) = OpenFrame(p.Team, p.Layer);
                File.WriteAllBytes(path, p.Payload);
                File.AppendAllText(idx, $"{_frameIndex},{p.TickNo},{p.RoundTime:F2}\n");
            }
            catch (Exception ex) { MelonLogger.Warning("[RTSA/Layers] Deferred write threw: " + ex.Message); }
        }

        static void WriteByteFrame(string teamName, string layerName, LayerB layer, int tickNo, float roundTime)
        {
            // Serialize payload NOW so we snapshot layer state at call time —
            // otherwise a subsequent RefreshAllForSnapshot would mutate the
            // layer bytes before our deferred write runs.
            _pending.Enqueue(new Pending {
                Team = teamName, Layer = layerName,
                Payload = BuildFrameBytes(TYPE_BYTE, tickNo, roundTime, layer.Data),
                Type = "byte", TickNo = tickNo, RoundTime = roundTime,
            });
        }

        static void WriteIntFrame(string teamName, string layerName, LayerI layer, int tickNo, float roundTime)
        {
            byte[] data = new byte[layer.Data.Length * sizeof(int)];
            Buffer.BlockCopy(layer.Data, 0, data, 0, data.Length);
            _pending.Enqueue(new Pending {
                Team = teamName, Layer = layerName,
                Payload = BuildFrameBytes(TYPE_INT, tickNo, roundTime, data),
                Type = "int", TickNo = tickNo, RoundTime = roundTime,
            });
        }

        static byte[] BuildFrameBytes(byte typeCode, int tickNo, float roundTime, byte[] data)
        {
            const int header = 4 + 1 + 4 + 4 + 4 + 4;
            var buf = new byte[header + data.Length];
            using (var ms = new MemoryStream(buf))
            using (var bw = new BinaryWriter(ms))
            {
                bw.Write(MAGIC);
                bw.Write(typeCode);
                bw.Write(tickNo);
                bw.Write(roundTime);
                bw.Write(GridWorld.Width);
                bw.Write(GridWorld.Height);
                bw.Write(data);
            }
            return buf;
        }

        static void UpdateCatalog(string teamName, string layerName, string typeCode, int tick, float roundTime)
        {
            lock (_cacheLock)
            {
                string key = KeyOf(teamName, layerName);
                if (!_catalog.TryGetValue(key, out var e))
                {
                    e = new CatalogEntry
                    {
                        TeamName = teamName, LayerName = layerName, TypeCode = typeCode,
                        Width = GridWorld.Width, Height = GridWorld.Height,
                        OriginX = GridWorld.OriginX, OriginZ = GridWorld.OriginZ,
                        CellSize = GridWorld.CellSize
                    };
                    _catalog[key] = e;
                }
                e.LastTick = tick;
                e.LastRoundTime = roundTime;
                // Reflect any grid resize between rounds (unlikely mid-round).
                e.Width = GridWorld.Width;
                e.Height = GridWorld.Height;
                e.OriginX = GridWorld.OriginX;
                e.OriginZ = GridWorld.OriginZ;
            }
        }

        static void CacheBytes(string teamName, string layerName, byte[] payload)
        {
            lock (_cacheLock)
            {
                _cache[KeyOf(teamName, layerName)] = payload;
            }
        }

        static (string framePath, string idxPath) OpenFrame(string teamName, string layerName)
        {
            var layerDir = Path.Combine(_roundDir, teamName, layerName);
            Directory.CreateDirectory(layerDir);
            return (Path.Combine(layerDir, $"frame-{_frameIndex:D5}.bin"),
                    Path.Combine(layerDir, "frames.txt"));
        }

        static string SafeTeamName(Team team)
        {
            try { return Safe(team?.name ?? "team?"); } catch { return "team?"; }
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
