using SkyrimCraftingTool.Model;
using SkyrimCraftingTool.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;

namespace SkyrimCraftingTool.ViewModel
{
    public class ItemNodeVM : ViewModelBase
    {
        private readonly IKeywordService _keywordService;

        public MainContentVM Main { get; }

        // flag
        public bool IsLoading { get; set; }

        // "This item has persisted edits" - drives the tree-node badge and the edited-count / filter.
        // Set from the DB's IsEdited* columns at tree-build time (MainContentVM) and flipped true on
        // the first live field/recipe change; cleared by "Reset all changes for this item". Corrected
        // on the next full scan.
        private bool _isEdited;
        public bool IsEdited
        {
            get => _isEdited;
            set => SetProperty(ref _isEdited, value);
        }

        public ItemNodeVM(ArmorRecord armor, MainContentVM main) : this()
        {
            Main = main;
            _keywordService = main?.KeywordService ?? throw new InvalidOperationException("KeywordService not available");

            // load Container definitions
            ContainerSelection = new ContainerSelectionVM(main.AllContainers);

            AllAvailablePerks = main.AllAvailablePerks;
            AllAvailableKeywords = main.AllAvailableKeywords;
            AllAvailableWorkbenches = main.AllAvailableWorkbenches;
            AllAvailableQuests = main.AllAvailableQuests;

            ApplyArmorRecord(armor);
        }

        public ItemNodeVM(WeaponRecord weapon, MainContentVM main) : this()
        {
            Main = main;
            _keywordService = main?.KeywordService ?? throw new InvalidOperationException("KeywordService not available");

            ContainerSelection = new ContainerSelectionVM(main.AllContainers);

            AllAvailablePerks = main.AllAvailablePerks;
            AllAvailableKeywords = main.AllAvailableKeywords;
            AllAvailableWorkbenches = main.AllAvailableWorkbenches;
            AllAvailableQuests = main.AllAvailableQuests;

            ApplyWeaponRecord(weapon);
        }

        
        

        private COBJNodeVM? _craftingRecipe;
        private COBJNodeVM? _temperRecipe;

        private string _searchText = string.Empty;
        private bool _showAllKeywords;
        private bool _isArmor;

        private ObservableCollection<IngredientEntryVM> _craftingIngredients = new();
        private ObservableCollection<IngredientEntryVM> _temperIngredients = new();

        private CollectionViewSource? _keywordViewSource;
        private CollectionViewSource? _selectedKeywordViewSource;

        private CancellationTokenSource _searchDebounce;

        public string Key { get; set; }

        // Multi-selection in the TreeView (Ctrl/Shift click), independent of TreeViewItem.IsSelected -
        // see MainContentVM.HandleItemNodeClick.
        private bool _isSelected;
        public bool IsSelected
        {
            get => _isSelected;
            set => SetProperty(ref _isSelected, value);
        }

        // Whether LoadSelectedItemDetails has already run once for this item (AllKeywords/
        // Crafting-/TemperRecipe hydrated, autosave wiring set up). Prevents double hydration when a
        // click goes through both MainContentVM.SetItemSelected and the SelectedNode setter
        // (a single-select click goes through both paths).
        public bool HasLoadedDetails { get; set; }

        /// <summary>
        /// Local keywords collection for this item.
        /// Gets initialized when an item is loaded, and filtered for UI display.
        /// </summary>
        private readonly ObservableCollection<KeywordSelectionVM> _allKeywords = new();
        public ObservableCollection<KeywordSelectionVM> AllKeywords => _allKeywords;

        public ICollectionView FilteredKeywordsView
        {
            get
            {
                EnsureViewSources();
                return _keywordViewSource!.View;
            }
        }

        public ICollectionView SelectedKeywordsView
        {
            get
            {
                EnsureViewSources();
                return _selectedKeywordViewSource!.View;
            }
        }

        public event Action<ItemNodeVM, string>? FieldChanged;

        public void NotifyFieldChanged(string fieldName)
        {
            if (IsLoading)
                return;

            // Ingredient row edits (add/remove/Key/Count/SelectedMaterial - see IngredientEntryVM.
            // NotifyParent) and condition row edits (add/remove/Type/Target/Value/RunOn - see
            // OnConditionPropertyChanged/ReplaceConditionForTypeChange) all funnel through here with
            // the same field name, so this is the one place that needs to catch every path that can
            // change either list.
            if (fieldName == nameof(CraftingIngredients))
            {
                OnPropertyChanged(nameof(IsCraftingIngredientsChanged));
                OnPropertyChanged(nameof(HasCraftingChanges));
                OnPropertyChanged(nameof(CraftingRecipeMissingIngredients));
            }
            else if (fieldName == nameof(TemperIngredients))
            {
                OnPropertyChanged(nameof(IsTemperIngredientsChanged));
                OnPropertyChanged(nameof(HasTemperChanges));
                OnPropertyChanged(nameof(TemperRecipeMissingIngredients));
            }
            else if (fieldName == nameof(CraftingWorkbenchKey))
            {
                OnPropertyChanged(nameof(CraftingRecipeMissingWorkbench));
            }
            else if (fieldName == nameof(CraftingConditions))
            {
                OnPropertyChanged(nameof(IsCraftingConditionsChanged));
                OnPropertyChanged(nameof(HasCraftingChanges));
            }
            else if (fieldName == nameof(TemperConditions))
            {
                OnPropertyChanged(nameof(IsTemperConditionsChanged));
                OnPropertyChanged(nameof(HasTemperChanges));
            }

            // Any real edit (item field or recipe) funnels through here - mark the item dirty and
            // keep the "Reset all" button's enabled state fresh.
            if (!IsEdited)
            {
                IsEdited = true;
                Main?.NotifyItemBecameEdited();
            }
            OnPropertyChanged(nameof(HasAnyItemOrRecipeChanges));

            FieldChanged?.Invoke(this, fieldName);
        }

        // Crafting Workbench
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
            AllAvailableWorkbenches
                .Where(w =>
                    string.IsNullOrWhiteSpace(CraftingWorkbenchSearchText)
                    || w.Name.Contains(CraftingWorkbenchSearchText, StringComparison.OrdinalIgnoreCase));

        // --------------------
        // Container System
        // --------------------
        private string _containerString = "{}";
        public string ContainerString
        {
            get => _containerString;
            set
            {
                if (SetProperty(ref _containerString, value))
                {
                    if (!IsLoading)
                        NotifyFieldChanged(nameof(ContainerString));
                    OnPropertyChanged(nameof(IsContainerChanged));
                    OnPropertyChanged(nameof(HasAnyChanges));
                }
            }
        }

        public ContainerSelectionVM ContainerSelection { get; private set; }

        public ICommand ToggleContainerCommand => new RelayCommand<string>(key =>
        {
            ContainerSelection.ToggleContainer(key);
            ContainerString = ContainerSelection.BuildString();
            NotifyFieldChanged(nameof(ContainerString));
        });

        public ICommand ClearContainerSelectionCommand => new RelayCommand(() =>
        {
            ContainerSelection.Clear();
            ContainerString = ContainerSelection.BuildString();
            NotifyFieldChanged(nameof(ContainerString));
        });

        public void OnContainerSliderChanged()
        {
            ContainerString = ContainerSelection.BuildString();
            NotifyFieldChanged(nameof(ContainerString));
        }

        // --------------------
        // Presets (Output/Presets/*.json, see PresetFileStore/PresetApplyService)
        // --------------------
        public IEnumerable<PresetFile> ConfigPresets => Main?.AllPresets ?? Enumerable.Empty<PresetFile>();

        private PresetFile? _selectedConfigPreset;
        public PresetFile? SelectedConfigPreset
        {
            get => _selectedConfigPreset;
            set => SetProperty(ref _selectedConfigPreset, value);
        }

        // Applying a preset touches several fields on this one item in immediate succession
        // (Weight/Value/ArmorRating/...). The normal autosave path funnels every field through one
        // shared, cancel-on-next-call debouncer (see MainContentVM.OnItemFieldChanged) - fine for a
        // human typing into one field at a time, but back-to-back calls within the same method here
        // would cancel each other's pending save, silently persisting only the last field touched.
        // Route through the same explicit, awaited MainContentVM.PersistFieldAsync the multi-select
        // bulk-apply path already uses instead (see MultiSelectDetailVM.ApplyPresetAsync).
        public ICommand ApplyPresetCommand => new RelayCommand(async () =>
        {
            if (SelectedConfigPreset == null || Main == null) return;

            var touchedFields = PresetApplyService.Apply(this, SelectedConfigPreset);
            foreach (var field in touchedFields)
                await Main.PersistFieldAsync(this, field);
        });

