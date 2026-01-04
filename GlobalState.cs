using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Skyrim;
using System.Collections.ObjectModel;
using System.Diagnostics;
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

    public static List<string> SelectedVendorKeywords { get; set; }


   
    public static void SetSelectedVendorKeywords(IEnumerable<string> keywords)
    {
        SelectedVendorKeywords.Clear();

        foreach (var kw in keywords)
        {
            Debug.WriteLine($"Adding keyword: '{kw}'");
            if (!string.IsNullOrWhiteSpace(kw))
                SelectedVendorKeywords.Add(kw);
        }
    }

}
