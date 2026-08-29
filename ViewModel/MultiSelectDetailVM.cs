using SkyrimCraftingTool.Model;
using SkyrimCraftingTool.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Data;
using System.Windows.Input;

namespace SkyrimCraftingTool.ViewModel
{
    // Bulk editing for the item multi-selection in the TreeView (see MainContentVM.SelectedItems).
    // Keywords are additive (add/remove), Container additive. Crafting/Temper are free-form
    // templates the user builds directly right here (Workbench, Conditions, Ingredients - the same
    // fields the single-item editor exposes), NOT copied from one of the selected items - a user
    // explicitly did not want an implicit "pick one item as the source" workflow. Which of those
    // parts actually get applied is the user's own choice via checkboxes (Workbench/Conditions/
    // Ingredients for Crafting, Conditions/Ingredients for Temper - no Workbench there, same as the
    // single-item editor, where the Temper workbench is always auto-derived at COBJ-creation time).
    // The Conditions/Ingredients template editors below intentionally reuse the exact same property/
    // command names as ItemNodeVM's single-item editor (FilteredCraftingPerks, FilteredQuests,
    // AddCraftingConditionCommand, AddIngredientCommand, etc.) so MainContentView.xaml's proven
    // DataTemplates for them could be copied here verbatim instead of duplicating that UI logic.
    // Auto-Apply (Presets) is deliberately only a placeholder for now, see MultiSelectDetailView.xaml.
    //
    // Performance: Keywords/Container now deliberately mirror the single-item editor's filtering
    // behavior (ItemNodeVM.KeywordFilter, MainContentVM.LimitedContainerVMs) - without the
    // relevance/merchant filter, ALL keywords/containers from the entire mod list got rendered here
    // (several hundred, not virtualized), which caused noticeable lag when expanding and on every
    // keystroke in the search box.
    public class MultiSelectDetailVM : ViewModelBase
    {
        private readonly MainContentVM _main;

        public ObservableCollection<ItemNodeVM> SelectedItems => _main.SelectedItems;

        private string? _statusMessage;
        public string? StatusMessage
        {
            get => _statusMessage;
            private set => SetProperty(ref _statusMessage, value);
        }

        // --- Auto-Apply (Presets) ---
        public List<PresetFile> AvailablePresets => _main.AllPresets;

        private PresetFile? _selectedPreset;
        public PresetFile? SelectedPreset
        {
            get => _selectedPreset;
            set => SetProperty(ref _selectedPreset, value);
        }

        public ICommand ApplyPresetCommand { get; }

        // --- Keywords (additive) ---
        public ObservableCollection<KeywordRowVM> KeywordRows { get; } = new();

        private readonly ICollectionView _keywordRowsView;
        public ICollectionView KeywordRowsView => _keywordRowsView;

        private string _keywordSearchText = string.Empty;
        public string KeywordSearchText
        {
            get => _keywordSearchText;
            set
            {
                if (SetProperty(ref _keywordSearchText, value))
                    Debounce(ref _keywordSearchCts, 180, () => _keywordRowsView.Refresh());
            }
        }

        private bool _showAllKeywords;
        public bool ShowAllKeywords
        {
            get => _showAllKeywords;
            set
            {
                if (SetProperty(ref _showAllKeywords, value))
                    _keywordRowsView.Refresh();
            }
        }

        private static readonly string[] ArmorPrefixes = { "Armor", "Clothing", "Jewelry", "VendorItemArmor", "Vendor", "Material" };
        private static readonly string[] WeaponPrefixes = { "Weap", "Weapon", "VendorItemWeapon", "Vendor", "Material", "DamageType" };

        // --- Crafting Recipe: free-form template, built by the user ---
        // Workbench: same catalog/search pattern as ItemNodeVM.FilteredCraftingWorkbenches/
        // SelectedWorkbench, just sourced from _main instead of a loaded item.
        private string _craftingWorkbenchSearchText = string.Empty;
        public string CraftingWorkbenchSearchText
        {
            get => _craftingWorkbenchSearchText;
            set
            {
                if (SetProperty(ref _craftingWorkbenchSearchText, value))
                    OnPropertyChanged(nameof(FilteredCraftingWorkbenches));
            }
        }

