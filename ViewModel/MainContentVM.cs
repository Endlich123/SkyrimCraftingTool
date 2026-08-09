using Microsoft.Data.Sqlite;
using SkyrimCraftingTool.Model;
using SkyrimCraftingTool.Services;
using SkyrimCraftingTool.Services.Adapters;
using SkyrimCraftingTool.Services.SavePipline;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using SkyrimCraftingTool.Services;

namespace SkyrimCraftingTool.ViewModel
{
    public class MainContentVM : ViewModelBase
    {
        // --- Services ---
        private readonly IItemService _itemService;
        private readonly IFileService _fileService;
        private readonly IFormIdService _formIdService;
        private readonly ICacheManager _cacheManager;
        private readonly IKeywordService _keywordService;
        private readonly ITreeBuilderService _treeBuilder;
        private readonly ISaveRequestService _saveRequestService;

        // --- Helpers ---
        private readonly Debouncer _debouncer = new();
        private readonly BackgroundFilterRunner<string, List<PluginNodeVM>> _filterRunner = new();
        private readonly Debouncer _saveDebouncer = new();

        // --- Service helpers ---
        internal IItemService ItemService => _itemService;
        internal IFileService FileService => _fileService;
        internal IFormIdService FormIdService => _formIdService;
        internal IKeywordService KeywordService => _keywordService;

        // --- State ---
        private bool _isInitialized;
        private bool _isInitializing;
        private Task _initializationTask;

        private object _selectedNode;
        private ItemNodeVM? _subscribedItemForContainerSelection;
        public object SelectedNode
        {
            get => _selectedNode;
            set
            {
                Debug.WriteLine($"[SelectedNode] SET → {value}");
                if (SetProperty(ref _selectedNode, value))
                {
                    Debug.WriteLine($"[SelectedNode] SetProperty SUCCESS → {value?.GetType().Name}");

                    if (value is ItemNodeVM item)
                    {
                        Debug.WriteLine($"[SelectedNode] ItemNodeVM detected → Key={item.Key}");
                        LoadSelectedItemDetails(item);

                        if (_subscribedItemForContainerSelection != null)
                        {
                            Debug.WriteLine("[SelectedNode] Unsubscribing old container selection");
                            _subscribedItemForContainerSelection.ContainerSelection.SelectedContainers.CollectionChanged -= OnSelectedContainersChanged;
                        }

                        _subscribedItemForContainerSelection = item;
                        _subscribedItemForContainerSelection.ContainerSelection.SelectedContainers.CollectionChanged += OnSelectedContainersChanged;

                        UpdateAllContainerSelectionFlags(item);
                    }
                    else
                    {
                        Debug.WriteLine("[SelectedNode] Non-item node selected");

                        if (_subscribedItemForContainerSelection != null)
                        {
                            Debug.WriteLine("[SelectedNode] Unsubscribing container selection");
                            _subscribedItemForContainerSelection.ContainerSelection.SelectedContainers.CollectionChanged -= OnSelectedContainersChanged;
                            _subscribedItemForContainerSelection = null;
                        }

                        UpdateAllContainerSelectionFlags(null);
                    }
                }
                else
                {
                    Debug.WriteLine("[SelectedNode] SetProperty FAILED (value identical?)");
                }
            }
        }


        private string _treeSearchText = string.Empty;
        public string TreeSearchText
        {
            get => _treeSearchText;
            set
            {
                if (SetProperty(ref _treeSearchText, value))
                    ApplyFilterDebounced(value);
            }
        }

        // --- Trees ---
        public ObservableCollection<PluginNodeVM> ModItemsTree { get; } = new();
        public ObservableCollection<PluginNodeVM> FilteredTree { get; } = new();

        // --- Caches ---
        public Dictionary<string, ArmorRecord> ArmorCache { get; } = new();
        public Dictionary<string, WeaponRecord> WeaponCache { get; } = new();
        public Dictionary<string, FormIDRecord> KeywordCache { get; } = new();
        public Dictionary<string, FormIDRecord> MaterialCache { get; } = new();
        public Dictionary<string, List<COBJRecord>> RecipeCacheByCreatedItem { get; } = new();
        public Dictionary<string, List<COBJConditionRecord>> COBJConditionCache { get; } = new();

