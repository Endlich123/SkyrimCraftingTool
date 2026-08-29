namespace SkyrimCraftingTool.Model
{
    public class COBJRecord
    {
        // Plugin|FormID
        public string Key { get; set; } = "";

        public string Name { get; set; } = "";
        public int Original { get; set; } = 1;

        // Plugin|FormID of the created item
        public string CreatedItemKey { get; set; } = "";

        // Plugin|FormID of the workbench keyword
        public string WorkbenchKeywordKey { get; set; } = "";

        // Plugin|FormID of the perk
        public string PerkKey { get; set; } = "";

        // List of "Plugin|FormID*Count"
        public List<string> IngredientKeys { get; set; } = new();

        // List Condition
        public List<COBJConditionRecord> Conditions { get; set; } = new();

    }
}
