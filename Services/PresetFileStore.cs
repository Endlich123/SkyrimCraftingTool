using SkyrimCraftingTool.Model;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace SkyrimCraftingTool.Services
{
    // Manages the on-disk layout for Presets — one JSON file per preset, no file dialogs, everything
    // lives under a fixed, predictable folder:
    //   Output/Presets/<SanitizedPresetName>.json
    // Mirrors the ExportFileStore pattern used for Item Export/Import.
    public static class PresetFileStore
    {
        public static string PresetsRoot => Path.Combine(GlobalState.Tool.OutputFolder, "Presets");

        public static string GetPresetFilePath(string presetName)
        {
            return Path.Combine(PresetsRoot, Sanitize(presetName) + ".json");
        }

        public static bool Exists(string presetName)
        {
            return File.Exists(GetPresetFilePath(presetName));
        }

        public static void WritePreset(PresetFile file)
        {
            var path = GetPresetFilePath(file.PresetName);
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            file.SchemaVersion = PresetFile.CurrentSchemaVersion;

            var json = JsonSerializer.Serialize(file, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json);
        }

        public static PresetFile ReadPreset(string path)
        {
            var json = File.ReadAllText(path);
            var file = JsonSerializer.Deserialize<PresetFile>(json);
            if (file == null) return null;

            if (file.SchemaVersion <= 0)
            {
                // File predates schema versioning.
                file.SchemaVersion = 1;
            }
            else if (file.SchemaVersion > PresetFile.CurrentSchemaVersion)
            {
                AppLogger.LogWarning(
                    $"Preset '{Path.GetFileName(path)}' has SchemaVersion {file.SchemaVersion}, " +
                    $"newer than this build supports ({PresetFile.CurrentSchemaVersion}). Loading anyway; unknown fields are ignored.");

                IssueHub.Current.Report(new AppIssue(
                    AppIssueSeverity.Warning,
                    $"Preset '{Path.GetFileName(path)}' is from a newer build (schema v{file.SchemaVersion}).",
                    Context: "It loads, but unknown fields are dropped - saving it here downgrades it to the current format.",
                    Category: "presets"));
            }

            return file;
        }

        // All preset files currently on disk, for listing/loading the Presets tree.
        public static List<string> FindAllPresetFiles()
        {
            if (!Directory.Exists(PresetsRoot)) return new List<string>();
            return Directory.GetFiles(PresetsRoot, "*.json", SearchOption.TopDirectoryOnly).ToList();
        }

        public static void DeletePresetFile(string presetName)
        {
            var path = GetPresetFilePath(presetName);
            if (File.Exists(path))
                File.Delete(path);
        }

        // Renames a preset: writes the file under the new name first (with PresetName updated inside),
        // then removes the old file — avoids losing the preset if the write fails partway through.
        public static void RenamePresetFile(string oldName, string newName)
        {
            var oldPath = GetPresetFilePath(oldName);
            if (!File.Exists(oldPath))
                throw new FileNotFoundException($"Preset '{oldName}' not found.", oldPath);

            var newPath = GetPresetFilePath(newName);
            var samePath = string.Equals(oldPath, newPath, StringComparison.OrdinalIgnoreCase);
            if (!samePath && File.Exists(newPath))
                throw new IOException($"A preset file already exists at '{newPath}'.");

            var file = ReadPreset(oldPath);
            file.PresetName = newName;
            WritePreset(file);

            if (!samePath)
                File.Delete(oldPath);
        }

        private static string Sanitize(string raw)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var chars = raw.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
            var result = new string(chars).Trim();
            return string.IsNullOrEmpty(result) ? "Preset" : result;
        }
    }
}
