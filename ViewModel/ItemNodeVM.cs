using SkyrimCraftingTool.Model;
using SkyrimCraftingTool.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
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

        /// <summary>
        /// Lokale Keywords Collection für dieses Item.
        /// Diese wird initialisiert wenn ein Item geladen wird und gefiltert für die UI-Anzeige.
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

        // Create recipe helpers used by save pipeline handlers
        public void CreateCraftingRecipe()
        {
            var rec = Main?.ItemService.CreateNewCOBJRecordForItem(this, false);
            if (rec == null) return;
            CraftingRecipe = new COBJNodeVM(this, rec, Main.FormIdService, false);
        }

        public void CreateTemperRecipe()
        {
            var rec = Main?.ItemService.CreateNewCOBJRecordForItem(this, true);
            if (rec == null) return;
            TemperRecipe = new COBJNodeVM(this, rec, Main.FormIdService, true);
        }

        private string _name;
        public string Name
        {
            get => _name;
            set
            {
                if (SetProperty(ref _name, value))
                    NotifyFieldChanged(nameof(Name));
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
                    NotifyFieldChanged(nameof(Weight));
            }
        }

        private int _value;
        public int Value
        {
            get => _value;
            set
            {
                if (SetProperty(ref _value, value))
                    NotifyFieldChanged(nameof(Value));
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
        /// Read-only observable collection der aktuell ausgewählten Keywords.
        /// Dies wird automatisch synchronisiert, wenn ein Keyword selektiert/deselektiert wird.
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
                Debug.WriteLine($"[CraftingRecipe-SET] Incoming recipe: {(value?.Key ?? "NULL")}");
                Debug.WriteLine($"[CraftingRecipe-SET] Incoming WorkbenchKey: {(value?.WorkbenchKeywordKey ?? "NULL")}");

                if (SetProperty(ref _craftingRecipe, value))
                {
                    Debug.WriteLine($"[CraftingRecipe-SET] SetProperty SUCCESS");

                    // Ingredients
                    CraftingIngredients = value?.Ingredients ?? new ObservableCollection<IngredientEntryVM>();
                    Debug.WriteLine($"[CraftingRecipe-SET] Ingredients count: {CraftingIngredients.Count}");

                    // Workbench + Perk
                    _craftingWorkbenchKey = value?.WorkbenchKeywordKey ?? "";
                    _craftingPerkKey = value?.PerkKey ?? "";

                    // Conditions
                    CraftingConditions = value?.Conditions ?? new ObservableCollection<BaseConditionViewModel>();

                    Debug.WriteLine($"[CraftingRecipe-SET] _craftingWorkbenchKey SET TO: {_craftingWorkbenchKey}");
                    Debug.WriteLine($"[CraftingRecipe-SET] _craftingPerkKey SET TO: {_craftingPerkKey}");

                    Debug.WriteLine($"[CraftingRecipe-SET] CraftingWorkbenchKey GETTER RETURNS: {CraftingWorkbenchKey}");

                    OnPropertyChanged(nameof(CraftingWorkbenchKey));
                    SelectedWorkbench =
                        AllAvailableWorkbenches.FirstOrDefault(x => x.Key == CraftingWorkbenchKey);
                    Debug.WriteLine($"Selected {SelectedWorkbench}");

                    OnPropertyChanged(nameof(CraftingPerkKey));

                    SelectedCraftingPerk =
                        AllAvailablePerks.FirstOrDefault(x => x.Key == CraftingPerkKey);


                    Debug.WriteLine($"[CraftingRecipe-SET] HasCraftingRecipe: {HasCraftingRecipe}");
                    Debug.WriteLine($"[CraftingRecipe-SET] CraftingEditorID: {CraftingEditorID}");

                    if (!IsLoading)
                    {
                        Debug.WriteLine($"[CraftingRecipe-SET] NotifyFieldChanged firing (IsLoading=false)");
                        NotifyFieldChanged(nameof(CraftingRecipe));
                        NotifyFieldChanged(nameof(CraftingIngredients));
                        NotifyFieldChanged(nameof(CraftingWorkbenchKey));
                        NotifyFieldChanged(nameof(CraftingPerkKey));
                    }
                    else
                    {
                        Debug.WriteLine($"[CraftingRecipe-SET] NotifyFieldChanged SKIPPED (IsLoading=true)");
                    }
                }
                else
                {
                    Debug.WriteLine($"[CraftingRecipe-SET] SetProperty FAILED (value identical?)");
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

        private void OnConditionPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (_isReplacingConditionType) return;
            if (sender is not BaseConditionViewModel condition) return;

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
                Debug.WriteLine($"[CraftingWorkbenchKey-SET] Incoming value: {value}");

                if (SetProperty(ref _craftingWorkbenchKey, value))
                {
                    Debug.WriteLine($"[CraftingWorkbenchKey-SET] SetProperty SUCCESS → new value: {_craftingWorkbenchKey}");

                    if (CraftingRecipe != null)
                    {
                        Debug.WriteLine($"[CraftingWorkbenchKey-SET] Writing into CraftingRecipe.WorkbenchKeywordKey");
                        CraftingRecipe.WorkbenchKeywordKey = value;
                    }
                    else
                    {
                        Debug.WriteLine($"[CraftingWorkbenchKey-SET] CraftingRecipe is NULL → cannot write");
                    }

                    if (!IsLoading)
                    {
                        Debug.WriteLine($"[CraftingWorkbenchKey-SET] NotifyFieldChanged firing");
                        NotifyFieldChanged(nameof(CraftingWorkbenchKey));
                    }
                    else
                    {
                        Debug.WriteLine($"[CraftingWorkbenchKey-SET] NotifyFieldChanged SKIPPED (IsLoading=true)");
                    }
                }
                else
                {
                    Debug.WriteLine($"[CraftingWorkbenchKey-SET] SetProperty FAILED (value identical?)");
                }
            }
        }

        public FormIDRecord _selectedWorkbench;
        public FormIDRecord? SelectedWorkbench
        {
            get => _selectedWorkbench;
            set
            {
                if (SetProperty(ref _selectedWorkbench, value))
                {
                    CraftingWorkbenchKey = value?.Key;
                    if (string.IsNullOrEmpty(CraftingWorkbenchSearchText))
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
                    OnPropertyChanged(nameof(TemperPerkKey));
                    SelectedTemperPerk =
                            AllAvailablePerks.FirstOrDefault(x => x.Key == TemperPerkKey);


                    OnPropertyChanged(nameof(HasTemperRecipe));
                    OnPropertyChanged(nameof(TemperEditorID));

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
                    NotifyFieldChanged(nameof(ArmorRating));
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
                    NotifyFieldChanged(nameof(Damage));
            }
        }

        private float _speed;
        public float Speed
        {
            get => _speed;
            set
            {
                if (SetProperty(ref _speed, value))
                    NotifyFieldChanged(nameof(Speed));
            }
        }

        private float _reach;
        public float Reach
        {
            get => _reach;
            set
            {
                if (SetProperty(ref _reach, value))
                    NotifyFieldChanged(nameof(Reach));
            }
        }

        private float _stagger;
        public float Stagger
        {
            get => _stagger;
            set
            {
                if (SetProperty(ref _stagger, value))
                    NotifyFieldChanged(nameof(Stagger));
            }
        }

        // --------------------
        // Constructor
        // --------------------
        public ItemNodeVM()
        {
            // Note: AllKeywords Synchronization wird in EnsureViewSources() durchgeführt
            // um sicherzustellen, dass _keywordService bereits initialisiert ist

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
                ? new[] { "Armor", "Clothing", "Jewelry", "VendorItemArmor", "Material" }
                : new[] { "Weap", "Weapon", "VendorItemWeapon", "Material", "DamageType" };

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
            TemperIngredients = TemperRecipe.Ingredients;
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

            // Appliziere Regeln auf die lokalen Keywords dieses Items
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

            // Synchronisiere SelectedKeywordKeys mit der aktuellen Auswahl
            SelectedKeywordKeys = AllKeywords
                .Where(kw => kw.IsSelected)
                .Select(kw => kw.Key)
                .ToList();

            // Aktualisiere BEIDE Views (für IsReadOnly, IsSelected, etc.)
            _keywordViewSource?.View?.Refresh();
            _selectedKeywordViewSource?.View?.Refresh();
            OnPropertyChanged(nameof(SelectedKeywords));

            // WICHTIG: Speichere SelectedKeywordKeys, nicht SelectedKeywords!
            NotifyFieldChanged(nameof(SelectedKeywordKeys));
        }

        /// <summary>
        /// Appliziert Business-Regeln auf Keywords in diesem Item
        /// (anders als GlobalKeywords im Service)
        /// </summary>
        public void ApplyKeywordRules(KeywordSelectionVM changedKeyword)
        {
            if (changedKeyword == null) return;

            bool isLight = IsArmorLight(changedKeyword);
            bool isHeavy = IsArmorHeavy(changedKeyword);
            bool isClothing = IsArmorClothing(changedKeyword);

            // ---------------------------------------------------------
            // 0) ArmorLight / ArmorHeavy / ArmorClothing sind exklusiv
            // ---------------------------------------------------------
            if (changedKeyword.IsSelected && IsArmorCategory(changedKeyword))
            {
                foreach (var kw in AllKeywords)
                {
                    if (kw != changedKeyword && IsArmorCategory(kw) && kw.IsSelected)
                    {
                        kw.IsSelected = false;
                    }
                }
            }

            if (changedKeyword.IsSelected && IsArmorMaterial(changedKeyword))
            {
                foreach (var kw in AllKeywords)
                {
                    if (kw != changedKeyword && IsArmorMaterial(kw) && kw.IsSelected)
                    {
                        kw.IsSelected = false;
                    }
                }
            }


            // ---------------------------------------------------------
            // 1) ArmorLight / ArmorHeavy -> blockiert alles Clothing*
            // ---------------------------------------------------------
            if (changedKeyword.IsSelected && (isLight || isHeavy))
            {
                foreach (var kw in AllKeywords)
                {
                    if (IsClothingKeyword(kw))
                    {
                        kw.IsReadOnly = true;
                        kw.IsSelected = false;
                    }
                }
            }
            else
            {
                // Clothing wieder freigeben, wenn kein Light/Heavy aktiv ist
                bool anyLightOrHeavy = AllKeywords.Any(kw => kw.IsSelected &&
                    (IsArmorLight(kw) || IsArmorHeavy(kw)));

                foreach (var kw in AllKeywords)
                {
                    if (IsClothingKeyword(kw))
                    {
                        kw.IsReadOnly = anyLightOrHeavy;
                        if (anyLightOrHeavy && kw.IsSelected)
                            kw.IsSelected = false;
                    }
                }
            }

            // ---------------------------------------------------------
            // 2) ArmorClothing -> blockiert alles Armor* außer Ausnahmen
            // ---------------------------------------------------------
            if (changedKeyword.IsSelected && isClothing)
            {
                foreach (var kw in AllKeywords)
                {
                    if (IsArmorKeyword(kw) &&
                        !IsArmorMaterial(kw) &&
                        !IsArmorLight(kw) &&
                        !IsArmorHeavy(kw) &&
                        !IsArmorClothing(kw))
                    {
                        kw.IsReadOnly = true;
                        kw.IsSelected = false;
                    }
                }
            }
            else
            {
                // Armor wieder freigeben, wenn kein Clothing aktiv ist
                bool anyClothing = AllKeywords.Any(kw => kw.IsSelected && IsArmorClothing(kw));

                foreach (var kw in AllKeywords)
                {
                    if (IsArmorKeyword(kw) &&
                        !IsArmorMaterial(kw) &&
                        !IsArmorLight(kw) &&
                        !IsArmorHeavy(kw) &&
                        !IsArmorClothing(kw))
                    {
                        kw.IsReadOnly = anyClothing;
                        if (anyClothing && kw.IsSelected)
                            kw.IsSelected = false;
                    }
                }
            }
        }

        private bool IsArmorLight(KeywordSelectionVM kw)
        {
            return kw?.Name?.StartsWith("ArmorLight", StringComparison.OrdinalIgnoreCase) ?? false;
        }

        private bool IsArmorHeavy(KeywordSelectionVM kw)
        {
            return kw?.Name?.StartsWith("ArmorHeavy", StringComparison.OrdinalIgnoreCase) ?? false;
        }

        private bool IsArmorClothing(KeywordSelectionVM kw)
        {
            return kw?.Name?.StartsWith("ArmorClothing", StringComparison.OrdinalIgnoreCase) ?? false;
        }

        private bool IsArmorMaterial(KeywordSelectionVM kw)
        {
            return kw?.Name?.StartsWith("ArmorMaterial", StringComparison.OrdinalIgnoreCase) ?? false;
        }

        private bool IsClothingKeyword(KeywordSelectionVM kw)
        {
            return kw?.Name?.StartsWith("Clothing", StringComparison.OrdinalIgnoreCase) ?? false;
        }

        private bool IsArmorKeyword(KeywordSelectionVM kw)
        {
            return kw?.Name?.StartsWith("Armor", StringComparison.OrdinalIgnoreCase) ?? false;
        }

        private bool IsWeaponType(KeywordSelectionVM kw)
        {
            return kw?.Name != null && kw.Name.StartsWith("WeapType", StringComparison.OrdinalIgnoreCase);
        }

        private bool IsArmorCategory(KeywordSelectionVM kw)
        {
            if (kw?.Name == null) return false;
            var name = kw.Name.ToLowerInvariant();
            return name.StartsWith("armorlight") ||
                   name.StartsWith("armorheavy") ||
                   name.StartsWith("armorclothing");
        }
    }
}
