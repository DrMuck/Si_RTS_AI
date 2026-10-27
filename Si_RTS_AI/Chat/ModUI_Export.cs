// ModUI_Export.cs - builder for the document a server mod hands to Si_ModUI (and any other client that speaks
// its contract). Copy this file into the mod project; it depends on Newtonsoft.Json only, which every server mod has.
//
// Contract (Si_ModUI README, "Mod data"): the ModUI server looks for `public static string ModUIData()` on any type
// of the mod's assembly (found by assembly name = mod id), calls it on every frame build (every few seconds, cached by
// hash), embeds the JSON as the mod's `data` and merges its `commands` into the mod's command list. Every entry here is
// purely additive: the UI only ever sends chat commands, so a mod must stay fully usable from chat without it.
//
//   public static string ModUIData() => ModUIExport.New()
//       .Info("Logistics ON | load 300/s")
//       .Action("/logistics status", "Status", "Settings and your cargo.")
//       .OnOff("/logistics enabled", "Logistics", "Transports carry Balterium.", admin: true, on: Cfg.Enabled)
//       .Value("/logistics share", "Bonus share (0..1)", "Share paid to the pilot.", admin: true, current: "0.05")
//       .Switch("/rtsai", "RTS AI mod", "...", admin: true, current: "auto", ("on", "ON"), ("off", "OFF"), ("auto", "AUTO"))
//       .Picker("/elo seat", "Pin commander", "...", admin: true, arg: "team,player")
//       .Build();
//
// Rules the ModUI server enforces: an entry whose text the Admin Mod registry already knows (the bare "/elo") only
// decorates that command and cannot change its admin flag, so a switch or admin entry needs its own sub-command text
// ("/elo matchmaking", not "/elo"). Admin entries never reach players without the power. Descriptions are plain text:
// angle brackets and rich-text tags are stripped. Keep ModUIData() cheap or cache its result: it runs every few
// seconds; the server only pushes a diff when the result changes.
#nullable disable
using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ModUI
{
    public sealed class ModUIExport
    {
        public const string ContractVersion = "SiModUI/2";

        private readonly JObject root = new JObject();
        private readonly JArray info = new JArray();
        private readonly JArray commands = new JArray();

        public static ModUIExport New() => new ModUIExport();

        // ---- status lines shown above the mod's command list (both tabs) ----
        public ModUIExport Info(string line) { if (!string.IsNullOrEmpty(line)) info.Add(line); return this; }
        // Master switch of the mod: false hides the mod from the player tab (admins still see it, marked "off").
        public ModUIExport Enabled(bool on) { root["enabled"] = on; return this; }
        // Document for `public static string ModUIPlayerData(ulong steamId)`: this player's own state for the named
        // switch / value rows (for example ("/flex", on ? "on" : "off")); the shared export keeps current = "".
        public static string PlayerValues(params (string text, string value)[] values)
        {
            var v = new JObject();
            foreach (var p in values) if (!string.IsNullOrEmpty(p.text) && p.value != null) v[p.text] = p.value;
            return v.Count == 0 ? null : JsonConvert.SerializeObject(new JObject { ["v"] = v }, Formatting.None);
        }
        // A table shown on the mod page above the command list: {"n":title,"h":[headers],"r":[[cells],...]}.
        public ModUIExport Table(string name, string[] headers, params string[][] rows)
        {
            if (!(root["tables"] is JArray tables)) root["tables"] = tables = new JArray();
            tables.Add(TableObject(name, headers, rows)); return this;
        }
        internal static JObject TableObject(string name, string[] headers, string[][] rows)
        {
            var r = new JArray(); foreach (var row in rows ?? new string[0][]) if (row != null) r.Add(new JArray(row));
            return new JObject { ["n"] = name ?? "", ["h"] = new JArray(headers ?? new string[0]), ["r"] = r };
        }
        // Builder for `public static string ModUIPlayerData(ulong steamId)`: this player's own switch / value states and
        // tables (a Leaderboard "my stats" grid). The server refreshes per-player data at most once a minute, or right
        // after a command of that player.
        public sealed class PlayerExport
        {
            private readonly JObject v = new JObject(); private readonly JArray tables = new JArray();
            public PlayerExport Value(string text, string value) { if (!string.IsNullOrEmpty(text) && value != null) v[text] = value; return this; }
            public PlayerExport Table(string name, string[] headers, params string[][] rows) { tables.Add(TableObject(name, headers, rows)); return this; }
            public string Build()
            {
                var doc = new JObject();
                if (v.Count > 0) doc["v"] = v;
                if (tables.Count > 0) doc["tables"] = tables;
                return doc.Count == 0 ? null : JsonConvert.SerializeObject(doc, Formatting.None);
            }
        }
        public static PlayerExport ForPlayer() => new PlayerExport();

        // ---- plain button: sends `text` (plus the Arguments field, if the player typed one) ----
        public ModUIExport Action(string text, string label, string description, bool admin = false)
            => Add(text, label, description, admin, null, null, null);
        // ---- read-only report (status, help, top list): an Action flagged "i", so the chat directory (/mods) and clients
        //      list it under "info" instead of "do and adjust". Without the flag the server guesses from text and label. ----
        public ModUIExport Report(string text, string label, string description, bool admin = false)
            => Add(text, label, description, admin, null, null, null, info: true);

        // ---- button that opens pickers: arg = chain of stages "player", "team", "map", "ban", "choice" (comma separated);
        //      a "choice" stage takes its options from `choices`. The chosen tokens are appended to `text`. ----
        public ModUIExport Picker(string text, string label, string description, bool admin, string arg, params (string value, string label)[] choices)
            => Add(text, label, description, admin, arg, null, Choices(choices));

        // ---- switch row: one button per choice, the one equal to `current` highlighted; a click sends `text + " " + value` ----
        public ModUIExport Switch(string text, string label, string description, bool admin, string current, params (string value, string label)[] choices)
            => Add(text, label, description, admin, null, current ?? "", Choices(choices));
        public ModUIExport OnOff(string text, string label, string description, bool admin, bool on)
            => Switch(text, label, description, admin, on ? "on" : "off", ("on", "ON"), ("off", "OFF"));
        public ModUIExport OnOffAuto(string text, string label, string description, bool admin, bool? setting)
            => Switch(text, label, description, admin, setting == null ? "auto" : setting.Value ? "on" : "off", ("on", "ON"), ("off", "OFF"), ("auto", "AUTO"));
        // ---- multi-select row: one button per choice, every value listed in `current` (comma separated) highlighted;
        //      a click sends `text + " " + value` and the mod adds or removes that value (the next frame shows the real set) ----
        public ModUIExport Multi(string text, string label, string description, bool admin, string current, params (string value, string label)[] choices)
            => Add(text, label, description, admin, null, current ?? "", Choices(choices), multi: true);

        // ---- value row: label with the current value, a text field and SET; SET sends `text + " " + field` ----
        public ModUIExport Value(string text, string label, string description, bool admin, string current)
            => Add(text, label, description, admin, null, current ?? "", null);
        public ModUIExport Value(string text, string label, string description, bool admin, double current)
            => Value(text, label, description, admin, current.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));

        // ---- catalog (Si_Buy, Si_Spawn): a button grid per faction, a click sends `command + " " + name`. Extras the
        //      client understands: "admin": true (shown on the admin tab instead of the player tab), "all": true (faction
        //      tabs instead of the viewer's own faction), "credits": false (no credits line / status call), "offText". ----
        public CatalogBuilder Catalog(string command, bool enabled = true) => new CatalogBuilder(this, command, enabled);
        public sealed class CatalogBuilder
        {
            private readonly ModUIExport owner; private readonly JObject doc; private readonly JObject factions = new JObject();
            private JArray category; private JArray faction;
            internal CatalogBuilder(ModUIExport owner, string command, bool enabled)
            { this.owner = owner; doc = new JObject { ["cmd"] = command, ["enabled"] = enabled, ["catalog"] = factions }; }
            public CatalogBuilder Faction(string name) { faction = new JArray(); factions[name] = faction; category = null; return this; }
            public CatalogBuilder Category(string name) { category = new JArray(); faction.Add(new JObject { ["c"] = name, ["u"] = category }); return this; }
            // An item is [name, cost] or [name, cost, label]: the client shows `label` (or the name) and sends `command + " " + name`;
            // a cost of 0 or less is not shown.
            public CatalogBuilder Item(string name, int cost, string label = null)
            { category.Add(label == null || label == name ? new JArray(name, cost) : new JArray(name, cost, label)); return this; }
            public CatalogBuilder Extra(string key, JToken value) { doc[key] = value; return this; }
            public ModUIExport Done() { foreach (var p in doc.Properties()) owner.root[p.Name] = p.Value; return owner; }
        }

        // ---- browse tree (Si_UnitBalance): sections > entries > groups > rows, read-only, with a "changes only" filter.
        //      A row is [label, value] or [label, value, vanilla, note] when it differs from vanilla. ----
        public BrowseBuilder Browse(string title) => new BrowseBuilder(this, title);
        public sealed class BrowseBuilder
        {
            private readonly ModUIExport owner; private readonly JObject doc; private readonly JArray sections = new JArray();
            private JArray entries, groups, rows; private JObject entry; private int changes;
            internal BrowseBuilder(ModUIExport owner, string title) { this.owner = owner; doc = new JObject { ["title"] = title, ["sections"] = sections }; }
            public BrowseBuilder Section(string name) { CloseEntry(); entries = new JArray(); sections.Add(new JObject { ["n"] = name, ["u"] = entries }); return this; }
            public BrowseBuilder Entry(string name) { CloseEntry(); groups = new JArray(); changes = 0; entry = new JObject { ["n"] = name, ["c"] = 0, ["g"] = groups }; entries.Add(entry); return this; }
            public BrowseBuilder Group(string name) { rows = new JArray(); groups.Add(new JObject { ["n"] = name, ["r"] = rows }); return this; }
            public BrowseBuilder Row(string label, string value, string vanilla = null, string note = null)
            {
                var row = new JArray(label, value);
                if (vanilla != null) { row.Add(vanilla); if (note != null) row.Add(note); changes++; }
                rows.Add(row); return this;
            }
            private void CloseEntry() { if (entry != null) entry["c"] = changes; entry = null; }
            public ModUIExport Done() { CloseEntry(); owner.root["browse"] = doc; return owner; }
        }

        // ---- anything else a client may understand later ----
        public ModUIExport Extra(string key, JToken value) { root[key] = value; return this; }

        public string Build()
        {
            if (info.Count > 0) root["info"] = info;
            if (commands.Count > 0) root["commands"] = commands;
            return root.Count == 0 ? null : JsonConvert.SerializeObject(root, Formatting.None);
        }

        private ModUIExport Add(string text, string label, string description, bool admin, string arg, string value, JArray choices, bool multi = false, bool info = false)
        {
            var o = new JObject { ["t"] = text, ["l"] = label ?? text, ["d"] = description ?? "", ["a"] = admin };
            if (!string.IsNullOrEmpty(arg)) o["arg"] = arg;
            if (value != null) o["v"] = value;
            if (multi) o["m"] = true;
            if (info) o["i"] = true;
            if (choices != null && choices.Count > 0) o["ch"] = choices;
            commands.Add(o);
            return this;
        }
        private static JArray Choices((string value, string label)[] choices)
        {
            if (choices == null || choices.Length == 0) return null;
            var a = new JArray();
            foreach (var c in choices) a.Add(new JArray(c.value, c.label ?? c.value));
            return a;
        }
    }
}
