using SkyrimCraftingTool.Model;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;

namespace SkyrimCraftingTool.ViewModel
{
    public class MainContentVM : ViewModelBase
    {
        // --- Services ---
        private readonly ItemDBHandler _handler;
        private readonly FileDBHandler _fileHandler;
        private readonly FormIDDBHandler _formidhandler;

        private readonly Debouncer _debouncer = new();
        private readonly BackgroundFilterRunner<string, List<PluginNodeVM>> _filterRunner = new();

        // Flag
        private bool _isInitialized = false;
        private bool _isInitializing = false;
        private Task _initializationTask;


        // --- Logging helper ---
        private void Log(string msg)
        {
            Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {msg}");
        }

        // --- Tree Selection ---
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

        // --- Tree Search ---
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

        // --- Tree Collections ---
        public ObservableCollection<PluginNodeVM> ModItemsTree { get; } = new();
        public ObservableCollection<PluginNodeVM> FilteredTree { get; } = new();

        // --- Caches ---
        public Dictionary<string, ArmorRecord> ArmorCache { get; } = new();
        public Dictionary<string, WeaponRecord> WeaponCache { get; } = new();
        public Dictionary<string, FormIDRecord> KeywordCache { get; } = new();
        public Dictionary<string, FormIDRecord> MaterialCache { get; } = new();
        public Dictionary<string, List<COBJRecord>> RecipeCacheByCreatedItem { get; } = new();

        public List<FormIDRecord> AllAvailableMaterials { get; private set; }

        // --- Commands ---
        //public RelayCommand ExpandAllCommand { get; }
        public RelayCommand CollapseAllCommand { get; }
        public RelayCommand ScanModsCommand { get; }

        // --- Constructor ---
        public MainContentVM()
            : this(new ItemDBHandler(), new FileDBHandler(), new FormIDDBHandler())
        {
        }

        public MainContentVM(ItemDBHandler handler, FileDBHandler fileHandler, FormIDDBHandler formidhandler)
        {
            _handler = handler;
            _fileHandler = fileHandler;
            _formidhandler = formidhandler;

            //ExpandAllCommand = new RelayCommand(() => ExpandAll(true));
            CollapseAllCommand = new RelayCommand(() => ExpandAll(false));
            ScanModsCommand = new RelayCommand(async () => await ExecuteFullScanAsync());

            //LoadInitialData();
        }

