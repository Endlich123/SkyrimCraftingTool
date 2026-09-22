namespace SkyrimCraftingTool.Model
{
    // One description of a child-row table: what it hangs off, which flag says it has been edited,
    // where its pre-edit snapshot lives, and what a row is made of.
    //
    // Three tables work this way - a recipe's conditions, an enchantment's effects, an FLST's
    // members - and all three follow the same rule: the first edit freezes the pristine rows into
    // the matching _Original table, and Reset restores from there. That rule used to be written out
    // by hand at every writer, six times over, and it was got wrong twice: the enchantment import
    // and later the recipe import each destroyed the user's scanned data, months apart, in exactly
    // the same way. See docs/ImportExport-Analyse.md (B2, IE-3/IE-4).
    internal sealed class ChildTableSpec
    {
        // The record these rows belong to, and the flag on it that means "already edited once".
        public string ParentTable { get; init; } = "";
        public string ParentKeyColumn { get; init; } = "";
        public string EditedFlagColumn { get; init; } = "";

        public string Table { get; init; } = "";
        public string OriginalTable { get; init; } = "";
        public string KeyColumn { get; init; } = "";

        // Source and target carry the same names, so one list serves the INSERT and the SELECT.
        // Deliberately without COBJ_Conditions.Id: the snapshot re-inserts rows, it does not
        // preserve their identity, and _Original has no such column.
        public string[] Columns { get; init; } = System.Array.Empty<string>();

        public string ColumnList => string.Join(", ", Columns);

        // Where this unit's rows sit on an exported record, as the thing itself rather than its
        // count: an EMPTY list is a payload - it says "the user removed them all" - and only null
        // means "this export never carried them". Reading Count here is what made a cleared list
        // indistinguishable from an untouched one and dropped the removal on the floor (B1).
        public System.Func<EditedItemDto, object?> Payload { get; init; } = _ => null;
    }

    internal static class ChildTables
    {
        public static readonly ChildTableSpec CobjConditions = new()
        {
            ParentTable = "COBJ",
            ParentKeyColumn = "Key",
            EditedFlagColumn = "ConditionsEdited",
            Table = "COBJ_Conditions",
            OriginalTable = "COBJ_Conditions_Original",
            KeyColumn = "COBJKey",
            Columns = new[] { "COBJKey", "ConditionType", "Target", "Value", "Extra", "RunOn", "CompareOperator", "Flags" },
            Payload = d => d.ConditionRows,
        };

        public static readonly ChildTableSpec EnchantmentEffects = new()
        {
            ParentTable = "Enchantments",
            ParentKeyColumn = "Key",
            EditedFlagColumn = "EffectsEdited",
            Table = "EnchantmentEffects",
            OriginalTable = "EnchantmentEffects_Original",
            KeyColumn = "EnchantmentKey",
            Columns = new[] { "EnchantmentKey", "MagicEffectKey", "EditorID", "Name", "Magnitude", "Duration", "Area" },
            Payload = d => d.EffectRows,
        };

        // E3: the FLST's edit flag lives on its own state table, not on the enchantments that
        // reference the list - and that row only appears on the first edit. A missing row therefore
        // reads as "never edited", which is precisely what the snapshot needs it to mean.
        public static readonly ChildTableSpec WornRestrictionKeywords = new()
        {
            ParentTable = "WornRestrictionListState",
            ParentKeyColumn = "ListKey",
            EditedFlagColumn = "IsEdited",
            Table = "WornRestrictionKeywords",
            OriginalTable = "WornRestrictionKeywords_Original",
            KeyColumn = "ListKey",
            Columns = new[] { "ListKey", "KeywordKey" },
            Payload = d => d.WornRestrictionKeywords,
        };

        // Every child-row unit there is. HasPayload walks this, so a fourth one cannot be added
        // without the export preview learning to carry it.
        public static readonly ChildTableSpec[] All =
            { CobjConditions, EnchantmentEffects, WornRestrictionKeywords };
    }
}
