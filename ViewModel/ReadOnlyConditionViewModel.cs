using Mutagen.Bethesda.Skyrim;
using SkyrimCraftingTool.Services;
using System;
using System.Globalization;
using System.Linq;

namespace SkyrimCraftingTool.ViewModel
{
    // A condition the tool scans and writes back faithfully, but does not let the user edit.
    //
    // Two kinds land here:
    //   - ConditionCatalog.ReadOnly types (EPTemperingItemIsEnchanted, HasSpell, ...). Every
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
        public string RawRunOnRef { get; set; } = "";
        public string RawCompareOperator { get; set; } = "";
        private string _rawFlags = "";
        public string RawFlags
        {
            get => _rawFlags;
            set
            {
                if (SetProperty(ref _rawFlags, value ?? ""))
                {
                    OnPropertyChanged(nameof(HasOrFlag));
                    OnPropertyChanged(nameof(ValueDisplay));
                    OnPropertyChanged(nameof(IsOrRaw));
                }
            }
        }

        public override bool HasOrFlag => RawFlags
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Contains("OR", StringComparer.OrdinalIgnoreCase);

        // The OR checkbox on a read-only row. Everything else stays locked, but the flag is safe to edit:
        // a rebuilt read-only type takes its flags from the row, and a placeholder's copied original
        // takes them too (CobjEspBuilder) - so this is how a locked condition is moved in or out of an
        // OR group. Other flags (SwapSubjectAndTarget, ...) are kept.
        public bool IsOrRaw
        {
            get => HasOrFlag;
            set
            {
                if (value == HasOrFlag || !CanEditOr) return;
                if (value) RawFlags = string.IsNullOrEmpty(RawFlags) ? "OR" : "OR," + RawFlags;
                else ClearOrFlag();
            }
        }

        // A placeholder from a scan before 2026-10-23 stored no operator and no flags; the builder then
        // keeps the plugin's flags for it, so a ticked box would not reach the ESP. Locked for those.
        public bool CanEditOr => !IsUnsupported || !string.IsNullOrEmpty(RawCompareOperator);

        public override bool HasEditableOrBox => false;   // the overlay draws its own (IsOrRaw)

        // The flags are the one thing on a read-only row that may change: when the row after it is
        // deleted (ConditionGroups.Remove). CobjEspBuilder writes the row's flags, also onto a
        // placeholder's copied original.
        public override void ClearOrFlag() =>
            RawFlags = string.Join(",", RawFlags
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(f => !f.Equals("OR", StringComparison.OrdinalIgnoreCase)));

        // Deletable since 2026-10-24. Not a placeholder without a stored position, though: the builder
        // then matches placeholders by count, and one fewer would make every edit of this recipe be
        // withheld. A rescan stores the position.
        public override bool CanRemove => !IsUnsupported
            || ConditionCatalog.TryGetPlaceholderPosition(
                   new Model.COBJConditionRecord { ConditionType = RawConditionType, Extra = RawExtra }, out _);

        public override string RemoveToolTip => CanRemove
            ? base.RemoveToolTip
            : $"'{FunctionName}' was read by an older scan that did not record where it sits in the recipe, "
              + "so removing it could put the wrong condition back. Run Scan/Rescan once to make it removable.";

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
                return op + val;   // the OR has its own checkbox on the row (IsOrRaw)
            }
        }

        // Since C1 (docs/Conditions-Plan.md) none of these block a condition edit any more: an
        // unreadable one is copied back from the plugin at its position, a global comparison is
        // rebuilt from its stored key.
        public string Explanation => IsUnsupported
            ? $"'{FunctionName}' is a condition type this tool cannot read. It is copied over from the plugin "
              + "unchanged, at this position, when the recipe is patched."
            : RawValue.Contains('|')
                ? $"'{FunctionName}' compares against a global variable ({RawValue}), not a number. It is "
                  + "preserved exactly as found, but is not editable here."
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
