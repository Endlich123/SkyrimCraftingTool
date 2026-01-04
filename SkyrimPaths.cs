using System.IO;

namespace SkyrimCraftingTool;

public class SkyrimPaths
{
    public string GameDataPath { get; }
    public string ModDirectory { get; }
    public string PluginsFile { get; }

    public SkyrimPaths(string gameDataPath, string modDirectory, string pluginsFile)
    {
        GameDataPath = gameDataPath;
        ModDirectory = modDirectory;
        PluginsFile = pluginsFile;
    }

    public string SkyrimEsm => Path.Combine(GameDataPath, "Skyrim.esm");
    public string UpdateEsm => Path.Combine(GameDataPath, "Update.esm");
}
