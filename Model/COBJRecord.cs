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

        // How MANY of it the recipe produces (NAM1). A COBJ can only ever create ONE kind of
        // object, so this is a single number, not a list - "1 ingot AND 2 leather strips" is two
        // separate recipes and, in game, two separate menu entries.
        //
        // Untracked until now: not scanned, not stored, and CobjEspBuilder wrote a hard 1 into every
        // record it created from scratch. That was invisible while the tool only ever made forge and
        // tempering recipes, which produce one item - but it is THE value of a breakdown recipe
        // ("gives 2 steel ingots"), and it was also silently wrong for anything that yields a stack,
        // arrows being the obvious case.
        //
        // 1 is the right default: it is what every untracked record effectively had, and what a
        // recipe with no NAM1 means.
        public int CreatedObjectCount { get; set; } = 1;

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
