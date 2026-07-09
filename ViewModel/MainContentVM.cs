using Microsoft.Data.Sqlite;
using SkyrimCraftingTool.Model;
using SkyrimCraftingTool.Services;
using SkyrimCraftingTool.Services.Adapters;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;

namespace SkyrimCraftingTool.ViewModel
{
    public class MainContentVM : ViewModelBase
    {
        // --- Services ---
        private ItemDBHandler? _handler;
        private FileDBHandler? _fileHandler;
        private FormIDDBHandler? _formidhandler;

        private readonly IItemService _itemService;
        private readonly IFileService _fileService;
        private readonly IFormIdService _formIdService;
        private readonly ICacheManager _cacheManager;
        private readonly IKeywordService _keywordService;

        private readonly Debouncer _debouncer = new();
        private readonly BackgroundFilterRunner<string, List<PluginNodeVM>> _filterRunner = new();

        // service helpers (wrap concrete handlers when services not provided)
        private IItemService ItemService => _itemService ?? (_handler != null ? new ItemServiceAdapter(_handler) : throw new System.InvalidOperationException("No IItemService provided"));
        private IFileService FileService => _fileService ?? (_fileHandler != null ? new FileServiceAdapter(_fileHandler) : throw new System.InvalidOperationException("No IFileService provided"));
        private IFormIdService FormIdService => _formIdService ?? (_formidhandler != null ? new FormIdServiceAdapter(_formidhandler) : throw new System.InvalidOperationException("No IFormIdService provided"));

        // >>> AUTOSAVE
        private readonly Debouncer _saveDebouncer = new();

        private bool _isInitialized = false;
        private bool _isInitializing = false;
        private Task _initializationTask;

        private void Log(string msg)
        {
            Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {msg}");
        }

        private object _selectedNode;
        public object SelectedNode
        {
            get => _selectedNode;
            set
            {
                Debug.WriteLine(">>> SelectedNode SET: " + value);
                if (SetProperty(ref _selectedNode, value))
                {
                    Log($"SelectedNode = {value?.GetType().Name}");
                    if (value is ItemNodeVM item)
                        LoadSelectedItemDetails(item);
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

        public ObservableCollection<PluginNodeVM> ModItemsTree { get; } = new();
        public ObservableCollection<PluginNodeVM> FilteredTree { get; } = new();

        public Dictionary<string, ArmorRecord> ArmorCache { get; } = new();
        public Dictionary<string, WeaponRecord> WeaponCache { get; } = new();
        public Dictionary<string, FormIDRecord> KeywordCache { get; } = new();
        public Dictionary<string, FormIDRecord> MaterialCache { get; } = new();
        public Dictionary<string, List<COBJRecord>> RecipeCacheByCreatedItem { get; } = new();

        public List<FormIDRecord> AllAvailableMaterials { get; private set; }

        public System.Collections.ObjectModel.ObservableCollection<KeywordSelectionVM> GlobalKeywords => _keywordService.GlobalKeywords;

        public RelayCommand CollapseAllCommand { get; }
        public RelayCommand ScanModsCommand { get; }

        public MainContentVM()
            : this(new ItemDBHandler(), new FileDBHandler(), new FormIDDBHandler())
        {
        }

        // New constructor accepting service abstractions
        public MainContentVM(IItemService itemService, IFileService fileService, IFormIdService formIdService,
            ICacheManager cacheManager = null, ITreeBuilderService treeBuilder = null, IKeywordService keywordService = null)
        {
            // assign provided services; do not create concrete handlers here to allow test stubs
            _itemService = itemService;
            _fileService = fileService;
            _formIdService = formIdService;

            _cacheManager = cacheManager ?? new CacheManager(_itemService, _formIdService);
            _treeBuilder = treeBuilder ?? new TreeBuilderService();
            _keywordService = keywordService ?? new KeywordService();

            CollapseAllCommand = new RelayCommand(() => ExpandAll(false));
            ScanModsCommand = new RelayCommand(async () => await ExecuteFullScanAsync());
        }

        public MainContentVM(ItemDBHandler handler, FileDBHandler fileHandler, FormIDDBHandler formidhandler)
        {
            _handler = handler;
            _fileHandler = fileHandler;
            _formidhandler = formidhandler;

            // Cache manager wraps item + formid access to build in-memory caches
            _cacheManager = new CacheManager(new ItemServiceAdapter(_handler), new FormIdServiceAdapter(_formidhandler));

            // Keyword service holds the global keywords collection used by the UI
            _keywordService = new KeywordService();

            CollapseAllCommand = new RelayCommand(() => ExpandAll(false));
            ScanModsCommand = new RelayCommand(async () => await ExecuteFullScanAsync());
        }

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
                activePlugins.Add(new PluginInfo
                {
                    FileName = "SkyrimCraftingTool.esp",
                });

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
            });
            Log($"Full scan + caches DONE in {sw.ElapsedMilliseconds} ms");

