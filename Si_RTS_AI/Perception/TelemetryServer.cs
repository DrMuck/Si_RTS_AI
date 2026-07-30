using MelonLoader;
using Newtonsoft.Json;
using Silica;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using UnityEngine;

namespace Si_RTS_AI.Perception
{
    /// <summary>
    /// Tiny HTTP server that streams live AI state + perception-layer snapshots to
    /// a browser-based viewer for real-time debugging on the ultrawide monitor.
    ///
    /// v0.7.56 endpoints (all GET, all localhost by default):
    ///   /catalog             → JSON list of every {team, layer} that has been snapshot at least once
    ///                          + width/height/origin/cellSize/typeCode/lastTick/lastRoundTime
    ///   /layer/{team}/{name} → raw binary of the LATEST cached frame (same format as disk frames)
    ///   /state               → JSON: {map, roundTime, teams:[{name, cash, capacity, structures:{...}}]}
    ///   /                    → tiny status page
    ///
    /// Wire-up: TestHarness starts/stops the listener based on a MelonPreferences
    /// int pref HeadlessTest_TelemetryPort (default 0 = disabled). Real games leave
    /// this off — the port only opens for headless soak runs.
    ///
    /// Thread model: HttpListener callbacks run on a background thread. All reads
    /// go through LayerReplay's cached byte[] snapshot (which is only replaced on
    /// the main thread, never mutated in-place) — so no locking needed on the
    /// response path beyond LayerReplay's own catalog lock. State() also snapshots
    /// Team data into local variables before formatting to avoid mid-response
    /// mutation.
    /// </summary>
    internal static class TelemetryServer
    {
        static HttpListener? _listener;
        static Thread?       _thread;
        static volatile bool _running;
        static int           _port;

        internal static bool IsRunning => _running;
        internal static int  Port      => _port;

        internal static void Start(int port)
        {
            if (_running) return;
            try
            {
                _listener = new HttpListener();
                _listener.Prefixes.Add($"http://localhost:{port}/");
                _listener.Start();
                _port = port;
                _running = true;
                _thread = new Thread(Loop) { IsBackground = true, Name = "RTSA-Telemetry" };
                _thread.Start();
                MelonLogger.Msg($"[RTSA/Telemetry] Listening on http://localhost:{port}/");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[RTSA/Telemetry] Failed to start on port {port}: {ex.Message}");
                _running = false;
                _listener = null;
            }
        }

        internal static void Stop()
        {
            if (!_running) return;
            _running = false;
            try { _listener?.Stop(); _listener?.Close(); } catch { }
            _listener = null;
            _thread = null;
            MelonLogger.Msg("[RTSA/Telemetry] Stopped.");
        }

        // ================================================================

        static void Loop()
        {
            while (_running && _listener != null)
            {
                HttpListenerContext ctx;
                try { ctx = _listener.GetContext(); }
                catch { break; }
                try { Handle(ctx); }
                catch (Exception ex)
                {
                    try
                    {
                        MelonLogger.Warning($"[RTSA/Telemetry] handler threw: {ex.Message}");
                        ctx.Response.StatusCode = 500;
                        ctx.Response.Close();
                    }
                    catch { }
                }
            }
        }

        static void Handle(HttpListenerContext ctx)
        {
            var req = ctx.Request;
            var res = ctx.Response;
            res.Headers.Add("Access-Control-Allow-Origin", "*");   // let file:// viewer talk to us
            res.Headers.Add("Cache-Control", "no-store");

            string path = req.Url.AbsolutePath;

            if (path == "/" || path == "/index.html")
            {
                WriteText(res, 200, "text/plain",
                    $"Si_RTS_AI telemetry server\nport = {_port}\n" +
                    "endpoints:\n  /catalog\n  /layer/{team}/{name}\n  /state\n");
                return;
            }
            if (path == "/catalog")   { WriteCatalog(res);   return; }
            if (path == "/state")     { WriteState(res);     return; }
            if (path == "/entities")  { WriteEntities(res);  return; }
            if (path == "/patches")   { WritePatches(res);   return; }
            if (path == "/resources") { WriteResources(res); return; }
            if (path.StartsWith("/layer/")) { WriteLayer(res, path); return; }
            if (path.StartsWith("/icon/"))  { WriteIcon(res, path);  return; }

            res.StatusCode = 404;
            res.Close();
        }

