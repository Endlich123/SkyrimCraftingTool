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

        // Emitted as "; <text>" above the rule. Usually EditorID + name.
        public string? Comment { get; init; }

        // Ordered "op=value" fragments, fully formatted.
        public IReadOnlyList<string> Operations { get; init; } = Array.Empty<string>();

        // Raw "Plugin|FormID" keyword keys this rule references (add + remove), 6-hex, for the
        // generator's dead-reference validation pass. Not used by the writer.
        public IReadOnlyList<string> ReferencedKeywordKeys { get; init; } = Array.Empty<string>();

        public bool HasChanges => Operations.Count > 0;
    }
}
