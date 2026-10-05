namespace SkyrimCraftingTool.Model;

public static class GlobalState
{
    // paths
    public static string GameDataPath { get; private set; }
    public static string ModDirectoryPath { get; private set; }
    public static string PluginsFilePath { get; private set; }

    // MO2's profiles\<profile>\modlist.txt, empty for every other manager. Read by the scan to
    // decide which copy of a duplicated plugin file wins - see Services/PluginPathPicker.
    public static string ModListFilePath { get; private set; }

    public static ModManagerKind ManagerKind { get; private set; } = ModManagerKind.Unknown;

    public static ToolPaths Tool { get; private set; }

    public static SkyrimPaths Skyrim { get; private set; }

    public static void Initialize(FolderSettings settings)
    {
        GameDataPath = settings.GameDataPath;
        ModDirectoryPath = settings.ModDirectoryPath;
        PluginsFilePath = settings.PluginsFilePath;
        ModListFilePath = settings.ModListFilePath;
        ManagerKind = settings.ManagerKind;

        Skyrim = new SkyrimPaths(GameDataPath, ModDirectoryPath, PluginsFilePath);
        Tool = new ToolPaths();
    }
}
