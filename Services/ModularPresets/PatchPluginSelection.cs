using SkyrimCraftingTool.Model;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace SkyrimCraftingTool.Services.ModularPresets
{
    // Which plugins the preset patch leaves out (user, 2026-10-22: "beim Preset-Patchen entscheiden
    // können, welche ESP gepatcht werden"). Stored as the EXCLUDED names, so a mod added later is
    // patched unless someone takes it out. Lives next to the building blocks, not inside them: it is
    // about this load order, not part of a preset someone shares.
    public sealed class PatchPluginSelection
    {
        public static string DefaultPath => Path.Combine(PresetFileStore.PresetsRoot, "PatchPlugins.json");

        // The weapon dry run remembers its own: a plugin card's "Type = Heavy" means nothing to its
        // weapons, and switching a plugin's armor off must not switch off its weapons.
        public static string WeaponsPath => Path.Combine(PresetFileStore.PresetsRoot, "PatchPluginsWeapons.json");

        private readonly string _path;
        private readonly HashSet<string> _excluded = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _excludedItems = new(StringComparer.OrdinalIgnoreCase);   // item keys
        private readonly Dictionary<string, BuildChoice> _pluginChoices = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, BuildChoice> _itemChoices = new(StringComparer.OrdinalIgnoreCase);

        public PatchPluginSelection(string? path = null)
        {
            _path = path ?? DefaultPath;
            try
            {
                if (File.Exists(_path))
                {
                    var shape = JsonSerializer.Deserialize<FileShape>(File.ReadAllText(_path));
                    foreach (var name in shape?.Excluded ?? new List<string>())
                        if (!string.IsNullOrWhiteSpace(name)) _excluded.Add(name.Trim());
                    foreach (var key in shape?.ExcludedItems ?? new List<string>())
                        if (!string.IsNullOrWhiteSpace(key)) _excludedItems.Add(key.Trim());
                    foreach (var (k, c) in shape?.PluginChoices ?? new()) if (c != null) _pluginChoices[k] = c;
                    foreach (var (k, c) in shape?.ItemChoices ?? new()) if (c != null) _itemChoices[k] = c;
                }
            }
            catch (Exception ex)
            {
                // A broken file must not block the patch: everything is included then, and the
                // next change writes a clean file.
                AppLogger.LogError($"Reading {_path}", ex);
            }
        }

        public bool IsIncluded(string plugin) => !_excluded.Contains(plugin);

        public int ExcludedCount => _excluded.Count;

        // Single items, by key (Plugin|FormID) - switched off inside a plugin that stays on.
        public bool IsItemIncluded(string itemKey) => !_excludedItems.Contains(itemKey);

        // What the patch touches: the plugin is on AND the item is on.
        public bool Patches(string plugin, string itemKey) => IsIncluded(plugin) && IsItemIncluded(itemKey);

        // The preset combination chosen in the dry run, per plugin and per item (user, 2026-10-22:
        // "die Preset-Kombis sollten dort auch änderbar sein"). Applied on top of the detection:
        // detected -> plugin choice -> item choice.
        public BuildChoice? PluginChoice(string plugin) => _pluginChoices.TryGetValue(plugin, out var c) ? c : null;
        public BuildChoice? ItemChoice(string itemKey) => _itemChoices.TryGetValue(itemKey, out var c) ? c : null;

        public void SetPluginChoice(string plugin, BuildChoice? choice)
        {
            if (IsEmpty(choice)) _pluginChoices.Remove(plugin); else _pluginChoices[plugin] = choice!;
            Save();
        }

        public void SetItemChoice(string itemKey, BuildChoice? choice)
        {
            if (IsEmpty(choice)) _itemChoices.Remove(itemKey); else _itemChoices[itemKey] = choice!;
            Save();
        }

        // The dry run's "Reset all": everything these plugins and items carry goes back to on +
        // "(auto)". One write, however many keys. Returns how many entries were dropped.
        public int Reset(IEnumerable<string> plugins, IEnumerable<string> itemKeys)
        {
            int dropped = 0;
            foreach (var p in plugins)
                dropped += (_excluded.Remove(p) ? 1 : 0) + (_pluginChoices.Remove(p) ? 1 : 0);
            foreach (var k in itemKeys)
                dropped += (_excludedItems.Remove(k) ? 1 : 0) + (_itemChoices.Remove(k) ? 1 : 0);
            if (dropped > 0) Save();
            return dropped;
        }

        public ModularSelection Resolve(string plugin, string itemKey, ModularSelection detected)
        {
            var s = PluginChoice(plugin)?.Resolve(detected) ?? detected;
            return ItemChoice(itemKey)?.Resolve(s) ?? s;
        }

        private static bool IsEmpty(BuildChoice? c)
            => c == null || (c.TypeName == null && c.MaterialName == null && c.SlotNames == null && c.AddonNames == null
                             && c.AdditionalAddons.Count == 0 && c.ExtraNames.Count == 0);

        public void SetItem(string itemKey, bool included)
        {
            bool changed = included ? _excludedItems.Remove(itemKey) : _excludedItems.Add(itemKey);
            if (changed) Save();
        }

        public void Set(string plugin, bool included)
        {
            bool changed = included ? _excluded.Remove(plugin) : _excluded.Add(plugin);
            if (changed) Save();
        }

        public void SetMany(IEnumerable<string> plugins, bool included)
        {
            bool changed = false;
            foreach (var p in plugins)
                changed |= included ? _excluded.Remove(p) : _excluded.Add(p);
            if (changed) Save();
        }

        private void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                var shape = new FileShape
                {
                    Excluded = _excluded.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList(),
                    ExcludedItems = _excludedItems.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList(),
                    PluginChoices = new Dictionary<string, BuildChoice>(_pluginChoices),
                    ItemChoices = new Dictionary<string, BuildChoice>(_itemChoices),
                };
                File.WriteAllText(_path, JsonSerializer.Serialize(shape, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex)
            {
                AppLogger.LogError($"Writing {_path}", ex);
            }
        }

        private sealed class FileShape
        {
            public int SchemaVersion { get; set; } = 1;
            public List<string> Excluded { get; set; } = new();
            public List<string> ExcludedItems { get; set; } = new();
            public Dictionary<string, BuildChoice> PluginChoices { get; set; } = new();
            public Dictionary<string, BuildChoice> ItemChoices { get; set; } = new();
        }
    }
}