        public IEnumerable<FormIDRecord> FilteredCraftingWorkbenches =>
            _main.AllAvailableWorkbenches.Where(w =>
                string.IsNullOrWhiteSpace(CraftingWorkbenchSearchText)
                || w.Name.Contains(CraftingWorkbenchSearchText, StringComparison.OrdinalIgnoreCase));

        private FormIDRecord? _selectedCraftingWorkbench;
        public FormIDRecord? SelectedCraftingWorkbench
        {
            get => _selectedCraftingWorkbench;
            set
            {
                if (SetProperty(ref _selectedCraftingWorkbench, value) && value != null
                    && string.IsNullOrEmpty(CraftingWorkbenchSearchText))
                    CraftingWorkbenchSearchText = value.Name;
            }
        }

        // Perks/Quests catalogs for the Conditions template editors below (Crafting AND Temper, same
        // as the single-item editor uses one shared FilteredCraftingPerks/FilteredQuests for both) -
        // names must match ItemNodeVM's exactly, the Conditions DataTemplate in
        // MultiSelectDetailView.xaml is copied verbatim from MainContentView.xaml and binds to them
        // via the ItemsControl's DataContext.
        public IEnumerable<FormIDRecord> FilteredCraftingPerks => _main.AllAvailablePerks;
        public IEnumerable<FormIDRecord> FilteredQuests => _main.AllAvailableQuests;

        public ObservableCollection<BaseConditionViewModel> CraftingConditionsTemplate { get; } = new();
        public ObservableCollection<IngredientEntryVM> CraftingIngredientsTemplate { get; } = new();

        public ICommand AddCraftingConditionCommand { get; }
        public ICommand RemoveCraftingConditionCommand { get; }
        public ICommand AddIngredientCommand { get; }
        public ICommand RemoveIngredientCommand { get; }

        private bool _includeCraftingWorkbench = true;
        public bool IncludeCraftingWorkbench
        {
            get => _includeCraftingWorkbench;
            set => SetProperty(ref _includeCraftingWorkbench, value);
        }

        private bool _includeCraftingConditions = true;
        public bool IncludeCraftingConditions
        {
            get => _includeCraftingConditions;
            set => SetProperty(ref _includeCraftingConditions, value);
        }

        private bool _includeCraftingIngredients;
        public bool IncludeCraftingIngredients
        {
            get => _includeCraftingIngredients;
            set => SetProperty(ref _includeCraftingIngredients, value);
        }

        // --- Temper Recipe: same idea, minus Workbench ---
        // Like in the single-item editor, the Temper workbench is always auto-derived from the item
        // at COBJ-creation time, never user-settable - so there's nothing to build a template for
        // here (see ItemNodeVM/CreateNewCOBJRecordForItem).
        public ObservableCollection<BaseConditionViewModel> TemperConditionsTemplate { get; } = new();
        public ObservableCollection<IngredientEntryVM> TemperIngredientsTemplate { get; } = new();

        public ICommand AddTemperConditionCommand { get; }
        public ICommand RemoveTemperConditionCommand { get; }
        public ICommand AddTemperIngredientCommand { get; }
        public ICommand RemoveTemperIngredientCommand { get; }

        private bool _includeTemperConditions = true;
        public bool IncludeTemperConditions
        {
            get => _includeTemperConditions;
            set => SetProperty(ref _includeTemperConditions, value);
        }

        private bool _includeTemperIngredients;
        public bool IncludeTemperIngredients
        {
            get => _includeTemperIngredients;
            set => SetProperty(ref _includeTemperIngredients, value);
        }

        // --- Container (additive) ---
        // Own ContainerEntryVM instances, decoupled from the single-item editor: MainContentVM.AllContainerVMs
        // gets continuously resynced via UpdateAllContainerSelectionFlags(SelectedNode) (among other things,
        // reset to null as soon as multiple items are selected) - toggle state derived from it would have
        // been wiped out here constantly.
        public ObservableCollection<ContainerEntryVM> ContainerTemplateRows { get; } = new();

