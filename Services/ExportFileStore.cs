using SkyrimCraftingTool.Model;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace SkyrimCraftingTool.Services
{
    // Manages the on-disk layout for Import/Export — no file dialogs, everything lives under a fixed,
    // predictable structure so Export and Import always agree on where a given item's file is:
    //   Output/Exports/<PluginName>/<DisplayName>_<FormID>.json
    // One file per exported item/recipe. The FormID suffix guarantees uniqueness within a plugin
    // folder even if two items share a display name; DisplayName alone would collide.
    public static class ExportFileStore
    {
        public static string ExportsRoot => Path.Combine(GlobalState.Tool.OutputFolder, "Exports");

        public static string GetItemFilePath(string key, string displayName)
        {
            var (plugin, formId) = SplitKey(key);
            var safeName = Sanitize(string.IsNullOrWhiteSpace(displayName) ? formId : displayName);
            var safePlugin = Sanitize(plugin);
            var fileName = string.IsNullOrEmpty(formId) ? $"{safeName}.json" : $"{safeName}_{formId}.json";
            return Path.Combine(ExportsRoot, safePlugin, fileName);
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
            var chars = raw.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
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

        // All export files currently on disk, for the "Alles importieren" flow.
        public static List<string> FindAllFiles()
        {
            if (!Directory.Exists(ExportsRoot)) return new List<string>();
            return Directory.GetFiles(ExportsRoot, "*.json", SearchOption.AllDirectories).ToList();
        }

        // Export files under one plugin's folder only, for the per-plugin Import flow.
        public static List<string> FindFilesForPlugin(string pluginName)
        {
            var dir = Path.Combine(ExportsRoot, Sanitize(pluginName));
            if (!Directory.Exists(dir)) return new List<string>();
            return Directory.GetFiles(dir, "*.json", SearchOption.TopDirectoryOnly).ToList();
        }
    }
}
