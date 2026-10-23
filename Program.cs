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

            // The armor catalog moved from Modules/ to Modules/Armor/ (2026-10-23); then a fresh
            // install gets the shipped catalog. An existing one is left alone.
            Services.ModularPresets.ModularCatalogStore.MoveLegacyArmorCatalog(
                Services.ModularPresets.ModularCatalogStore.ModulesRoot);
            Services.ModularPresets.ModularCatalogStore.SeedDefaultsIfEmpty(
                Services.ModularPresets.ModularCatalogStore.ArmorRoot);
            Services.ModularPresets.ModularCatalogStore.SeedDefaultsIfEmpty(
                Services.ModularPresets.ModularCatalogStore.WeaponsRoot, "Weapons",
                Services.ModularPresets.WeaponCatalogStore.FileNames);

            // filter mods after keywords, materials(misc)
            // create json with format for keywords: Skyrim.esm{keyword:FormID, keyword:FormID, ...}, Update.esm{keyword:FormID, keyword:FormID, ...}, ModName{keyword:FormID, keyword:FormID, ...}
            // create json with format for keywords: same as above but for materials(misc)

        }

    }
}
