namespace SkyrimCraftingTool.Model;

public static class GlobalState
{
    // paths
    public static string GameDataPath { get; private set; }
    public static string ModDirectoryPath { get; private set; }
    public static string PluginsFilePath { get; private set; }
    public static ToolPaths Tool { get; private set; }

    public static SkyrimPaths Skyrim { get; private set; }

    public static void Initialize(FolderSettings settings)
    {
        GameDataPath = settings.GameDataPath;
        ModDirectoryPath = settings.ModDirectoryPath;
        PluginsFilePath = settings.PluginsFilePath;

        Skyrim = new SkyrimPaths(GameDataPath, ModDirectoryPath, PluginsFilePath);
        Tool = new ToolPaths();
    }
}
