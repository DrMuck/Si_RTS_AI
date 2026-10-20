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

        // ---- plain button: sends `text` (plus the Arguments field, if the player typed one) ----
        public ModUIExport Action(string text, string label, string description, bool admin = false)
            => Add(text, label, description, admin, null, null, null);

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

        // ---- value row: label with the current value, a text field and SET; SET sends `text + " " + field` ----
        public ModUIExport Value(string text, string label, string description, bool admin, string current)
            => Add(text, label, description, admin, null, current ?? "", null);
        public ModUIExport Value(string text, string label, string description, bool admin, double current)
            => Value(text, label, description, admin, current.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));

        // ---- catalog (Si_Buy): a button grid per faction, a click sends `command + " " + name` ----
        public CatalogBuilder Catalog(string command, bool enabled = true) => new CatalogBuilder(this, command, enabled);
        public sealed class CatalogBuilder
        {
            private readonly ModUIExport owner; private readonly JObject doc; private readonly JObject factions = new JObject();
            private JArray category; private JArray faction;
            internal CatalogBuilder(ModUIExport owner, string command, bool enabled)
            { this.owner = owner; doc = new JObject { ["cmd"] = command, ["enabled"] = enabled, ["catalog"] = factions }; }
            public CatalogBuilder Faction(string name) { faction = new JArray(); factions[name] = faction; category = null; return this; }
            public CatalogBuilder Category(string name) { category = new JArray(); faction.Add(new JObject { ["c"] = name, ["u"] = category }); return this; }
            public CatalogBuilder Item(string name, int cost) { category.Add(new JArray(name, cost)); return this; }
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

        private ModUIExport Add(string text, string label, string description, bool admin, string arg, string value, JArray choices)
        {
            var o = new JObject { ["t"] = text, ["l"] = label ?? text, ["d"] = description ?? "", ["a"] = admin };
            if (!string.IsNullOrEmpty(arg)) o["arg"] = arg;
            if (value != null) o["v"] = value;
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
