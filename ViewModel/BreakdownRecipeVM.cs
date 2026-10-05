using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using SkyrimCraftingTool.Model;
using SkyrimCraftingTool.Services;

namespace SkyrimCraftingTool.ViewModel
{
    // One recipe that CONSUMES the selected item.
    //
    // Every other recipe in this tool is found through the item it CREATES
    // (RecipeCacheByCreatedItem). A breakdown recipe has the item on the ingredient side and
    // produces a MISC record the item tree does not even carry, so it is the first recipe type
    // found the other way round - see docs/TODO.md.
    //
    // EDITABLE ONLY WHEN IT IS ACTUALLY A BREAKDOWN. The same list also holds the
    // variant-conversion recipes outfit mods ship (on the author's modlist those outnumber the real
    // breakdowns - a Silver Ring is consumed by one smelter recipe and 31 forge ones). Those are
    // ordinary crafting recipes for ANOTHER item, and that item's own Crafting section already
    // edits them. Offering a second editor here would mean two screens writing the same record.
    public sealed class BreakdownRecipeVM : ViewModelBase
    {
        // CraftingSmelter and CraftingTanningRack, read out of the author's own formid.db rather
        // than typed from memory - the two tempering keywords were once swapped that way and cost a
        // tester an evening (see ItemDBHandler.CreateNewCOBJRecordForItem).
        public const string SmelterKey = "Skyrim.esm|0A5CCE";
        public const string TanningRackKey = "Skyrim.esm|07866A";

        public static bool IsBreakdownWorkbench(string workbenchKey) =>
            string.Equals(workbenchKey, SmelterKey, StringComparison.OrdinalIgnoreCase)
            || string.Equals(workbenchKey, TanningRackKey, StringComparison.OrdinalIgnoreCase);

        // The two benches the section owns. Not derived from the item: whether a piece belongs in
        // the furnace or on the tanning rack cannot be told from its record - ArmorType says
        // nothing about leather vs. metal, and keywords are a lottery on modded armour. So the user
        // picks, from exactly two options.
        public static IReadOnlyList<FormIDRecord> Benches { get; } = new List<FormIDRecord>
        {
            new() { Key = SmelterKey, Name = "Smelter" },
            new() { Key = TanningRackKey, Name = "Tanning Rack" },
        };

        private readonly string _itemKey;
        private readonly IFormIdService _formIds;
        private readonly Action<COBJRecord> _save;
        private readonly Action<BreakdownRecipeVM> _delete;
        private readonly Action<BreakdownRecipeVM> _reset;
        private readonly Action<string, List<COBJConditionRecord>> _saveConditions;

        // ONE DEBOUNCER PER ROW, and that is not incidental. Debouncer keeps a single pending
        // action per instance, so a shared one would let an edit on recipe B cancel the still
        // pending save of recipe A - its own doc comment spells that out and sends multi-target
        // paths elsewhere. One instance per save target is the shape the contract asks for.
        private readonly Debouncer _saveDebouncer = new();

        private bool _loading = true;

        public COBJRecord Record { get; }

        public string Key => Record.Key;

        public bool IsBreakdown => IsBreakdownWorkbench(Record.WorkbenchKeywordKey);

        // Original == 0 means the tool made it, so removing it is just dropping a row. Taking out a
        // recipe that came from a MOD needs an override with the Deleted record flag, which is its
        // own TODO item - so it is not offered here.
        //
        // NOT read off Record.Original, and that cost a bug: ItemDBHandler.InsertCOBJ flips
        // rec.Original to 1 IN MEMORY right after writing the row (so a later SaveCOBJ routes to
        // UPDATE instead of re-INSERT), while the DB column stays 0 forever. A freshly added or
        // preset-applied recipe therefore looked like a mod's: its Delete button was hidden, and
        // Reset ran the mod path, which then called DeleteBreakdownRecipe, which bailed on the very
        // same flag - so the Reset button did nothing at all. The crafting side has exactly this
        // separation for exactly this reason (see MarkCraftingRecipeUserCreated).
        private bool _isUserCreated;

        public bool IsUserCreated => _isUserCreated || Record.Original == 0;

        public void MarkUserCreated(bool isUserCreated)
        {
            if (_isUserCreated == isUserCreated) return;
            _isUserCreated = isUserCreated;
            OnPropertyChanged(nameof(IsUserCreated));
            RaiseChangeFlags();
        }