        private readonly ICollectionView _containerRowsView;
        public ICollectionView ContainerRowsView => _containerRowsView;

        // The "Template" on the right: the containers currently selected for the bulk apply, with LVLi levels.
        public ObservableCollection<ContainerEntryVM> SelectedContainerTemplateRows { get; } = new();

        private string _containerSearchText = string.Empty;
        public string ContainerSearchText
        {
            get => _containerSearchText;
            set
            {
                if (SetProperty(ref _containerSearchText, value))
                    Debounce(ref _containerSearchCts, 180, () => _containerRowsView.Refresh());
            }
        }

        private bool _showExpertContainers;
        public bool ShowExpertContainers
        {
            get => _showExpertContainers;
            set
            {
                if (SetProperty(ref _showExpertContainers, value))
                    _containerRowsView.Refresh();
            }
        }

        public ICommand ApplyCraftingRecipeCommand { get; }
        public ICommand ApplyTemperRecipeCommand { get; }
        public ICommand ApplyContainersCommand { get; }

        private CancellationTokenSource? _keywordSearchCts;
        private CancellationTokenSource? _containerSearchCts;
        private CancellationTokenSource? _selectionRefreshCts;

        public MultiSelectDetailVM(MainContentVM main)
        {
            _main = main;

            foreach (var kw in main.AllAvailableKeywords.OrderBy(k => k.Name))
                KeywordRows.Add(new KeywordRowVM(kw.Key, kw.Name, this));

            _keywordRowsView = CollectionViewSource.GetDefaultView(KeywordRows);
            _keywordRowsView.Filter = FilterKeywordRow;

            // ContainerEntryVM.ToggleSelectedCommand only fires an event (intended for the "Remove"
            // button in the single-item editor) instead of flipping IsSelected itself - own, simple
            // toggle logic here instead of repurposing that event pattern.
            foreach (var c in main.AllContainers.OrderBy(c => c.Name))
            {
                var row = new ContainerEntryVM(c);
                row.ToggleSelectedRequested += OnContainerRowToggleRequested;
                ContainerTemplateRows.Add(row);
            }

            _containerRowsView = CollectionViewSource.GetDefaultView(ContainerTemplateRows);
            _containerRowsView.Filter = FilterContainerRow;

            ApplyCraftingRecipeCommand = new RelayCommand(async () => await ApplyRecipeAsync(isTemper: false));
            ApplyTemperRecipeCommand = new RelayCommand(async () => await ApplyRecipeAsync(isTemper: true));
            ApplyContainersCommand = new RelayCommand(async () => await ApplyContainersAsync());
            ApplyPresetCommand = new RelayCommand(async () => await ApplyPresetAsync());

            AddCraftingConditionCommand = new RelayCommand(() => CraftingConditionsTemplate.Add(new PerkConditionViewModel()));
            RemoveCraftingConditionCommand = new RelayCommand<BaseConditionViewModel>(c => { if (c != null) CraftingConditionsTemplate.Remove(c); });
            AddIngredientCommand = new RelayCommand(() => AddIngredientTemplateRow(CraftingIngredientsTemplate));
            RemoveIngredientCommand = new RelayCommand<IngredientEntryVM>(ing => { if (ing != null) CraftingIngredientsTemplate.Remove(ing); });

            AddTemperConditionCommand = new RelayCommand(() => TemperConditionsTemplate.Add(new PerkConditionViewModel()));
            RemoveTemperConditionCommand = new RelayCommand<BaseConditionViewModel>(c => { if (c != null) TemperConditionsTemplate.Remove(c); });
            AddTemperIngredientCommand = new RelayCommand(() => AddIngredientTemplateRow(TemperIngredientsTemplate));
            RemoveTemperIngredientCommand = new RelayCommand<IngredientEntryVM>(ing => { if (ing != null) TemperIngredientsTemplate.Remove(ing); });

            // See ItemNodeVM's own SubscribeConditionEvents/ReplaceConditionForTypeChange (the
            // single-item editor had the exact same "changing Type doesn't swap the Target/Value
            // editor" bug once - fixed there by forcing a fresh CLR instance of the right subclass
            // in, since WPF's ContentControl DataTemplate selection is keyed on concrete type, not on
            // the Type enum value). CraftingConditionsTemplate/TemperConditionsTemplate are never
            // reassigned wholesale (readonly auto-properties), so a one-time Subscribe here is enough.
            SubscribeConditionTemplateEvents(CraftingConditionsTemplate);
            SubscribeConditionTemplateEvents(TemperConditionsTemplate);

            SelectedItems.CollectionChanged += OnSelectedItemsChanged;
        }

