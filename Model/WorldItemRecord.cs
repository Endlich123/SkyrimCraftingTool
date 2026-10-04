namespace SkyrimCraftingTool.Model
{
    // A record that can be PLACED and nothing else: a book, a scroll, a misc item, a soul gem, ammo,
    // food, an ingredient, a key.
    //
    // DELIBERATELY NOT A STRIPPED-DOWN ArmorRecord. It carries a key, a name, what kind of thing it
    // is, and the one field the user can edit. No stats, no keywords, no recipe - not because those
    // were left out for later, but because the tool has nothing to say about them. That is what makes
    // carrying eight more record types cheap.
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

        public string Display => string.IsNullOrWhiteSpace(Name) ? EditorID : Name;
    }
}
