using SkyrimCraftingTool.Model;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace SkyrimCraftingTool.Services
{
    // Manages the on-disk layout for Import/Export — no file dialogs, everything lives under a fixed,
    // predictable structure so Export and Import always agree on where a given item's file is:
    //   Output/Exports/<Tab>/<PluginName>/<DisplayName>_<FormID>.json
    // One file per exported item/recipe. The FormID suffix guarantees uniqueness within a plugin
    // folder even if two items share a display name; DisplayName alone would collide. <Tab> is the
    // module's Folder (TabExportModule), so each tab reads back only its own exports.
    //
    // LEGACY LAYOUT. Before TE-1 there was no <Tab> level: files sat at Exports/<PluginName>/...
    // Those are still read by every tab's import (each keeps only the DTOs of its own units), so an
    // existing export is not stranded by the new layout. A top-level folder counts as legacy when it
    // is not one of the tab folders - plugin folders are named after a plugin file and can never
    // collide with "Items" or "Enchantments".
    public static class ExportFileStore
    {
        public static string ExportsRoot => Path.Combine(GlobalState.Tool.OutputFolder, "Exports");

        public static string TabRoot(string folder) => Path.Combine(ExportsRoot, Sanitize(folder));

        public static string GetItemFilePath(string folder, string key, string displayName)
            => Path.Combine(TabRoot(folder), RelativeItemPath(key, displayName));

        // Where the same file lived before TE-1 - the per-item import falls back to it.
        public static string GetLegacyItemFilePath(string key, string displayName)
            => Path.Combine(ExportsRoot, RelativeItemPath(key, displayName));

        // EVERY part is sanitized, the one after the '|' included. For records that part is a FormID
        // and harmless, but not every unit's key is "Plugin|FormID": an NPC group is "auto|<seed>"
        // or "user|<name>", and seeds look like "tier:OREO_EncBandit06". Unsanitized, the ':' made
        // Windows write an NTFS alternate data stream - "Bandit 01 (CYR)_tier" with the JSON hidden
        // in a stream ":CYREncBandit01.json" - so the file had no .json name, the import never found
        // it, and groups sharing a name prefix landed in the same file (found 2026-10-17 on the
        // user's first real group export). A name with '/' would have made a sub folder.
        private static string RelativeItemPath(string key, string displayName)
        {
            var (plugin, formId) = SplitKey(key);
            var safeName = Sanitize(string.IsNullOrWhiteSpace(displayName) ? formId : displayName);
            var safePlugin = Sanitize(plugin);
            var safeId = string.IsNullOrEmpty(formId) ? "" : Sanitize(formId);
            var fileName = safeId.Length == 0 ? $"{safeName}.json" : $"{safeName}_{safeId}.json";
            return Path.Combine(safePlugin, fileName);
        }

        private static (string Plugin, string FormId) SplitKey(string key)
        {
            if (string.IsNullOrEmpty(key)) return ("Unknown", "");
            int idx = key.IndexOf('|');
            return idx < 0 ? (key, "") : (key.Substring(0, idx), key.Substring(idx + 1));
        }

        private static string Sanitize(string raw)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var chars = (raw ?? "").Select(c => invalid.Contains(c) ? '_' : c).ToArray();
            var result = new string(chars).Trim();
            return string.IsNullOrEmpty(result) ? "Item" : result;
        }

        public static void WriteFile(string path, ExportFile file)
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(file, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json);
        }

        public static ExportFile ReadFile(string path)
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<ExportFile>(json);
        }

        // All export files for one tab: its own folder, plus every legacy file.
        public static List<string> FindAllFiles(string folder)
        {
            var files = new List<string>();
            var own = TabRoot(folder);
            if (Directory.Exists(own))
                files.AddRange(Directory.GetFiles(own, "*.json", SearchOption.AllDirectories));
            files.AddRange(FindLegacyFiles());
            return files;
        }

        // Export files under one plugin's folder only, for the per-plugin Import flow - the tab's
        // own plugin folder plus the legacy one.
        public static List<string> FindFilesForPlugin(string folder, string pluginName)
        {
            var files = new List<string>();
            foreach (var dir in new[] { Path.Combine(TabRoot(folder), Sanitize(pluginName)), Path.Combine(ExportsRoot, Sanitize(pluginName)) })
            {
                if (Directory.Exists(dir))
                    files.AddRange(Directory.GetFiles(dir, "*.json", SearchOption.TopDirectoryOnly));
            }
            return files;
        }

        private static IEnumerable<string> FindLegacyFiles()
        {
            if (!Directory.Exists(ExportsRoot)) return Enumerable.Empty<string>();

            var tabFolders = new HashSet<string>(
                TabExportModules.All.Select(m => Sanitize(m.Folder)), StringComparer.OrdinalIgnoreCase);

            var legacyDirs = Directory.GetDirectories(ExportsRoot)
                .Where(d => !tabFolders.Contains(Path.GetFileName(d)));

            return Directory.GetFiles(ExportsRoot, "*.json", SearchOption.TopDirectoryOnly)
                .Concat(legacyDirs.SelectMany(d => Directory.GetFiles(d, "*.json", SearchOption.AllDirectories)));
        }
    }
}
