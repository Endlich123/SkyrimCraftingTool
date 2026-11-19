namespace SkyrimCraftingTool.Model
{
    public class WorldItemRecord
    {
        // Plugin|FormID
        public string Key { get; set; } = "";

        public string EditorID { get; set; } = "";
        public string Name { get; set; } = "";

        // "Book", "Scroll", "Misc", "SoulGem", "Ammo", "Food", "Ingredient", "Key". A string rather
        // than an enum because it is written by the scan and read by a filter, and a new record type
        // should not mean a schema migration.
        public string Kind { get; set; } = "";

        // The only editable field there is - see ArmorRecord.ContainerString for the format.
        public string ContainerString { get; set; } = "{}";

        // What the plugins say, under any edit - where a reset of the placement goes back to.
        public string ScannedContainerString { get; set; } = "{}";

        public string Display => string.IsNullOrWhiteSpace(Name) ? EditorID : Name;

        // The kinds a recipe can take as an ingredient - the material pickers' catalogue. Exactly what
        // formid.db's Materials table held (MISC, SLGM, AMMO, ALCH, INGR; 6.332 rows on the real load
        // order, row for row the same) until that copy went on 2026-11-07. Books, scrolls and keys are
        // left out because they were left out then; adding one here is all it takes.
        public static readonly IReadOnlySet<string> IngredientKinds =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Misc", "SoulGem", "Ammo", "Food", "Ingredient" };

        // One of a kind, for a suffix or a filter chip ("IngotIron  · Misc"). The sub-tab titles are
        // the plurals of these (WorldContentVM.Plural).
        public static string KindLabel(string? kind) => kind switch
        {
            null or "" => "",
            "SoulGem" => "Soul gem",
            _ => kind,
        };
    }
}