        // Create recipe helpers used by save pipeline handlers. Whichever field the user touched
        // first is exactly what triggers this call (see Crafting/TemperSaveHandler's
        // "if (!item.HasCraftingRecipe) item.CreateCraftingRecipe();" guard) - CraftingRecipe's setter
        // below unconditionally re-derives CraftingWorkbenchKey/CraftingPerkKey/CraftingIngredients/
        // CraftingConditions from rec, so without seeding rec from whatever's already live on this
        // item first, it would wipe out the very edit that caused this method to run in the first
        // place (e.g. picking a Workbench, or adding the first Ingredient/Condition, would silently
        // revert to CreateNewCOBJRecordForItem's empty/default stub the moment the debounced autosave
        // fires).
        public void CreateCraftingRecipe()
        {
            var rec = Main?.ItemService.CreateNewCOBJRecordForItem(this, false);
            if (rec == null) return;

            if (!string.IsNullOrEmpty(CraftingWorkbenchKey))
                rec.WorkbenchKeywordKey = CraftingWorkbenchKey;
            if (!string.IsNullOrEmpty(CraftingPerkKey))
                rec.PerkKey = CraftingPerkKey;
            // Skip not-yet-filled rows (the "+" button adds an empty row the user picks a material
            // into next) - otherwise a bare "*1" gets persisted and comes back on reload as a ghost
            // ingredient with a blank material. Mirrors PresetRecipeVM.SyncIngredientsAndNotify.
            var craftIngredientKeys = CraftingIngredients
                .Where(i => !string.IsNullOrEmpty(i.Key))
                .GroupBy(i => i.Key)
                .Select(g => $"{g.Key}*{g.Sum(i => i.Count)}").ToList();
            if (craftIngredientKeys.Count > 0)
                rec.IngredientKeys = craftIngredientKeys;
            var craftConditions = CraftingConditions
                .Where(ConditionMapper.HasUsableTarget)
                .Select(vm => ConditionMapper.ToRecord(vm, rec.Key)).ToList();
            if (craftConditions.Count > 0)
                rec.Conditions = craftConditions;

            CraftingRecipe = new COBJNodeVM(this, rec, Main.FormIdService, false);
            // The new COBJNodeVM rebuilt its IngredientEntryVMs from rec.IngredientKeys without a
            // material catalog - wire it now, exactly like the load path does, so the material
            // ComboBox isn't empty until the next restart.
            Main.InitializeRecipeIngredients(CraftingRecipe!.Ingredients);
            MarkCraftingRecipeUserCreated(true);
            // Baseline the change tracking against the just-created state so per-field "changed"
            // markers work from here on (a fresh recipe otherwise had no snapshot -> Is*Changed
            // always false). _craftingRecipeIsUserCreated still marks the whole recipe as new.
            CaptureCraftingOriginalSnapshot(rec.WorkbenchKeywordKey, rec.IngredientKeys, rec.Conditions);
            Main.RegisterNewRecipe(rec);
        }

        public void CreateTemperRecipe()
        {
            var rec = Main?.ItemService.CreateNewCOBJRecordForItem(this, true);
            if (rec == null) return;

            if (!string.IsNullOrEmpty(TemperWorkbenchKey))
                rec.WorkbenchKeywordKey = TemperWorkbenchKey;
            if (!string.IsNullOrEmpty(TemperPerkKey))
                rec.PerkKey = TemperPerkKey;
            var temperIngredientKeys = TemperIngredients
                .Where(i => !string.IsNullOrEmpty(i.Key))
                .GroupBy(i => i.Key)
                .Select(g => $"{g.Key}*{g.Sum(i => i.Count)}").ToList();
            if (temperIngredientKeys.Count > 0)
                rec.IngredientKeys = temperIngredientKeys;
            var temperConditions = TemperConditions
                .Where(ConditionMapper.HasUsableTarget)
                .Select(vm => ConditionMapper.ToRecord(vm, rec.Key)).ToList();
            if (temperConditions.Count > 0)
                rec.Conditions = temperConditions;

            TemperRecipe = new COBJNodeVM(this, rec, Main.FormIdService, true);
            Main.InitializeRecipeIngredients(TemperRecipe!.Ingredients);
            MarkTemperRecipeUserCreated(true);
            // See CreateCraftingRecipe: baseline against the just-created state.
            CaptureTemperOriginalSnapshot(rec.WorkbenchKeywordKey, rec.IngredientKeys, rec.Conditions);
            Main.RegisterNewRecipe(rec);
        }

