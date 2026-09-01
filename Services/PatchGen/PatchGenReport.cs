using System;
using System.Collections.Generic;

namespace SkyrimCraftingTool.Services.PatchGen
{
    public sealed class PatchGenReport
    {
        // --- SkyPatcher (Phase A) ---
        public int ArmorRuleCount { get; set; }
        public int WeaponRuleCount { get; set; }

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

        public int SkyPatcherRuleCount => ArmorRuleCount + WeaponRuleCount;
        public int CobjRecordCount => CobjNewCount + CobjOverrideCount;
        public bool AnythingGenerated => SkyPatcherRuleCount > 0 || CobjRecordCount > 0;

        public string Summary
        {
            get
            {
                var parts = new List<string>
                {
                    $"{ArmorRuleCount} armor rule(s), {WeaponRuleCount} weapon rule(s) across {WrittenFiles.Count} file(s)",
                };
                if (CobjRecordCount > 0 || CobjEspPath != null)
                    parts.Add($"COBJ: {CobjNewCount} new + {CobjOverrideCount} override" +
                              (CobjEslFlagged ? " (ESL)" : ""));
                if (Warnings.Count > 0)
                    parts.Add($"{Warnings.Count} warning(s)");
                return string.Join("; ", parts);
            }
        }
    }
}
