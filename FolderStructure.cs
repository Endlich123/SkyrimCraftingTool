using System;
using System.IO;

namespace SkyrimCraftingTool;

public class FolderStructure
{
    public static FolderStructure Current { get; private set; }

    public string ModDirectory { get; }
    public string PluginsFilePath { get; }
    public string SkyrimEsmPath { get; }
    public string UpdateEsmPath { get; }

    public FolderStructure(FolderSettings settings)
    {
        var paths = new SkyrimPaths(
            settings.GameDataPath,
            settings.ModDirectoryPath,
            settings.PluginsFilePath
        );

        ModDirectory = paths.ModDirectory;
        PluginsFilePath = paths.PluginsFile;
        SkyrimEsmPath = paths.SkyrimEsm;
        UpdateEsmPath = paths.UpdateEsm;

        Current = this;

        if (!File.Exists(SkyrimEsmPath))
            throw new FileNotFoundException($"Skyrim.esm nicht gefunden unter: {SkyrimEsmPath}");
    }

    public void CheckFoldersAndLog()
    {
        string[] foldersToCheck =
        {
            ModDirectory,
            Path.GetDirectoryName(PluginsFilePath),
            Path.GetDirectoryName(SkyrimEsmPath),
            Path.GetDirectoryName(UpdateEsmPath)
        };

        foreach (string folder in foldersToCheck)
        {
            if (Directory.Exists(folder))
                Console.WriteLine($"✔ Folder found: {folder}");
            else
                Console.WriteLine($"❌ Folder missing: {folder}");
        }
    }
}