        // Export/Import for just this item: its own Armor/Weapon field edits plus its Crafting/Temper
        // recipe (if present), bundled into one file under Output/Exports/<Plugin>/<Item>.json — see
        // ExportFileStore for the path convention. No file dialogs; Import reads back the exact file
        // Export wrote for this same item.
        public ICommand ExportItemCommand => new RelayCommand(async () =>
        {
            if (Main?.ImportExportService == null) return;

            // Flush first: this is the likeliest place to hit the 350ms autosave window — the user
            // edits a field on this very item and immediately hits Export on it.
            await Main.FlushPendingSavesAsync();

            var keys = new List<string> { Key };
            if (CraftingRecipe != null) keys.Add(CraftingRecipe.Key);
            if (TemperRecipe != null) keys.Add(TemperRecipe.Key);

            var items = new List<EditedItemDto>();
            try
            {
                foreach (var key in keys)
                    items.AddRange(Main.ImportExportService.GetEditedItems(ExportScope.Item, key));
            }
            catch (Exception ex)
            {
                AppLogger.LogError("ItemNodeVM.ExportItemCommand failed", ex);
                System.Windows.MessageBox.Show($"Export failed:{Environment.NewLine}{ex.Message}",
                    "Error", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
                return;
            }

            if (items.Count == 0)
            {
                System.Windows.MessageBox.Show("This item has no edited data to export.",
                    "Export", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
                return;
            }

            var path = ExportFileStore.GetItemFilePath(Key, Name);
            ExportFileStore.WriteFile(path, new ExportFile { ExportedAt = ItemDBHandler.NowIso(), Items = items });

            System.Windows.MessageBox.Show($"Exported to{Environment.NewLine}{path}",
                "Export Successful", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
        });

        public ICommand ImportItemCommand => new RelayCommand(async () =>
        {
            if (Main == null) return;

            var path = ExportFileStore.GetItemFilePath(Key, Name);
            if (!System.IO.File.Exists(path))
            {
                System.Windows.MessageBox.Show($"No export file found for this item:{Environment.NewLine}{path}",
                    "Import", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
                return;
            }

            ExportFile file;
            try
            {
                file = ExportFileStore.ReadFile(path);
            }
            catch (Exception ex)
            {
                AppLogger.LogError("ItemNodeVM.ImportItemCommand (read file) failed", ex);
                System.Windows.MessageBox.Show($"File could not be read:{Environment.NewLine}{ex.Message}",
                    "Error", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
                return;
            }

            await Main.RunImportAsync(file?.Items ?? new List<EditedItemDto>());
        });

        private string _name;
        public string Name
        {
            get => _name;
            set
            {
                if (SetProperty(ref _name, value))
                {
                    NotifyFieldChanged(nameof(Name));
                    OnPropertyChanged(nameof(IsNameChanged));
                    OnPropertyChanged(nameof(HasAnyChanges));
                }
            }
        }

        private string _editorID;
        public string EditorID
        {
            get => _editorID;
            set
            {
                if (SetProperty(ref _editorID, value))
                    NotifyFieldChanged(nameof(EditorID));
            }
        }

        private float _weight;
        
        public float Weight
        {
            get => _weight;
            set
            {
                if (SetProperty(ref _weight, value))
                {
                    NotifyFieldChanged(nameof(Weight));
                    OnPropertyChanged(nameof(IsWeightChanged));
                    OnPropertyChanged(nameof(HasAnyChanges));
                }
            }
        }

        private int _value;
        public int Value
        {
            get => _value;
            set
            {
                if (SetProperty(ref _value, value))
                {
                    NotifyFieldChanged(nameof(Value));
                    OnPropertyChanged(nameof(IsValueChanged));
                    OnPropertyChanged(nameof(HasAnyChanges));
                }
            }
        }

        public string SearchText
        {
            get => _searchText;
            set
            {
                if (SetProperty(ref _searchText, value))
                    DebouncedRefresh();
            }
        }

        public bool ShowAllKeywords
        {
            get => _showAllKeywords;
            set
            {
                if (SetProperty(ref _showAllKeywords, value))
                {
                    OnPropertyChanged(nameof(KeywordColumns));
                    OnPropertyChanged(nameof(KeywordFontSize));
                    RefreshKeywords();
                }
            }
        }

        private readonly ObservableCollection<KeywordSelectionVM> _selectedKeywords = new();
        /// <summary>
        /// Read-only observable collection of the currently selected keywords.
        /// Gets automatically synchronized whenever a keyword is selected/deselected.
        /// </summary>
        public IEnumerable<KeywordSelectionVM> SelectedKeywords => _selectedKeywords;

        public List<FormIDRecord> AllAvailablePerks { get; private set; }
        public List<FormIDRecord> AllAvailableKeywords { get; private set; }
        public List<FormIDRecord> AllAvailableWorkbenches { get; private set; }
        public List<FormIDRecord> AllAvailableQuests { get; private set; } = new();

        public IEnumerable<FormIDRecord> FilteredQuests => AllAvailableQuests;

        public List<string> SelectedKeywordKeys { get; set; } = new();

        // --------------------
        // Crafting Recipe
        // --------------------
        public COBJNodeVM? CraftingRecipe
        {
            get => _craftingRecipe;
            set
            {
                if (SetProperty(ref _craftingRecipe, value))
                {
                    CraftingIngredients = value?.Ingredients ?? new ObservableCollection<IngredientEntryVM>();

                    _craftingWorkbenchKey = value?.WorkbenchKeywordKey ?? "";
                    _craftingPerkKey = value?.PerkKey ?? "";

                    CraftingConditions = value?.Conditions ?? new ObservableCollection<BaseConditionViewModel>();

                    OnPropertyChanged(nameof(CraftingWorkbenchKey));
                    OnPropertyChanged(nameof(IsCraftingWorkbenchDeadRef));
                    SelectedWorkbench =
                        AllAvailableWorkbenches.FirstOrDefault(x => x.Key == CraftingWorkbenchKey);

                    // A dead workbench ref resolves to no SelectedWorkbench - show the raw key in the
                    // (red-bordered) box instead of leaving it blank.
                    if (SelectedWorkbench == null && !string.IsNullOrEmpty(CraftingWorkbenchKey))
                        _craftingWorkbenchSearchText = CraftingWorkbenchKey;

                    OnPropertyChanged(nameof(CraftingWorkbenchSearchText));
                    OnPropertyChanged(nameof(CraftingPerkKey));

                    SelectedCraftingPerk =
                        AllAvailablePerks.FirstOrDefault(x => x.Key == CraftingPerkKey);

                    RaiseRecipeWarningFlags();

                    if (!IsLoading)
                    {
                        NotifyFieldChanged(nameof(CraftingRecipe));
                        NotifyFieldChanged(nameof(CraftingIngredients));
                        NotifyFieldChanged(nameof(CraftingWorkbenchKey));
                        NotifyFieldChanged(nameof(CraftingPerkKey));
                    }
                }
            }
        }


        public bool HasCraftingRecipe => CraftingRecipe != null;

        public ObservableCollection<IngredientEntryVM> CraftingIngredients
        {
            get => _craftingIngredients;
            set
            {
                if (SetProperty(ref _craftingIngredients, value))
                {
                    if (!IsLoading)
                        NotifyFieldChanged(nameof(CraftingIngredients));
                }
                else
                {
                    OnPropertyChanged(nameof(CraftingIngredients));
                }
            }
        }

        public string CraftingEditorID =>
            CraftingRecipe?.Key ?? "(no crafting recipe)";

        private ObservableCollection<BaseConditionViewModel> _craftingConditions
            = new ObservableCollection<BaseConditionViewModel>();

        public ObservableCollection<BaseConditionViewModel> CraftingConditions
        {
            get => _craftingConditions;
            set
            {
                UnsubscribeConditionEvents(_craftingConditions);

                bool changed = SetProperty(ref _craftingConditions, value ?? new ObservableCollection<BaseConditionViewModel>());

                // Always resubscribe, even if the reference didn't change (e.g. when
                // the same collection instance is reassigned from elsewhere). Without
                // this, Unsubscribe above would permanently detach the per-condition
                // PropertyChanged handlers with nothing ever restoring them, silently
                // breaking Type-change detection and autosave for existing conditions.
                SubscribeConditionEvents(_craftingConditions);

                if (changed && !IsLoading)
                {
                    NotifyFieldChanged(nameof(CraftingConditions));
                }
            }
        }

        // --------------------
        // Condition change tracking: keeps autosave firing when a single
        // condition's properties change, and swaps the concrete VM type
        // when the user changes the "Type" combo box (WPF DataTemplates
        // are selected by concrete CLR type, not by the Type enum value).
        // --------------------
        private void SubscribeConditionEvents(ObservableCollection<BaseConditionViewModel> collection)
        {
            if (collection == null) return;

            foreach (var condition in collection)
                condition.PropertyChanged += OnConditionPropertyChanged;

            collection.CollectionChanged += OnConditionsCollectionChanged;
        }

        private void UnsubscribeConditionEvents(ObservableCollection<BaseConditionViewModel> collection)
        {
            if (collection == null) return;

            foreach (var condition in collection)
                condition.PropertyChanged -= OnConditionPropertyChanged;

            collection.CollectionChanged -= OnConditionsCollectionChanged;
        }

        private void OnConditionsCollectionChanged(object sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            if (e.OldItems != null)
                foreach (BaseConditionViewModel condition in e.OldItems)
                    condition.PropertyChanged -= OnConditionPropertyChanged;

            if (e.NewItems != null)
                foreach (BaseConditionViewModel condition in e.NewItems)
                    condition.PropertyChanged += OnConditionPropertyChanged;

            // While a Type-driven swap is in progress, ReplaceConditionForTypeChange
            // itself is responsible for the single NotifyFieldChanged call once the
            // swap completes. Avoid firing a second (premature) notification here.
            if (_isReplacingConditionType) return;

            if (!IsLoading)
                NotifyFieldChanged(GetConditionsFieldName(sender));
        }

        private bool _isReplacingConditionType;

        // PerkSearchText/SelectedPerk/SelectedQuest are UI-only mirrors (the editable Perk combo's
        // search text, and the ComboBox's own SelectedItem echo) - WPF writes into them as a side
        // effect of merely realizing the ComboBox for the first time (IsEditable plus a separate Text
        // binding, in PerkConditionViewModel's DataTemplate), with IsLoading already back to false by
        // the time that deferred container generation runs. None of them are read by
        // ConditionMapper.ToRecord, so treating them as edits would wrongly mark Conditions as
        // user-edited - and, worse, silently undo a just-completed Reset - from nothing more than
        // scrolling a recipe's Conditions into view. Any real perk/quest selection still reaches here
        // via the PerkFormKey/QuestFormKey change those setters also raise.
        private static bool IsConditionUiOnlyProperty(string propertyName) => propertyName is
            nameof(PerkConditionViewModel.PerkSearchText)
            or nameof(PerkConditionViewModel.SelectedPerk)
            or nameof(QuestStageConditionViewModel.SelectedQuest);

        private void OnConditionPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (_isReplacingConditionType) return;
            if (sender is not BaseConditionViewModel condition) return;
            if (IsConditionUiOnlyProperty(e.PropertyName)) return;

            var collection = FindOwningConditionsCollection(condition);
            if (collection == null) return;

            if (e.PropertyName == nameof(BaseConditionViewModel.Type))
            {
                ReplaceConditionForTypeChange(collection, condition);
                return;
            }

            if (!IsLoading)
                NotifyFieldChanged(GetConditionsFieldName(collection));
        }

        private ObservableCollection<BaseConditionViewModel>? FindOwningConditionsCollection(BaseConditionViewModel condition)
        {
            if (_craftingConditions.Contains(condition))
                return _craftingConditions;
            if (_temperConditions.Contains(condition))
                return _temperConditions;
            return null;
        }

        private string GetConditionsFieldName(object collection) =>
            ReferenceEquals(collection, _temperConditions)
                ? nameof(TemperConditions)
                : nameof(CraftingConditions);

        private void ReplaceConditionForTypeChange(ObservableCollection<BaseConditionViewModel> collection, BaseConditionViewModel oldCondition)
        {
            var index = collection.IndexOf(oldCondition);
            if (index < 0) return;

            var newCondition = CreateConditionViewModel(oldCondition.Type);

            // Carry over whatever makes sense across condition types.
            newCondition.RunOnPlayer = oldCondition.RunOnPlayer;
            try { newCondition.ComparisonValue = oldCondition.ComparisonValue; } catch { /* not all values are meaningful across types */ }

            _isReplacingConditionType = true;
            try
            {
                oldCondition.PropertyChanged -= OnConditionPropertyChanged;
                newCondition.PropertyChanged += OnConditionPropertyChanged;

                // NOTE: using the ObservableCollection indexer (a single "Replace"
                // notification) is known to leave WPF's ItemsControl showing the
                // OLD item's DataTemplate in some cases, because the generated
                // container is reused instead of being re-selected for the new
                // concrete CLR type. Doing an explicit Remove + Insert forces two
                // separate notifications (Remove, then Add), which reliably makes
                // ItemsControl regenerate the container - and therefore re-run
                // implicit DataTemplate selection for the new type - every time.
                collection.RemoveAt(index);
                collection.Insert(index, newCondition);
            }
            finally
            {
                _isReplacingConditionType = false;
            }

            if (!IsLoading)
                NotifyFieldChanged(GetConditionsFieldName(collection));
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


        // --------------------
        // Workbench + Perk
        // --------------------
        private string _craftingWorkbenchKey = "";
        public string CraftingWorkbenchKey
        {
            get => _craftingWorkbenchKey;
            set
            {
                if (SetProperty(ref _craftingWorkbenchKey, value))
                {
                    if (CraftingRecipe != null)
                        CraftingRecipe.WorkbenchKeywordKey = value;

                    if (!IsLoading)
                        NotifyFieldChanged(nameof(CraftingWorkbenchKey));

                    OnPropertyChanged(nameof(IsCraftingWorkbenchChanged));
                    OnPropertyChanged(nameof(IsCraftingWorkbenchDeadRef));
                    OnPropertyChanged(nameof(CraftingRecipeMissingWorkbench));
                    OnPropertyChanged(nameof(HasCraftingChanges));
                }
            }
        }

        // True when a workbench key is set but doesn't resolve against the current scan (the mod it
        // came from is disabled/removed). See IReferenceResolver; the two vanilla Temper workbenches
        // are always treated as resolvable.
        public bool IsCraftingWorkbenchDeadRef =>
            !string.IsNullOrEmpty(CraftingWorkbenchKey)
            && Main?.References is { } r && !r.IsActive(CraftingWorkbenchKey);

        public bool IsTemperWorkbenchDeadRef =>
            !string.IsNullOrEmpty(TemperWorkbenchKey)
            && Main?.References is { } r && !r.IsActive(TemperWorkbenchKey);

        public FormIDRecord _selectedWorkbench;
        public FormIDRecord? SelectedWorkbench
        {
            get => _selectedWorkbench;
            set
            {
                if (SetProperty(ref _selectedWorkbench, value))
                {
                    CraftingWorkbenchKey = value?.Key;
                    if (value != null && string.IsNullOrEmpty(CraftingWorkbenchSearchText))
                        CraftingWorkbenchSearchText = value.Name;
                }
            }
        }

        private FormIDRecord? _selectedCraftingPerk;
        public FormIDRecord? SelectedCraftingPerk
        {
            get => _selectedCraftingPerk;
            set
            {
                if (SetProperty(ref _selectedCraftingPerk, value))
                {
                    if (value != null)
                    {
                        CraftingPerkKey = value.Key;

                        if (string.IsNullOrEmpty(CraftingPerkSearchText))
                            CraftingPerkSearchText = value.Name;
                    }
                }
            }
        }

        private string _craftingPerkSearchText = string.Empty;
        public string CraftingPerkSearchText
        {
            get => _craftingPerkSearchText;
            set
            {
                if (SetProperty(ref _craftingPerkSearchText, value))
                    OnPropertyChanged(nameof(FilteredCraftingPerks));
            }
        }

        public IEnumerable<FormIDRecord> FilteredCraftingPerks =>
            AllAvailablePerks.Where(p =>
                string.IsNullOrWhiteSpace(CraftingPerkSearchText)
                || p.Name.Contains(CraftingPerkSearchText, StringComparison.OrdinalIgnoreCase));

        private string _craftingPerkKey = "";
        public string CraftingPerkKey
        {
            get => _craftingPerkKey;
            set
            {
                if (SetProperty(ref _craftingPerkKey, value))
                {
                    if (CraftingRecipe != null)
                        CraftingRecipe.PerkKey = value;

                    if (!IsLoading)
                        NotifyFieldChanged(nameof(CraftingPerkKey));
                }
            }
        }

        // --------------------
        // Temper Recipe
        // --------------------
        public COBJNodeVM? TemperRecipe
        {
            get => _temperRecipe;
            set
            {
                if (SetProperty(ref _temperRecipe, value))
                {
                    TemperIngredients = value?.Ingredients ?? new ObservableCollection<IngredientEntryVM>();

                    // Workbench + Perk
                    _temperWorkbenchKey = value?.WorkbenchKeywordKey ?? "";
                    _temperPerkKey = value?.PerkKey ?? "";

                    TemperConditions = value?.Conditions ?? new ObservableCollection<BaseConditionViewModel>();

                    OnPropertyChanged(nameof(TemperWorkbenchKey));
                    OnPropertyChanged(nameof(IsTemperWorkbenchDeadRef));
                    OnPropertyChanged(nameof(TemperPerkKey));
                    SelectedTemperPerk =
                            AllAvailablePerks.FirstOrDefault(x => x.Key == TemperPerkKey);


                    OnPropertyChanged(nameof(HasTemperRecipe));
                    OnPropertyChanged(nameof(TemperEditorID));
                    RaiseRecipeWarningFlags();

                    if (!IsLoading)
                    {
                        NotifyFieldChanged(nameof(TemperRecipe));
                        NotifyFieldChanged(nameof(TemperIngredients));
                        NotifyFieldChanged(nameof(TemperWorkbenchKey));
                        NotifyFieldChanged(nameof(TemperPerkKey));
                    }
                }
            }
        }

        public bool HasTemperRecipe => TemperRecipe != null;

        public ObservableCollection<IngredientEntryVM> TemperIngredients
        {
            get => _temperIngredients;
            set
            {
                if (SetProperty(ref _temperIngredients, value))
                {
                    if (!IsLoading)
                        NotifyFieldChanged(nameof(TemperIngredients));
                }
            }
        }

        public string TemperEditorID =>
            TemperRecipe?.Key ?? "(no temper recipe)";

        private ObservableCollection<BaseConditionViewModel> _temperConditions
            = new ObservableCollection<BaseConditionViewModel>();

        public ObservableCollection<BaseConditionViewModel> TemperConditions
        {
            get => _temperConditions;
            set
            {
                UnsubscribeConditionEvents(_temperConditions);

                bool changed = SetProperty(ref _temperConditions, value ?? new ObservableCollection<BaseConditionViewModel>());

                // See CraftingConditions setter for why this must run unconditionally.
                SubscribeConditionEvents(_temperConditions);

                if (changed && !IsLoading)
                {
                    NotifyFieldChanged(nameof(TemperConditions));
                }
            }
        }


        // --------------------
        // Workbench + Perk
        // --------------------
        private string _temperWorkbenchKey = "";
        public string TemperWorkbenchKey
        {
            get => _temperWorkbenchKey;
            set
            {
                if (SetProperty(ref _temperWorkbenchKey, value))
                {
                    if (TemperRecipe != null)
                        TemperRecipe.WorkbenchKeywordKey = value;

                    if (!IsLoading)
                        NotifyFieldChanged(nameof(TemperWorkbenchKey));

                    OnPropertyChanged(nameof(IsTemperWorkbenchChanged));
                    OnPropertyChanged(nameof(IsTemperWorkbenchDeadRef));
                    OnPropertyChanged(nameof(HasTemperChanges));
                }
            }
        }

        private string _temperPerkKey = "";
        public string TemperPerkKey
        {
            get => _temperPerkKey;
            set
            {
                if (SetProperty(ref _temperPerkKey, value))
                {
                    if (TemperRecipe != null)
                        TemperRecipe.PerkKey = value;

                    if (!IsLoading)
                        NotifyFieldChanged(nameof(TemperPerkKey));
                }
            }
        }

        private FormIDRecord? _selectedTemperPerk;
        public FormIDRecord? SelectedTemperPerk
        {
            get => _selectedTemperPerk;
            set
            {
                if (SetProperty(ref _selectedTemperPerk, value))
                {
                    if (value != null)
                    {
                        TemperPerkKey = value.Key;

                        if (string.IsNullOrEmpty(TemperPerkSearchText))
                            TemperPerkSearchText = value.Name;
                    }
                }
            }
        }

        private string _temperPerkSearchText = string.Empty;
        public string TemperPerkSearchText
        {
            get => _temperPerkSearchText;
            set
            {
                if (SetProperty(ref _temperPerkSearchText, value))
                    OnPropertyChanged(nameof(FilteredTemperPerks));
            }
        }

        public IEnumerable<FormIDRecord> FilteredTemperPerks =>
            AllAvailablePerks.Where(p =>
                string.IsNullOrWhiteSpace(TemperPerkSearchText)
                || p.Name.Contains(TemperPerkSearchText, StringComparison.OrdinalIgnoreCase));

        // --------------------
        // Commands
        // --------------------
        public ICommand AddIngredientCommand { get; }
        public ICommand RemoveIngredientCommand { get; }

        public ICommand AddTemperIngredientCommand { get; }
        public ICommand RemoveTemperIngredientCommand { get; }

        public ICommand AddCraftingConditionCommand { get; }
        public ICommand RemoveCraftingConditionCommand { get; }
        public ICommand RemoveConditionCommand => RemoveCraftingConditionCommand;

        public ICommand AddTemperConditionCommand { get; }
        public ICommand RemoveTemperConditionCommand { get; }


        public int KeywordColumns => ShowAllKeywords ? 5 : 4;
        public double KeywordFontSize => ShowAllKeywords ? 11 : 12;

        // --------------------
        // Armor / Weapon Flags
        // --------------------
        public bool IsArmor
        {
            get => _isArmor;
            private set
            {
                if (SetProperty(ref _isArmor, value))
                    OnPropertyChanged(nameof(IsWeapon));
            }
        }

        public bool IsWeapon => !IsArmor;

        // --------------------
        // Armor-specific fields
        // --------------------
        private float _armorRating;
        public float ArmorRating
        {
            get => _armorRating;
            set
            {
                if (SetProperty(ref _armorRating, value))
                {
                    NotifyFieldChanged(nameof(ArmorRating));
                    OnPropertyChanged(nameof(IsArmorRatingChanged));
                    OnPropertyChanged(nameof(HasAnyChanges));
                }
            }
        }

        private uint _bodySlotMask;
        private bool _isSyncingSlots;

        public uint BodySlotMask
        {
            get => _bodySlotMask;
            set
            {
                if (SetProperty(ref _bodySlotMask, value))
                {
                    SyncDataToGui();
                    NotifyFieldChanged(nameof(BodySlotMask));
                    OnPropertyChanged(nameof(IsBodySlotMaskChanged));
                    OnPropertyChanged(nameof(HasAnyChanges));
                }
            }
        }

        private SlotVM _selectedSlot;
        public SlotVM SelectedSlot
        {
            get => _selectedSlot;
            set
            {
                if (SetProperty(ref _selectedSlot, value))
                {
                    if (value != null)
                        BodySlotMask = value.Flag; // explizite User-Aktion
                }
            }
        }

        public ObservableCollection<SlotVM> SlotOptions { get; } = new();

        // --------------------
        // Weapon-specific fields
        // --------------------
        private int _damage;
        public int Damage
        {
            get => _damage;
            set
            {
                if (SetProperty(ref _damage, value))
                {
                    NotifyFieldChanged(nameof(Damage));
                    OnPropertyChanged(nameof(IsDamageChanged));
                    OnPropertyChanged(nameof(HasAnyChanges));
                }
            }
        }

        private float _speed;
        public float Speed
        {
            get => _speed;
            set
            {
                if (SetProperty(ref _speed, value))
                {
                    NotifyFieldChanged(nameof(Speed));
                    OnPropertyChanged(nameof(IsSpeedChanged));
                    OnPropertyChanged(nameof(HasAnyChanges));
                }
            }
        }

        private float _reach;
        public float Reach
        {
            get => _reach;
            set
            {
                if (SetProperty(ref _reach, value))
                {
                    NotifyFieldChanged(nameof(Reach));
                    OnPropertyChanged(nameof(IsReachChanged));
                    OnPropertyChanged(nameof(HasAnyChanges));
                }
            }
        }

        private float _stagger;
        public float Stagger
        {
            get => _stagger;
            set
            {
                if (SetProperty(ref _stagger, value))
                {
                    NotifyFieldChanged(nameof(Stagger));
                    OnPropertyChanged(nameof(IsStaggerChanged));
                    OnPropertyChanged(nameof(HasAnyChanges));
                }
            }
        }

        // --------------------
        // Change tracking / Reset (Item-Detail-Felder only)
        // --------------------
        // Snapshot of the pristine, never-edited base values for this item, read straight from the
        // DB's base columns (not the IsEdited* shadow columns) by MainContentVM.LoadSelectedItemDetails
        // via IItemService.GetOriginalArmor/GetOriginalWeapon. Comparing against this (rather than
        // whatever ArmorCache/WeaponCache happened to hold when this session loaded the item) is what
        // makes the changed-field highlighting and the Reset button survive an app restart.
        private bool _hasOriginalSnapshot;
        private string _originalName;
        private int _originalValue;
        private float _originalWeight;
        private float _originalArmorRating;
        private uint _originalBodySlotMask;
        private int _originalDamage;
        private float _originalSpeed;
        private float _originalReach;
        private float _originalStagger;
        private string _originalContainerString;
        private List<string> _originalSelectedKeywordKeys = new();

        public void CaptureOriginalSnapshot(string name, int value, float weight, float armorRating,
            uint bodySlotMask, int damage, float speed, float reach, float stagger, string containerString, List<string> keywordKeys)
        {
            _originalName = name;
            _originalValue = value;
            _originalWeight = weight;
            _originalArmorRating = armorRating;
            _originalBodySlotMask = bodySlotMask;
            _originalDamage = damage;
            _originalSpeed = speed;
            _originalReach = reach;
            _originalStagger = stagger;
            _originalContainerString = containerString;
            _originalSelectedKeywordKeys = new List<string>(keywordKeys ?? new List<string>());
            _hasOriginalSnapshot = true;
            RaiseChangeFlags();
        }

        private void RaiseChangeFlags()
        {
            OnPropertyChanged(nameof(IsNameChanged));
            OnPropertyChanged(nameof(IsValueChanged));
            OnPropertyChanged(nameof(IsWeightChanged));
            OnPropertyChanged(nameof(IsArmorRatingChanged));
            OnPropertyChanged(nameof(IsBodySlotMaskChanged));
            OnPropertyChanged(nameof(IsDamageChanged));
            OnPropertyChanged(nameof(IsSpeedChanged));
            OnPropertyChanged(nameof(IsReachChanged));
            OnPropertyChanged(nameof(IsStaggerChanged));
            OnPropertyChanged(nameof(IsContainerChanged));
            OnPropertyChanged(nameof(IsKeywordsChanged));
            OnPropertyChanged(nameof(HasAnyChanges));
        }

        public bool IsNameChanged => _hasOriginalSnapshot && Name != _originalName;
        public bool IsValueChanged => _hasOriginalSnapshot && Value != _originalValue;
        public bool IsWeightChanged => _hasOriginalSnapshot && Math.Abs(Weight - _originalWeight) > 0.0001f;
        public bool IsArmorRatingChanged => _hasOriginalSnapshot && Math.Abs(ArmorRating - _originalArmorRating) > 0.0001f;
        public bool IsBodySlotMaskChanged => _hasOriginalSnapshot && BodySlotMask != _originalBodySlotMask;
        public bool IsDamageChanged => _hasOriginalSnapshot && Damage != _originalDamage;
        public bool IsSpeedChanged => _hasOriginalSnapshot && Math.Abs(Speed - _originalSpeed) > 0.0001f;
        public bool IsReachChanged => _hasOriginalSnapshot && Math.Abs(Reach - _originalReach) > 0.0001f;
        public bool IsStaggerChanged => _hasOriginalSnapshot && Math.Abs(Stagger - _originalStagger) > 0.0001f;
        public bool IsContainerChanged => _hasOriginalSnapshot && ContainerString != _originalContainerString;
        public bool IsKeywordsChanged => _hasOriginalSnapshot &&
            !new HashSet<string>(SelectedKeywordKeys ?? new List<string>())
                .SetEquals(_originalSelectedKeywordKeys ?? new List<string>());

        public bool HasAnyChanges =>
            IsNameChanged || IsValueChanged || IsWeightChanged || IsContainerChanged || IsKeywordsChanged ||
            (IsArmor ? (IsArmorRatingChanged || IsBodySlotMaskChanged) : (IsDamageChanged || IsSpeedChanged || IsReachChanged || IsStaggerChanged));

        // Delegates to MainContentVM.ResetItemEdits, which clears this item's IsEdited* shadow columns
        // in the DB and updates ArmorCache/WeaponCache, then calls back into ApplyResetValues below —
        // just setting properties here directly would re-trigger NotifyFieldChanged on each one and
        // immediately re-save them as "edited" (with the shadow value merely matching the original).
        public ICommand ResetChangesCommand => new RelayCommand(() =>
        {
            if (!_hasOriginalSnapshot) return;
            Main?.ResetItemEdits(this);
            RefreshEditedState();
        });

        // After a reset (any section, or all), the item is still "edited" only if some OTHER section
        // still differs from the scanned baseline. Keeps the tree badge / edited-count / "only
        // edited" filter in sync instead of leaving a stale mark until the next scan.
        private void RefreshEditedState()
        {
            bool nowEdited = HasAnyItemOrRecipeChanges;
            if (IsEdited == nowEdited) return;

            IsEdited = nowEdited;
            Main?.NotifyItemEditedStateChanged(nowEdited);
            OnPropertyChanged(nameof(HasAnyItemOrRecipeChanges));
        }

        // Called by MainContentVM.ResetItemEdits after it has cleared the DB-side edit flags for this
        // item. IsLoading suppresses NotifyFieldChanged (see each property setter above and
        // OnKeywordPropertyChanged) so applying the reverted values doesn't immediately re-save them
        // as a fresh edit.
        public void ApplyResetValues(string name, int value, float weight, float armorRating,
            uint bodySlotMask, int damage, float speed, float reach, float stagger, string containerString, List<string> keywordKeys)
        {
            IsLoading = true;

            Name = name;
            Value = value;
            Weight = weight;

            if (IsArmor)
            {
                ArmorRating = armorRating;
                BodySlotMask = bodySlotMask; // setter calls SyncDataToGui(), keeping SlotOptions in sync
            }
            else
            {
                Damage = damage;
                Speed = speed;
                Reach = reach;
                Stagger = stagger;
            }

            ContainerString = containerString;
            ContainerSelection.LoadFromString(containerString);

            var keywordSet = new HashSet<string>(keywordKeys ?? new List<string>());
            foreach (var kw in AllKeywords)
                kw.IsSelected = keywordSet.Contains(kw.Key);

            IsLoading = false;

            CaptureOriginalSnapshot(name, value, weight, armorRating, bodySlotMask, damage, speed, reach, stagger, containerString, keywordKeys);
        }

        // --------------------
        // Change tracking / Reset: Crafting + Temper recipe (Workbench + Ingredients + Conditions -
        // Perk/Name/CreatedItem aren't bound to any editable control anywhere in the UI today).
        // Conditions revert relies on COBJ_Conditions_Original (see Model/ItemDBHandler.cs), a lazy
        // snapshot table added specifically because COBJ_Conditions itself is destructively
        // DELETE+INSERTed on every save and has no shadow-column protection of its own.
        // --------------------
        private bool _hasCraftingSnapshot;
        private string _originalCraftingWorkbenchKey;
        private List<string> _originalCraftingIngredientKeys = new();
        private List<string> _originalCraftingConditionKeys = new();

        private bool _hasTemperSnapshot;
        private string _originalTemperWorkbenchKey;
        private List<string> _originalTemperIngredientKeys = new();
        private List<string> _originalTemperConditionKeys = new();

        // A user-created recipe (no plugin origin - Original stays 0 forever for these in the DB,
        // see ItemDBHandler.InsertCOBJ) must be Reset-able the moment it's created, before it has ever
        // gone through Capture*OriginalSnapshot - which only happens on the next load/reload of this
        // item. Without this, HasCraftingChanges/HasTemperChanges stays false (no snapshot yet to
        // diff against) and Reset stays disabled for the rest of the session, even though the freshly
        // created COBJ row already exists in the DB and needs a way to be undone. Tracked separately
        // from _hasCraftingSnapshot/_hasTemperSnapshot rather than folded into it, since
        // COBJRecord.Original itself gets deliberately flipped to 1 in memory right after the first
        // save purely as insert-vs-update bookkeeping (see InsertCOBJ's comment) and is therefore not
        // a reliable "is this new" signal on its own.
        private bool _craftingRecipeIsUserCreated;
        private bool _temperRecipeIsUserCreated;

        public void MarkCraftingRecipeUserCreated(bool isUserCreated)
        {
            if (_craftingRecipeIsUserCreated == isUserCreated) return;
            _craftingRecipeIsUserCreated = isUserCreated;
            OnPropertyChanged(nameof(HasCraftingChanges));
        }

        public void MarkTemperRecipeUserCreated(bool isUserCreated)
        {
            if (_temperRecipeIsUserCreated == isUserCreated) return;
            _temperRecipeIsUserCreated = isUserCreated;
            OnPropertyChanged(nameof(HasTemperChanges));
        }

        public void CaptureCraftingOriginalSnapshot(string workbenchKey, List<string> ingredientKeys, List<COBJConditionRecord> conditions)
        {
            _originalCraftingWorkbenchKey = workbenchKey;
            _originalCraftingIngredientKeys = new List<string>(ingredientKeys ?? new List<string>());
            _originalCraftingConditionKeys = (conditions ?? new List<COBJConditionRecord>()).Select(SerializeCondition).ToList();
            _hasCraftingSnapshot = true;
            OnPropertyChanged(nameof(IsCraftingWorkbenchChanged));
            OnPropertyChanged(nameof(IsCraftingIngredientsChanged));
            OnPropertyChanged(nameof(IsCraftingConditionsChanged));
            OnPropertyChanged(nameof(HasCraftingChanges));
        }

        public void CaptureTemperOriginalSnapshot(string workbenchKey, List<string> ingredientKeys, List<COBJConditionRecord> conditions)
        {
            _originalTemperWorkbenchKey = workbenchKey;
            _originalTemperIngredientKeys = new List<string>(ingredientKeys ?? new List<string>());
            _originalTemperConditionKeys = (conditions ?? new List<COBJConditionRecord>()).Select(SerializeCondition).ToList();
            _hasTemperSnapshot = true;
            OnPropertyChanged(nameof(IsTemperWorkbenchChanged));
            OnPropertyChanged(nameof(IsTemperIngredientsChanged));
            OnPropertyChanged(nameof(IsTemperConditionsChanged));
            OnPropertyChanged(nameof(HasTemperChanges));
        }

        // Called after a user-created recipe (no plugin origin) gets fully deleted instead of merely
        // reset (see MainContentVM.ResetCraftingRecipeEdits/ResetTemperRecipeEdits). Without dropping
        // the snapshot, HasCraftingChanges/HasTemperChanges would keep comparing the now-empty
        // Workbench/Ingredients/Conditions against the deleted recipe's stub values and immediately
        // re-enable the just-used Reset button.
        public void ClearCraftingSnapshot()
        {
            _hasCraftingSnapshot = false;
            _craftingRecipeIsUserCreated = false;
            OnPropertyChanged(nameof(IsCraftingWorkbenchChanged));
            OnPropertyChanged(nameof(IsCraftingIngredientsChanged));
            OnPropertyChanged(nameof(IsCraftingConditionsChanged));
            OnPropertyChanged(nameof(HasCraftingChanges));
        }

        public void ClearTemperSnapshot()
        {
            _hasTemperSnapshot = false;
            _temperRecipeIsUserCreated = false;
            OnPropertyChanged(nameof(IsTemperWorkbenchChanged));
            OnPropertyChanged(nameof(IsTemperIngredientsChanged));
            OnPropertyChanged(nameof(IsTemperConditionsChanged));
            OnPropertyChanged(nameof(HasTemperChanges));
        }

        // Order-insensitive: add/remove buttons only ever append/remove entries, there's no reorder
        // UI, so two lists with the same entries in a different order aren't a real edit.
        private static bool StringListsEqual(List<string> a, List<string> b)
        {
            if (a.Count != b.Count) return false;
            return a.OrderBy(x => x, StringComparer.Ordinal).SequenceEqual(b.OrderBy(x => x, StringComparer.Ordinal));
        }

        private static string SerializeCondition(COBJConditionRecord c) =>
            $"{c.ConditionType}|{c.Target}|{c.Value}|{c.Extra}|{c.RunOn}";

        private List<string> CurrentCraftingConditionKeys =>
            CraftingConditions.Select(vm => SerializeCondition(ConditionMapper.ToRecord(vm, ""))).ToList();
        private List<string> CurrentTemperConditionKeys =>
            TemperConditions.Select(vm => SerializeCondition(ConditionMapper.ToRecord(vm, ""))).ToList();

        public bool IsCraftingWorkbenchChanged => _hasCraftingSnapshot && CraftingWorkbenchKey != _originalCraftingWorkbenchKey;
        public bool IsCraftingIngredientsChanged => _hasCraftingSnapshot &&
            !StringListsEqual(CraftingIngredients.Select(i => $"{i.Key}*{i.Count}").ToList(), _originalCraftingIngredientKeys);
        public bool IsCraftingConditionsChanged => _hasCraftingSnapshot &&
            !StringListsEqual(CurrentCraftingConditionKeys, _originalCraftingConditionKeys);
        // _craftingRecipeIsUserCreated alone is enough to enable Reset: a user-created recipe is
        // always fully deletable regardless of whether any individual field was touched after
        // creation (see MainContentVM.ResetCraftingRecipeEdits).
        public bool HasCraftingChanges => _craftingRecipeIsUserCreated || IsCraftingWorkbenchChanged || IsCraftingIngredientsChanged || IsCraftingConditionsChanged;

        public bool IsTemperWorkbenchChanged => _hasTemperSnapshot && TemperWorkbenchKey != _originalTemperWorkbenchKey;
        public bool IsTemperIngredientsChanged => _hasTemperSnapshot &&
            !StringListsEqual(TemperIngredients.Select(i => $"{i.Key}*{i.Count}").ToList(), _originalTemperIngredientKeys);
        public bool IsTemperConditionsChanged => _hasTemperSnapshot &&
            !StringListsEqual(CurrentTemperConditionKeys, _originalTemperConditionKeys);
        public bool HasTemperChanges => _temperRecipeIsUserCreated || IsTemperWorkbenchChanged || IsTemperIngredientsChanged || IsTemperConditionsChanged;

        // --- Recipe completeness warnings (inline + status strip) ---
        // Meaningful only while the recipe exists. A crafting recipe with no workbench can't be used
        // in-game; one with no ingredients (or a temper recipe with none) is free - usually a mistake.
        public bool CraftingRecipeMissingWorkbench =>
            HasCraftingRecipe && string.IsNullOrEmpty(CraftingWorkbenchKey);

        public bool CraftingRecipeMissingIngredients =>
            HasCraftingRecipe && !CraftingIngredients.Any(i => !string.IsNullOrEmpty(i.Key));

        public bool TemperRecipeMissingIngredients =>
            HasTemperRecipe && !TemperIngredients.Any(i => !string.IsNullOrEmpty(i.Key));

        private void RaiseRecipeWarningFlags()
        {
            OnPropertyChanged(nameof(CraftingRecipeMissingWorkbench));
            OnPropertyChanged(nameof(CraftingRecipeMissingIngredients));
            OnPropertyChanged(nameof(TemperRecipeMissingIngredients));
        }

        // Surfaces recipe problems into the status strip: dead references (workbench / ingredient
        // material / condition perk|quest not in the active load order) and completeness gaps (no
        // workbench, no ingredients). Called once per item right after it's hydrated
        // (MainContentVM.LoadSelectedItemDetails). Category is per item so a fix drops that item's
        // entries on the next hydration; identical issues dedupe.
        internal void ReportRecipeIssues()
        {
            var refs = Main?.References;
            if (refs == null) return;

            var category = "recipe:" + Key;
            IssueHub.Current.Clear(category);

            void FlagKey(string what, string? key)
            {
                if (string.IsNullOrEmpty(key) || refs.IsActive(key)) return;
                IssueHub.Current.Report(new AppIssue(
                    AppIssueSeverity.Warning,
                    $"{EditorID}: {what} is not in the active load order.",
                    Context: key,
                    Category: category));
            }

            void FlagCondition(string what, BaseConditionViewModel c)
            {
                bool dead = c switch
                {
                    PerkConditionViewModel p => p.IsDeadReference,
                    QuestStageConditionViewModel q => q.IsDeadReference,
                    _ => false,
                };
                if (!dead) return;
                IssueHub.Current.Report(new AppIssue(
                    AppIssueSeverity.Warning,
                    $"{EditorID}: {what} target no longer resolves.",
                    Category: category));
            }

            FlagKey("Crafting workbench", CraftingWorkbenchKey);
            FlagKey("Temper workbench", TemperWorkbenchKey);
            foreach (var ing in CraftingIngredients) FlagKey("Crafting ingredient", ing.Key);
            foreach (var ing in TemperIngredients) FlagKey("Temper ingredient", ing.Key);
            foreach (var c in CraftingConditions) FlagCondition("Crafting condition", c);
            foreach (var c in TemperConditions) FlagCondition("Temper condition", c);

            if (CraftingRecipeMissingWorkbench)
                IssueHub.Current.Report(new AppIssue(AppIssueSeverity.Warning,
                    $"{EditorID}: crafting recipe has no workbench set.", Category: category));
            if (CraftingRecipeMissingIngredients)
                IssueHub.Current.Report(new AppIssue(AppIssueSeverity.Warning,
                    $"{EditorID}: crafting recipe has no ingredients - it will be free to craft.", Category: category));
            if (TemperRecipeMissingIngredients)
                IssueHub.Current.Report(new AppIssue(AppIssueSeverity.Warning,
                    $"{EditorID}: temper recipe has no ingredients.", Category: category));
        }

        // Guard on HasCraftingChanges/HasTemperChanges - the same property the Reset button's
        // IsEnabled binds to - not the narrower _hasCraftingSnapshot/_hasTemperSnapshot fields.
        // Those are only ever set by Capture*OriginalSnapshot (i.e. only once a recipe has actually
        // been loaded from the DB); a recipe created fresh in this session never goes through that
        // and so never sets them, even though _craftingRecipeIsUserCreated/HasCraftingChanges
        // correctly says there's something to undo. Guarding on the old fields here meant the button
        // looked enabled but every click on a freshly created recipe silently no-opped right here,
        // before ever reaching MainContentVM.ResetCraftingRecipeEdits.
        public ICommand ResetCraftingRecipeCommand => new RelayCommand(() =>
        {
            if (!HasCraftingChanges) return;
            Main?.ResetCraftingRecipeEdits(this);
            RefreshEditedState();
        });

        public ICommand ResetTemperRecipeCommand => new RelayCommand(() =>
        {
            if (!HasTemperChanges) return;
            Main?.ResetTemperRecipeEdits(this);
            RefreshEditedState();
        });

        // Item fields OR either recipe has pending edits vs the scanned baseline.
        public bool HasAnyItemOrRecipeChanges => HasAnyChanges || HasCraftingChanges || HasTemperChanges;

        // One click to revert everything on this item back to its scanned state.
        public ICommand ResetAllChangesCommand => new RelayCommand(() =>
        {
            if (!HasAnyItemOrRecipeChanges) return;

            var result = System.Windows.MessageBox.Show(
                $"Revert ALL edits on '{EditorID}' - item fields, crafting recipe and temper recipe - back to the scanned state?",
                "Reset item", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning);
            if (result != System.Windows.MessageBoxResult.Yes) return;

            if (_hasOriginalSnapshot) Main?.ResetItemEdits(this);
            if (HasCraftingChanges) Main?.ResetCraftingRecipeEdits(this);
            if (HasTemperChanges) Main?.ResetTemperRecipeEdits(this);

            RefreshEditedState();
        });

        // --------------------
        // Constructor
        // --------------------
        public ItemNodeVM()
        {
            // Note: AllKeywords synchronization is done in EnsureViewSources()
            // to ensure _keywordService is already initialized

            _keywordViewSource = null;
            _selectedKeywordViewSource = null;

            AddIngredientCommand = new RelayCommand(AddCraftingIngredient);
            RemoveIngredientCommand = new RelayCommand<IngredientEntryVM>(RemoveCraftingIngredient);

            AddTemperIngredientCommand = new RelayCommand(AddTemperIngredient);
            RemoveTemperIngredientCommand = new RelayCommand<IngredientEntryVM>(RemoveTemperIngredient);

            AddCraftingConditionCommand = new RelayCommand(AddCraftingCondition);
            RemoveCraftingConditionCommand = new RelayCommand<BaseConditionViewModel>(RemoveCraftingCondition);

            AddTemperConditionCommand = new RelayCommand(AddTemperCondition);
            RemoveTemperConditionCommand = new RelayCommand<BaseConditionViewModel>(RemoveTemperCondition);


            foreach (ArmorSlotMask slot in Enum.GetValues(typeof(ArmorSlotMask)))
            {
                if (slot == ArmorSlotMask.None)
                    continue;

                uint flag = (uint)slot;
                int bit = (int)Math.Log(flag, 2);

                var opt = new SlotVM(slot.ToString(), bit);
                opt.SelectionChanged += SlotSelectionChanged;

                SlotOptions.Add(opt);
            }
        }

        public ItemNodeVM(ArmorRecord rec) : this() => ApplyArmorRecord(rec);
        public ItemNodeVM(WeaponRecord rec) : this() => ApplyWeaponRecord(rec);

        // --------------------
        // Apply Records
        // --------------------
        public void ApplyArmorRecord(ArmorRecord rec)
        {
            IsArmor = true;
            ApplyBaseRecord(rec.Key, rec.EditorID, rec.Name, rec.Value, rec.Weight);

            ArmorRating = rec.ArmorRating;
            BodySlotMask = rec.BodySlotMask;

            ContainerString = rec.ContainerString ?? "{}";
            ContainerSelection.LoadFromString(ContainerString);
        }

        public void ApplyWeaponRecord(WeaponRecord rec)
        {
            IsArmor = false;
            ApplyBaseRecord(rec.Key, rec.EditorID, rec.Name, rec.Value, rec.Weight);

            Damage = rec.Damage;
            Speed = rec.Speed;
            Reach = rec.Reach;
            Stagger = rec.Stagger;

            ContainerString = rec.ContainerString ?? "{}";
            ContainerSelection.LoadFromString(ContainerString);
        }

        private void ApplyBaseRecord(string key, string editorID, string name, int value, float weight)
        {
            Key = key;
            EditorID = editorID;
            Name = name;
            Value = value;
            Weight = weight;
        }

        // --------------------
        // Slot Mask Sync
        // --------------------
        private void SlotSelectionChanged(object sender, EventArgs e)
        {
            if (_isSyncingSlots) return;

            uint mask = 0;

            foreach (var opt in SlotOptions)
            {
                if (opt.IsSelected)
                    mask |= opt.Flag;
            }

            BodySlotMask = mask;
        }

        private void SyncDataToGui()
        {
            _isSyncingSlots = true;

            foreach (var opt in SlotOptions)
            {
                opt.SetSelectedSilent((BodySlotMask & opt.Flag) != 0);
            }

            _isSyncingSlots = false;
        }

        // --------------------
        // Keyword Filtering
        // --------------------
        private void EnsureViewSources()
        {
            if (_keywordViewSource != null && _selectedKeywordViewSource != null)
                return;

            var createViewSources = () =>
            {
                // Ensure AllKeywords collection is synchronized for thread-safe access
                try
                {
                    BindingOperations.EnableCollectionSynchronization(AllKeywords, new object());
                }
                catch
                {
                    // Already enabled or error - continue anyway
                }

                _keywordViewSource = new CollectionViewSource { Source = AllKeywords };
                _keywordViewSource.Filter += KeywordFilter;

                _selectedKeywordViewSource = new CollectionViewSource { Source = AllKeywords };
                _selectedKeywordViewSource.Filter += (s, e) =>
                {
                    if (e.Item is KeywordSelectionVM kw)
                        e.Accepted = kw.IsSelected;
                    else
                        e.Accepted = false;
                };
            };

            var app = System.Windows.Application.Current;
            if (app == null)
            {
                createViewSources();
                return;
            }

            var disp = app.Dispatcher;
            if (disp.CheckAccess())
            {
                createViewSources();
            }
            else
            {
                disp.Invoke(createViewSources);
            }
        }

        public void RefreshKeywords()
        {
            EnsureViewSources();

            var view = _keywordViewSource?.View;
            if (view == null) return;

            var disp = System.Windows.Application.Current?.Dispatcher;
            if (disp != null && !disp.CheckAccess())
            {
                disp.Invoke(() => view.Refresh());
            }
            else
            {
                view.Refresh();
            }
        }

        private void DebouncedRefresh()
        {
            _searchDebounce?.Cancel();
            _searchDebounce = new CancellationTokenSource();
            var token = _searchDebounce.Token;

            Task.Delay(180, token).ContinueWith(t =>
            {
                if (!t.IsCanceled)
                {
                    System.Windows.Application.Current.Dispatcher.Invoke(RefreshKeywords);
                }
            }, TaskScheduler.Default);
        }

        private void KeywordFilter(object sender, FilterEventArgs e)
        {
            if (e.Item is not KeywordSelectionVM kw)
            {
                e.Accepted = false;
                return;
            }

            if (ShowAllKeywords)
            {
                if (!string.IsNullOrWhiteSpace(SearchText))
                    e.Accepted = kw.Name.Contains(SearchText, StringComparison.OrdinalIgnoreCase);
                else
                    e.Accepted = true;

                return;
            }

            var relevantPrefixes = _isArmor
                ? new[] { "Armor", "Clothing", "Jewelry", "VendorItemArmor", "Vendor", "Material" }
                : new[] { "Weap", "Weapon", "VendorItemWeapon", "Vendor", "Material", "DamageType" };

            bool isRelevant =
                kw.IsSelected ||
                relevantPrefixes.Any(p =>
                    kw.Name.StartsWith(p, StringComparison.OrdinalIgnoreCase));

            if (!isRelevant)
            {
                e.Accepted = false;
                return;
            }

            if (!string.IsNullOrWhiteSpace(SearchText) &&
                !kw.Name.Contains(SearchText, StringComparison.OrdinalIgnoreCase))
            {
                e.Accepted = false;
                return;
            }

            e.Accepted = true;
        }

        // --------------------
        // Crafting Ingredients
        // --------------------
        private void AddCraftingIngredient()
        {
            var newEntry = new IngredientEntryVM(this, false);

            newEntry.InitializeMaterials(Main.AllAvailableMaterials);

            CraftingIngredients.Add(newEntry);
            NotifyFieldChanged(nameof(CraftingIngredients));
        }

        private void RemoveCraftingIngredient(IngredientEntryVM ing)
        {
            if (ing == null) return;

            CraftingIngredients.Remove(ing);
            foreach (var e in CraftingIngredients) e.RefreshMaterialFilter(); // freed material reappears

            NotifyFieldChanged(nameof(CraftingIngredients));
        }

        // --------------------
        // Temper Ingredients
        // --------------------
        private void AddTemperIngredient()
        {
            var newEntry = new IngredientEntryVM(this, true);

            newEntry.InitializeMaterials(Main.AllAvailableMaterials);

            TemperIngredients.Add(newEntry);
            NotifyFieldChanged(nameof(TemperIngredients));
        }

        private void RemoveTemperIngredient(IngredientEntryVM ing)
        {
            if (TemperRecipe == null || ing == null)
                return;

            TemperRecipe.Ingredients.Remove(ing);
            foreach (var e in TemperRecipe.Ingredients) e.RefreshMaterialFilter(); // freed material reappears

            // NOT "TemperIngredients = TemperRecipe.Ingredients" — that's the same reference the
            // property already holds, so SetProperty returns false and the change never registers
            // (this was the bug: removing a temper material didn't mark the recipe edited, while the
            // crafting side did). Mirror RemoveCraftingIngredient and notify explicitly.
            NotifyFieldChanged(nameof(TemperIngredients));
        }

        // --------------------
        // ADD/REMOVE Buttons
        // --------------------

        private void AddCraftingCondition()
        {
            var newCondition = new PerkConditionViewModel();
            CraftingConditions.Add(newCondition);
            NotifyFieldChanged(nameof(CraftingConditions));
        }

        private void RemoveCraftingCondition(BaseConditionViewModel condition)
        {
            if (condition == null) return;
            CraftingConditions.Remove(condition);
            NotifyFieldChanged(nameof(CraftingConditions));
        }

        private void AddTemperCondition()
        {
            var newCondition = new PerkConditionViewModel();
            TemperConditions.Add(newCondition);
            NotifyFieldChanged(nameof(TemperConditions));
        }

        private void RemoveTemperCondition(BaseConditionViewModel condition)
        {
            if (condition == null) return;
            TemperConditions.Remove(condition);
            NotifyFieldChanged(nameof(TemperConditions));
        }


        private void RegisterKeywordEvents()
        {
            foreach (var kw in AllKeywords)
            {
                kw.PropertyChanged += OnKeywordPropertyChanged;
            }
        }

        private bool _isEnforcingRule = false;

        public void OnKeywordPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(KeywordSelectionVM.IsSelected)) return;
            if (sender is not KeywordSelectionVM changedKw) return;

            // Apply rules to this item's local keywords
            if (!_isEnforcingRule)
            {
                try
                {
                    _isEnforcingRule = true;
                    ApplyKeywordRules(changedKw);
                }
                finally
                {
                    _isEnforcingRule = false;
                }
            }

            // Sync SelectedKeywordKeys with the current selection
            SelectedKeywordKeys = AllKeywords
                .Where(kw => kw.IsSelected)
                .Select(kw => kw.Key)
                .ToList();

            // Refresh BOTH views (for IsReadOnly, IsSelected, etc.)
            _keywordViewSource?.View?.Refresh();
            _selectedKeywordViewSource?.View?.Refresh();
            OnPropertyChanged(nameof(SelectedKeywords));

            // IMPORTANT: save SelectedKeywordKeys, not SelectedKeywords!
            NotifyFieldChanged(nameof(SelectedKeywordKeys));
            OnPropertyChanged(nameof(IsKeywordsChanged));
            OnPropertyChanged(nameof(HasAnyChanges));
        }

        /// <summary>
        /// Applies business rules to keywords on this item
        /// (different from GlobalKeywords in the service)
        /// </summary>
        public void ApplyKeywordRules(KeywordSelectionVM changedKeyword) =>
            Services.KeywordRuleEngine.ApplyExclusivityRules(AllKeywords, changedKeyword);
    }
}
