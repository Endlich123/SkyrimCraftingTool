using System;
using System.IO;

namespace SkyrimCraftingTool;

public class FolderSettings
{
    public string GameDataPath { get; set; }
    public string ModDirectoryPath { get; set; }
    public string PluginsFilePath { get; set; }

    public static FolderSettings LoadSavedSettings()
    {
        string configPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SkyrimCraftingTool",
            "settings.json"
        );

        if (!File.Exists(configPath))
            throw new FileNotFoundException("Settings file not found.");

        string json = File.ReadAllText(configPath);
        return System.Text.Json.JsonSerializer.Deserialize<FolderSettings>(json);
    }

    public void Save()
    {
        string folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SkyrimCraftingTool"
        );

        Directory.CreateDirectory(folder);

        string configPath = Path.Combine(folder, "settings.json");
        string json = System.Text.Json.JsonSerializer.Serialize(this, new System.Text.Json.JsonSerializerOptions
        {
            WriteIndented = true
        });

        File.WriteAllText(configPath, json);
    }
}
