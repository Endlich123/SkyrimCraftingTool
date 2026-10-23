using SkyrimCraftingTool.Model;
using System.IO;

namespace SkyrimCraftingTool.Services.ModularPresets
{
    // The weapon catalog in Output/Presets/Modules/Weapons - the same one-file-per-kind format as the
    // armor catalog (ModularCatalogStore), with WeaponTypes.json where armor has Types and Slots.
    public class WeaponCatalogStore
    {
        public static readonly string[] FileNames =
            { "WeaponTypes.json", "Materials.json", "Addons.json", "Rules.json", "Extras.json" };

        private readonly string _root;

        public WeaponCatalogStore(string root) { _root = root; }

        public WeaponCatalog Read() => new()
        {
            Types = ModularCatalogStore.ReadEntries<WeaponTypeModule>(_root, "WeaponTypes.json"),
            Materials = ModularCatalogStore.ReadEntries<WeaponMaterialModule>(_root, "Materials.json"),
            Addons = ModularCatalogStore.ReadEntries<KeywordAddon>(_root, "Addons.json"),
            Rules = ModularCatalogStore.ReadEntries<CombinationRule>(_root, "Rules.json"),
            Extras = ModularCatalogStore.ReadEntries<ExtraModule>(_root, "Extras.json"),
        };

        public void Write(WeaponCatalog catalog)
        {
            Directory.CreateDirectory(_root);
            ModularCatalogStore.WriteEntries(_root, "WeaponTypes.json", catalog.Types);
            ModularCatalogStore.WriteEntries(_root, "Materials.json", catalog.Materials);
            ModularCatalogStore.WriteEntries(_root, "Addons.json", catalog.Addons);
            ModularCatalogStore.WriteEntries(_root, "Rules.json", catalog.Rules);
            ModularCatalogStore.WriteEntries(_root, "Extras.json", catalog.Extras);
            Saved?.Invoke();
        }

        // Same role as ModularCatalogStore.Saved, for the weapon side.
        public static event System.Action? Saved;
    }
}