        // --- Global data lists ---
        public List<FormIDRecord> AllAvailableMaterials { get; private set; } = new();
        public List<FormIDRecord> AllAvailablePerks { get; private set; } = new();
        public List<FormIDRecord> AllAvailableQuests { get; private set; } = new();
        public List<FormIDRecord> AllAvailableKeywords { get; private set; } = new();
        public List<FormIDRecord> AllAvailableWorkbenches { get; private set; } = new();

        // --- Global keyword VM list ---
        public ObservableCollection<KeywordSelectionVM> GlobalKeywords => _keywordService.GlobalKeywords;

        // --- Container ---
        public List<ContainerRecord> AllContainers { get; private set; } = new();
        // --- Container UI State ---
        private bool _showExpertContainers;
        public bool ShowExpertContainers
        {
            get => _showExpertContainers;
            set
            {
                if (SetProperty(ref _showExpertContainers, value))
                    OnPropertyChanged(nameof(FilteredContainers));
            }
        }

        private string _containerSearchText = string.Empty;
        public string ContainerSearchText
        {
            get => _containerSearchText;
            set
            {
                if (SetProperty(ref _containerSearchText, value))
                    OnPropertyChanged(nameof(FilteredContainers));
            }
        }

        public RelayCommand ToggleExpertContainersCommand { get; }


        // limited list (z. B. nur 20 Container)
        public ObservableCollection<ContainerEntryVM> LimitedContainerVMs { get; } = new();

        // filtered list für die UI
        public IEnumerable<ContainerEntryVM> FilteredContainers =>
            (ShowExpertContainers ? AllContainerVMs : LimitedContainerVMs)
                .Where(c =>
                    string.IsNullOrWhiteSpace(ContainerSearchText)
                    || c.Name.Contains(ContainerSearchText, StringComparison.OrdinalIgnoreCase));


        // UI-facing container VMs used by the left-hand list
        public ObservableCollection<ContainerEntryVM> AllContainerVMs { get; } = new();

        // --- Commands ---
        public RelayCommand CollapseAllCommand { get; }
        public RelayCommand ScanModsCommand { get; }
        public RelayCommand<string> ToggleContainerForSelectedItemCommand { get; }
        public RelayCommand ClearContainerSelectionCommand { get; }

        private void Log(string msg)
        {
            Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {msg}");
        }

        // --- ctor ---
        public MainContentVM(
            IItemService itemService,
            IFileService fileService,
            IFormIdService formIdService,
            ICacheManager cacheManager = null,
            ITreeBuilderService treeBuilder = null,
            IKeywordService keywordService = null)
        {
            _itemService = itemService;
            _fileService = fileService;
            _formIdService = formIdService;

            _cacheManager = cacheManager ?? new CacheManager(_itemService, _formIdService);
            _treeBuilder = treeBuilder ?? new TreeBuilderService();
            _keywordService = keywordService ?? new KeywordService();

            // SavePipeline extended via ContainerSaveHandler
            _saveRequestService = new SaveRequestService(new ISaveHandler[]
            {
                new ArmorSaveHandler(_itemService, _cacheManager),
                new WeaponSaveHandler(_itemService, _cacheManager),
                new CraftingSaveHandler(_itemService, _cacheManager),
                new TemperSaveHandler(_itemService, _cacheManager),
            });

            CollapseAllCommand = new RelayCommand(() => ExpandAll(false));
            ScanModsCommand = new RelayCommand(async () => await ExecuteFullScanAsync());
            ToggleContainerForSelectedItemCommand = new RelayCommand<string>(key =>
            {
                if (SelectedNode is ItemNodeVM item)
                {
                    item.ContainerSelection.ToggleContainer(key);
                    item.ContainerString = item.ContainerSelection.BuildString();

                    // Update UI flag on left list
                    var vm = AllContainerVMs.FirstOrDefault(c => c.ContainerKey == key);
                    if (vm != null)
                        vm.IsSelected = item.ContainerSelection.SelectedContainers.Any(sc => sc.ContainerKey == key);
                }
            });
            ClearContainerSelectionCommand = new RelayCommand(() =>
            {
                if (SelectedNode is ItemNodeVM item)
                {
                    item.ContainerSelection.Clear();
                    item.ContainerString = item.ContainerSelection.BuildString();
                    UpdateAllContainerSelectionFlags(item);
                }
            });
            ToggleExpertContainersCommand = new RelayCommand(() =>
            {
                ShowExpertContainers = !ShowExpertContainers;
            });

        }