        private bool FilterKeywordRow(object o)
        {
            if (o is not KeywordRowVM row) return false;

            if (!string.IsNullOrWhiteSpace(KeywordSearchText) &&
                !row.Name.Contains(KeywordSearchText, StringComparison.OrdinalIgnoreCase))
                return false;

            if (ShowAllKeywords) return true;

            bool anyArmor = SelectedItems.Any(i => i.IsArmor);
            bool anyWeapon = SelectedItems.Any(i => i.IsWeapon);

            if (!anyArmor && !anyWeapon) return false;

            return (anyArmor && ArmorPrefixes.Any(p => row.Name.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
                || (anyWeapon && WeaponPrefixes.Any(p => row.Name.StartsWith(p, StringComparison.OrdinalIgnoreCase)));
        }

        private bool FilterContainerRow(object o)
        {
            if (o is not ContainerEntryVM row) return false;

            if (!string.IsNullOrWhiteSpace(ContainerSearchText))
                return row.Name.Contains(ContainerSearchText, StringComparison.OrdinalIgnoreCase);

            // Same as in the single-item editor (LimitedContainerVMs): only vendors by default, to
            // keep the list small. "Expert" shows the full mod-list catalog.
            return ShowExpertContainers || row.Name.Contains("Merchant", StringComparison.OrdinalIgnoreCase);
        }

        // Template row for the free-form Crafting/Temper Ingredients editors: no parent ItemNodeVM
        // yet (see IngredientEntryVM's nullable _parentItem), it only becomes one once cloned onto a
        // real target item in ApplyRecipeAsync/CloneIngredientsInto.
        private void AddIngredientTemplateRow(ObservableCollection<IngredientEntryVM> target)
        {
            var entry = new IngredientEntryVM(null);
            entry.InitializeMaterials(_main.AllAvailableMaterials);
            target.Add(entry);
        }

        // --- Conditions template: Type-change re-templating ---
        // Mirrors ItemNodeVM's SubscribeConditionEvents/OnConditionPropertyChanged/
        // ReplaceConditionForTypeChange for the single-item editor's own Conditions, adapted to a
        // plain template collection with no owning ItemNodeVM. Without this, switching the "Type"
        // combo just changes an enum property on the SAME PerkConditionViewModel/etc. instance - the
        // ContentControl's Target editor is selected by concrete CLR type (DataType=), so it keeps
        // showing the OLD editor since the object's actual type never changed.
        private bool _isReplacingTemplateConditionType;

        private void SubscribeConditionTemplateEvents(ObservableCollection<BaseConditionViewModel> collection)
        {
            foreach (var condition in collection)
                condition.PropertyChanged += OnConditionTemplatePropertyChanged;

            collection.CollectionChanged += OnConditionTemplateCollectionChanged;
        }

        private void OnConditionTemplateCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.OldItems != null)
                foreach (BaseConditionViewModel condition in e.OldItems)
                    condition.PropertyChanged -= OnConditionTemplatePropertyChanged;

            if (e.NewItems != null)
                foreach (BaseConditionViewModel condition in e.NewItems)
                    condition.PropertyChanged += OnConditionTemplatePropertyChanged;
        }

