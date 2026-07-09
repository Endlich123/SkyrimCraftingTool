using SkyrimCraftingTool.Model;
using System.Diagnostics;

namespace SkyrimCraftingTool
{
    public class Program
    {
        public static void Handler()
        {
            // load Folder settings
            var settings = FolderSettings.LoadSavedSettings();

            GlobalState.Initialize(settings);

            var folders = new FolderStructure(settings);
            folders.CheckFoldersAndLog();

            // Test
            Debug.WriteLine("GameDataPath: " + GlobalState.GameDataPath);
            Debug.WriteLine("ModDirectoryPath: " + GlobalState.ModDirectoryPath);
            Debug.WriteLine("PluginsFilePath: " + GlobalState.PluginsFilePath);
            Debug.WriteLine("Skyrim.esm: " + GlobalState.Skyrim.SkyrimEsm);
            Debug.WriteLine("Update.esm: " + GlobalState.Skyrim.UpdateEsm);

            // Define subfolder structure
            Debug.WriteLine("Crafting subfolders:");
            Debug.WriteLine("Input: " + GlobalState.Tool.InputFolder);
            Debug.WriteLine("Output: " + GlobalState.Tool.OutputFolder);
            Debug.WriteLine("SkyPatcher: " + GlobalState.Tool.SkyPatcherFolder);
            Debug.WriteLine("SkyPatcherWeaponsFolder: " + GlobalState.Tool.SkyPatcherWeaponsFolder);
            Debug.WriteLine("SkyPatcherContainerFolder: " + GlobalState.Tool.SkyPatcherContainerFolder);
            Debug.WriteLine("SkyPatcherConstructibleObjectFolder: " + GlobalState.Tool.SkyPatcherConstructibleObjectFolder);
            Debug.WriteLine("SkyPatcherEnchantmentFolder: " + GlobalState.Tool.SkyPatcherEnchantmentFolder);

            // filter mods after keywords, materials(misc)
            // create json with format for keywords: Skyrim.esm{keyword:FormID, keyword:FormID, ...}, Update.esm{keyword:FormID, keyword:FormID, ...}, ModName{keyword:FormID, keyword:FormID, ...}
            // create json with format for keywords: same as above but for materials(misc)

        }

    }
}