        static void WriteCatalog(HttpListenerResponse res)
        {
            var cat = MapLayers.LayerReplay.CatalogSnapshot();
            // Materialize into a JSON-friendly array. Newtonsoft handles the rest.
            var list = new List<object>(cat.Count);
            foreach (var kv in cat)
            {
                var e = kv.Value;
                list.Add(new
                {
                    team = e.TeamName,
                    name = e.LayerName,
                    type = e.TypeCode,
                    w = e.Width,
                    h = e.Height,
                    originX = e.OriginX,
                    originZ = e.OriginZ,
                    cellSize = e.CellSize,
                    lastTick = e.LastTick,
                    lastRoundTime = e.LastRoundTime
                });
            }
            var body = new
            {
                map = MapLayers.LayerReplay.CurrentMap,
                roundTime = MapLayers.LayerReplay.CurrentRoundTime,
                layers = list
            };
            WriteJson(res, 200, body);
        }

        static void WriteLayer(HttpListenerResponse res, string path)
        {
            // /layer/{team}/{name}
            var parts = path.Substring("/layer/".Length).Split(new[] { '/' }, 2);
            if (parts.Length != 2 || string.IsNullOrEmpty(parts[0]) || string.IsNullOrEmpty(parts[1]))
            {
                WriteText(res, 400, "text/plain", "usage: /layer/{team}/{name}");
                return;
            }
            var bytes = MapLayers.LayerReplay.GetCachedBytes(parts[0], parts[1]);
            if (bytes == null) { WriteText(res, 404, "text/plain", "no snapshot yet"); return; }
            res.StatusCode = 200;
            res.ContentType = "application/octet-stream";
            res.ContentLength64 = bytes.LongLength;
            res.OutputStream.Write(bytes, 0, bytes.Length);
            res.OutputStream.Close();
        }

        static void WriteState(HttpListenerResponse res)
        {
            // Snapshot teams + counts into local structs before formatting. Team
            // collections may be mutated on the main thread; we take fields once.
            var teams = new List<object>();
            try
            {
                foreach (var kv in Silica.AI.AIManager.Commanders)
                {
                    var team = kv.Key;
                    if (team == null) continue;
                    int cash = 0, cap = 0, refCount = 0, hqCount = 0, unitCount = 0;
                    try { cash = team.TotalResources; } catch { }
                    try { cap = team.ResourceCapacity; } catch { }
                    try
                    {
                        var s = team.Structures;
                        if (s != null)
                            for (int i = 0; i < s.Count; i++)
                            {
                                var st = s[i]; if (st?.ObjectInfo == null) continue;
                                var n = st.ObjectInfo.DisplayName;
                                if (n == "Refinery") refCount++;
                                else if (n == "Headquarters" || n == "Nest") hqCount++;
                            }
                    }
                    catch { }
                    try { unitCount = team.Units?.Count ?? 0; } catch { }
                    // Cumulative gross income since round start — used by the
                    // viewer's eco chart as the "total resources over time" line.
                    // Includes drained cash (which was earned before it was drained).
                    int cumulativeIncome = 0;
                    try { cumulativeIncome = EcoRateSampler.GetCumulativeIncome(team); } catch { }

                    // Tech tier for the viewer's stats table. Reads the game's
                    // own team.TechnologyTier / TechnologyTierLimitMax properties
                    // (populated as Research structures complete). Also emit the
                    // display-name list of Research structures for hover diagnostics.
                    int techTier = 0, techTierMax = 0;
                    try { var (t, mx) = Planning.TechPlanner.GetTierLevels(team); techTier = t; techTierMax = mx; } catch { }
                    List<string> techBuilt = null;
                    try { techBuilt = Planning.TechPlanner.GetBuiltTechTiers(team); } catch { }

                    teams.Add(new
                    {
                        name = team.name,
                        cash,
                        capacity = cap,
                        refineries = refCount,
                        bases = hqCount,
                        units = unitCount,
                        cumulativeIncome,
                        techTier,
                        techTierMax,
                        tech = techBuilt ?? new List<string>()
                    });
                }
            }
            catch (Exception ex) { MelonLogger.Warning($"[RTSA/Telemetry] state build threw: {ex.Message}"); }

            // Server FPS — EMA maintained in Si_RTS_AI.OnUpdate on the main
            // thread. Reading from this HTTP thread is a plain float load;
            // torn reads produce visible garbage very rarely and the viewer
            // averages anyway.
            int serverFps = (int)Si_RTS_AI._serverFps;

            var body = new
            {
                map = MapLayers.LayerReplay.CurrentMap,
                roundTime = MapLayers.LayerReplay.CurrentRoundTime,
                serverFps,
                teams
            };
            WriteJson(res, 200, body);
        }

