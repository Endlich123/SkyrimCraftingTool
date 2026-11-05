using Mutagen.Bethesda.Skyrim;
using System;
using System.Collections.Generic;
using System.Linq;

namespace SkyrimCraftingTool.ViewModel
{
    public enum CustomConditionType
    {
        HasPerk,
        GetIsSex,
        GetActorValue,
        GetLevel,
        GetStageDone,
        GetItemCount,
        EPTemperingItemIsEnchanted,
        GetStage,

        // Scanned and written back verbatim, but NOT editable - see ReadOnlyConditionViewModel.
        // Excluded from the ConditionTypes picker below: turning one into an editable type would
        // throw away a parameter the tool cannot re-create.
        ReadOnly
    }

    // Where a row sits in its OR group (see BaseConditionViewModel.HasOrFlag). Alone = not in one.
    public enum ConditionGroupPosition { Alone, First, Middle, Last }

    public abstract class BaseConditionViewModel : ViewModelBase
    {
        // NOTE: These must be INSTANCE properties (not static). WPF's plain
        // {Binding PropertyName} resolves CLR properties via TypeDescriptor,
        // which only looks at instance members - static properties are
        // silently invisible to it, leaving the bound ComboBox empty.
        public IEnumerable<CustomConditionType> ConditionTypes =>
            Enum.GetValues(typeof(CustomConditionType))
                .Cast<CustomConditionType>()
                .Where(t => t != CustomConditionType.ReadOnly);

        private static readonly string[] DefaultRunOnOptions = { "Subject", "Target" };

        // Subject and Target, plus the condition's own RunOn when it is neither (Reference,
        // CombatTarget, ...) - so the picker can show it instead of going blank.
        public IEnumerable<string> RunOnOptions =>
            _otherRunOn == null ? DefaultRunOnOptions : DefaultRunOnOptions.Append(_otherRunOn);

        // Operator, flags and RunOn as the plugin stored them, carried through so a save writes the
        // condition back as it came in (operator and OR are editable below). Before, every editable condition went back as "==", without its OR
        // flag and with RunOn Subject or Target: GetLevel < 10 became >= 10, an "either perk" pair
        // became "both perks", and a Reference condition reverted to Target - which is also why
        // such recipes could not be reset (the reloaded condition never matched its original,
        // 2026-10-23). Empty = the ESP builder's per-type default, as for a new condition.
        public string StoredCompareOperator
        {
            get => _storedCompareOperator;
            set
            {
                if (SetProperty(ref _storedCompareOperator, value ?? ""))
                    OnPropertyChanged(nameof(CompareSymbol));
            }
        }
        private string _storedCompareOperator = "";

        public string StoredFlags
        {
            get => _storedFlags;
            set
            {
                if (SetProperty(ref _storedFlags, value ?? ""))
                {
                    OnPropertyChanged(nameof(IsOr));
                    OnPropertyChanged(nameof(HasOrFlag));
                }
            }
        }
        private string _storedFlags = "";

        // The reference a RunOn = Reference condition runs on (PlayerRef almost always). Not
        // editable; carried so a save writes it back. ToRecord drops it once RunOn is no longer
        // Reference, so picking Subject or Target cannot leave a stale one behind.
        public string StoredRunOnRef { get; set; } = "";

        // ---- The operator and the OR flag in the editor ----

        private static readonly (string Name, string Symbol)[] Operators =
        {
            ("EqualTo", "=="), ("NotEqualTo", "!="), ("GreaterThan", ">"),
            ("GreaterThanOrEqualTo", ">="), ("LessThan", "<"), ("LessThanOrEqualTo", "<="),
        };

        public IEnumerable<string> CompareSymbols => Operators.Select(o => o.Symbol);

        // What the ESP builder writes when no operator is stored (CobjEspBuilder.BuildCondition) -
        // shown instead of an empty box, so a new or old-scan condition reads as what it does.
        private string DefaultOperator => Type is CustomConditionType.GetActorValue or CustomConditionType.GetLevel
                                                  or CustomConditionType.GetItemCount or CustomConditionType.GetStage
            ? "GreaterThanOrEqualTo"
            : "EqualTo";

        // Choosing the default stores it explicitly - harmless, it is what would be written anyway.
        public string CompareSymbol
        {
            get
            {
                var name = string.IsNullOrEmpty(StoredCompareOperator) ? DefaultOperator : StoredCompareOperator;
                return Operators.FirstOrDefault(o => o.Name == name).Symbol ?? name;
            }
            set
            {
                if (value == null || value == CompareSymbol) return;
                var name = Operators.FirstOrDefault(o => o.Symbol == value).Name;
                if (name != null) StoredCompareOperator = name;
            }
        }

