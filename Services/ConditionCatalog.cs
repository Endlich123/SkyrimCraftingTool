using System;
using System.Collections.Generic;

namespace SkyrimCraftingTool.Services
{
    // One place that says what the tool can do with a given COBJ condition function.
    //
    // Three tiers, and the difference matters:
    //
    //  Editable    - the 5 types with their own ViewModel and XAML template. Fully round-trips.
    //  ReadOnly    - scanned, stored and written back verbatim, but not editable in the UI. These
    //                exist because dropping them was destroying recipes: measured against the
    //                vanilla masters, GetItemCount and EPTemperingItemIsEnchanted alone account for
    //                1528 of the 1688 conditions the scan used to discard.
    //  Unsupported - everything else. Only the function name survives (under UnsupportedPrefix), so
    //                it can be counted and shown, never rebuilt. A recipe holding one of these must
    //                never have its condition list rewritten - see CobjEspBuilder.
    public static class ConditionCatalog
    {
        // Editable in the recipe UI.
        public static readonly IReadOnlySet<string> Editable = new HashSet<string>(StringComparer.Ordinal)
        {
            "HasPerk", "GetIsSex", "GetActorValue", "GetLevel", "GetStageDone",
        };

        // Scanned and rebuilt faithfully, but read-only in the UI. Each takes either no parameter or
        // exactly one FormLink, so Target/Value carry them without a schema change.
        // GetVMQuestVariable additionally uses Extra for its script variable name.
        public static readonly IReadOnlySet<string> ReadOnly = new HashSet<string>(StringComparer.Ordinal)
        {
            "GetItemCount", "EPTemperingItemIsEnchanted", "GetGlobalValue", "HasSpell",
            "HasKeyword", "GetQuestCompleted", "GetInCurrentLoc", "GetVMQuestVariable",
        };

        // ConditionType prefix for a function the scan could not map at all. The remainder of the
        // string is the raw Mutagen function name, kept so the UI and the report can name it.
        public const string UnsupportedPrefix = "?";

        public static bool IsEditable(string? conditionType) =>
            conditionType != null && Editable.Contains(conditionType);

        // True when the tool can turn this row back into a real Mutagen condition.
        public static bool IsRebuildable(string? conditionType) =>
            conditionType != null && (Editable.Contains(conditionType) || ReadOnly.Contains(conditionType));

        public static bool IsUnsupported(string? conditionType) =>
            conditionType != null && conditionType.StartsWith(UnsupportedPrefix, StringComparison.Ordinal);

        // "?GetInFaction" -> "GetInFaction"
        public static string FunctionName(string? conditionType) =>
            IsUnsupported(conditionType) ? conditionType![UnsupportedPrefix.Length..] : conditionType ?? "";
    }
}