        // What the recipe produces: the same row type the crafting and temper sections use for
        // their ingredients, because the shape is identical - a material picker plus an amount.
        // Here it reads as the OUTPUT instead of as a cost.
        //
        // Exactly ONE row, never a list: a COBJ creates one KIND of object. "1 ingot AND 2 leather
        // strips" is two recipes and, in game, two menu entries the player chooses between.
        public IngredientEntryVM Output { get; }

        public ObservableCollection<IngredientEntryVM> OutputRow { get; }

        // Instance view of Benches, because a DataTemplate binds against the row, not the type.
        public IReadOnlyList<FormIDRecord> AvailableBenches => Benches;

        private FormIDRecord _selectedBench;
        public FormIDRecord SelectedBench
        {
            get => _selectedBench;
            set
            {
                if (!SetProperty(ref _selectedBench, value)) return;

                Record.WorkbenchKeywordKey = value?.Key ?? "";
                OnPropertyChanged(nameof(WorkbenchName));
                OnPropertyChanged(nameof(IsBreakdown));
                RaiseChangeFlags();
                QueueSave();
            }
        }

        public string WorkbenchName => FriendlyWorkbench(Record.WorkbenchKeywordKey, _formIds);

        // How many of the SELECTED item one run eats. Read-only here: it is the ingredient side,
        // and it is what made this recipe show up in the list at all - letting the user edit it
        // away would make the row delete itself under their hands. Its own decision, later.
        public int ConsumedCount { get; }

        // The other ingredients, if any. Empty for the usual single-input breakdown, which is why
        // it is a string and not another list.
        public string OtherIngredients { get; }

        // The view needs this as a flag. Binding the string through a StringFormat instead put a
        // stray " + " on screen for every recipe without extra inputs - which is almost all of
        // them - because StringFormat renders its literal text around an empty value too.
        public bool HasOtherIngredients => !string.IsNullOrWhiteSpace(OtherIngredients);

        public string OutputName => Output.MaterialName ?? Record.CreatedItemKey;
        public int OutputCount => Record.CreatedObjectCount;
        public string OutputSummary => $"{OutputCount}x {OutputName}";

        // A row the user started and has not finished. Worth saying out loud: until an output is
        // picked the ESP builder skips the record and only mentions it in the patch report.
        public bool IsIncomplete => IsBreakdown && string.IsNullOrWhiteSpace(Record.CreatedItemKey);

        public ICommand DeleteCommand { get; }
        public ICommand ResetCommand { get; }
        public ICommand AddConditionCommand { get; }
        public ICommand RemoveConditionCommand { get; }

        // ---- conditions ----
        //
        // Vanilla smelter recipes carry none; mods routinely hang a HasPerk on theirs, and without
        // an editor here those were invisible and unchangeable on a recipe this section otherwise
        // owns completely.
        //
        // The shared Target-cell templates in Styles/ConditionTemplates.xaml read their catalogues
        // off the nearest ancestor ItemsControl's DataContext - which is this object here - so the
        // two property names below are deliberately the same ones ItemNodeVM, MultiSelectDetailVM
        // and PresetRecipeVM expose. Renaming either breaks all four at once.
        public ObservableCollection<BaseConditionViewModel> Conditions { get; } = new();

        public IEnumerable<FormIDRecord> FilteredCraftingPerks { get; private set; } = new List<FormIDRecord>();
        public IEnumerable<FormIDRecord> FilteredQuests { get; private set; } = new List<FormIDRecord>();

        public void AttachConditionCatalogues(IEnumerable<FormIDRecord> perks, IEnumerable<FormIDRecord> quests)
        {
            FilteredCraftingPerks = perks ?? new List<FormIDRecord>();
            FilteredQuests = quests ?? new List<FormIDRecord>();
            OnPropertyChanged(nameof(FilteredCraftingPerks));
            OnPropertyChanged(nameof(FilteredQuests));
        }

        // Only conditions the editor understands are compared and written back. The read-only ones
        // are shown (via ReadOnlyConditionViewModel) and preserved, exactly as on the crafting side,
        // because the ESP builder refuses to rewrite a set it cannot fully represent.
        private List<string> _originalConditionKeys = new();

        public bool AreConditionsChanged =>
            _hasSnapshot && !ConditionKeysEqual(CurrentConditionKeys, _originalConditionKeys);

        private List<string> CurrentConditionKeys =>
            Conditions
                .Where(ConditionMapper.HasUsableTarget)
                .Select(vm => ConditionMapper.ToRecord(vm, Record.Key))
                .Where(r => r != null)
                .Select(SerializeCondition)
                .ToList();

        private static string SerializeCondition(COBJConditionRecord c) =>
            $"{c.ConditionType}|{c.Target}|{c.Value}|{c.Extra}|{c.RunOn}";

