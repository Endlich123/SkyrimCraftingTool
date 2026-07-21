namespace SkyrimCraftingTool.Model
{
    public class COBJRecord
    {
        // Plugin|FormID
        public string Key { get; set; } = "";

        public string Name { get; set; } = "";
        public int Original { get; set; } = 1;

        // Plugin|FormID des erzeugten Items
        public string CreatedItemKey { get; set; } = "";

        // Plugin|FormID des Workbench-Keywords
        public string WorkbenchKeywordKey { get; set; } = "";

        // Plugin|FormID des Perks
        public string PerkKey { get; set; } = "";

        // Liste von "Plugin|FormID*Count"
        public List<string> IngredientKeys { get; set; } = new();

    }
}
