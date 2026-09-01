using Microsoft.Data.Sqlite;
using SkyrimCraftingTool.Model;
using SkyrimCraftingTool.Services;
using SkyrimCraftingTool.Services.Adapters;
using SkyrimCraftingTool.Services.PatchGen;
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
        private readonly IImportExportService _importExportService;

        // --- Helpers ---
        private readonly Debouncer _debouncer = new();
        private readonly BackgroundFilterRunner<string, List<PluginNodeVM>> _filterRunner = new();
        // Shared per-VM autosave debouncer. Only ever holds ONE pending action - bulk / multi-target
        // saves must bypass it (see PersistFieldAsync). Flushed on app shutdown via
        // FlushPendingSavesAsync so a last-second edit isn't lost.
        private readonly Debouncer _saveDebouncer = new();

        public Task FlushPendingSavesAsync() => _saveDebouncer.FlushAsync();

        // Resolves "Plugin|FormID" keys against the current scan - rebuilt in ApplyCacheSnapshot.
        // Consumed (soon) by dead-reference marking, quest validation, orphaned-edit detection.
        private readonly ReferenceResolver _referenceResolver = new();
        public IReferenceResolver References => _referenceResolver;

        // --- Service helpers ---
        internal IItemService ItemService => _itemService;
        internal IFileService FileService => _fileService;
        internal IFormIdService FormIdService => _formIdService;
        internal IKeywordService KeywordService => _keywordService;
        internal IImportExportService ImportExportService => _importExportService;

        // --- State ---
        private bool _isInitialized;
        private bool _isInitializing;
        private Task _initializationTask;

        // Raised after the item DB has (re)loaded successfully — both from the initial auto-load
        // and from a full rescan. Other VMs built from the same DB (e.g. EnchantmentMenuVM, whose
        // tree/plugin list otherwise only ever reflected what existed at app-startup, before any
        // scan had run) subscribe to this to know when to refresh themselves.
        public event Action DataLoaded;
        public List<PluginInfo> ActivePlugins { get; private set; } = new();

        private object _selectedNode;
        private ItemNodeVM? _subscribedItemForContainerSelection;
        public object SelectedNode
        {
            get => _selectedNode;
            set
            {
                if (SetProperty(ref _selectedNode, value))
                {
                    if (value is ItemNodeVM item)
                    {
                        EnsureItemHydrated(item);

                        if (_subscribedItemForContainerSelection != null)
                            _subscribedItemForContainerSelection.ContainerSelection.SelectedContainers.CollectionChanged -= OnSelectedContainersChanged;

                        _subscribedItemForContainerSelection = item;
                        _subscribedItemForContainerSelection.ContainerSelection.SelectedContainers.CollectionChanged += OnSelectedContainersChanged;

                        UpdateAllContainerSelectionFlags(item);
                    }
                    else
                    {
                        if (_subscribedItemForContainerSelection != null)
                        {
                            _subscribedItemForContainerSelection.ContainerSelection.SelectedContainers.CollectionChanged -= OnSelectedContainersChanged;
                            _subscribedItemForContainerSelection = null;
                        }

                        UpdateAllContainerSelectionFlags(null);
                    }
                }
            }
        }


        // --- Multi-selection (item level only) ---
        // TreeView itself only supports single-select - item-level clicks get intercepted in
        // MainContentView (PreviewMouseLeftButtonDown, style of CategoryNodeVM.Items) and evaluated
        // here, instead of using TreeView.SelectedItem/SelectedItemChanged.
        private ItemNodeVM? _selectionAnchor;
        public ObservableCollection<ItemNodeVM> SelectedItems { get; } = new();

        // Drives the visibility of MultiSelectDetailView in MainContentView.xaml.
        public bool IsMultiSelectActive => SelectedItems.Count > 1;

        public void HandleItemNodeClick(ItemNodeVM clicked, bool ctrl, bool shift)
        {
            if (shift && _selectionAnchor != null)
            {
                var flat = GetFlatItemNodes();
                int anchorIndex = flat.IndexOf(_selectionAnchor);
                int clickedIndex = flat.IndexOf(clicked);

                if (anchorIndex >= 0 && clickedIndex >= 0)
                {
                    if (!ctrl)
                    {
                        foreach (var item in SelectedItems.ToList())
                            SetItemSelected(item, false);
                    }

                    int lo = Math.Min(anchorIndex, clickedIndex);
                    int hi = Math.Max(anchorIndex, clickedIndex);
                    for (int i = lo; i <= hi; i++)
                        SetItemSelected(flat[i], true);
                }
            }
            else if (ctrl)
            {
                SetItemSelected(clicked, !clicked.IsSelected);
                _selectionAnchor = clicked;
            }
            else
            {
                foreach (var item in SelectedItems.ToList())
                    if (item != clicked)
                        SetItemSelected(item, false);

                SetItemSelected(clicked, true);
                _selectionAnchor = clicked;
            }

            // The single-detail panel stays active for exactly one selection, as before;
            // at 0 or 2+ items it's the (MultiSelectDetailView's) domain.
            SelectedNode = SelectedItems.Count == 1 ? SelectedItems[0] : null;
        }

        // Called from MainContentView's native TreeView.SelectedItemChanged handler whenever the user
        // navigates via single-select semantics (plugin/category clicks, arrow keys) after having
        // multi-selected items - see the comment there for why this needs to run first.
        internal void ClearMultiSelection()
        {
            foreach (var item in SelectedItems.ToList())
                SetItemSelected(item, false);
            _selectionAnchor = null;
        }

        private void SetItemSelected(ItemNodeVM item, bool value)
        {
            if (item.IsSelected == value)
                return;

            item.IsSelected = value;

            if (value)
            {
                // Items only get lazily "hydrated" (Keywords/Crafting/Temper/autosave wiring), so far
                // exclusively on single-select via SelectedNode. For multi-selection this needs to
                // happen the same way here, otherwise AllKeywords is empty and Crafting/TemperRecipe
                // stays null even though the item actually already has a saved recipe. HasLoadedDetails
                // avoids an item getting fully re-hydrated again on every repeated click/re-selection
                // (noticeable on larger Shift range-selections).
                if (!item.HasLoadedDetails)
                    LoadSelectedItemDetails(item);
                SelectedItems.Add(item);
            }
            else
            {
                SelectedItems.Remove(item);
            }

            OnPropertyChanged(nameof(IsMultiSelectActive));
        }

        // Bulk-apply operations (MultiSelectDetailVM) must not go through the normal
        // NotifyFieldChanged path: OnItemFieldChanged debounces via a single, shared debouncer
        // (_saveDebouncer) - with rapid successive changes across multiple items, each new call would
        // cancel the previous one, so only the last-changed item would actually get saved. A direct,
        // awaited call bypasses that.
        internal Task PersistFieldAsync(ItemNodeVM item, string fieldName) =>
            _saveRequestService.SaveAsync(new SaveRequest(item, fieldName));

        private MultiSelectDetailVM? _multiSelectVM;
        public MultiSelectDetailVM MultiSelectVM
        {
            get => _multiSelectVM ??= new MultiSelectDetailVM(this);
            private set => SetProperty(ref _multiSelectVM, value);
        }

        // MultiSelectDetailVM's keyword/container lists are a snapshot taken at creation time -
        // rebuild after a (re)scan so they reflect the current AllAvailableKeywords/AllContainers.
        private void RebuildMultiSelectVM() => MultiSelectVM = new MultiSelectDetailVM(this);

        // Order = tree order (plugin -> category -> item), not the actually visible Y position:
        // IsExpanded on plugin/category nodes isn't currently bound to the TreeViewItem (Collapse-All
        // only works because ApplyFilter rebuilds the containers entirely), so there's no reliable
        // "currently collapsed" state to query. Because of this, a Shift range can also select
        // currently-collapsed items.
        private List<ItemNodeVM> GetFlatItemNodes()
        {
            var result = new List<ItemNodeVM>();
            foreach (var plugin in FilteredTree)
                foreach (var category in plugin.Categories)
                    result.AddRange(category.Items);

            return result;
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

        // --- Dirty-state (edited items) ---
        private bool _showOnlyEditedItems;
        public bool ShowOnlyEditedItems
        {
            get => _showOnlyEditedItems;
            set
            {
                if (SetProperty(ref _showOnlyEditedItems, value))
                    ApplyFilterDebounced(_treeSearchText);
            }
        }

        private int _editedItemCount;
        public int EditedItemCount
        {
            get => _editedItemCount;
            private set => SetProperty(ref _editedItemCount, value);
        }

        // Called by ItemNodeVM the first time an item picks up a live edit.
        internal void NotifyItemBecameEdited()
        {
            EditedItemCount++;
        }

        // Called by ItemNodeVM after a reset (section or all) when its edited state actually flipped.
        internal void NotifyItemEditedStateChanged(bool edited)
        {
            if (edited) EditedItemCount++;
            else if (EditedItemCount > 0) EditedItemCount--;
            ApplyFilterDebounced(_treeSearchText); // keep the "only edited" view in sync
        }

        private IEnumerable<ItemNodeVM> GetAllItemNodes()
        {
            foreach (var plugin in ModItemsTree)
                foreach (var cat in plugin.Categories)
                    foreach (var item in cat.Items)
                        yield return item;
        }

        // Item keys that have persisted edits, from the DB's IsEdited* columns. A COBJ edit is
        // attributed to the item the recipe creates (via RecipeCacheByCreatedItem). Best-effort.
        private HashSet<string> GetEditedItemKeys()
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            try
            {
                var edited = _importExportService?.GetEditedItems(ExportScope.All) ?? new List<EditedItemDto>();

                foreach (var e in edited)
                    if (e.Table is "Armor" or "Weapons")
                        set.Add(e.Key);

                var editedCobj = edited.Where(e => e.Table == "COBJ").Select(e => e.Key)
                    .ToHashSet(StringComparer.Ordinal);
                if (editedCobj.Count > 0)
                    foreach (var kv in RecipeCacheByCreatedItem)
                        if (kv.Value.Any(r => editedCobj.Contains(r.Key)))
                            set.Add(kv.Key);
            }
            catch (Exception ex)
            {
                AppLogger.LogError("Reading edited item keys for the tree badges failed", ex);
            }
            return set;
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

        // --- Presets (Output/Presets/*.json, see PresetFileStore) ---
        public List<PresetFile> AllPresets { get; private set; } = new();

        // Presets aren't scan-derived (see PresetFileStore) so they're loaded once at construction;
        // call again (e.g. when leaving the Presets tab, see MainWindowVM.OpenMainContentCommand) to
        // pick up edits made there.
        public void RefreshAvailablePresets()
        {
            var presets = new List<PresetFile>();
            foreach (var path in Services.PresetFileStore.FindAllPresetFiles())
            {
                try
                {
                    var file = Services.PresetFileStore.ReadPreset(path);
                    if (file != null) presets.Add(file);
                }
                catch (Exception ex)
                {
                    AppLogger.LogError($"Failed to load preset file '{path}'", ex);
                }
            }
            AllPresets = presets.OrderBy(p => p.PresetName).ToList();
            OnPropertyChanged(nameof(AllPresets));
        }
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

        // --- Generate Patch options (session-only, like the container toggles) ---

        private bool _splitPatchPerPlugin;
        public bool SplitPatchPerPlugin
        {
            get => _splitPatchPerPlugin;
            set => SetProperty(ref _splitPatchPerPlugin, value);
        }

        // When set, the patch is written next to the app (SKSE\... and the .esp at the tool root)
        // so the tool folder itself works as an MO2 mod. Otherwise it goes under Output\.
        private bool _patchIntoAppFolder;
        public bool PatchIntoAppFolder
        {
            get => _patchIntoAppFolder;
            set => SetProperty(ref _patchIntoAppFolder, value);
        }


        // limited list (e.g. only 20 containers)
        public ObservableCollection<ContainerEntryVM> LimitedContainerVMs { get; } = new();

        // filtered list for the UI
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
        public RelayCommand ExportAllCommand { get; }
        public RelayCommand ImportAllCommand { get; }
        public RelayCommand GeneratePatchCommand { get; }

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
            IKeywordService keywordService = null,
            IImportExportService importExportService = null)
        {
            _itemService = itemService;
            _fileService = fileService;
            _formIdService = formIdService;
            _importExportService = importExportService;

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

            ExportAllCommand = new RelayCommand(ExportAll);
            ImportAllCommand = new RelayCommand(async () => await ImportAllAsync());
            GeneratePatchCommand = new RelayCommand(async () => await GeneratePatchAsync());

            RefreshAvailablePresets();
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
                try
                {
                    await ReloadFromDatabaseCoreAsync();
                }
                finally
                {
                    _isInitializing = false;
                }
            });

            return _initializationTask;
        }

        // Re-runnable version of the initial DB->cache->tree load, for callers that need to refresh
        // after the DB changed without a full plugin rescan (e.g. after applying an Import). Unlike
        // LoadInitialDataAsync, this has no one-shot guard — every call actually reloads.
        public Task RefreshFromDatabaseAsync() => Task.Run(ReloadFromDatabaseCoreAsync);

        // --- Import/Export ---
        //
        // No file dialogs: everything lives under a fixed, predictable folder structure
        // (Output/Exports/<Plugin>/<Item>.json, see ExportFileStore) so Export and Import always
        // agree on where a given item's data is without the user having to pick a location.

        private void ExportAll()
        {
            List<EditedItemDto> items;
            try
            {
                items = _importExportService.GetEditedItems(ExportScope.All);
            }
            catch (Exception ex)
            {
                AppLogger.LogError("MainContentVM.ExportAll failed", ex);
                System.Windows.MessageBox.Show($"Export failed:{Environment.NewLine}{ex.Message}",
                    "Error", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
                return;
            }

            if (items.Count == 0)
            {
                System.Windows.MessageBox.Show("No edited items found - nothing to export.",
                    "Export", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
                return;
            }

            foreach (var item in items)
            {
                var path = ExportFileStore.GetItemFilePath(item.Key, item.DisplayName);
                ExportFileStore.WriteFile(path, new ExportFile { ExportedAt = ItemDBHandler.NowIso(), Items = new List<EditedItemDto> { item } });
            }

            System.Windows.MessageBox.Show(
                $"{items.Count} item(s) exported to{Environment.NewLine}{ExportFileStore.ExportsRoot}",
                "Export Successful", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
        }

        // Writes the full patch:
        //  - SkyPatcher INIs for every edited armor/weapon field
        //    (Output\SKSE\Plugins\SkyPatcher\{armor,weapon}\zzz_SkyrimCraftingTool\<Plugin>.esp.ini)
        //  - one SkyrimCraftingTool.esp for created / edited COBJ recipes
        // See docs/PatchGenerator-Plan.md.
        private async Task GeneratePatchAsync()
        {
            await FlushPendingSavesAsync();

            var options = new PatchGenOptions
            {
                CobjSplitMode = SplitPatchPerPlugin
                    ? PatchCobjSplitMode.PerSourcePlugin
                    : PatchCobjSplitMode.Global,
                OutputRoot = PatchIntoAppFolder
                    ? GlobalState.Tool.ModFolder
                    : GlobalState.Tool.OutputFolder,
            };
            PatchGenReport report;
            try
            {
                var svc = new PatchGeneratorService(references: References);
                report = await Task.Run(() => svc.Generate(options));
            }
            catch (Exception ex)
            {
                AppLogger.LogError("MainContentVM.GeneratePatchAsync failed", ex);
                System.Windows.MessageBox.Show($"Patch generation failed:{Environment.NewLine}{ex.Message}",
                    "Error", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
                return;
            }

            if (!report.AnythingGenerated)
            {
                System.Windows.MessageBox.Show("No edited armor, weapon or recipe found - nothing to patch.",
                    "Generate Patch", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
                return;
            }

            var msg = new System.Text.StringBuilder();
            msg.AppendLine(report.Summary);
            msg.AppendLine();
            foreach (var f in report.WrittenFiles)
                msg.AppendLine("  " + MakeRelative(options.OutputRoot, f));
            if (report.CobjMasters.Count > 0)
            {
                msg.AppendLine();
                msg.AppendLine($"ESP masters: {string.Join(", ", report.CobjMasters)}");
            }
            if (report.Warnings.Count > 0)
            {
                msg.AppendLine();
                msg.AppendLine($"Warnings ({report.Warnings.Count}):");
                foreach (var w in report.Warnings.Take(15))
                    msg.AppendLine("  " + w);
                if (report.Warnings.Count > 15)
                    msg.AppendLine($"  ... and {report.Warnings.Count - 15} more (see log).");
            }
            msg.AppendLine();
            msg.Append("Open the output folder?");

            var choice = System.Windows.MessageBox.Show(msg.ToString(), "Patch generated",
                System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Information);

            if (choice == System.Windows.MessageBoxResult.Yes)
            {
                try
                {
                    Process.Start(new ProcessStartInfo("explorer.exe", $"\"{options.OutputRoot}\"") { UseShellExecute = true });
                }
                catch (Exception ex)
                {
                    AppLogger.LogError("Opening the patch output folder failed", ex);
                }
            }
        }

        private static string MakeRelative(string root, string path)
        {
            try { return Path.GetRelativePath(root, path); }
            catch { return path; }
        }

        private async Task ImportAllAsync()
        {
            var files = ExportFileStore.FindAllFiles();
            if (files.Count == 0)
            {
                System.Windows.MessageBox.Show(
                    $"No export files found under{Environment.NewLine}{ExportFileStore.ExportsRoot}",
                    "Import", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
                return;
            }

            var allItems = new List<EditedItemDto>();
            foreach (var path in files)
            {
                try
                {
                    var file = ExportFileStore.ReadFile(path);
                    if (file?.Items != null)
                        allItems.AddRange(file.Items);
                }
                catch (Exception ex)
                {
                    AppLogger.LogError($"MainContentVM.ImportAllAsync: failed reading {path}", ex);
                }
            }

            await RunImportAsync(allItems);
        }

        // Shared Preview -> conflict resolution -> Apply -> refresh -> summary flow, used by both
        // ImportAllCommand and ItemNodeVM.ImportItemCommand (via Main.RunImportAsync).
        public async Task RunImportAsync(List<EditedItemDto> items)
        {
            if (items == null || items.Count == 0)
            {
                System.Windows.MessageBox.Show("No importable items found.", "Import",
                    System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                return;
            }

            ImportPlan plan;
            try
            {
                plan = _importExportService.PreviewImport(items);
            }
            catch (Exception ex)
            {
                AppLogger.LogError("MainContentVM.RunImportAsync (PreviewImport) failed", ex);
                System.Windows.MessageBox.Show($"Import failed:{Environment.NewLine}{ex.Message}",
                    "Error", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
                return;
            }

            var useFileVersion = new HashSet<string>();
            if (plan.Conflicts.Count > 0)
            {
                var resolved = View.ImportConflictWindow.ShowDialog(plan.Conflicts);
                if (resolved == null)
                    return; // user cancelled — abort, leaving conflicting items untouched
                useFileVersion = resolved;
            }

            ImportResult result;
            try
            {
                result = _importExportService.ApplyImport(plan, useFileVersion);
            }
            catch (Exception ex)
            {
                AppLogger.LogError("MainContentVM.RunImportAsync (ApplyImport) failed", ex);
                System.Windows.MessageBox.Show($"Import failed:{Environment.NewLine}{ex.Message}",
                    "Error", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
                return;
            }

            try
            {
                await RefreshFromDatabaseAsync();
            }
            catch (Exception ex)
            {
                AppLogger.LogError("MainContentVM.RunImportAsync (refresh) failed", ex);
            }

            var summary =
                $"Updated: {result.Applied}{Environment.NewLine}" +
                $"Skipped (identical): {result.SkippedEqual}{Environment.NewLine}" +
                $"Skipped (not present locally): {result.SkippedMissing.Count}{Environment.NewLine}" +
                $"Conflicts - used import: {result.ConflictsUsedFile}{Environment.NewLine}" +
                $"Conflicts - kept local: {result.ConflictsKeptLocal}";

            System.Windows.MessageBox.Show(summary, "Import Complete",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
        }

        private async Task ReloadFromDatabaseCoreAsync()
        {
            try
            {
                Log("LoadInitialData START");

                var activePlugins = FileService.GetActivePlugins();

                if (!File.Exists(GlobalState.Tool.InputFolder + "/Item/item.db") ||
                    !File.Exists(GlobalState.Tool.InputFolder + "/FormID/formid.db"))
                {
                    Log("DB files missing.");
                    return;
                }

                activePlugins.Add(new PluginInfo { FileName = KeyFactory.UserPluginName });
                ActivePlugins = activePlugins;

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

                // Must happen before the ApplyFilter call below — ApplyFilter no-ops while
                // _isInitializing is still true (guards against filtering mid-load).
                _isInitializing = false;

                System.Windows.Application.Current.Dispatcher.Invoke(() =>
                {
                    ApplyFilter(_treeSearchText);
                    RebuildMultiSelectVM();
                    DataLoaded?.Invoke();
                });

                Log("LoadInitialData END");
            }
            catch (Exception ex)
            {
                AppLogger.LogError("LoadInitialDataAsync failed", ex);
                System.Windows.Application.Current.Dispatcher.Invoke(() =>
                {
                    System.Windows.MessageBox.Show(
                        $"Failed to load data:{Environment.NewLine}{ex.Message}{Environment.NewLine}{Environment.NewLine}Details were saved to Logs\\error.log.",
                        "Load Error", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
                });
            }
        }

        private void ExpandAll(bool expand)
        {
            foreach (var p in ModItemsTree)
            {
                p.IsExpanded = expand;
                foreach (var c in p.Categories)
                    c.IsExpanded = expand;
            }
            ApplyFilter(_treeSearchText);

        }

        // --- Full rescan ---
        internal sealed record ScanReport(
            int Added, int Removed, int TotalAfter, int EditsStillActive,
            System.Collections.Generic.IReadOnlyList<EditedItemDto> OrphanedEdits);

        internal static ScanReport BuildScanReport(HashSet<string> before, HashSet<string> after, List<EditedItemDto> editedBefore)
        {
            int added = after.Count(k => !before.Contains(k));
            int removed = before.Count(k => !after.Contains(k));

            var itemEdits = editedBefore.Where(e => e.Table is "Armor" or "Weapons").ToList();
            int stillActive = itemEdits.Count(e => after.Contains(e.Key));
            var orphans = itemEdits.Where(e => !after.Contains(e.Key)).ToList();

            return new ScanReport(added, removed, after.Count, stillActive, orphans);
        }

        private static string FormatScanReport(ScanReport r)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("Scan complete.");
            sb.AppendLine();
            sb.AppendLine($"Items:   +{r.Added}   -{r.Removed}     ({r.TotalAfter} total)");
            sb.AppendLine($"Your edits still applying: {r.EditsStillActive}");
            if (r.OrphanedEdits.Count > 0)
                sb.AppendLine($"⚠ {r.OrphanedEdits.Count} edit(s) no longer match any item - see the warnings strip.");
            return sb.ToString();
        }

        private async Task ExecuteFullScanAsync()
        {
            Log("ExecuteFullScanAsync START");

            // Rescan-Guard: flush a just-made edit (still inside the autosave debounce) to disk
            // before we read/rebuild from the DB.
            await FlushPendingSavesAsync();

            List<PluginInfo> activePlugins = null;
            ScanReport? report = null;

            try
            {
                var sw = Stopwatch.StartNew();
                await Task.Run(() =>
                {
                    // Previous scan's active item set (in-memory, no DB access). editedRows are read
                    // AFTER ItemService.PutIntoDataBank below - it's what first creates the DB folder,
                    // and it keeps the IsEdited* shadow columns (removed items just go Active=0), so
                    // GetEditedItems still returns orphaned edits afterwards.
                    var beforeKeys = new HashSet<string>(ArmorCache.Keys.Concat(WeaponCache.Keys), StringComparer.Ordinal);

                    var step = Stopwatch.StartNew();

                    FileService.RefreshPluginDatabase();
                    activePlugins = FileService.GetActivePlugins();
                    ActivePlugins = activePlugins;
                    Log($"  [scan] RefreshPluginDatabase+GetActivePlugins ({activePlugins.Count} plugins): {step.ElapsedMilliseconds} ms");

                    // Load-order trace, disabled — re-enable if a similar investigation is needed again.
                    //Log("  [scan] Load order: " + string.Join(" > ", activePlugins.Select((p, idx) => $"{idx}:{p.FileName}")));

                    step.Restart();
                    FormIdService.PutIntoDataBank(activePlugins);
                    Log($"  [scan] FormIdService.PutIntoDataBank: {step.ElapsedMilliseconds} ms");

                    step.Restart();
                    ItemService.PutIntoDataBank(activePlugins);
                    Log($"  [scan] ItemService.PutIntoDataBank: {step.ElapsedMilliseconds} ms");

                    // Added only after the DB writes above, same as LoadInitialDataAsync — this
                    // pseudo-plugin never corresponds to a real file, so it must never reach
                    // FormIdService/ItemService.PutIntoDataBank (which try to parse actual plugin
                    // files), but cache/tree building needs to know about it so user-created COBJ
                    // recipes stay visible after a rescan.
                    activePlugins.Add(new PluginInfo { FileName = KeyFactory.UserPluginName });

                    step.Restart();
                    var snapshot = _cacheManager.BuildCachesFromDB(activePlugins);
                    Log($"  [scan] BuildCachesFromDB: {step.ElapsedMilliseconds} ms");

                    step.Restart();
                    ApplyCacheSnapshot(snapshot);
                    _keywordService.InitializeFrom(snapshot.Keywords);
                    Log($"  [scan] ApplyCacheSnapshot+InitializeFrom: {step.ElapsedMilliseconds} ms");

                    // The merge report is a nice-to-have - never let it abort the scan.
                    try
                    {
                        var editedRows = _importExportService?.GetEditedItems(ExportScope.All) ?? new List<EditedItemDto>();
                        var afterKeys = new HashSet<string>(ArmorCache.Keys.Concat(WeaponCache.Keys), StringComparer.Ordinal);
                        report = BuildScanReport(beforeKeys, afterKeys, editedRows);
                    }
                    catch (Exception reportEx)
                    {
                        AppLogger.LogError("Rescan merge report failed (scan itself is fine)", reportEx);
                    }
                });
                Log($"Full scan + caches DONE in {sw.ElapsedMilliseconds} ms");

                sw.Restart();
                await BuildTreeFromCacheAsync(activePlugins);
                Log($"Tree rebuild DONE in {sw.ElapsedMilliseconds} ms");

                RebuildMultiSelectVM();
                DataLoaded?.Invoke();

                // After DataLoaded: MainWindowVM's handler clears category "scan" and posts the
                // scan-complete note, so orphan warnings have to go in after it. Guarded - the scan
                // already succeeded, a report hiccup must not surface as "Scan failed".
                try
                {
                    if (report != null)
                    {
                        foreach (var o in report.OrphanedEdits)
                        {
                            var label = string.IsNullOrEmpty(o.DisplayName) ? o.Key : o.DisplayName;
                            IssueHub.Current.Report(new AppIssue(
                                AppIssueSeverity.Warning,
                                $"Edited {o.Table} '{label}' no longer matches any item in the load order.",
                                Context: o.Key + " - the edit stays in the DB but won't be applied anywhere.",
                                Category: "scan"));
                        }

                        System.Windows.MessageBox.Show(FormatScanReport(report), "Scan complete",
                            System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
                    }
                    else
                    {
                        System.Windows.MessageBox.Show("DB updated!", "System", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
                    }
                }
                catch (Exception reportEx)
                {
                    AppLogger.LogError("Rescan report/dialog failed (scan itself is fine)", reportEx);
                }

                Log("ExecuteFullScanAsync END");
            }
            catch (Exception ex)
            {
                AppLogger.LogError("ExecuteFullScanAsync failed", ex);
                System.Windows.MessageBox.Show(
                    $"Scan failed:{Environment.NewLine}{ex.Message}{Environment.NewLine}{Environment.NewLine}Details were saved to Logs\\error.log.",
                    "Scan Error", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            }
        }

        // --- Tree build ---
        private async Task BuildTreeFromCacheAsync(List<PluginInfo> activePlugins)
        {
            Log("BuildTreeFromCacheAsync START");

            var nodes = await Task.Run(() => _treeBuilder.BuildTreeFromCache(activePlugins, ArmorCache, WeaponCache, this));

            var editedKeys = await Task.Run(GetEditedItemKeys);

            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                ModItemsTree.Clear();
                foreach (var n in nodes)
                    ModItemsTree.Add(n);

                int edited = 0;
                foreach (var item in GetAllItemNodes())
                {
                    item.IsEdited = editedKeys.Contains(item.Key);
                    if (item.IsEdited) edited++;
                }
                EditedItemCount = edited;

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

            if (string.IsNullOrWhiteSpace(text) && !_showOnlyEditedItems)
            {
                foreach (var p in ModItemsTree)
                    FilteredTree.Add(p);
                return;
            }

            foreach (var plugin in ModItemsTree)
            {
                var filtered = plugin.FilterReference(text, _showOnlyEditedItems);
                if (filtered != null)
                {
                    filtered.IsExpanded = plugin.IsExpanded;
                    foreach (var cat in filtered.Categories)
                        cat.IsExpanded = cat.IsExpanded; 

                    FilteredTree.Add(filtered);
                }
            }
        }

        private List<PluginNodeVM> FilterOnBackground(string search, CancellationToken token)
        {
            bool onlyEdited = _showOnlyEditedItems;

            if (string.IsNullOrWhiteSpace(search) && !onlyEdited)
                return ModItemsTree.ToList();

            search = search.ToLowerInvariant();

            var result = new List<PluginNodeVM>();

            foreach (var plugin in ModItemsTree)
            {
                token.ThrowIfCancellationRequested();

                var filtered = plugin.FilterReference(search, onlyEdited);
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

            AllContainers = snapshot.Containers?.OrderBy(c => c.Name).ToList()
                ?? new List<ContainerRecord>();

            _referenceResolver.Rebuild(
                AllAvailableKeywords, AllAvailableMaterials, AllAvailableWorkbenches,
                AllAvailablePerks, AllAvailableQuests, AllContainers);

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
        // Bulk-apply paths (MultiSelectDetailVM, PluginNodeVM) operate on items that may never have
        // been individually clicked in the tree - without this, their AllKeywords/CraftingRecipe/
        // TemperRecipe/ContainerSelection would still be empty/default, so PresetApplyService would
        // wrongly think e.g. a weapon has no WeapType keyword, or that an item with a real COBJ has
        // none yet (and create a duplicate one). Call this before applying a preset to any item that
        // wasn't reached through the normal single-select path.
        internal void EnsureItemHydrated(ItemNodeVM item)
        {
            if (!item.HasLoadedDetails)
                LoadSelectedItemDetails(item);
        }

        // ItemNodeVM.CreateCraftingRecipe/CreateTemperRecipe call this the moment a brand-new recipe
        // is created (an item that had zero recipes before now has one). RecipeCacheByCreatedItem is
        // only ever bulk-populated once, from ApplyCacheSnapshot - ICacheManager.UpdateRecipe (called
        // right after, from CraftingSaveHandler/TemperSaveHandler) mutates its OWN separate
        // CacheSnapshot.RecipesByCreatedItem instead, and only an already-existing List<COBJRecord>
        // for that CreatedItemKey is actually shared by reference between the two. A CreatedItemKey
        // with no recipe yet has no such list here at all, so without this, LoadSelectedItemDetails/
        // EnsureItemHydrated would keep finding nothing under item.Key and treat the recipe as if it
        // never existed until the next full rescan repopulates this dictionary from scratch.
        internal void RegisterNewRecipe(COBJRecord rec)
        {
            if (rec == null || string.IsNullOrEmpty(rec.CreatedItemKey)) return;

            if (!RecipeCacheByCreatedItem.TryGetValue(rec.CreatedItemKey, out var list))
            {
                list = new List<COBJRecord>();
                RecipeCacheByCreatedItem[rec.CreatedItemKey] = list;
            }
            if (!list.Contains(rec))
                list.Add(rec);
        }

        private void LoadSelectedItemDetails(ItemNodeVM item)
        {
            item.IsLoading = true;

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
            item.SelectedKeywordKeys = activeKeywords.ToList();

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

            foreach (var kw in item.AllKeywords.Where(k => k.IsSelected))
                item.ApplyKeywordRules(kw);

            RegisterItemKeywordEvents(item);
            item.RefreshKeywords();

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

                    var originalCraft = ItemService.GetOriginalCOBJ(craftRec.Key);
                    if (originalCraft != null)
                    {
                        var originalCraftConditions = ItemService.GetOriginalCOBJConditions(craftRec.Key);
                        item.CaptureCraftingOriginalSnapshot(originalCraft.WorkbenchKeywordKey, originalCraft.IngredientKeys, originalCraftConditions);
                        // Original == 0 forever means this recipe has no plugin origin (see
                        // ItemDBHandler.InsertCOBJ) - keeps Reset enabled across a reload, not just in
                        // the same session it was created in (see MarkCraftingRecipeUserCreated).
                        item.MarkCraftingRecipeUserCreated(originalCraft.Original == 0);
                    }
                }
                else
                {
                    item.CraftingRecipe = null;
                    item.CraftingIngredients = new ObservableCollection<IngredientEntryVM>();
                    item.CraftingConditions = new ObservableCollection<BaseConditionViewModel>();
                    item.MarkCraftingRecipeUserCreated(false);
                }

                if (temperRec != null)
                {
                    System.Windows.Application.Current.Dispatcher.Invoke(() =>
                    {
                        item.TemperRecipe = new COBJNodeVM(item, temperRec, FormIdService, true);
                        InitializeRecipeIngredients(item.TemperRecipe.Ingredients);
                        item.TemperIngredients = item.TemperRecipe.Ingredients;
                        item.TemperConditions = item.TemperRecipe.Conditions;
                    });

                    var originalTemper = ItemService.GetOriginalCOBJ(temperRec.Key);
                    if (originalTemper != null)
                    {
                        var originalTemperConditions = ItemService.GetOriginalCOBJConditions(temperRec.Key);
                        item.CaptureTemperOriginalSnapshot(originalTemper.WorkbenchKeywordKey, originalTemper.IngredientKeys, originalTemperConditions);
                        item.MarkTemperRecipeUserCreated(originalTemper.Original == 0);
                    }
                }
                else
                {
                    item.TemperRecipe = null;
                    item.TemperIngredients = new ObservableCollection<IngredientEntryVM>();
                    item.TemperConditions = new ObservableCollection<BaseConditionViewModel>();
                    item.MarkTemperRecipeUserCreated(false);
                }
            }
            else
            {
                item.CraftingRecipe = null;
                item.CraftingIngredients = new ObservableCollection<IngredientEntryVM>();
                item.TemperRecipe = null;
                item.TemperIngredients = new ObservableCollection<IngredientEntryVM>();
                item.CraftingConditions = new ObservableCollection<BaseConditionViewModel>();
                item.TemperConditions = new ObservableCollection<BaseConditionViewModel>();
                item.MarkCraftingRecipeUserCreated(false);
                item.MarkTemperRecipeUserCreated(false);
            }

            item.FieldChanged -= OnItemFieldChanged;
            item.FieldChanged += OnItemFieldChanged;

            item.IsLoading = false;
            item.HasLoadedDetails = true;

            // Snapshot against the pristine (never-edited) base columns, not the possibly-already-
            // edited effective values ArmorCache/WeaponCache just applied above - otherwise change-
            // tracking/Reset would only ever see edits made in *this* session (see ResetItemEdits).
            if (item.IsArmor)
            {
                var originalArmor = ItemService.GetOriginalArmor(item.Key);
                if (originalArmor != null)
                    item.CaptureOriginalSnapshot(originalArmor.Name, originalArmor.Value, originalArmor.Weight,
                        originalArmor.ArmorRating, originalArmor.BodySlotMask, 0, 0, 0, 0, originalArmor.ContainerString, originalArmor.Keywords);
            }
            else
            {
                var originalWeapon = ItemService.GetOriginalWeapon(item.Key);
                if (originalWeapon != null)
                    item.CaptureOriginalSnapshot(originalWeapon.Name, originalWeapon.Value, originalWeapon.Weight,
                        0, 0, originalWeapon.Damage, originalWeapon.Speed, originalWeapon.Reach, originalWeapon.Stagger,
                        originalWeapon.ContainerString, originalWeapon.Keywords);
            }

            item.ReportRecipeIssues();
        }

        // Reverts item's Name/Cost/Weight/Armor-or-Weapon-stats/Keywords/Container back to the
        // pristine plugin-scanned values by clearing the DB's IsEdited* shadow columns for this row
        // (not just pushing the old values back through the normal edit pipeline, which would leave
        // IsEdited=1 with the shadow value merely matching the original - see the earlier, session-only
        // version of this feature). Also updates the live ArmorCache/WeaponCache entry so the reverted
        // values take effect immediately without a rescan.
        internal void ResetItemEdits(ItemNodeVM item)
        {
            if (item.IsArmor)
            {
                ItemService.ResetArmorEdits(item.Key);
                var original = ItemService.GetOriginalArmor(item.Key);
                if (original == null) return;

                _cacheManager.UpdateArmorName(item.Key, original.Name);
                _cacheManager.UpdateArmorWeight(item.Key, original.Weight);
                _cacheManager.UpdateArmorValue(item.Key, original.Value);
                _cacheManager.UpdateArmorRating(item.Key, original.ArmorRating);
                _cacheManager.UpdateArmorBodySlotMask(item.Key, original.BodySlotMask);
                _cacheManager.UpdateArmorKeywords(item.Key, original.Keywords);
                _cacheManager.UpdateArmorContainerString(item.Key, original.ContainerString);

                item.ApplyResetValues(original.Name, original.Value, original.Weight,
                    original.ArmorRating, original.BodySlotMask, 0, 0, 0, 0, original.ContainerString, original.Keywords);
            }
            else
            {
                ItemService.ResetWeaponEdits(item.Key);
                var original = ItemService.GetOriginalWeapon(item.Key);
                if (original == null) return;

                _cacheManager.UpdateWeaponName(item.Key, original.Name);
                _cacheManager.UpdateWeaponWeight(item.Key, original.Weight);
                _cacheManager.UpdateWeaponValue(item.Key, original.Value);
                _cacheManager.UpdateWeaponDamage(item.Key, original.Damage);
                _cacheManager.UpdateWeaponSpeed(item.Key, original.Speed);
                _cacheManager.UpdateWeaponReach(item.Key, original.Reach);
                _cacheManager.UpdateWeaponStagger(item.Key, original.Stagger);
                _cacheManager.UpdateWeaponKeywords(item.Key, original.Keywords);
                _cacheManager.UpdateWeaponContainerString(item.Key, original.ContainerString);

                item.ApplyResetValues(original.Name, original.Value, original.Weight,
                    0, 0, original.Damage, original.Speed, original.Reach, original.Stagger,
                    original.ContainerString, original.Keywords);
            }
        }

        // Reverts the item's Crafting/Temper recipe Workbench + Ingredients + Conditions back to the
        // pristine plugin-scanned values, same shape as ResetItemEdits above. Conditions revert via
        // ResetCOBJConditions, which restores COBJ_Conditions from the lazily-snapshotted
        // COBJ_Conditions_Original table (see Model/ItemDBHandler.cs's schema comment there).
        internal void ResetCraftingRecipeEdits(ItemNodeVM item)
        {
            if (!item.HasCraftingRecipe) return;
            var key = item.CraftingRecipe.Record.Key;

            ItemService.ResetCOBJEdits(key);
            var original = ItemService.GetOriginalCOBJ(key);
            if (original == null) return;

            // A user-created recipe (Original stays 0 forever for these, see ItemDBHandler.
            // InsertCOBJ) never existed in the plugin - ResetCOBJEdits above only cleared the shadow
            // edit columns, so "restoring" it would just bring back the empty just-created stub
            // instead of undoing the recipe entirely. Delete it outright so no orphaned COBJ row is
            // left behind to get patched into the ESP later.
            if (original.Original == 0)
            {
                ItemService.DeleteCOBJ(key);
                _cacheManager.RemoveRecipe(key);
                // See RegisterNewRecipe's comment: this item's entry here holds a plain List<COBJRecord>
                // that only RegisterNewRecipe/this method ever touch, unrelated to _cacheManager's own
                // snapshot - leaving the deleted rec behind would let the next LoadSelectedItemDetails
                // resurrect it as a phantom recipe with a Key that no longer exists in the DB.
                if (RecipeCacheByCreatedItem.TryGetValue(item.Key, out var craftingList))
                    craftingList.RemoveAll(r => r.Key == key);

                item.IsLoading = true;
                item.CraftingRecipe = null;
                item.IsLoading = false;

                item.ClearCraftingSnapshot();
                return;
            }

            ItemService.ResetCOBJConditions(key);
            var restoredConditions = ItemService.GetOriginalCOBJConditions(key);
            original.Conditions = restoredConditions;

            _cacheManager.UpdateRecipe(original);
            _cacheManager.UpdateRecipeConditions(key, restoredConditions);

            item.IsLoading = true;
            item.CraftingRecipe = new COBJNodeVM(item, original, FormIdService, false);
            item.IsLoading = false;

            InitializeRecipeIngredients(item.CraftingRecipe.Ingredients);
            item.CaptureCraftingOriginalSnapshot(original.WorkbenchKeywordKey, original.IngredientKeys, restoredConditions);
        }

        internal void ResetTemperRecipeEdits(ItemNodeVM item)
        {
            if (!item.HasTemperRecipe) return;
            var key = item.TemperRecipe.Record.Key;

            ItemService.ResetCOBJEdits(key);
            var original = ItemService.GetOriginalCOBJ(key);
            if (original == null) return;

            // See the identical guard in ResetCraftingRecipeEdits above: a user-created recipe never
            // existed in the plugin, so it must be deleted outright on reset instead of being
            // restored to its empty just-created stub.
            if (original.Original == 0)
            {
                ItemService.DeleteCOBJ(key);
                _cacheManager.RemoveRecipe(key);
                if (RecipeCacheByCreatedItem.TryGetValue(item.Key, out var temperList))
                    temperList.RemoveAll(r => r.Key == key);

                item.IsLoading = true;
                item.TemperRecipe = null;
                item.IsLoading = false;

                item.ClearTemperSnapshot();
                return;
            }

            ItemService.ResetCOBJConditions(key);
            var restoredConditions = ItemService.GetOriginalCOBJConditions(key);
            original.Conditions = restoredConditions;

            _cacheManager.UpdateRecipe(original);
            _cacheManager.UpdateRecipeConditions(key, restoredConditions);

            item.IsLoading = true;
            item.TemperRecipe = new COBJNodeVM(item, original, FormIdService, true);
            item.IsLoading = false;

            InitializeRecipeIngredients(item.TemperRecipe.Ingredients);
            item.CaptureTemperOriginalSnapshot(original.WorkbenchKeywordKey, original.IngredientKeys, restoredConditions);
        }

        private void RegisterItemKeywordEvents(ItemNodeVM item)
        {
            foreach (var kw in item.AllKeywords)
            {
                kw.PropertyChanged -= item.OnKeywordPropertyChanged;
                kw.PropertyChanged += item.OnKeywordPropertyChanged;
            }
        }

        // Wires each ingredient row's material ComboBox catalog (LocalMaterialsView) and silently
        // binds its already-known material. Called on every recipe load path AND when a recipe is
        // first created in-session (ItemNodeVM.CreateCraftingRecipe/CreateTemperRecipe) - a freshly
        // built COBJNodeVM rebuilds its IngredientEntryVMs from the record's IngredientKeys and does
        // NOT touch the material catalog, so without this call the dropdown stays empty until the
        // next app restart reloads via the load path.
        internal void InitializeRecipeIngredients(IEnumerable<IngredientEntryVM> ingredients)
        {
            if (AllAvailableMaterials == null || AllAvailableMaterials.Count == 0)
                return;

            foreach (var ingVM in ingredients)
            {
                ingVM.InitializeMaterials(AllAvailableMaterials);

                var mat = AllAvailableMaterials.FirstOrDefault(m => m.Key == ingVM.Key);
                if (mat != null)
                    ingVM.SetSelectedMaterialSilent(mat);
                else if (!string.IsNullOrEmpty(ingVM.Key))
                    ingVM.ShowUnresolvedKey(); // dead reference - show the raw key, not a blank box
            }
        }

        // --- Autosave ---
        private void OnItemFieldChanged(ItemNodeVM item, string fieldName)
        {
            _saveDebouncer.DebounceAsync(350, async ct =>
            {
                await SaveItemFieldAsync(item, fieldName);
            });
        }

        private async Task SaveItemFieldAsync(ItemNodeVM item, string fieldName)
        {
            await _saveRequestService.SaveAsync(new SaveRequest(item, fieldName));
        }

    }
}
