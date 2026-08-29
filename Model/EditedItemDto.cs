using System.Collections.Generic;

namespace SkyrimCraftingTool.Model
{
    public enum ExportScope { Item, Plugin, All }

    // One exported record: a single Armor/Weapons/COBJ/Enchantments row's user-edited data, plus any
    // edited child rows (Conditions/Effects/WornRestrictionKeywords). Field values are kept as strings
    // end-to-end (export -> JSON -> import) — SQLite's type affinity converts a well-formed numeric
    // string back into INTEGER/REAL on insert, so no per-column type table is needed.
    public class EditedItemDto
    {
        public string Table { get; set; } = "";
        public string Key { get; set; } = "";
        public string LastChanged { get; set; } = "";
        public int? Original { get; set; }

        // IsEdited*-column-name -> value, only non-null ones. Exception: COBJ rows with Original == 0
        // (user-created, no scan to fall back to) hold ALL base columns instead (Name, CreatedItem,
        // WorkbenchKeyword, Ingredients), since there is no scanned base row an import target could
        // fall back to.
        public Dictionary<string, string> Fields { get; set; } = new();

        // COBJ only, present when ConditionsEdited was set at export time.
        public List<Dictionary<string, string>> ConditionRows { get; set; }

        // Enchantments only, present when EffectsEdited was set at export time.
        public List<Dictionary<string, string>> EffectRows { get; set; }

        // Enchantments only, present when KeywordsEdited was set at export time.
        public List<string> WornRestrictionKeywords { get; set; }

        // Display-only convenience for the item picker / conflict dialog — not required on import.
        public string DisplayName { get; set; } = "";
    }

    public class ExportFile
    {
        public string ExportedAt { get; set; } = "";
        public List<EditedItemDto> Items { get; set; } = new();
    }

    public class ImportConflict
    {
        public EditedItemDto FileItem { get; set; }
        public string LocalLastChanged { get; set; } = "";
    }

    public class ImportPlan
    {
        public List<EditedItemDto> ToApply { get; set; } = new();
        public List<EditedItemDto> ToSkipEqual { get; set; } = new();
        public List<EditedItemDto> ToSkipMissing { get; set; } = new();
        public List<ImportConflict> Conflicts { get; set; } = new();
    }

    public class ImportResult
    {
        public int Applied { get; set; }
        public int SkippedEqual { get; set; }
        public List<EditedItemDto> SkippedMissing { get; set; } = new();
        public int ConflictsKeptLocal { get; set; }
        public int ConflictsUsedFile { get; set; }
    }
}