            sw.Restart();
            await BuildTreeFromCacheAsync(activePlugins);
            Log($"Tree rebuild DONE in {sw.ElapsedMilliseconds} ms");

            System.Windows.MessageBox.Show("DB updated!", "System", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);

            Log("ExecuteFullScanAsync END");
        }

        private readonly ITreeBuilderService _treeBuilder = new TreeBuilderService();

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

        private void ApplyCacheSnapshot(CacheSnapshot snapshot)
        {
            if (snapshot == null) return;

            Log("ApplyCacheSnapshot START");

            ArmorCache.Clear();
            WeaponCache.Clear();
            RecipeCacheByCreatedItem.Clear();

            foreach (var kv in snapshot.Armor)
                ArmorCache[kv.Key] = kv.Value;

            foreach (var kv in snapshot.Weapons)
                WeaponCache[kv.Key] = kv.Value;

            foreach (var kv in snapshot.RecipesByCreatedItem)
                RecipeCacheByCreatedItem[kv.Key] = kv.Value;

            KeywordCache.Clear();
            MaterialCache.Clear();

            foreach (var kw in snapshot.Keywords)
                KeywordCache[kw.Key] = kw;

            foreach (var mat in snapshot.Materials)
                MaterialCache[mat.Key] = mat;

            AllAvailableMaterials = MaterialCache.Values.OrderBy(m => m.Name).ToList();
            Debug.WriteLine($"[ApplyCacheSnapshot] AllAvailableMaterials = {AllAvailableMaterials.Count}");

            Log("ApplyCacheSnapshot END");
        }

        private void LoadSelectedItemDetails(ItemNodeVM item)
        {
            Log($"LoadSelectedItemDetails: {item.Key}");

            item.IsLoading = true; // 🔹 Start: Ladephase

            ArmorRecord armor = null;
            WeaponRecord weapon = null;
            List<string> activeKeywords = null;

            if (ArmorCache.TryGetValue(item.Key, out armor))
            {
                item.ApplyArmorRecord(armor);
                activeKeywords = armor.Keywords;
            }
            else if (WeaponCache.TryGetValue(item.Key, out weapon))
            {
                item.ApplyWeaponRecord(weapon);
                activeKeywords = weapon.Keywords;
            }

            activeKeywords ??= new List<string>();

            AllAvailableMaterials = MaterialCache.Values.OrderBy(m => m.Name).ToList();

            if (RecipeCacheByCreatedItem.TryGetValue(item.Key, out var recipes))
            {
                var craftRec = recipes.FirstOrDefault(r =>
                    r.WorkbenchKeywordKey != "Skyrim.esm|088108" &&
                    r.WorkbenchKeywordKey != "Skyrim.esm|0ADB78");

                var temperRec = recipes.FirstOrDefault(r =>
                    r.WorkbenchKeywordKey == "Skyrim.esm|088108" ||
                    r.WorkbenchKeywordKey == "Skyrim.esm|0ADB78");

                if (craftRec != null)
                {
                    System.Windows.Application.Current.Dispatcher.Invoke(() =>
                    {
                        item.CraftingRecipe = new COBJNodeVM(item, craftRec, FormIdService, false);
                        InitializeRecipeIngredients(item.CraftingRecipe.Ingredients);
                    });
                }
                else
                {
                    item.CraftingRecipe = null;
                    item.CraftingIngredients = new ObservableCollection<IngredientEntryVM>();
                }

                if (temperRec != null)
                {
                    System.Windows.Application.Current.Dispatcher.Invoke(() =>
                    {
                        item.TemperRecipe = new COBJNodeVM(item, temperRec, FormIdService, true);
                        InitializeRecipeIngredients(item.TemperRecipe.Ingredients);
                        item.TemperIngredients = item.TemperRecipe.Ingredients;
                    });
                }
                else
                {
                    item.TemperRecipe = null;
                    item.TemperIngredients = new ObservableCollection<IngredientEntryVM>();
                }
            }
            else
            {
                item.CraftingRecipe = null;
                item.CraftingIngredients = new ObservableCollection<IngredientEntryVM>();
                item.TemperRecipe = null;
                item.TemperIngredients = new ObservableCollection<IngredientEntryVM>();
            }

            // Populate per-item keyword list (used for item-local filtering)
            item.AllKeywords.Clear();

            foreach (var kw in KeywordCache.Values.OrderBy(k => k.Name))
            {
                item.AllKeywords.Add(new KeywordSelectionVM
                {
                    Key = kw.Key,
                    Name = kw.Name,
                    IsSelected = activeKeywords.Contains(kw.Key),
                    ParentItem = item
                });
            }

            // Update global keyword selection (UI "All" list) to reflect selected item's keywords
            _keywordService.SetSelectionForActiveItem(activeKeywords);

            item.RefreshKeywords();

            item.FieldChanged -= OnItemFieldChanged;
            item.FieldChanged += OnItemFieldChanged;

            item.IsLoading = false;
        }