        // --- Initial Load ---
        public Task LoadInitialDataAsync()
        {
            if (_isInitialized)
                return _initializationTask;

            _isInitialized = true;
            _isInitializing = true;

            _initializationTask = Task.Run(async () =>
            {
                Log("LoadInitialData START");

                var activePlugins = _fileHandler.GetActivePlugins();

                if (!File.Exists(GlobalState.Tool.InputFolder + "/Item/item.db") ||
                    !File.Exists(GlobalState.Tool.InputFolder + "/FormID/formid.db"))
                {
                    Log("DB files missing.");
                    _isInitializing = false;
                    return;
                }

                var sw = Stopwatch.StartNew();
                await Task.Run(() => BuildCachesFromDB(activePlugins));
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



        // --- Expand/Collapse ---
        private void ExpandAll(bool expand)
        {
            foreach (var p in FilteredTree)
            {
                p.IsExpanded = expand;
                foreach (var c in p.Categories)
                    c.IsExpanded = expand;
            }
        }

        // --- Scan Mods ---
        private async Task ExecuteFullScanAsync()
        {
            Log("ExecuteFullScanAsync START");

            List<PluginInfo> activePlugins = null;

            var sw = Stopwatch.StartNew();
            await Task.Run(() =>
            {
                _fileHandler.RefreshPluginDatabase();
                activePlugins = _fileHandler.GetActivePlugins();
                _formidhandler.PutIntoDataBank(activePlugins);
                _handler.PutIntoDataBank(activePlugins);

                BuildCachesFromDB(activePlugins);
            });
            Log($"Full scan + caches DONE in {sw.ElapsedMilliseconds} ms");

            sw.Restart();
            await BuildTreeFromCacheAsync(activePlugins);
            Log($"Tree rebuild DONE in {sw.ElapsedMilliseconds} ms");

            System.Windows.MessageBox.Show("DB updated!", "System", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);

            Log("ExecuteFullScanAsync END");
        }

        // --- Build Tree (async) ---
        private async Task BuildTreeFromCacheAsync(List<PluginInfo> activePlugins)
        {
            Log("BuildTreeFromCacheAsync START");

            var tempData = await Task.Run(() =>
            {
                var armorByPlugin = ArmorCache.Values
                    .GroupBy(a => a.Key.Split('|')[0].ToLowerInvariant())
                    .ToDictionary(g => g.Key, g => g.ToList());

                var weaponByPlugin = WeaponCache.Values
                    .GroupBy(w => w.Key.Split('|')[0].ToLowerInvariant())
                    .ToDictionary(g => g.Key, g => g.ToList());

                return new
                {
                    Armor = armorByPlugin,
                    Weapons = weaponByPlugin
                };
            });

            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                ModItemsTree.Clear();

                foreach (var plugin in activePlugins)
                {
                    var pluginKey = plugin.FileName.ToLowerInvariant();
                    var pluginNode = new PluginNodeVM { PluginName = plugin.FileName };

                    var armorCategory = new CategoryNodeVM { CategoryName = "Armor" };
                    var weaponCategory = new CategoryNodeVM { CategoryName = "Weapons" };

                    if (tempData.Armor.TryGetValue(pluginKey, out var armorList))
                    {
                        foreach (var armor in armorList)
                            armorCategory.Items.Add(new ItemNodeVM(armor));
                    }

                    if (tempData.Weapons.TryGetValue(pluginKey, out var weaponList))
                    {
                        foreach (var weapon in weaponList)
                            weaponCategory.Items.Add(new ItemNodeVM(weapon));
                    }

                    if (armorCategory.Items.Count > 0)
                        pluginNode.Categories.Add(armorCategory);

                    if (weaponCategory.Items.Count > 0)
                        pluginNode.Categories.Add(weaponCategory);

                    if (pluginNode.Categories.Count > 0)
                        ModItemsTree.Add(pluginNode);
                }

                if (!_isInitializing)
                    ApplyFilter(_treeSearchText);
            });

            Log("BuildTreeFromCacheAsync END");
        }



        // --- Filter ---
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

        // --- Cache Loading ---
        private void BuildCachesFromDB(List<PluginInfo> activePlugins)
        {
            Log("BuildCachesFromDB START");

            var armorLocal = new Dictionary<string, ArmorRecord>();
            var weaponLocal = new Dictionary<string, WeaponRecord>();
            var recipeLocal = new Dictionary<string, List<COBJRecord>>();

            Parallel.ForEach(activePlugins, plugin =>
            {
                foreach (var armor in _handler.GetArmorByPlugin(plugin.FileName))
                    lock (armorLocal) armorLocal[armor.Key] = armor;

                foreach (var weapon in _handler.GetWeaponsByPlugin(plugin.FileName))
                    lock (weaponLocal) weaponLocal[weapon.Key] = weapon;

                foreach (var recipe in _handler.GetCOBJByPlugin(plugin.FileName))
                {
                    lock (recipeLocal)
                    {
                        if (!recipeLocal.TryGetValue(recipe.CreatedItemKey, out var list))
                            recipeLocal[recipe.CreatedItemKey] = list = new List<COBJRecord>();
                        list.Add(recipe);
                    }
                }
            });

            ArmorCache.Clear();
            WeaponCache.Clear();
            RecipeCacheByCreatedItem.Clear();

            foreach (var kv in armorLocal)
                ArmorCache[kv.Key] = kv.Value;

            foreach (var kv in weaponLocal)
                WeaponCache[kv.Key] = kv.Value;

            foreach (var kv in recipeLocal)
                RecipeCacheByCreatedItem[kv.Key] = kv.Value;

            KeywordCache.Clear();
            MaterialCache.Clear();

            foreach (var kw in _formidhandler.SearchByType("Keyword"))
                KeywordCache[kw.Key] = kw;

            foreach (var mat in _formidhandler.SearchByType("Material"))
                MaterialCache[mat.Key] = mat;

            Log("BuildCachesFromDB END");
        }


        // --- Item Details ---
        private void LoadSelectedItemDetails(ItemNodeVM item)
        {
            Log($"LoadSelectedItemDetails: {item.Key}");

            // 1. Armor/Weapon + Keywords
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

            // 2. Materials
            if (AllAvailableMaterials == null)
                AllAvailableMaterials = MaterialCache.Values.OrderBy(m => m.Name).ToList();

            // 3. Recipes
            item.CraftingRecipe = null;
            item.TemperRecipe = null;

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
                    item.CraftingRecipe = new COBJNodeVM(craftRec, _formidhandler);
                    InitializeRecipeIngredients(item.CraftingRecipe.Ingredients);
                }

                if (temperRec != null)
                {
                    item.TemperRecipe = new COBJNodeVM(temperRec, _formidhandler);
                    InitializeRecipeIngredients(item.TemperRecipe.Ingredients);
                    item.TemperIngredients = item.TemperRecipe.Ingredients;
                }
                else
                {
                    item.TemperIngredients = new ObservableCollection<IngredientEntryVM>();
                }
            }
            else
            {
                item.TemperIngredients = new ObservableCollection<IngredientEntryVM>();
            }

            // 4. Keywords
            item.AllKeywords.Clear();

            // CollectionViews erzeugen
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

            // 5. Filter
            item.RefreshKeywords();
        }


        private void InitializeRecipeIngredients(IEnumerable<IngredientEntryVM> ingredients)
        {
            if (AllAvailableMaterials == null)
                return;

            foreach (var ingVM in ingredients)
            {
                ingVM.InitializeMaterials(AllAvailableMaterials);
                ingVM.SelectedMaterial = AllAvailableMaterials.FirstOrDefault(m => m.Key == ingVM.Key);
            }
        }
    }
}
