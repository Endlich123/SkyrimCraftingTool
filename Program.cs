using SkyrimCraftingTool.Model;

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

            // filter mods after keywords, materials(misc)
            // create json with format for keywords: Skyrim.esm{keyword:FormID, keyword:FormID, ...}, Update.esm{keyword:FormID, keyword:FormID, ...}, ModName{keyword:FormID, keyword:FormID, ...}
            // create json with format for keywords: same as above but for materials(misc)

        }

    }
}
