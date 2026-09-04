using Mutagen.Bethesda.Skyrim;
using SkyrimCraftingTool.Services;
using System;
using System.Globalization;

namespace SkyrimCraftingTool.ViewModel
{
    // A condition the tool scans and writes back faithfully, but does not let the user edit.
    //
    // Two kinds land here:
    //   - ConditionCatalog.ReadOnly types (GetItemCount, EPTemperingItemIsEnchanted, ...). Every
    //     field is stored, so these round-trip exactly.
    //   - ConditionCatalog.Unsupported ones, where only the function name survived the scan.
    //
    // Before this existed the scan simply discarded both, which is how 69% of all COBJ conditions
    // went missing - the recipe editor showed an incomplete list and an ESP override wrote that
    // incomplete list back into the game.
    //
    // It deliberately carries the raw record fields rather than parsed ones: nothing may be lost
    // between load and save, and there is no editor that would need them parsed.
    public sealed class ReadOnlyConditionViewModel : BaseConditionViewModel
    {
        public string RawConditionType { get; set; } = "";
        public string RawTarget { get; set; } = "";
        public string RawValue { get; set; } = "";
        public string RawExtra { get; set; } = "";
        public string RawRunOn { get; set; } = "";
        public string RawCompareOperator { get; set; } = "";
        public string RawFlags { get; set; } = "";

        // Name of the underlying condition function, without the "?" marker used for unsupported ones.
        public string FunctionName => ConditionCatalog.FunctionName(RawConditionType);

        public bool IsUnsupported => ConditionCatalog.IsUnsupported(RawConditionType);

        // Belt and braces for the UI: the row is visually covered by a read-only banner, but a
        // keyboard user could still tab into the hidden Type picker. Pinning the value here means
        // even that cannot turn a preserved condition into an editable one and lose its parameter.
        public override CustomConditionType Type
        {
            get => CustomConditionType.ReadOnly;
            set { /* pinned */ }
        }

        // What the Target column shows. The raw key is the honest answer here - these conditions
        // point at globals, spells, keywords and quests, and the tool has no name lookup for most
        // of those. An empty target means the function takes no parameter.
        public string TargetDisplay =>
            string.IsNullOrWhiteSpace(RawTarget) ? "(no parameter)" : RawTarget;

        public string ValueDisplay
        {
            get
            {
                var op = string.IsNullOrWhiteSpace(RawCompareOperator) ? "" : OperatorSymbol(RawCompareOperator) + " ";
                var val = string.IsNullOrWhiteSpace(RawValue) ? "?" : RawValue;
                var or = RawFlags.Contains("OR", StringComparison.OrdinalIgnoreCase) ? "  [OR]" : "";
                return op + val + or;
            }
        }

        public string Explanation => IsUnsupported
            ? $"'{FunctionName}' is a condition type this tool cannot read yet. It is preserved as-is, "
              + "but editing this recipe's conditions will be refused so it can't be deleted."
            : $"'{FunctionName}' is preserved exactly as found, but is not editable here.";

        private static string OperatorSymbol(string op) => op switch
        {
            "EqualTo" => "==",
            "NotEqualTo" => "!=",
            "GreaterThan" => ">",
            "GreaterThanOrEqualTo" => ">=",
            "LessThan" => "<",
            "LessThanOrEqualTo" => "<=",
            _ => op,
        };

        // Not editable, so there is nothing meaningful to expose. The base class needs the member.
        public override float ComparisonValue
        {
            get => float.TryParse(RawValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0f;
            set { /* read-only */ }
        }

        // Nothing calls this (the ESP path builds conditions from COBJConditionRecord, not from
        // ViewModels), and a faithful rebuild would need the typed parameter this VM deliberately
        // does not parse. Throwing is better than returning a silently wrong condition.
        public override ConditionFloat ToMutagenCondition() =>
            throw new NotSupportedException(
                $"{FunctionName} conditions are read-only and are written from the stored record, not from the view model.");
    }
}
