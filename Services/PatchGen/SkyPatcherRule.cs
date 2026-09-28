using System;
using System.Collections.Generic;

namespace SkyrimCraftingTool.Services.PatchGen
{
    // One SkyPatcher patch string: a single filter clause + one or more already-formatted
    // operation fragments ("damageResist=50", "keywordsToAdd=Plugin|000ABCDE"). The writer is
    // dumb — every value here is final. See docs/PatchGenerator-Plan.md §2.
    public sealed class SkyPatcherRule
    {
        // "filterByArmors" | "filterByWeapons"
        public string FilterDirective { get; init; } = "";

        // Source plugin file name incl. extension, e.g. "NyesLatexPack.esp".
        public string TargetPlugin { get; init; } = "";

        // FormID exactly as stored in the DB key (6 hex, no "0x"). The writer left-pads to 8.
        public string TargetFormId { get; init; } = "";

        // Which <Plugin>.ini this rule is written into. Empty means "same as TargetPlugin", which is
        // right for every rule that EDITS the record it filters on (armor, weapon, enchantment,
        // formList) - the file name is SkyPatcher's conditional load, so such a rule is only read
        // when the very plugin owning the record is active.
        //
        // The Container-tab rules break that identity: filterByLLs/filterByContainers target a list
        // or container (usually Skyrim.esm), but the forms they ADD come from the item's plugin.
        // Filing them under the target would put them in Skyrim.esm.ini - always loaded - and let
        // SkyPatcher add forms from a mod that may not be installed at all. They set this to the
        // ITEM's plugin instead, so the rule only exists while the mod providing those items does.
        public string FilePlugin { get; init; } = "";

        public string FileNamePlugin => string.IsNullOrEmpty(FilePlugin) ? TargetPlugin : FilePlugin;

        // Emitted as "; <text>" above the rule. Usually EditorID + name.
        public string? Comment { get; init; }

        // Ordered "op=value" fragments, fully formatted.
        public IReadOnlyList<string> Operations { get; init; } = Array.Empty<string>();

        // More than one target on one line, as "Plugin|FormID" strings ready to write.
        //
        // Only the NPC rules use this, and only after merging: several NPCs that get exactly the
        // same treatment share a line rather than taking one each (NpcRuleBuilder.MergeIdenticalRules).
        // Empty means the single TargetPlugin/TargetFormId pair above is the target, which is how
        // every other rule in this tool works.
        public IReadOnlyList<string> ExplicitTargets { get; init; } = Array.Empty<string>();

        // Raw "Plugin|FormID" keyword keys this rule references (add + remove), 6-hex, for the
        // generator's dead-reference validation pass. Not used by the writer.
        public IReadOnlyList<string> ReferencedKeywordKeys { get; init; } = Array.Empty<string>();

        // Several complete filter clauses, already formatted ("filterByClass=Skyrim.esm|01CE17",
        // "filterByEditorIdContainsExcluded=DLC2EncBandit03"). Used by the GROUP rules (G5,
        // docs/NPC-Gruppen-Plan.md section 5) and by nothing else.
        //
        // WHY IT CANNOT BE THE FIELDS ABOVE: every other rule in this tool filters on ONE record it
        // also edits, so "directive + target" says everything. A group filters on a class, a name
        // fragment and two flags at once, and none of those is a target. When this list is non-empty
        // the writer emits it verbatim instead of building "directive=target" - see
        // SkyPatcherIniWriter.
        public IReadOnlyList<string> FilterClauses { get; init; } = Array.Empty<string>();

        public bool HasChanges => Operations.Count > 0;
    }
}
