using System;
using System.IO;

namespace SkyrimCraftingTool.Model;

public class FolderSettings
{
    public string GameDataPath { get; set; }
    public string ModDirectoryPath { get; set; }
    public string PluginsFilePath { get; set; }

    // Which manager the setup is built around. Written as a NAME, not a number, so settings.json
    // stays readable and a bug report can be understood without decoding it.
    //
    // Absent in every settings.json written before this existed, which deserializes to Unknown -
    // and Unknown must stay harmless: it selects the same "deployed copy in Data wins" rule as
    // Vortex and Other (see Services/PluginPathPicker). No migration, no forced re-entry.
    [System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter))]
    public ModManagerKind ManagerKind { get; set; } = ModManagerKind.Unknown;

    // MO2 only: profiles\<profile>\modlist.txt. It is the mod PRIORITY, and the only thing that can
    // say which copy of a duplicated plugin file MO2 actually loads. Empty for every other manager,
    // where the question does not arise because deployment already resolved it.
    public string ModListFilePath { get; set; }

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