        // Order-insensitive, like the crafting side: the add/remove buttons only append and remove,
        // there is no reordering UI, so a different order is not a real edit.
        private static bool ConditionKeysEqual(List<string> a, List<string> b) =>
            a.Count == b.Count
            && a.OrderBy(x => x, StringComparer.Ordinal).SequenceEqual(b.OrderBy(x => x, StringComparer.Ordinal));

        public void LoadConditions(IEnumerable<COBJConditionRecord> records)
        {
            _loading = true;
            try
            {
                foreach (var existing in Conditions)
                    existing.PropertyChanged -= OnConditionChanged;
                Conditions.Clear();

                foreach (var record in records ?? Enumerable.Empty<COBJConditionRecord>())
                {
                    var vm = ConditionMapper.ToViewModel(record, FilteredCraftingPerks, FilteredQuests);
                    if (vm == null) continue;
                    vm.PropertyChanged += OnConditionChanged;
                    Conditions.Add(vm);
                }

                _originalConditionKeys = CurrentConditionKeys;
            }
            finally
            {
                _loading = false;
            }

            OnPropertyChanged(nameof(AreConditionsChanged));
        }

        private void OnConditionChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
            => NotifyConditionsChanged();

        private void NotifyConditionsChanged()
        {
            if (_loading) return;

            OnPropertyChanged(nameof(AreConditionsChanged));
            RaiseChangeFlags();
            QueueConditionSave();
        }

        // ---- change tracking against the scanned baseline ----
        //
        // Same shape as the crafting and temper sections: a snapshot taken when the row is built,
        // compared field by field. Without a snapshot nothing is "changed" - that is the state of a
        // recipe whose COBJ row the database does not have, where there is nothing to compare to.
        private bool _hasSnapshot;
        private string _originalWorkbenchKey = "";
        private string _originalOutputKey = "";
        private int _originalOutputCount = 1;

        public void CaptureOriginalSnapshot(COBJRecord original)
        {
            if (original == null) return;

            _originalWorkbenchKey = original.WorkbenchKeywordKey ?? "";
            _originalOutputKey = original.CreatedItemKey ?? "";
            _originalOutputCount = original.CreatedObjectCount < 1 ? 1 : original.CreatedObjectCount;
            _hasSnapshot = true;

            RaiseChangeFlags();
        }

        public bool IsWorkbenchChanged =>
            _hasSnapshot && !string.Equals(Record.WorkbenchKeywordKey ?? "", _originalWorkbenchKey,
                StringComparison.OrdinalIgnoreCase);

        public bool IsOutputChanged =>
            _hasSnapshot && (!string.Equals(Record.CreatedItemKey ?? "", _originalOutputKey,
                                 StringComparison.OrdinalIgnoreCase)
                             || Record.CreatedObjectCount != _originalOutputCount);

        // A recipe the tool created never existed in any plugin, so it is a change in its entirety -
        // exactly like HasCraftingChanges treats one.
        public bool HasChanges =>
            IsUserCreated || IsWorkbenchChanged || IsOutputChanged || AreConditionsChanged;

        private void RaiseChangeFlags()
        {
            OnPropertyChanged(nameof(IsWorkbenchChanged));
            OnPropertyChanged(nameof(IsOutputChanged));
            OnPropertyChanged(nameof(HasChanges));
            _changed?.Invoke();
        }

        private readonly Action _changed;

