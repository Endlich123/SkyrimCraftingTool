using System;
using System.IO;

namespace SkyrimCraftingTool.Model;

public class FolderSettings
{
    public string GameDataPath { get; set; }
    public string ModDirectoryPath { get; set; }
    public string PluginsFilePath { get; set; }

    // Portable: lives next to the app (same base as ToolPaths' Input/Output), not under %AppData%.
    // No migration from the old %AppData% location on purpose - a stale copy there could silently
    // resurrect old paths if this file ever goes missing. Re-entering once is fine.
    private static string SettingsPath =>
        Path.Combine(AppContext.BaseDirectory, "Input", "settings.json");

    public static FolderSettings LoadSavedSettings()
    {
        var path = SettingsPath;
        if (!File.Exists(path))
            throw new FileNotFoundException("Settings file not found.", path);

        string json = File.ReadAllText(path);
        return System.Text.Json.JsonSerializer.Deserialize<FolderSettings>(json);
    }

    public void Save()
    {
        var path = SettingsPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        string json = System.Text.Json.JsonSerializer.Serialize(this, new System.Text.Json.JsonSerializerOptions
        {
            WriteIndented = true
        });

        File.WriteAllText(path, json);
    }
}