        // Returns every ResourceArea (biotic / minerite patch) on the map as
        // JSON: position, current amount, max amount, type. Used by the offline
        // eco simulator to reconstruct the map's resource layout. Emitted for
        // every ResourceType — the sim / analysis tool decides which are
        // relevant per faction.
        static void WritePatches(HttpListenerContext ctxOrNull, HttpListenerResponse res)
        {
            var patches = new List<object>();
            try
            {
                var all = ResourceArea.AllResourceAreas;
                if (all != null)
                    for (int i = 0; i < all.Count; i++)
                    {
                        var ra = all[i];
                        if (ra == null) continue;
                        try
                        {
                            Vector3 p = ra.SignalCenter;
                            patches.Add(new
                            {
                                x = p.x,
                                z = p.z,
                                y = p.y,
                                type = ra.ResourceType.ToString(),
                                current = ra.ResourceAmountCurrent,
                                max = ra.ResourceAmountMax,
                                empty = ra.IsEmpty,
                            });
                        }
                        catch { }
                    }
            }
            catch (Exception ex) { MelonLogger.Warning($"[RTSA/Telemetry] /patches build threw: {ex.Message}"); }

            var body = new
            {
                map = MapLayers.LayerReplay.CurrentMap,
                roundTime = MapLayers.LayerReplay.CurrentRoundTime,
                patches
            };
            WriteJson(res, 200, body);
        }
        static void WritePatches(HttpListenerResponse res) => WritePatches(null, res);

        // Serves tactical icons (Tac_*.png) exported from the Silica assets so the
        // browser viewer can overlay real unit sprites on the map. Files under a fixed
        // root; strict filename filter to prevent path traversal.
        const string ICON_ROOT = @"C:\SilicaAssets\ExportedProject\Assets\Texture2D";
        static readonly Dictionary<string, byte[]> _iconCache = new Dictionary<string, byte[]>();
        static readonly object _iconCacheLock = new object();