        public BreakdownRecipeVM(
            COBJRecord record,
            string itemKey,
            IFormIdService formIds,
            Action<COBJRecord> save = null,
            Action<BreakdownRecipeVM> delete = null,
            Action<BreakdownRecipeVM> reset = null,
            Action changed = null,
            Action<string, List<COBJConditionRecord>> saveConditions = null)
        {
            Record = record;
            _itemKey = itemKey;
            _formIds = formIds;
            _save = save;
            _delete = delete;
            _reset = reset;
            _changed = changed;
            _saveConditions = saveConditions;

            var parsed = record.IngredientKeys?.Select(ParseIngredient).ToList()
                         ?? new List<(string Key, int Count)>();

            ConsumedCount = parsed
                .Where(p => string.Equals(p.Key, itemKey, StringComparison.OrdinalIgnoreCase))
                .Sum(p => p.Count);

            OtherIngredients = string.Join(", ", parsed
                .Where(p => !string.Equals(p.Key, itemKey, StringComparison.OrdinalIgnoreCase))
                .Select(p => $"{NameOf(p.Key, formIds)} x{p.Count}"));

            // The output row routes its changes back here rather than into the item's crafting or
            // temper ingredient list - see IngredientEntryVM's onChanged parameter.
            Output = new IngredientEntryVM(null, isTemper: false, onChanged: OnOutputChanged)
            {
                Key = record.CreatedItemKey ?? "",
                Count = record.CreatedObjectCount < 1 ? 1 : record.CreatedObjectCount,
                MaterialName = NameOf(record.CreatedItemKey, formIds),
            };
            OutputRow = new ObservableCollection<IngredientEntryVM> { Output };

            _selectedBench = Benches.FirstOrDefault(b =>
                string.Equals(b.Key, record.WorkbenchKeywordKey, StringComparison.OrdinalIgnoreCase));

            DeleteCommand = new RelayCommand(() => _delete?.Invoke(this), () => IsUserCreated);
            ResetCommand = new RelayCommand(() =>
            {
                if (HasChanges) _reset?.Invoke(this);
            });

            AddConditionCommand = new RelayCommand(() =>
            {
                var condition = new PerkConditionViewModel();
                condition.PropertyChanged += OnConditionChanged;
                Conditions.Add(condition);
                NotifyConditionsChanged();
            });

            RemoveConditionCommand = new RelayCommand<BaseConditionViewModel>(condition =>
            {
                if (condition == null) return;
                condition.PropertyChanged -= OnConditionChanged;
                Conditions.Remove(condition);
                NotifyConditionsChanged();
            });

            _loading = false;
        }

        private void OnOutputChanged()
        {
            if (_loading) return;

            Record.CreatedItemKey = Output.Key ?? "";
            Record.CreatedObjectCount = Output.Count < 1 ? 1 : Output.Count;

            OnPropertyChanged(nameof(OutputName));
            OnPropertyChanged(nameof(OutputCount));
            OnPropertyChanged(nameof(OutputSummary));
            OnPropertyChanged(nameof(IsIncomplete));
            RaiseChangeFlags();

            QueueSave();
        }

        // Typing in the amount box is a burst of keystrokes, so it is debounced like every other
        // field edit. The 350 ms matches MainContentVM's own autosave window.
        private void QueueSave()
        {
            if (_loading || _save == null) return;

            _saveDebouncer.DebounceAsync(350, _ =>
            {
                _save(Record);
                return System.Threading.Tasks.Task.CompletedTask;
            });
        }

        // Its OWN debouncer, not the one above. Conditions go to a different table through a
        // different call, and a shared instance keeps only one pending action - so editing a
        // condition would have thrown away a not yet written amount, and the other way round.
        private readonly Debouncer _conditionDebouncer = new();

        private void QueueConditionSave()
        {
            if (_loading || _saveConditions == null) return;

            _conditionDebouncer.DebounceAsync(350, _ =>
            {
                // Half-built rows are skipped, not written: a HasPerk with no perk yet would be
                // saved as an empty condition and come back as one. Same filter the crafting
                // handler applies.
                var records = Conditions
                    .Where(ConditionMapper.HasUsableTarget)
                    .Select(vm => ConditionMapper.ToRecord(vm, Record.Key))
                    .Where(r => r != null)
                    .ToList();

                Record.Conditions = records;
                _saveConditions(Record.Key, records);
                return System.Threading.Tasks.Task.CompletedTask;
            });
        }

        // "Plugin|FormID*Count", the same shape COBJNodeVM parses. A missing or unreadable count
        // means one, which is what an ingredient without a quantity is.
        internal static (string Key, int Count) ParseIngredient(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return ("", 1);

            var star = raw.IndexOf('*');
            if (star < 0) return (raw, 1);

            var key = raw.Substring(0, star);
            return int.TryParse(raw.Substring(star + 1), out var count) && count >= 1
                ? (key, count)
                : (key, 1);
        }

        private static string FriendlyWorkbench(string key, IFormIdService formIds)
        {
            if (string.Equals(key, SmelterKey, StringComparison.OrdinalIgnoreCase)) return "Smelter";
            if (string.Equals(key, TanningRackKey, StringComparison.OrdinalIgnoreCase)) return "Tanning Rack";

            // Anything else is an upgrade or variant-conversion recipe that happens to eat this
            // item. Worth showing, but it gets the raw EditorID rather than an invented name.
            return NameOf(key, formIds);
        }

        private static string NameOf(string key, IFormIdService formIds)
        {
            if (string.IsNullOrWhiteSpace(key)) return "(pick a material)";

            var record = formIds?.GetByKey(key);
            return string.IsNullOrWhiteSpace(record?.Name) ? key : record.Name;
        }
    }
}