        // --- Initial load ---
        public Task LoadInitialDataAsync()
        {
            if (_isInitialized)
                return _initializationTask;

            _isInitialized = true;
            _isInitializing = true;

            _initializationTask = Task.Run(async () =>
            {
                Log("LoadInitialData START");

                var activePlugins = FileService.GetActivePlugins();

                if (!File.Exists(GlobalState.Tool.InputFolder + "/Item/item.db") ||
                    !File.Exists(GlobalState.Tool.InputFolder + "/FormID/formid.db"))
                {
                    Log("DB files missing.");
                    _isInitializing = false;
                    return;
                }

                activePlugins.Add(new PluginInfo { FileName = "SkyrimCraftingTool.esp" });

                var sw = Stopwatch.StartNew();
                await Task.Run(() =>
                {
                    var snapshot = _cacheManager.BuildCachesFromDB(activePlugins);
                    ApplyCacheSnapshot(snapshot);
                    _keywordService.InitializeFrom(snapshot.Keywords);
                });
                Log($"BuildCachesFromDB DONE in {sw.ElapsedMilliseconds} ms");

                sw.Restart();
                await BuildTreeFromCacheAsync(activePlugins);
                Log($"BuildTreeFromCacheAsync DONE in {sw.ElapsedMilliseconds} ms");

                _isInitializing = false;

                System.Windows.Application.Current.Dispatcher.Invoke(() =>
                {
                    ApplyFilter(_treeSearchText);
                });

                Log("LoadInitialData END");
            });

            return _initializationTask;
        }

        private void ExpandAll(bool expand)
        {
            foreach (var p in FilteredTree)
            {
                p.IsExpanded = expand;
                foreach (var c in p.Categories)
                    c.IsExpanded = expand;
            }
        }

        // --- Full rescan ---
        private async Task ExecuteFullScanAsync()
        {
            Log("ExecuteFullScanAsync START");

            List<PluginInfo> activePlugins = null;

            var sw = Stopwatch.StartNew();
            await Task.Run(() =>
            {
                FileService.RefreshPluginDatabase();
                activePlugins = FileService.GetActivePlugins();
                FormIdService.PutIntoDataBank(activePlugins);
                ItemService.PutIntoDataBank(activePlugins);

                var snapshot = _cacheManager.BuildCachesFromDB(activePlugins);
                ApplyCacheSnapshot(snapshot);
                _keywordService.InitializeFrom(snapshot.Keywords);
            });
            Log($"Full scan + caches DONE in {sw.ElapsedMilliseconds} ms");

            sw.Restart();
            await BuildTreeFromCacheAsync(activePlugins);
            Log($"Tree rebuild DONE in {sw.ElapsedMilliseconds} ms");

            System.Windows.MessageBox.Show("DB updated!", "System", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);

            Log("ExecuteFullScanAsync END");
        }

        // --- Tree build ---
        private async Task BuildTreeFromCacheAsync(List<PluginInfo> activePlugins)
        {
            Log("BuildTreeFromCacheAsync START");

            var nodes = await Task.Run(() => _treeBuilder.BuildTreeFromCache(activePlugins, ArmorCache, WeaponCache, this));

            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                ModItemsTree.Clear();
                foreach (var n in nodes)
                    ModItemsTree.Add(n);

                if (!_isInitializing)
                    ApplyFilter(_treeSearchText);
            });