        private void OnConditionTemplatePropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (_isReplacingTemplateConditionType) return;
            if (sender is not BaseConditionViewModel condition) return;
            if (e.PropertyName != nameof(BaseConditionViewModel.Type)) return;

            var collection = CraftingConditionsTemplate.Contains(condition) ? CraftingConditionsTemplate
                : TemperConditionsTemplate.Contains(condition) ? TemperConditionsTemplate
                : null;
            if (collection == null) return;

            ReplaceTemplateConditionForTypeChange(collection, condition);
        }

        private void ReplaceTemplateConditionForTypeChange(
            ObservableCollection<BaseConditionViewModel> collection,
            BaseConditionViewModel oldCondition)
        {
            var index = collection.IndexOf(oldCondition);
            if (index < 0) return;

            var newCondition = CreateConditionViewModel(oldCondition.Type);

            // Carry over whatever makes sense across condition types - same as the single-item editor.
            newCondition.RunOnPlayer = oldCondition.RunOnPlayer;
            try { newCondition.ComparisonValue = oldCondition.ComparisonValue; } catch { /* not all values are meaningful across types */ }

            _isReplacingTemplateConditionType = true;
            try
            {
                oldCondition.PropertyChanged -= OnConditionTemplatePropertyChanged;
                newCondition.PropertyChanged += OnConditionTemplatePropertyChanged;

                // Explicit Remove + Insert (not the collection indexer's single "Replace"
                // notification) - see ItemNodeVM.ReplaceConditionForTypeChange for why: WPF's
                // ItemsControl otherwise reuses the old container instead of regenerating it, which
                // leaves the old DataTemplate showing for the new type.
                collection.RemoveAt(index);
                collection.Insert(index, newCondition);
            }
            finally
            {
                _isReplacingTemplateConditionType = false;
            }
        }

        private static BaseConditionViewModel CreateConditionViewModel(CustomConditionType type) => type switch
        {
            CustomConditionType.HasPerk => new PerkConditionViewModel(),
            CustomConditionType.GetIsSex => new SexConditionViewModel(),
            CustomConditionType.GetActorValue => new ActorValueConditionViewModel(),
            CustomConditionType.GetLevel => new LevelConditionViewModel(),
            CustomConditionType.GetStageDone => new QuestStageConditionViewModel(),
            _ => new PerkConditionViewModel()
        };

        private void OnContainerRowToggleRequested(ContainerEntryVM row)
        {
            row.IsSelected = !row.IsSelected;

            if (row.IsSelected)
            {
                if (!SelectedContainerTemplateRows.Contains(row))
                    SelectedContainerTemplateRows.Add(row);
            }
            else
            {
                SelectedContainerTemplateRows.Remove(row);
            }
        }

        private void OnSelectedItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            // At 0/1 items the MultiSelectDetailView is invisible (see MainContentView.xaml,
            // IsMultiSelectActive) - recomputing the keyword status isn't worth it then, but would
            // otherwise still run on EVERY single-select click too (CollectionChanged fires
            // regardless of visibility), because this VM gets created once on first open and stays
            // alive afterward instead of being disposed together with the view.
            if (SelectedItems.Count <= 1)
                return;

