using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Skyrim;
using System.Collections.ObjectModel;
using System.Diagnostics;
namespace SkyrimCraftingTool;

public static class GlobalState
{
    public static CraftingSettingsService CraftingSettings { get; } = new();
    public static AllSettings LoadedSettings { get; private set; }
    static GlobalState() { LoadedSettings = SettingsStorage.Load(); }
    public static void Save() { SettingsStorage.Save(LoadedSettings); }


    public static List<ISkyrimModGetter> LoadedMods { get; set; } = new();
    public static ILinkCache LinkCache { get; set; }
    public static Dictionary<string, string> ModFilePathMap { get; set; }
        = new(StringComparer.OrdinalIgnoreCase);

    // Material-Mapping
    public static Dictionary<FormKey, string> MaterialMap { get; set; } = new();
    public static Dictionary<string, FormKey> MaterialMapReverse { get; set; } = new();

    // Keywords
    public static HashSet<string> WorkbenchKeywords { get; set; }
        = new(StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<string> WorkbenchTypes
        => WorkbenchKeywords.ToList();

    public static HashSet<string> VendorKeywords { get; set; }
        = new(StringComparer.OrdinalIgnoreCase);

    public static Dictionary<FormKey, string> KeywordMap { get; set; } = new();
    public static Dictionary<string, FormKey> KeywordMapReverse { get; set; } = new();
    public static Dictionary<FormKey, string> AllKeywords { get; set; } = new();

    // Perks
    public static Dictionary<FormKey, string> PerkMap { get; set; } = new();
    public static Dictionary<string, FormKey> PerkMapReverse { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    // Smithing Perks
    public static HashSet<FormKey> SmithingPerks { get; set; } = new();
    public static IReadOnlyList<string> SmithingPerkNames => SmithingPerks.Select(fk => PerkMap.TryGetValue(fk, out var name) ? name : fk.ToString()).OrderBy(n => n).ToList();
    public static readonly HashSet<string> SmithingPerkEditorIDs =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "SteelSmithing",
            "DwarvenSmithing",
            "ElvenSmithing",
            "OrcishSmithing",
            "AdvancedArmors",
            "GlassSmithing",
            "EbonySmithing",
            "DaedricSmithing",
            "DragonArmor",
            "ArcaneBlacksmith"
        };


    // Armorslots
    public static readonly List<ArmorSlot> AllArmorSlots =
    Enum.GetValues(typeof(ArmorSlot)).Cast<ArmorSlot>().ToList();

    public record ArmorSlotDisplay(int Id, string Name)
    {
        public ArmorSlot Slot => (ArmorSlot)Id;
        public string Display => $"{Id}: {Name}";
    }


    public static readonly List<ArmorSlotDisplay> AllArmorSlotDisplays =
        Enum.GetValues(typeof(ArmorSlot))
            .Cast<ArmorSlot>()
            .Select(e => new ArmorSlotDisplay((int)e, e.ToString()))
            .ToList();


    public static readonly List<WeaponTypes> AllWeaponTypes =
        Enum.GetValues(typeof(WeaponTypes)).Cast<WeaponTypes>().ToList();


    //
    public static Dictionary<(string Category, string Slot), SlotSettingsData> Settings
    = new();

    public class SlotSettingsData
    {
        public float Cost { get; set; }
        public float Weight { get; set; }
        public float Damage { get; set; }
        public float ArmorRating { get; set; }

        public List<MaterialEntryData> Materials { get; set; } = new();
        public List<string> Vendors { get; set; } = new();
        public string Workbench { get; set; }

        public class MaterialEntryData
        {
            public string Material { get; set; }
            public int Amount { get; set; }
        }
    }





}
