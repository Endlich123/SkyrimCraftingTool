using System;
using System.Collections.Generic;

namespace SkyrimCraftingTool.Services.PatchGen
{
    public sealed class PatchGenReport
    {
        // --- SkyPatcher (Phase A) ---
        public int ArmorRuleCount { get; set; }
        public int WeaponRuleCount { get; set; }
        public int EnchantmentRuleCount { get; set; }
        public int FormListRuleCount { get; set; }

        // COBJ overrides deep-copied from the winning record vs. rebuilt from tracked fields only.
        public int CobjDeepCopiedCount { get; set; }
        public int CobjFromScratchCount { get; set; }

        // Recipes whose condition edits were withheld because the real record holds condition types
        // the scan cannot represent - writing them would have deleted those.
        public int CobjConditionRewriteSkippedCount { get; set; }

        // Of those, the ones caused by an item.db written before the condition-scan fix. A rescan
        // clears them; nothing else will.
        public int StaleConditionDataCount { get; set; }

        // Enchantments overridden in the generated ESP because their worn-restriction FLST
        // assignment changed - the one enchantment edit SkyPatcher has no operation for (E-P4).
        public int EnchantmentEspOverrideCount { get; set; }

        // Same edit as above, but with ESP generation switched off there is no route for it at all.
        // Counted separately so it can be reported as genuinely unpatched rather than silently lost.
        public int EnchantmentAssignmentChangesUnpatched { get; set; }

        // Absolute paths of the .ini files written (empty on a dry run).
        public List<string> WrittenFiles { get; } = new();

        // --- COBJ ESP (Phase B) ---
        public int CobjNewCount { get; set; }
        public int CobjOverrideCount { get; set; }
        public bool CobjEslFlagged { get; set; }
        public string? CobjEspPath { get; set; }
        public IReadOnlyList<string> CobjMasters { get; set; } = Array.Empty<string>();

        // Non-fatal issues: dead keyword references, skipped name edits, recipes without output, etc.
        public List<string> Warnings { get; } = new();

        public int SkyPatcherRuleCount => ArmorRuleCount + WeaponRuleCount + EnchantmentRuleCount + FormListRuleCount;
        public int CobjRecordCount => CobjNewCount + CobjOverrideCount;
        // Enchantment ESP overrides count too: a run whose only change is a re-pointed worn-
        // restriction list produces no rules and no COBJ records, but it definitely generated something.
        public bool AnythingGenerated =>
            SkyPatcherRuleCount > 0 || CobjRecordCount > 0 || EnchantmentEspOverrideCount > 0;

        // Deliberately separate from Warnings: an ESP override is not a problem, but it IS a
        // consequence the user should take in knowingly - the record is copied wholesale, so
        // whatever other mods did to its remaining fields is frozen at generation time.
        // Same idea as EspOverrideNotice: not an error, but the user has to act on it.
        public string? StaleScanNotice =>
            StaleConditionDataCount == 0 ? null :
            $"{StaleConditionDataCount} recipe(s) were left unpatched because their stored conditions " +
            "are older than the scan that learned to read every condition type. Run Scan/Rescan and " +
            "generate again - until then those recipes keep their original conditions.";

        public string? EspOverrideNotice =>
            EnchantmentEspOverrideCount == 0 ? null :
            $"{EnchantmentEspOverrideCount} enchantment(s) needed an ESP override because their " +
            "worn-restriction list assignment was changed - SkyPatcher has no operation for that field. " +
            "An override copies the whole record, so later changes other mods make to these " +
            "enchantments will no longer apply. Editing the list CONTENTS instead stays additive " +
            "and avoids this.";

        public string Summary
        {
            get
            {
                var parts = new List<string>
                {
                    $"{ArmorRuleCount} armor rule(s), {WeaponRuleCount} weapon rule(s), " +
                    $"{EnchantmentRuleCount} enchantment rule(s), {FormListRuleCount} form-list rule(s) " +
                    $"across {WrittenFiles.Count} file(s)",
                };
                if (CobjRecordCount > 0 || CobjEspPath != null)
                    parts.Add($"COBJ: {CobjNewCount} new + {CobjOverrideCount} override" +
                              (CobjEslFlagged ? " (ESL)" : ""));
                if (EnchantmentEspOverrideCount > 0)
                    parts.Add($"{EnchantmentEspOverrideCount} enchantment ESP override(s)");
                if (CobjFromScratchCount > 0)
                    parts.Add($"{CobjFromScratchCount} COBJ override(s) rebuilt from scratch");
                if (CobjConditionRewriteSkippedCount > 0)
                    parts.Add($"{CobjConditionRewriteSkippedCount} condition edit(s) withheld");
                if (EnchantmentAssignmentChangesUnpatched > 0)
                    parts.Add($"{EnchantmentAssignmentChangesUnpatched} FLST assignment change(s) NOT patched");
                if (Warnings.Count > 0)
                    parts.Add($"{Warnings.Count} warning(s)");
                return string.Join("; ", parts);
            }
        }
    }
}
