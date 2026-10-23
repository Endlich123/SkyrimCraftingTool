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

        // User-created records only: the identity that travels across machines, because their Key
        // is handed out locally and means another record elsewhere. See
        // ItemDBHandler.ResolveUserCobjKeys. Empty in exports written before TE-2.
        public string Uid { get; set; } = "";

        // Field name -> Uid of the user-created record that field points at (TE-3). Set at export
        // when a field's value is one of the user's own records - an item's enchantment, say - so
        // the import finds that record by identity even after its Key changed on the way in.
        // Null when the DTO references no user-created record.
        public Dictionary<string, string> Refs { get; set; }

        // COBJ only, present when ConditionsEdited was set at export time.
        public List<Dictionary<string, string>> ConditionRows { get; set; }

        // Enchantments only, present when EffectsEdited was set at export time.
        public List<Dictionary<string, string>> EffectRows { get; set; }

        // Enchantments only, present when KeywordsEdited was set at export time.
        public List<string> WornRestrictionKeywords { get; set; }

        // Named child lists for every unit after TE-1 (docs/TabExport-Plan.md §3.4), instead of one
        // more dedicated property per unit. Same rule as the lists above: a name is present only when
        // that child list was edited, and an EMPTY list is a payload ("the user removed them all").
        public Dictionary<string, List<Dictionary<string, string>>> Rows { get; set; }

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

        // Unusable payload rather than a missing target — currently an unparsable LastChanged
        // timestamp. Kept separate from ToSkipMissing so the summary doesn't blame a missing plugin
        // for what is actually a corrupt export file.
        public List<EditedItemDto> ToSkipInvalid { get; set; } = new();

        public List<ImportConflict> Conflicts { get; set; } = new();

        // What the summary should tell the user beyond the counts - a reference to one of their own
        // records that this machine does not have, say. Filled while the plan is made (FollowKeys).
        public List<string> Notes { get; set; } = new();
    }

    public class ImportResult
    {
        public int Applied { get; set; }
        public int SkippedEqual { get; set; }
        public List<EditedItemDto> SkippedMissing { get; set; } = new();
        public int SkippedInvalid { get; set; }
        public int ConflictsKeptLocal { get; set; }
        public int ConflictsUsedFile { get; set; }

        // Carried over from ImportPlan.Notes.
        public List<string> Notes { get; set; } = new();
    }
}
