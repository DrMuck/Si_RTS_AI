using MelonLoader;
using System;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace Si_RTS_AI.Planning
{
    /// <summary>
    /// A plan you cannot look at is a plan you can only judge by what got built,
    /// which is how "noding madness" went unexplained for a whole round. Every
    /// revision is written to disk for post-match review and the latest one is
    /// served at /blueprint so the layers viewer can draw planned-versus-built
    /// while the round is running.
    ///
    /// Written as JSON with the coordinates in world space — the viewer already
    /// maps world to screen for entities, so no grid transform is needed here.
    /// </summary>
    internal static class BlueprintStore
    {
        static string _roundDir = "";
        static string _latestJson = "{\"revision\":0,\"items\":[]}";
        static readonly object _lock = new object();

        /// <summary>Latest plan as JSON, for the telemetry server.</summary>
        internal static string LatestJson { get { lock (_lock) return _latestJson; } }

        internal static void ResetForNewRound()
        {
            lock (_lock) _latestJson = "{\"revision\":0,\"items\":[]}";
            _roundDir = "";
        }

        internal static void Write(EcoState s)
        {
            string json;
            try { json = Serialize(s); }
            catch (Exception ex) { MelonLogger.Warning("[BLUEPRINT] serialize threw: " + ex.Message); return; }

            lock (_lock) _latestJson = json;
            if (!BlueprintConfig.Persist) return;

            try
            {
                if (_roundDir.Length == 0)
                {
                    string map = Perception.MapLayers.GridWorld.CurrentMapName ?? "?";
                    string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                    _roundDir = Path.Combine(Config.Paths.LogDir, "blueprint",
                                             $"round-{stamp}-{Safe(map)}");
                    Directory.CreateDirectory(_roundDir);
                    MelonLogger.Msg($"[BLUEPRINT] writing plans to {_roundDir}");
                }
                File.WriteAllText(Path.Combine(_roundDir, $"rev-{Blueprint.Revision:D4}.json"), json);
            }
            catch (Exception ex) { MelonLogger.Warning("[BLUEPRINT] write threw: " + ex.Message); }
        }

        static string Serialize(EcoState s)
        {
            var sb = new StringBuilder(8192);
            sb.Append('{');
            Str(sb, "map", Perception.MapLayers.GridWorld.CurrentMapName ?? "?"); sb.Append(',');
            Num(sb, "revision", Blueprint.Revision); sb.Append(',');
            Num(sb, "roundTime", Blueprint.PlannedAtRoundS); sb.Append(',');
            Num(sb, "cash", s.cash); sb.Append(',');
            Num(sb, "untapped", Blueprint.TerminalCount); sb.Append(',');
            Num(sb, "covered", Blueprint.CoveredCount); sb.Append(',');
            Num(sb, "sites", Blueprint.SiteCount); sb.Append(',');
            Num(sb, "nodes", Blueprint.NodeCount); sb.Append(',');
            Num(sb, "cysts", Blueprint.CystCount); sb.Append(',');
            Num(sb, "cost", Blueprint.PlanCost); sb.Append(',');

            // PLANNED — what this revision intends to build, in build order.
            sb.Append("\"items\":[");
            var items = Blueprint.Items;
            for (int i = 0; i < items.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append('{');
                Str(sb, "kind", items[i].kind.ToString()); sb.Append(',');
                Num(sb, "x", items[i].pos.x); sb.Append(',');
                Num(sb, "z", items[i].pos.z); sb.Append(',');
                Num(sb, "fx", items[i].from.x); sb.Append(',');
                Num(sb, "fz", items[i].from.z); sb.Append(',');
                Num(sb, "branch", items[i].branch); sb.Append(',');
                Num(sb, "site", items[i].site); sb.Append(',');
                Num(sb, "order", i); sb.Append(',');
                Str(sb, "why", items[i].why ?? "");
                sb.Append('}');
            }
            sb.Append("],");

            // BUILT — the structures the plan grew from, so a viewer can draw
            // planned against built without a second request.
            sb.Append("\"built\":[");
            bool first = true;
            if (s.nestPos != Vector3.zero) { Built(sb, ref first, "Nest", s.nestPos, true); }
            for (int i = 0; i < s.bcs.Count; i++)   Built(sb, ref first, "BioCache", s.bcs[i].pos,   s.bcs[i].finished);
            for (int i = 0; i < s.nodes.Count; i++) Built(sb, ref first, "Node",     s.nodes[i].pos, s.nodes[i].finished);
            for (int i = 0; i < s.cysts.Count; i++) Built(sb, ref first, "Cyst",     s.cysts[i].pos, s.cysts[i].finished);
            sb.Append("],");

            // BRIDGES — cross-branch joins worth making, best first. The top one
            // is emitted as buildable Node items (site = -1); the rest are here
            // to be looked at.
            sb.Append("\"bridges\":[");
            for (int i = 0; i < Blueprint.Bridges.Count; i++)
            {
                if (i > 0) sb.Append(',');
                var br = Blueprint.Bridges[i];
                sb.Append('{');
                Num(sb, "ax", br.a.x); sb.Append(',');
                Num(sb, "az", br.a.z); sb.Append(',');
                Num(sb, "bx", br.b.x); sb.Append(',');
                Num(sb, "bz", br.b.z); sb.Append(',');
                Num(sb, "hops", br.hops); sb.Append(',');
                Num(sb, "protects", br.protects); sb.Append(',');
                Num(sb, "savedM", br.savedM); sb.Append(',');
                Num(sb, "beyond", br.beyond);
                sb.Append('}');
            }
            sb.Append("],");

            // PATCHES — what the plan was judged against.
            sb.Append("\"patches\":[");
            for (int i = 0; i < s.patches.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append('{');
                Num(sb, "x", s.patches[i].pos.x); sb.Append(',');
                Num(sb, "z", s.patches[i].pos.z); sb.Append(',');
                Num(sb, "remaining", s.patches[i].remaining);
                sb.Append('}');
            }
            sb.Append(']');

            sb.Append('}');
            return sb.ToString();
        }

        static void Built(StringBuilder sb, ref bool first, string kind, Vector3 p, bool finished)
        {
            if (!first) sb.Append(',');
            first = false;
            sb.Append('{');
            Str(sb, "kind", kind); sb.Append(',');
            Num(sb, "x", p.x); sb.Append(',');
            Num(sb, "z", p.z); sb.Append(',');
            sb.Append("\"finished\":").Append(finished ? "true" : "false");
            sb.Append('}');
        }

        static void Str(StringBuilder sb, string k, string v)
        {
            sb.Append('"').Append(k).Append("\":\"");
            for (int i = 0; i < v.Length; i++)
            {
                char c = v[i];
                if (c == '"' || c == '\\') sb.Append('\\').Append(c);
                else if (c >= ' ') sb.Append(c);
            }
            sb.Append('"');
        }

        static void Num(StringBuilder sb, string k, float v) =>
            sb.Append('"').Append(k).Append("\":").Append(v.ToString("F1", CultureInfo.InvariantCulture));

        static void Num(StringBuilder sb, string k, int v) =>
            sb.Append('"').Append(k).Append("\":").Append(v.ToString(CultureInfo.InvariantCulture));

        static string Safe(string s)
        {
            var sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                sb.Append(char.IsLetterOrDigit(c) ? c : '-');
            }
            return sb.ToString();
        }
    }
}