            // A Shift range-click adds many items in quick succession (one CollectionChanged per
            // item) - coalesce instead of recomputing the entire (keyword) status list on every
            // single Add.
            Debounce(ref _selectionRefreshCts, 120, () =>
            {
                RefreshKeywordRows();
                _keywordRowsView.Refresh();
            });
        }

        // Counts ONCE per refresh how many of the selected items have each keyword set
        // (O(Items * KeywordsPerItem)). Previously, every single KeywordRowVM.StatusText called the
        // equivalent separately per row (O(Rows * Items * KeywordsPerItem)) - in Expert mode (several
        // hundred keywords), that multiplies massively and, together with the missing UI
        // virtualization (see MultiSelectDetailView.xaml), was the main cause of the lag.
        private Dictionary<string, int> _keywordSelectionCounts = new();

        private void RefreshKeywordRows()
        {
            _keywordSelectionCounts = BuildKeywordSelectionCounts();

            foreach (KeywordRowVM row in _keywordRowsView)
                row.Refresh();
        }

        private Dictionary<string, int> BuildKeywordSelectionCounts()
        {
            var counts = new Dictionary<string, int>();
            foreach (var item in SelectedItems)
            {
                foreach (var kw in item.AllKeywords)
                {
                    if (!kw.IsSelected) continue;
                    counts[kw.Key] = counts.TryGetValue(kw.Key, out var c) ? c + 1 : 1;
                }
            }
            return counts;
        }

        private void Debounce(ref CancellationTokenSource? cts, int delayMs, Action action)
        {
            cts?.Cancel();
            var newCts = new CancellationTokenSource();
            cts = newCts;
            var token = newCts.Token;

            Task.Delay(delayMs, token).ContinueWith(t =>
            {
                if (t.IsCanceled) return;
                System.Windows.Application.Current?.Dispatcher.Invoke(action);
            }, TaskScheduler.Default);
        }

        // Called by KeywordRowVM: X/Y of the currently selected items have this keyword. Reads from
        // the cache precomputed in RefreshKeywordRows() instead of iterating SelectedItems again per
        // row.
        internal string ComputeKeywordStatus(string key)
        {
            var items = SelectedItems;
            if (items.Count == 0) return string.Empty;

            int count = _keywordSelectionCounts.TryGetValue(key, out var c) ? c : 0;
            if (count == 0) return "–";
            if (count >= items.Count) return "all";
            return $"{count}/{items.Count}";
        }

        // Called by KeywordRowVM (one click per row): not set on all yet -> add it; already set on
        // all -> remove it.
        internal async Task ToggleKeywordAsync(string key)
        {
            bool select = ComputeKeywordStatus(key) != "all";
            int changed = 0;

            foreach (var item in SelectedItems.ToList())
            {
                var kw = item.AllKeywords.FirstOrDefault(k => k.Key == key);
                if (kw == null || kw.IsReadOnly || kw.IsSelected == select)
                    continue;

                // Kicks off business rules (exclusivity etc.) + SelectedKeywordKeys sync, exactly
                // like a click in the single-item editor - see ItemNodeVM.OnKeywordPropertyChanged.
                kw.IsSelected = select;
                changed++;

                await _main.PersistFieldAsync(item, nameof(ItemNodeVM.SelectedKeywordKeys));
            }

            RefreshKeywordRows();
            StatusMessage = changed == 0
                ? "No change (the keyword was already set that way on all items, or is read-only)."
                : $"Keyword {(select ? "added" : "removed")} on {changed} item(s).";
        }

        private async Task ApplyRecipeAsync(bool isTemper)
        {
            // Temper has no Workbench option - see IncludeTemperConditions/IncludeTemperIngredients
            // declaration above.
            bool includeWorkbench = !isTemper && IncludeCraftingWorkbench;
            bool includeConditions = isTemper ? IncludeTemperConditions : IncludeCraftingConditions;
            bool includeIngredients = isTemper ? IncludeTemperIngredients : IncludeCraftingIngredients;

            if (!includeWorkbench && !includeConditions && !includeIngredients)
            {
                StatusMessage = "Please select at least one field to apply (Workbench/Conditions/Ingredients).";
                return;
            }

            if (includeWorkbench && SelectedCraftingWorkbench == null)
            {
                StatusMessage = "Please select a Workbench first.";
                return;
            }

            var conditionsTemplate = isTemper ? TemperConditionsTemplate : CraftingConditionsTemplate;
            var ingredientsTemplate = isTemper ? TemperIngredientsTemplate : CraftingIngredientsTemplate;

            int applied = 0;

            foreach (var target in SelectedItems.ToList())
            {
                bool touched = false;

                // Multi-select items may never have been individually clicked, so their
                // Crafting/TemperRecipe could still be unloaded even though a real recipe already
                // exists for them on disk - hydrate first, exactly like ApplyPresetAsync, otherwise
                // the CreateCraftingRecipe()/CreateTemperRecipe() call below would think the item has
                // no recipe yet and create a second, disconnected one instead of reusing the real one.
                _main.EnsureItemHydrated(target);

                if (isTemper)
                {
                    if (target.TemperRecipe == null)
                        target.CreateTemperRecipe();
                    if (target.TemperRecipe == null)
                        continue;

                    if (includeConditions)
                    {
                        CloneConditionsInto(conditionsTemplate, target.TemperConditions, target);
                        await _main.PersistFieldAsync(target, nameof(ItemNodeVM.TemperConditions));
                        touched = true;
                    }

                    if (includeIngredients)
                    {
                        CloneIngredientsInto(ingredientsTemplate, target.TemperIngredients, target, isTemper: true);
                        await _main.PersistFieldAsync(target, nameof(ItemNodeVM.TemperIngredients));
                        touched = true;
                    }
                }
                else
                {
                    if (target.CraftingRecipe == null)
                        target.CreateCraftingRecipe();
                    if (target.CraftingRecipe == null)
                        continue;

                    if (includeWorkbench)
                    {
                        // Go through SelectedWorkbench (not the raw CraftingWorkbenchKey string
                        // directly) - its setter is what keeps CraftingWorkbenchKey in sync AND is
                        // what the single-item editor's Workbench ComboBox is actually bound to.
                        target.SelectedWorkbench = SelectedCraftingWorkbench;

                        // SelectedWorkbench's setter only fills CraftingWorkbenchSearchText (the
                        // editable ComboBox's displayed text) if it was still empty - fine for a
                        // normal UI click, where WPF's own ComboBox additionally syncs its Text box to
                        // the newly clicked entry regardless. There's no live ComboBox involved here
                        // though (the item usually isn't the single-selected node during a bulk
                        // apply), so without this the single-item editor kept showing the OLD
                        // workbench name in-session - correct again only after an app restart forced
                        // a fresh hydration. The DB/save itself was never affected by this, only the
                        // display of an already-hydrated item.
                        target.CraftingWorkbenchSearchText = SelectedCraftingWorkbench!.Name;

                        await _main.PersistFieldAsync(target, nameof(ItemNodeVM.CraftingWorkbenchKey));
                        touched = true;
                    }

                    if (includeConditions)
                    {
                        CloneConditionsInto(conditionsTemplate, target.CraftingConditions, target);
                        await _main.PersistFieldAsync(target, nameof(ItemNodeVM.CraftingConditions));
                        touched = true;
                    }

                    if (includeIngredients)
                    {
                        CloneIngredientsInto(ingredientsTemplate, target.CraftingIngredients, target, isTemper: false);
                        await _main.PersistFieldAsync(target, nameof(ItemNodeVM.CraftingIngredients));
                        touched = true;
                    }
                }

                if (touched)
                    applied++;
            }

            StatusMessage = $"{(isTemper ? "Temper" : "Crafting")} recipe applied to {applied} item(s).";
        }

        private static void CloneConditionsInto(
            ObservableCollection<BaseConditionViewModel> source,
            ObservableCollection<BaseConditionViewModel> target,
            ItemNodeVM targetItem)
        {
            target.Clear();
            foreach (var c in source)
            {
                var record = ConditionMapper.ToRecord(c, string.Empty);
                target.Add(ConditionMapper.ToViewModel(record, targetItem.AllAvailablePerks, targetItem.AllAvailableQuests));
            }
        }

        private void CloneIngredientsInto(
            ObservableCollection<IngredientEntryVM> source,
            ObservableCollection<IngredientEntryVM> target,
            ItemNodeVM targetItem,
            bool isTemper)
        {
            target.Clear();
            foreach (var ing in source)
            {
                var clone = new IngredientEntryVM(targetItem, isTemper);
                clone.InitializeMaterials(_main.AllAvailableMaterials);

                // Same lookup MainContentVM.InitializeRecipeIngredients uses when loading a recipe
                // from disk - setting Key/MaterialName alone (like a first draft of this method did)
                // leaves SelectedMaterial/SearchText empty, so the saved Key*Count is correct but the
                // material ComboBox shows blank/wrong the next time this item's editor is opened.
                var mat = _main.AllAvailableMaterials.FirstOrDefault(m => m.Key == ing.Key);
                if (mat != null)
                    clone.SetSelectedMaterialSilent(mat);
                else
                {
                    clone.Key = ing.Key;
                    clone.MaterialName = ing.MaterialName;
                }

                clone.Count = ing.Count;
                target.Add(clone);
            }
        }

        private async Task ApplyContainersAsync()
        {
            var templateRows = SelectedContainerTemplateRows.ToList();
            if (templateRows.Count == 0)
            {
                StatusMessage = "Please select at least one container first.";
                return;
            }

            int changedItems = 0;

            foreach (var item in SelectedItems.ToList())
            {
                bool changed = false;

                foreach (var templateRow in templateRows)
                {
                    if (item.ContainerSelection.SelectedContainers.Any(c => c.ContainerKey == templateRow.ContainerKey))
                        continue;

                    item.ContainerSelection.ToggleContainer(templateRow.ContainerKey);
                    changed = true;

                    var added = item.ContainerSelection.SelectedContainers
                        .FirstOrDefault(c => c.ContainerKey == templateRow.ContainerKey);

                    if (added != null && templateRow.LVLiEntries.Count > 0)
                        added.ApplyLevels(templateRow.LVLiEntries.ToDictionary(l => l.Key, l => l.Level));
                }

                if (!changed)
                    continue;

                item.ContainerString = item.ContainerSelection.BuildString();
                await _main.PersistFieldAsync(item, nameof(ItemNodeVM.ContainerString));
                changedItems++;
            }

            StatusMessage = changedItems == 0
                ? "No change (the containers were already assigned on all items)."
                : $"Container(s) added on {changedItems} item(s).";
        }

        // Bulk-applying a preset: PresetApplyService.Apply mutates the item completely normally via
        // its property setters (which incidentally also fires the shared _saveDebouncer), but
        // additionally reports back which fields were actually touched - those get explicitly
        // awaited and persisted here per item, for the same reason as ApplyRecipeAsync/
        // ApplyContainersAsync above (the shared debouncer would otherwise only actually save the
        // last-changed one when items change in quick succession).
        private async Task ApplyPresetAsync()
        {
            if (SelectedPreset == null)
            {
                StatusMessage = "Please select a preset first.";
                return;
            }

            int applied = 0;
            foreach (var item in SelectedItems.ToList())
            {
                // Multi-select items may never have been individually clicked, so their
                // AllKeywords/CraftingRecipe/TemperRecipe/ContainerSelection could still be empty -
                // hydrate first so PresetApplyService sees the item's real existing state.
                _main.EnsureItemHydrated(item);

                var touchedFields = PresetApplyService.Apply(item, SelectedPreset);
                foreach (var field in touchedFields)
                    await _main.PersistFieldAsync(item, field);

                if (touchedFields.Count > 0)
                    applied++;
            }

            StatusMessage = applied == 0
                ? $"Preset '{SelectedPreset.PresetName}' didn't match any of the selected items (no matching slots/types, or no fields enabled)."
                : $"Preset '{SelectedPreset.PresetName}' applied to {applied} item(s).";
        }
    }

    public class KeywordRowVM : ViewModelBase
    {
        private readonly MultiSelectDetailVM _owner;

        public string Key { get; }
        public string Name { get; }

        public ICommand ToggleCommand { get; }

        public string StatusText => _owner.ComputeKeywordStatus(Key);

        public KeywordRowVM(string key, string name, MultiSelectDetailVM owner)
        {
            Key = key;
            Name = name;
            _owner = owner;

            ToggleCommand = new RelayCommand(async () => await _owner.ToggleKeywordAsync(Key));
        }

        public void Refresh() => OnPropertyChanged(nameof(StatusText));
    }
}