        private void InitializeRecipeIngredients(IEnumerable<IngredientEntryVM> ingredients)
        {
            if (AllAvailableMaterials == null || AllAvailableMaterials.Count == 0)
            {
                Debug.WriteLine("[InitializeRecipeIngredients] AllAvailableMaterials empty");
                return;
            }

            foreach (var ingVM in ingredients)
            {
                ingVM.InitializeMaterials(AllAvailableMaterials);

                var mat = AllAvailableMaterials.FirstOrDefault(m => m.Key == ingVM.Key);
                if (mat != null)
                {
                    // Silent setzen, KEIN Autosave
                    ingVM.SetSelectedMaterialSilent(mat);
                }
            }
        }

        private void OnItemFieldChanged(ItemNodeVM item, string fieldName)
        {
            _saveDebouncer.Debounce(350, async ct =>
            {
                await SaveItemFieldAsync(item, fieldName);
            });
        }

        private async Task SaveItemFieldAsync(ItemNodeVM item, string fieldName)
        {
            try
            {
                await Task.Run(() =>
                {
                    if (item.IsArmor)
                    {
                        switch (fieldName)
                        {
                            case nameof(ItemNodeVM.Name):
                                ItemDBHandler.UpdateArmorName(item.Key, item.Name);
                                if (ArmorCache.TryGetValue(item.Key, out var rec1))
                                    rec1.Name = item.Name;
                                break;

                            case nameof(ItemNodeVM.Value):
                                ItemDBHandler.UpdateArmorValue(item.Key, item.Value);
                                if (ArmorCache.TryGetValue(item.Key, out var rec2))
                                    rec2.Value = item.Value;
                                break;

                            case nameof(ItemNodeVM.Weight):
                                ItemDBHandler.UpdateArmorWeight(item.Key, item.Weight);
                                if (ArmorCache.TryGetValue(item.Key, out var rec3))
                                    rec3.Weight = item.Weight;
                                break;

                            case nameof(ItemNodeVM.ArmorRating):
                                ItemDBHandler.UpdateArmorRating(item.Key, item.ArmorRating);
                                if (ArmorCache.TryGetValue(item.Key, out var rec4))
                                    rec4.ArmorRating = item.ArmorRating;
                                break;

                            case nameof(ItemNodeVM.BodySlotMask):
                                Debug.WriteLine($"[SAVE] BodySlotMask = {item.BodySlotMask}");
                                ItemDBHandler.UpdateArmorBodySlotMask(item.Key, item.BodySlotMask);
                                if (ArmorCache.TryGetValue(item.Key, out var rec5))
                                    rec5.BodySlotMask = item.BodySlotMask;
                                break;

                            case nameof(ItemNodeVM.SelectedKeywords):
                                ItemDBHandler.UpdateArmorKeywords(item.Key, item.SelectedKeywords);
                                if (ArmorCache.TryGetValue(item.Key, out var rec6))
                                    rec6.Keywords = new List<string>(item.SelectedKeywords.Select(k => k.Key));
                                break;
                        }
                    }

                    if (item.IsWeapon)
                    {
                        switch (fieldName)
                        {
                            case nameof(ItemNodeVM.Name):
                                ItemDBHandler.UpdateWeaponName(item.Key, item.Name);
                                if (WeaponCache.TryGetValue(item.Key, out var rec1))
                                    rec1.Name = item.Name;
                                break;

                            case nameof(ItemNodeVM.Value):
                                ItemDBHandler.UpdateWeaponValue(item.Key, item.Value);
                                if (WeaponCache.TryGetValue(item.Key, out var rec2))
                                    rec2.Value = item.Value;
                                break;

                            case nameof(ItemNodeVM.Weight):
                                ItemDBHandler.UpdateWeaponWeight(item.Key, item.Weight);
                                if (WeaponCache.TryGetValue(item.Key, out var rec3))
                                    rec3.Weight = item.Weight;
                                break;

                            case nameof(ItemNodeVM.Damage):
                                ItemDBHandler.UpdateWeaponDamage(item.Key, item.Damage);
                                if (WeaponCache.TryGetValue(item.Key, out var rec4))
                                    rec4.Damage = item.Damage;
                                break;

                            case nameof(ItemNodeVM.Speed):
                                Debug.WriteLine($"[SAVE] Speed = {item.Speed}");
                                ItemDBHandler.UpdateWeaponSpeed(item.Key, item.Speed);
                                if (WeaponCache.TryGetValue(item.Key, out var rec5))
                                    rec5.Speed = item.Speed;
                                break;

                            case nameof(ItemNodeVM.Reach):
                                Debug.WriteLine($"[SAVE] Reach = {item.Reach}");
                                ItemDBHandler.UpdateWeaponReach(item.Key, item.Reach);
                                if (WeaponCache.TryGetValue(item.Key, out var rec6))
                                    rec6.Reach = item.Reach;
                                break;

                            case nameof(ItemNodeVM.Stagger):
                                Debug.WriteLine($"[SAVE] Stagger = {item.Stagger}");
                                ItemDBHandler.UpdateWeaponStagger(item.Key, item.Stagger);
                                if (WeaponCache.TryGetValue(item.Key, out var rec7))
                                    rec7.Stagger = item.Stagger;
                                break;

                            case nameof(ItemNodeVM.SelectedKeywords):
                                ItemDBHandler.UpdateWeaponKeywords(item.Key, item.SelectedKeywords);
                                if (WeaponCache.TryGetValue(item.Key, out var rec8))
                                    rec8.Keywords = new List<string>(item.SelectedKeywords.Select(k => k.Key));
                                break;
                        }
                    }

                    // --------------------
                    // Crafting Recipe
                    // --------------------
                    if (fieldName == nameof(ItemNodeVM.CraftingIngredients))
                    {
                        if (!item.HasCraftingRecipe)
                        {
                            var newRec = ItemService.CreateNewCOBJRecordForItem(item, isTemper: false);

                            if (!RecipeCacheByCreatedItem.TryGetValue(item.Key, out var list))
                            {
                                list = new List<COBJRecord>();
                                RecipeCacheByCreatedItem[item.Key] = list;
                            }
                            list.Add(newRec);

                            item.CraftingRecipe = new COBJNodeVM(item, newRec, FormIdService, false);
                        }

                        var rec = item.CraftingRecipe.Record;

                        // 🔹 Sicherstellen, dass das Recipe wirklich zu diesem Item gehört
                        if (rec.CreatedItemKey != item.Key)
                        {
                            Debug.WriteLine($"[CRITICAL] Crafting COBJ mismatch: rec.CreatedItemKey={rec.CreatedItemKey}, item.Key={item.Key}. Fixing.");
                            rec.CreatedItemKey = item.Key;
                        }

                        rec.IngredientKeys = item.CraftingIngredients
                            .Select(i => $"{i.Key}*{i.Count}")
                            .ToList();

                        ItemService.SaveCOBJ(rec);
                    }

                    // --------------------
                    // Temper Recipe
                    // --------------------
                    if (fieldName == nameof(ItemNodeVM.TemperIngredients))
                    {
                        if (!item.HasTemperRecipe)
                        {
                            var newRec = ItemService.CreateNewCOBJRecordForItem(item, isTemper: true);

                            if (!RecipeCacheByCreatedItem.TryGetValue(item.Key, out var list))
                            {
                                list = new List<COBJRecord>();
                                RecipeCacheByCreatedItem[item.Key] = list;
                            }
                            list.Add(newRec);

                            item.TemperRecipe = new COBJNodeVM(item, newRec, FormIdService, true);
                        }

                        var rec = item.TemperRecipe.Record;

                        // 🔹 Sicherstellen, dass das Recipe wirklich zu diesem Item gehört
                        if (rec.CreatedItemKey != item.Key)
                        {
                            Debug.WriteLine($"[CRITICAL] Temper COBJ mismatch: rec.CreatedItemKey={rec.CreatedItemKey}, item.Key={item.Key}. Fixing.");
                            rec.CreatedItemKey = item.Key;
                        }

                        rec.IngredientKeys = item.TemperIngredients
                            .Select(i => $"{i.Key}*{i.Count}")
                            .ToList();

                        ItemService.SaveCOBJ(rec);
                    }


                    Debug.WriteLine($"[AutoSave] {item.Key} → {fieldName} gespeichert.");
                });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AutoSave ERROR] {ex.Message}");
            }
        }
    }
}