            Log("BuildTreeFromCacheAsync END");
        }

        // --- Tree filter ---
        private void ApplyFilterDebounced(string text)
        {
            if (_isInitializing)
                return;

            _debouncer.Debounce(120, _ =>
            {
                _filterRunner.Run(
                    text,
                    (search, token) => FilterOnBackground(search, token),
                    result => UpdateFilteredTree(result)
                );
            });
        }

        private void ApplyFilter(string text)
        {
            if (_isInitializing)
                return;

            FilteredTree.Clear();

            if (string.IsNullOrWhiteSpace(text))
            {
                foreach (var p in ModItemsTree)
                    FilteredTree.Add(p);
                return;
            }

            foreach (var plugin in ModItemsTree)
            {
                var filtered = plugin.FilterReference(text);
                if (filtered != null)
                {
                    filtered.IsExpanded = true;
                    foreach (var cat in filtered.Categories)
                        cat.IsExpanded = true;

                    FilteredTree.Add(filtered);
                }
            }
        }

        private List<PluginNodeVM> FilterOnBackground(string search, CancellationToken token)
        {
            if (string.IsNullOrWhiteSpace(search))
                return ModItemsTree.ToList();

            search = search.ToLowerInvariant();

            var result = new List<PluginNodeVM>();

            foreach (var plugin in ModItemsTree)
            {
                token.ThrowIfCancellationRequested();

                var filtered = plugin.FilterReference(search);
                if (filtered != null)
                    result.Add(filtered);
            }

            return result;
        }

        private void UpdateFilteredTree(List<PluginNodeVM> nodes)
        {
            FilteredTree.Clear();
            foreach (var n in nodes)
                FilteredTree.Add(n);
        }

        internal void UpdateAllContainerSelectionFlags(ItemNodeVM? item)
        {
            var selectedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (item != null)
            {
                foreach (var sc in item.ContainerSelection.SelectedContainers)
                    selectedKeys.Add(sc.ContainerKey);
            }

            foreach (var vm in AllContainerVMs)
                vm.IsSelected = selectedKeys.Contains(vm.ContainerKey);
        }

        private void OnSelectedContainersChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            if (_subscribedItemForContainerSelection != null)
                UpdateAllContainerSelectionFlags(_subscribedItemForContainerSelection);
        }

        // --- Cache snapshot ---
        private void ApplyCacheSnapshot(CacheSnapshot snapshot)
        {
            if (snapshot == null) return;

            Log("ApplyCacheSnapshot START");

            ArmorCache.Clear();
            WeaponCache.Clear();
            RecipeCacheByCreatedItem.Clear();
            COBJConditionCache.Clear();

            foreach (var kv in snapshot.Armor)
                ArmorCache[kv.Key] = kv.Value;

            foreach (var kv in snapshot.Weapons)
                WeaponCache[kv.Key] = kv.Value;

            foreach (var kv in snapshot.RecipesByCreatedItem)
                RecipeCacheByCreatedItem[kv.Key] = kv.Value;

            foreach (var kv in snapshot.COBJConditions)
                COBJConditionCache[kv.Key] = kv.Value;

            KeywordCache.Clear();
            MaterialCache.Clear();

            foreach (var kw in snapshot.Keywords)
                KeywordCache[kw.Key] = kw;

            foreach (var mat in snapshot.Materials)
                MaterialCache[mat.Key] = mat;

            AllAvailableMaterials = snapshot.Materials?.OrderBy(p => p.Name).ToList()
                ?? new List<FormIDRecord>();

            AllAvailablePerks = snapshot.Perks?.OrderBy(p => p.Name).ToList()
                ?? new List<FormIDRecord>();

            AllAvailableQuests = snapshot.Quests?.OrderBy(q => q.Name).ToList()
                ?? new List<FormIDRecord>();

            AllAvailableKeywords = snapshot.Keywords?.OrderBy(k => k.Name).ToList()
                ?? new List<FormIDRecord>();

            AllAvailableWorkbenches =
                snapshot.Keywords?
                    .Where(k =>
                        k.Name.StartsWith("Crafting", StringComparison.OrdinalIgnoreCase) &&
                        k.Key != "Skyrim.esm|088108" &&
                        k.Key != "Skyrim.esm|0ADB78")
                    .OrderBy(k => k.Name)
                    .ToList()
                ?? new List<FormIDRecord>();


            foreach (var wb in AllAvailableWorkbenches)
                Debug.WriteLine($"[WorkbenchList] {wb.Key} | {wb.Name}");


            AllContainers = snapshot.Containers?.OrderBy(c => c.Name).ToList()
                ?? new List<ContainerRecord>();

            // Populate UI VM collection
            AllContainerVMs.Clear();
            foreach (var c in AllContainers)
            {
                var vm = new ContainerEntryVM(c);
                AllContainerVMs.Add(vm);
            }

            // Limited list (z. B. Top 20 alphabetisch)
            LimitedContainerVMs.Clear();
            foreach (var vm in AllContainerVMs
                .Where(vm => vm.Name.Contains("Merchant", StringComparison.OrdinalIgnoreCase)))
                {
                    LimitedContainerVMs.Add(vm);
                }

            Log("ApplyCacheSnapshot END");
        }

        // --- Item selection ---
        private void LoadSelectedItemDetails(ItemNodeVM item)
        {
            Debug.WriteLine($"[LoadSelectedItemDetails] START for {item.Key}");
            Debug.WriteLine($"[LoadSelectedItemDetails] IsLoading BEFORE = {item.IsLoading}");

            item.IsLoading = true;
            Debug.WriteLine($"[LoadSelectedItemDetails] IsLoading SET TRUE");

            ArmorRecord armor = null;
            WeaponRecord weapon = null;
            List<string> activeKeywords = null;

            // Armor / Weapon
            if (ArmorCache.TryGetValue(item.Key, out armor))
            {
                Debug.WriteLine($"[LoadSelectedItemDetails] ArmorRecord FOUND → {armor.Key}");
                item.ApplyArmorRecord(armor);
                activeKeywords = armor.Keywords;
            }
            else if (WeaponCache.TryGetValue(item.Key, out weapon))
            {
                Debug.WriteLine($"[LoadSelectedItemDetails] WeaponRecord FOUND → {weapon.Key}");
                item.ApplyWeaponRecord(weapon);
                activeKeywords = weapon.Keywords;
            }
            else
            {
                Debug.WriteLine("[LoadSelectedItemDetails] NO Armor/Weapon record found");
            }

            activeKeywords ??= new List<string>();
            Debug.WriteLine($"[LoadSelectedItemDetails] ActiveKeywords count = {activeKeywords.Count}");

            item.SelectedKeywordKeys = activeKeywords.ToList();

            // Keywords - Baue lokale Keywords-Liste für dieses Item
            Debug.WriteLine("[LoadSelectedItemDetails] Building local AllKeywords list");
            item.AllKeywords.Clear();
            foreach (var kw in item.AllAvailableKeywords.OrderBy(k => k.Name))
            {
                item.AllKeywords.Add(new KeywordSelectionVM(
                    key: kw.Key,
                    name: kw.Name,
                    isSelected: activeKeywords.Contains(kw.Key),
                    onSelectedChanged: null
                ));
            }

            // Keyword-Regeln initial anwenden
            foreach (var kw in item.AllKeywords.Where(k => k.IsSelected))
            {
                item.ApplyKeywordRules(kw);
            }
            item.RefreshKeywords();


            // Event-Handler für Keyword-Änderungen registrieren
            RegisterItemKeywordEvents(item);
            item.RefreshKeywords();

            //string itemmasterkey = KeyFactory.ToMasterKey(item.Key);
            //Debug.WriteLine($"[LoadSelectedItemDetails] MasterKey = {itemmasterkey}");

            // Recipes
            if (RecipeCacheByCreatedItem.TryGetValue(item.Key, out var recipes))
            {
                Debug.WriteLine($"[LoadSelectedItemDetails] Recipes FOUND → Count={recipes.Count}");

                var craftRec = recipes.FirstOrDefault(r =>
                    r.WorkbenchKeywordKey != "Skyrim.esm|088108" &&
                    r.WorkbenchKeywordKey != "Skyrim.esm|0ADB78");

                var temperRec = recipes.FirstOrDefault(r =>
                    r.WorkbenchKeywordKey == "Skyrim.esm|088108" ||
                    r.WorkbenchKeywordKey == "Skyrim.esm|0ADB78");

                Debug.WriteLine($"[LoadSelectedItemDetails] CraftRec = {(craftRec?.Key ?? "NULL")} | WB={craftRec?.WorkbenchKeywordKey}");
                Debug.WriteLine($"[LoadSelectedItemDetails] TemperRec = {(temperRec?.Key ?? "NULL")} | WB={temperRec?.WorkbenchKeywordKey}");

                if (craftRec != null)
                {
                    Debug.WriteLine("[LoadSelectedItemDetails] Setting CraftingRecipe");
                    System.Windows.Application.Current.Dispatcher.Invoke(() =>
                    {
                        item.CraftingRecipe = new COBJNodeVM(item, craftRec, FormIdService, false);
                        InitializeRecipeIngredients(item.CraftingRecipe.Ingredients);
                        Debug.WriteLine($"[LoadSelectedItemDetails] CraftingRecipe SET → WB={item.CraftingRecipe.WorkbenchKeywordKey}");
                    });
                }
                else
                {
                    Debug.WriteLine("[LoadSelectedItemDetails] CraftingRecipe = NULL");
                    item.CraftingRecipe = null;
                    item.CraftingIngredients = new ObservableCollection<IngredientEntryVM>();
                    item.CraftingConditions = new ObservableCollection<BaseConditionViewModel>();
                }

                if (temperRec != null)
                {
                    Debug.WriteLine("[LoadSelectedItemDetails] Setting TemperRecipe");
                    System.Windows.Application.Current.Dispatcher.Invoke(() =>
                    {
                        item.TemperRecipe = new COBJNodeVM(item, temperRec, FormIdService, true);
                        InitializeRecipeIngredients(item.TemperRecipe.Ingredients);
                        item.TemperIngredients = item.TemperRecipe.Ingredients;
                        Debug.WriteLine($"[LoadSelectedItemDetails] TemperRecipe SET → WB={item.TemperRecipe.WorkbenchKeywordKey}");
                    });
                }
                else
                {
                    Debug.WriteLine("[LoadSelectedItemDetails] TemperRecipe = NULL");
                    item.TemperRecipe = null;
                    item.TemperIngredients = new ObservableCollection<IngredientEntryVM>();
                    item.TemperConditions = new ObservableCollection<BaseConditionViewModel>();

                }
            }
            else
            {
                Debug.WriteLine("[LoadSelectedItemDetails] NO recipes found");
                item.CraftingRecipe = null;
                item.CraftingIngredients = new ObservableCollection<IngredientEntryVM>();
                item.TemperRecipe = null;
                item.TemperIngredients = new ObservableCollection<IngredientEntryVM>();
                item.CraftingConditions = new ObservableCollection<BaseConditionViewModel>();
                item.TemperConditions = new ObservableCollection<BaseConditionViewModel>();

            }

            // Autosave hook
            Debug.WriteLine("[LoadSelectedItemDetails] Registering Autosave FieldChanged");
            item.FieldChanged -= OnItemFieldChanged;
            item.FieldChanged += OnItemFieldChanged;

            item.IsLoading = false;
            Debug.WriteLine($"[LoadSelectedItemDetails] IsLoading SET FALSE");
            Debug.WriteLine("[LoadSelectedItemDetails] END");
        }




        private void RegisterItemKeywordEvents(ItemNodeVM item)
        {
            foreach (var kw in item.AllKeywords)
            {
                kw.PropertyChanged -= item.OnKeywordPropertyChanged;
                kw.PropertyChanged += item.OnKeywordPropertyChanged;
            }
        }

        private void InitializeRecipeIngredients(IEnumerable<IngredientEntryVM> ingredients)
        {
            Debug.WriteLine($"[InitializeRecipeIngredients] Count={ingredients.Count()}");

            if (AllAvailableMaterials == null || AllAvailableMaterials.Count == 0)
            {
                Debug.WriteLine("[InitializeRecipeIngredients] AllAvailableMaterials EMPTY");
                return;
            }

            foreach (var ingVM in ingredients)
            {
                Debug.WriteLine($"[InitializeRecipeIngredients] Ingredient Key={ingVM.Key}");
                ingVM.InitializeMaterials(AllAvailableMaterials);

                var mat = AllAvailableMaterials.FirstOrDefault(m => m.Key == ingVM.Key);
                if (mat != null)
                {
                    Debug.WriteLine($"[InitializeRecipeIngredients] Material matched → {mat.Name}");
                    ingVM.SetSelectedMaterialSilent(mat);
                }
                else
                {
                    Debug.WriteLine("[InitializeRecipeIngredients] Material NOT FOUND");
                }
            }
        }


        // --- Autosave ---
        private void OnItemFieldChanged(ItemNodeVM item, string fieldName)
        {
            _saveDebouncer.Debounce(350, async ct =>
            {
                await SaveItemFieldAsync(item, fieldName);
            });
        }

        private async Task SaveItemFieldAsync(ItemNodeVM item, string fieldName)
        {
            Debug.WriteLine($"[Autosave] START → Field={fieldName}");
            Debug.WriteLine($"[Autosave] CraftingRecipe={item.CraftingRecipe?.Key ?? "NULL"}");
            Debug.WriteLine($"[Autosave] CraftingWorkbenchKey={item.CraftingWorkbenchKey}");
            Debug.WriteLine($"[Autosave] TemperRecipe={item.TemperRecipe?.Key ?? "NULL"}");
            Debug.WriteLine($"[Autosave] TemperWorkbenchKey={item.TemperWorkbenchKey}");

            await _saveRequestService.SaveAsync(new SaveRequest(item, fieldName));

            Debug.WriteLine($"[Autosave] END → Field={fieldName}");
        }

    }
}