        static void WriteIcon(HttpListenerResponse res, string path)
        {
            var name = path.Substring("/icon/".Length);
            if (string.IsNullOrEmpty(name) ||
                name.Contains('/') || name.Contains('\\') || name.Contains("..") ||
                !name.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            {
                WriteText(res, 400, "text/plain", "bad icon name");
                return;
            }

            byte[]? bytes;
            lock (_iconCacheLock) _iconCache.TryGetValue(name, out bytes);
            if (bytes == null)
            {
                var full = Path.Combine(ICON_ROOT, name);
                if (!File.Exists(full)) { WriteText(res, 404, "text/plain", "no such icon"); return; }
                try { bytes = File.ReadAllBytes(full); }
                catch (Exception ex) { WriteText(res, 500, "text/plain", "read err " + ex.Message); return; }
                lock (_iconCacheLock) _iconCache[name] = bytes;
            }

            res.StatusCode = 200;
            res.ContentType = "image/png";
            res.Headers.Add("Cache-Control", "public, max-age=3600");
            res.ContentLength64 = bytes.LongLength;
            res.OutputStream.Write(bytes, 0, bytes.Length);
            res.OutputStream.Close();
        }

        static void WriteResources(HttpListenerResponse res)
        {
            // All non-empty resource areas — full map view (matches the P32 planner
            // which reads AllResourceAreas). Position from SignalCenter, type from
            // ResourceType enum. Client uses these to overlay Balterium/Biotics
            // icons on the terrain PNG so the eco layout is visible at a glance.
            var list = new List<object>(64);
            try
            {
                var all = ResourceArea.AllResourceAreas;
                if (all != null)
                {
                    for (int i = 0; i < all.Count; i++)
                    {
                        var ra = all[i];
                        if (ra == null) continue;
                        if (ra.IsEmpty) continue;
                        UnityEngine.Vector3 p;
                        try { p = ra.SignalCenter; } catch { continue; }
                        string type = "?"; try { type = ra.ResourceType.ToString(); } catch { }
                        list.Add(new { type, x = p.x, z = p.z });
                    }
                }
            }
            catch (Exception ex) { MelonLogger.Warning($"[RTSA/Telemetry] resources build threw: {ex.Message}"); }

            var body = new
            {
                map = MapLayers.LayerReplay.CurrentMap,
                roundTime = MapLayers.LayerReplay.CurrentRoundTime,
                resources = list
            };
            WriteJson(res, 200, body);
        }

        static void WriteEntities(HttpListenerResponse res)
        {
            // Snapshot every unit + structure across every team into a flat list of
            // {team, kind, name, x, z} so the browser viewer can render a live map.
            // Coordinates are world-space; the /catalog world-origin + cellSize +
            // grid width/height let the client transform to canvas pixels.
            var units      = new List<object>(256);
            var structures = new List<object>(64);
            try
            {
                foreach (var kv in Silica.AI.AIManager.Commanders)
                {
                    var team = kv.Key;
                    if (team == null) continue;
                    string tname = team.name ?? "?";

                    try
                    {
                        var sList = team.Structures;
                        if (sList != null)
                        {
                            for (int i = 0; i < sList.Count; i++)
                            {
                                var s = sList[i];
                                if (s == null || s.ObjectInfo == null) continue;
                                UnityEngine.Vector3 p; try { p = s.transform.position; } catch { continue; }
                                structures.Add(new
                                {
                                    team = tname,
                                    name = s.ObjectInfo.DisplayName,
                                    x = p.x,
                                    z = p.z,
                                    building = false
                                });
                            }
                        }
                    } catch { }

                    try
                    {
                        var uList = team.Units;
                        if (uList != null)
                        {
                            for (int i = 0; i < uList.Count; i++)
                            {
                                var u = uList[i];
                                if (u == null || u.ObjectInfo == null) continue;
                                UnityEngine.Vector3 p; try { p = u.transform.position; } catch { continue; }
                                units.Add(new
                                {
                                    team = tname,
                                    name = u.ObjectInfo.DisplayName,
                                    x = p.x,
                                    z = p.z
                                });
                            }
                        }
                    } catch { }
                }
            }
            catch (Exception ex) { MelonLogger.Warning($"[RTSA/Telemetry] entities build threw: {ex.Message}"); }

            // Construction sites — buildings still being built. Added to the same
            // `structures` array with building=true so the client can render them
            // at reduced opacity without changing its rendering loop.
            try
            {
                var sites = ConstructionSite.ConstructionSites;
                if (sites != null)
                {
                    for (int i = 0; i < sites.Count; i++)
                    {
                        var cs = sites[i];
                        if (cs == null || cs.IsDestroyed || cs.ObjectInfo == null || cs.Team == null) continue;
                        UnityEngine.Vector3 p; try { p = cs.transform.position; } catch { continue; }
                        structures.Add(new
                        {
                            team = cs.Team.name ?? "?",
                            name = cs.ObjectInfo.DisplayName,
                            x = p.x,
                            z = p.z,
                            building = true
                        });
                    }
                }
            }
            catch (Exception ex) { MelonLogger.Warning($"[RTSA/Telemetry] construction sites build threw: {ex.Message}"); }

            var body = new
            {
                map = MapLayers.LayerReplay.CurrentMap,
                roundTime = MapLayers.LayerReplay.CurrentRoundTime,
                world = new
                {
                    originX = MapLayers.GridWorld.OriginX,
                    originZ = MapLayers.GridWorld.OriginZ,
                    cellSize = MapLayers.GridWorld.CellSize,
                    gridW = MapLayers.GridWorld.Width,
                    gridH = MapLayers.GridWorld.Height
                },
                structures,
                units
            };
            WriteJson(res, 200, body);
        }

        // ================================================================

        static void WriteJson(HttpListenerResponse res, int status, object body)
        {
            var json = JsonConvert.SerializeObject(body);
            WriteText(res, status, "application/json", json);
        }

        static void WriteText(HttpListenerResponse res, int status, string contentType, string body)
        {
            var buf = Encoding.UTF8.GetBytes(body);
            res.StatusCode = status;
            res.ContentType = contentType;
            res.ContentLength64 = buf.LongLength;
            res.OutputStream.Write(buf, 0, buf.Length);
            res.OutputStream.Close();
        }
    }
}