        // "Either this OR the next condition". Other flags stored with it (UseAliases, ...) stay.
        public bool IsOr
        {
            get => FlagList().Contains("OR", StringComparer.OrdinalIgnoreCase);
            set
            {
                if (value == IsOr) return;
                var flags = FlagList().Where(f => !f.Equals("OR", StringComparison.OrdinalIgnoreCase)).ToList();
                if (value) flags.Insert(0, "OR");
                StoredFlags = string.Join(",", flags);
            }
        }

        // ---- OR groups (presentation only) ----
        //
        // Skyrim reads a condition's OR flag as "this OR the NEXT one"; runs of OR rows form a
        // group, and the groups are ANDed: A[OR], B, C is (A or B) and C. The row view draws that
        // as a bar over each group with "OR"/"AND" between rows. ConditionGroups.Apply computes it
        // for the whole list - a row cannot know it alone. None of these reach the record.

        // The OR flag as stored, for every kind of row (a read-only row keeps its flags raw).
        public virtual bool HasOrFlag => IsOr;

        // Drops the OR flag. Used when the row after it is deleted - see ConditionGroups.Remove.
        public virtual void ClearOrFlag() => IsOr = false;

        // Read-only rows are deletable too, except a placeholder from a scan that did not store
        // its position - see ReadOnlyConditionViewModel.
        public virtual bool CanRemove => true;

        // Whether the row's own OR checkbox (in the RunOn column) is shown. A read-only row brings its
        // own, drawn by its overlay at the same spot - the row's box is hidden (not collapsed, so the
        // RunOn picker does not slide into its place) or it would show through.
        public virtual bool HasEditableOrBox => true;

        // Shown on the remove button, also while it is disabled - a greyed button with no reason
        // was the first report of that state (2026-10-24).
        public virtual string RemoveToolTip => "Remove Condition";

        private ConditionGroupPosition _groupPosition;
        public ConditionGroupPosition GroupPosition
        {
            get => _groupPosition;
            set => SetProperty(ref _groupPosition, value);
        }

        // Shown above the row: "OR" when the row before it has the flag, "AND" otherwise, "" for
        // the first row.
        private string _connector = "";
        public string Connector
        {
            get => _connector;
            set => SetProperty(ref _connector, value ?? "");
        }

        // The last row has the OR flag: there is no next condition for it to pair with.
        private bool _isDanglingOr;
        public bool IsDanglingOr
        {
            get => _isDanglingOr;
            set => SetProperty(ref _isDanglingOr, value);
        }

        private List<string> FlagList() =>
            StoredFlags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        private string? _otherRunOn;

        private CustomConditionType _type;
        public virtual CustomConditionType Type
        {
            get => _type;
            set
            {
                if (SetProperty(ref _type, value))
                {
                    OnPropertyChanged(nameof(Type));
                    OnPropertyChanged(nameof(CompareSymbol));   // its default depends on the type
                }
                    
            }
        }

        private string _displayName = string.Empty;
        public string DisplayName
        {
            get => _displayName;
            set => SetProperty(ref _displayName, value);
        }

        private bool _runOnPlayer = true; // Mostly Subject (Player) in the COBJ context
        public bool RunOnPlayer
        {
            get => _runOnPlayer;
            set
            {
                if (SetProperty(ref _runOnPlayer, value))
                {
                    // Picking Subject or Target replaces a RunOn the picker only displayed.
                    if (_otherRunOn != null) { _otherRunOn = null; OnPropertyChanged(nameof(RunOnOptions)); }
                    OnPropertyChanged(nameof(RunOn));
                }
            }
        }

        public string RunOn
        {
            get => RunOnPlayer ? "Subject" : _otherRunOn ?? "Target";
            set
            {
                if (value == RunOn) return;
                RunOnPlayer = value == "Subject";   // clears _otherRunOn when it flips
                var other = value is "Subject" or "Target" || string.IsNullOrEmpty(value) ? null : value;
                if (other != _otherRunOn)
                {
                    _otherRunOn = other;
                    OnPropertyChanged(nameof(RunOnOptions));
                }
                OnPropertyChanged(nameof(RunOn));
            }
        }

        // Shared comparison value of the condition, mapped to the concrete property depending on type
        public abstract float ComparisonValue { get; set; }

        // Helper to later convert the UI condition back into a real Mutagen condition
        public abstract ConditionFloat ToMutagenCondition();
    }
}
