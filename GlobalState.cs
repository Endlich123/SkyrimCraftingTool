using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Skyrim;

namespace SkyrimCraftingTool;

public static class GlobalState
{


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

    public static HashSet<FormKey> SmithingPerks { get; set; } = new();
    public static IReadOnlyList<string> SmithingPerkNames =>
        SmithingPerks.Select(fk => PerkMap.TryGetValue(fk, out var name) ? name : fk.ToString())
                     .OrderBy(n => n)
                     .ToList();

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
            "ArcaneBlacksmith",
            "Leather",
            "Clothing",
        };

    public static readonly HashSet<string> VendorSkyPatcher =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "Windhelm Blacksmith",
            "Whiterun Warmaidens",
            "Solitude Blacksmith",
            "Riverwood Alvor",
            "Riften Scorched Hammer",
            "Narzulbur Blacksmith",
            "Mor Khazgur Blacksmith",
            "Markarth Castle Blacksmith",
            "Markarth Blacksmith",
            "Largashbur Blacksmith",
            "Heljarchen Blacksmith",
            "Shor’s Stone Filnjar",
            "Falkreath Blacksmith",
            "Dushnikh Yal Blacksmith",
            "Dawnstar Rustleif",
            "Dawnguard Gunmar",
            "Dawnguard Hestla",
            "Thirsk Blacksmith",
            "Skaal Blacksmith",
        };

    public static readonly Dictionary<string, string> VendorChestFormIDs = new()
    {
        { "Windhelm Blacksmith", "000A3F17" },
        { "Whiterun Warmaidens", "0009CAFD" },
        { "Solitude Blacksmith", "000A6C07" },
        { "Riverwood Alvor", "00078C0D" },
        { "Riften Scorched Hammer", "000A31AF" },
        { "Narzulbur Blacksmith", "000B3FE1" },
        { "Mor Khazgur Blacksmith", "0009E46D" },
        { "Markarth Castle Blacksmith", "0006479F" },
        { "Markarth Blacksmith", "0009E0D8" },
        { "Largashbur Blacksmith", "000ACB6F" },
        { "Heljarchen Blacksmith", "0009E48E" },
        { "Shor’s Stone Filnjar", "000AC9CE" },
        { "Falkreath Blacksmith", "00072786" },
        { "Dushnikh Yal Blacksmith", "0009E128" },
        { "Dawnstar Rustleif", "0009DA3F" },

        // Dawnguard
        { "Dawnguard Gunmar", "0200F828" },
        { "Dawnguard Hestla", "02010477" },

        // Dragonborn
        { "Thirsk Blacksmith", "04027108" },
        { "Skaal Blacksmith", "0401F897" },
    };


    // Armor Slots
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

    // Weapon Types
    public static readonly List<WeaponTypes> AllWeaponTypes =
        Enum.GetValues(typeof(WeaponTypes)).Cast<WeaponTypes>().ToList();
}
