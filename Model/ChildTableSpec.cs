namespace SkyrimCraftingTool.Model
{
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
            Columns = new[] { "COBJKey", "ConditionType", "Target", "Value", "Extra", "RunOn", "CompareOperator", "Flags", "RunOnRef" },
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

        // The Spells tab (docs/Spells-Plan.md). Mutagen gives SPEL and ENCH the SAME effect type, so
        // the column list below is the enchantment one with the parent key renamed.
        public static readonly ChildTableSpec SpellEffects = new()
        {
            ParentTable = "Spells",
            ParentKeyColumn = "Key",
            EditedFlagColumn = "EffectsEdited",
            Table = "SpellEffects",
            OriginalTable = "SpellEffects_Original",
            KeyColumn = "SpellKey",
            Columns = new[] { "SpellKey", "MagicEffectKey", "EditorID", "Name", "Magnitude", "Duration", "Area" },

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
