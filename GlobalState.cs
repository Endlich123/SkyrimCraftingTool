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



    public static readonly List<VendorCategoryDef> VendorCategories = new()
    {
        new VendorCategoryDef
        {
            CategoryKey = "blacksmith",
            IniFileName = "blacksmith.ini",
            Vendors = new()
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
            }
        },

        new VendorCategoryDef
        {
            CategoryKey = "general_misc",
            IniFileName = "general_misc.ini",
            Vendors = new()
            {
                "RiverwoodTrader",
                "WhiterunBelethorsGoods",
                "WinterholdBirna",
                "MarkarthArnleifandSons",
                "RiftenPawnedPrawn",
                "RiftenGrelka",
                "RiftenBrandish",
                "RiftenMadesi",
                "WindhelmNiranye",
                "WindhelmAvalAtheron",
                "WindhelmRevynSadri",
                "FalkreathGrayPineGoods",
                "SolitudeRadiantRaiments",
                "SolitudeBitsAndPieces",
                "SolitudeEastEmpireCompany",
                "SorineJurard",
                "Ronthil",
                "Fethis",
                "RavenRockMerchants",
            }
        },

        new VendorCategoryDef
        {
            CategoryKey = "inn_stables_brewery",
            IniFileName = "inn_stables_brewery.ini",
            Vendors = new()
            {
                "MarkarthAntons",
                "RiftenBeeAndBarbTalen",
                "RiverwoodSleepingGiant",
                "MarkarthSilverFishInn",
                "WhiterunBanneredMare",
                "DawnstarWindpeakInn",
                "MorthalMoorsideInn",
                "WinterholdFrozenHearth",
                "OldHroldanHangedManInn",
                "NightgateInn",
                "RoriksteadFrostFruitInn",
                "RiftenBeeAndBarb",
                "IvarsteadVilemyrInn",
                "RiftenBlackBriarMeadery",
                "RiftenRaggedFlagon",
                "WindhelmCandlehearthHall",
                "WindhelmCornerclub",
                "KynesgroveBraidwoodInn",
                "SolitudeWinkingSkeever",
                "DragonBridgeFourShieldsTavern",
                "FalkreathDeadMansDrink",
                "SolitudeStables",
                "Honningbrew",
                "HonningbrewPost",

                // Dragonborn
                "Geldis",
                "Elmus",
            }
        },

        new VendorCategoryDef
        {
            CategoryKey = "magic",
            IniFileName = "magic.ini",
            Vendors = new()
            {
                "DawnstarMadenas",
                "KynesgroveDravynea",
                "MarkarthWizards",
                "MorthalFalion",
                "RiftenWylandriah",
                "SolitudeSybilleStentor",
                "CollegeColette",
                "CollegeDrevis",
                "CollegeEnthir",
                "CollegeFaralda",
                "CollegePhinis",
                "CollegeTolfdir",
                "WhiterunFarengar",
                "WindhelmWuunferth",
                "WinterholdNelacar",

                // Dragonborn
                "TelMithrynNeloth",
                "TelMithrynTalvas",
            }
        },

        new VendorCategoryDef
        {
            CategoryKey = "fletcher",
            IniFileName = "fletcher.ini",
            Vendors = new()
            {
                "WhiterunDrunkenHuntsman",
                "SolitudeFletcher",
            }
        },

        new VendorCategoryDef
        {
            CategoryKey = "alchemy",
            IniFileName = "alchemy.ini",
            Vendors = new()
            {
                "DawnstarMortarPestle",
                "DBSanctuaryMerchant",
                "DushnikhYalWiseWoman",
                "FalkreathGraveConcoctions",
                "HeljarchenApothecary",
                "LargashburAtub",
                "MarkarthHagsCure",
                "MorKhazgurWiseWoman",
                "MorthalLamis",
                "NarzulburWiseWoman",
                "RiftenElgrimsElixirs",
                "SolitudeAngelinesAromatics",
                "WindhelmWhitePhial",
                "WhiterunArcadiasCauldron",

                // Dawnguard
                "Florentius",
                "Feran",

                // Dragonborn
                "TelMithrynElynea",
            }
        },
    };


    public static readonly Dictionary<string, string> VendorChestFormIDs = new()
    {
        // --- Blacksmiths (bestehende) ---
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

        // --- General Merchants (neu) ---
        { "RiverwoodTrader", "00078C0C" },
        { "WhiterunBelethorsGoods", "0009CAF8" },
        { "WinterholdBirna", "0009DA63" },
        { "MarkarthArnleifandSons", "0009E0D9" },
        { "RiftenPawnedPrawn", "000A29AD" },
        { "RiftenGrelka", "000A31B4" },
        { "RiftenBrandish", "000A31B5" },
        { "RiftenMadesi", "000A31B7" },
        { "WindhelmNiranye", "000A3F05" },
        { "WindhelmAvalAtheron", "000A3F09" },
        { "WindhelmRevynSadri", "000A3F10" },
        { "FalkreathGrayPineGoods", "000A6BFC" },
        { "SolitudeRadiantRaiments", "000A6C04" },
        { "SolitudeBitsAndPieces", "000A6C06" },
        { "SolitudeEastEmpireCompany", "000D6AA4" },

        // Dawnguard
        { "SorineJurard", "0200F1A5" },
        { "Ronthil", "0201047A" },

        // Dragonborn
        { "Fethis", "04025020" },
        { "RavenRockMerchants", "04031DB4" },

        // --- Inn / Stables / Brewery ---
        { "MarkarthAntons", "0006479C" },
        { "RiftenBeeAndBarbTalen", "00065C36" },
        { "RiverwoodSleepingGiant", "00078C0E" },
        { "MarkarthSilverFishInn", "00094384" },
        { "WhiterunBanneredMare", "0009CAFA" },
        { "DawnstarWindpeakInn", "0009DA46" },
        { "MorthalMoorsideInn", "0009DA53" },
        { "WinterholdFrozenHearth", "0009DA5F" },
        { "OldHroldanHangedManInn", "0009E45F" },
        { "NightgateInn", "0009E48B" },
        { "RoriksteadFrostFruitInn", "0009F250" },
        { "RiftenBeeAndBarb", "000A0703" },
        { "IvarsteadVilemyrInn", "000A0706" },
        { "RiftenBlackBriarMeadery", "000A29AC" },
        { "RiftenRaggedFlagon", "000A29AE" },
        { "WindhelmCandlehearthHall", "000A3EFF" },
        { "WindhelmCornerclub", "000A3F14" },
        { "KynesgroveBraidwoodInn", "000A3F25" },
        { "SolitudeWinkingSkeever", "000A6BF0" },
        { "DragonBridgeFourShieldsTavern", "000A6BF1" },
        { "FalkreathDeadMansDrink", "000A6BF3" },
        { "SolitudeStables", "000A6C08" },
        { "Honningbrew", "000B2989" },
        { "HonningbrewPost", "000B31E8" },

        // Dragonborn
        { "Geldis", "0402501E" },
        { "Elmus", "0403572E" },

        // --- Magic Vendors ---
        { "DawnstarMadenas", "000A2987" },
        { "KynesgroveDravynea", "000A3F02" },
        { "MarkarthWizards", "0009438A" },
        { "MorthalFalion", "0009DA56" },
        { "RiftenWylandriah", "000A2988" },
        { "SolitudeSybilleStentor", "000A2989" },
        { "CollegeColette", "00098BA3" },
        { "CollegeDrevis", "00098B9E" },
        { "CollegeEnthir", "000EE9F7" },
        { "CollegeFaralda", "00098BA1" },
        { "CollegePhinis", "00098BA2" },
        { "CollegeTolfdir", "00098BA4" },
        { "WhiterunFarengar", "000A298A" },
        { "WindhelmWuunferth", "000A3F1B" },
        { "WinterholdNelacar", "000E7BCD" },

        // Dragonborn
        { "TelMithrynNeloth", "040177C1" },
        { "TelMithrynTalvas", "040177C0" },

        // --- Fletcher ---
        { "WhiterunDrunkenHuntsman", "0009F257" },
        { "SolitudeFletcher", "000B2035" },

        // --- Alchemy ---
        { "DawnstarMortarPestle", "0009E0DA" },
        { "DBSanctuaryMerchant", "000ABD9E" },
        { "DushnikhYalWiseWoman", "0009E129" },
        { "FalkreathGraveConcoctions", "0006A876" },
        { "HeljarchenApothecary", "0009E491" },
        { "LargashburAtub", "000ACB6C" },
        { "MarkarthHagsCure", "0009E0D7" },
        { "MorKhazgurWiseWoman", "0009E469" },
        { "MorthalLamis", "0009DA59" },
        { "NarzulburWiseWoman", "000B3FE0" },
        { "RiftenElgrimsElixirs", "000A31AE" },
        { "SolitudeAngelinesAromatics", "000A6C05" },
        { "WindhelmWhitePhial", "000AF632" },
        { "WhiterunArcadiasCauldron", "0009CD45" },

        // Dawnguard
        { "Florentius", "0200F82B" },
        { "Feran", "02010479" },

        // Dragonborn
        { "TelMithrynElynea", "040177BE" },
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
