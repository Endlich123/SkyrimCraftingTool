using SkyrimCraftingTool.Model;
using SkyrimCraftingTool.Services;
using System.Threading.Tasks;
using System.Windows.Input;

namespace SkyrimCraftingTool.ViewModel
{
    public class MainWindowVM : ViewModelBase
    {
        // Services (created once, shared across all views)
        private readonly ItemDBHandler _itemDB = new();
        private readonly FileDBHandler _fileDB = new();
        private readonly FormIDDBHandler _formIDDB = new();

        // Persistent ViewModels
        public MainContentVM ContentVM { get; }
        public EnchantmentMenuVM EnchantVM { get; }
        public NpcMenuVM NpcVM { get; }
        public TemplateTabVM TemplateVM { get; }

        // The group editor (docs/NPC-Gruppen-Plan.md). Its own view model rather than a pane of
        // NpcMenuVM: the NPC tab edits one record, this one edits a filter set, and sharing state
        // between the two would mean a selection in one silently changing what the other patches.
        public NpcGroupVM NpcGroupsVM { get; }
        public PresetsConfigVM PresetsVM { get; }
        public SettingsVM SettingsVM { get; }

        // Current view
        private object _currentView;
        public object CurrentView
        {
            get => _currentView;
            set
            {
                if (SetProperty(ref _currentView, value))
                {
                    OnPropertyChanged(nameof(IsMainContentActive));
                    OnPropertyChanged(nameof(IsEnchantmentActive));
                    OnPropertyChanged(nameof(IsPresetsActive));
                    OnPropertyChanged(nameof(IsNpcActive));
                    OnPropertyChanged(nameof(IsTemplatesActive));
                    OnPropertyChanged(nameof(IsNpcGroupsActive));
                    OnPropertyChanged(nameof(IsSettingsActive));
                }
            }
        }

        // Active status of the nav tabs, for the button highlight in MainWindow.xaml
        public bool IsMainContentActive => CurrentView == ContentVM;
        public bool IsEnchantmentActive => CurrentView == EnchantVM;
        public bool IsPresetsActive => CurrentView == PresetsVM;
        public bool IsNpcActive => CurrentView == NpcVM;
        public bool IsTemplatesActive => CurrentView == TemplateVM;
        public bool IsNpcGroupsActive => CurrentView == NpcGroupsVM;
        public bool IsSettingsActive => CurrentView == SettingsVM;

        // Non-blocking issue collector, shown in the status strip at the bottom of MainWindow.
        public IssueService Issues => IssueHub.Current;
        public ICommand ClearIssuesCommand { get; }

        // Commands
        public ICommand OpenMainContentCommand { get; }
        public ICommand OpenEnchantmentMenuCommand { get; }
        public ICommand OpenNpcMenuCommand { get; }
        public ICommand OpenTemplatesCommand { get; }
        public ICommand OpenNpcGroupsCommand { get; }
        public ICommand OpenPresetsConfigCommand { get; }
        public ICommand OpenSettingsCommand { get; }

        // Log viewer + bug-report generator. Offline by design: it builds the report locally and
        // hands it over via clipboard or file. A modding tool making network calls would rightly
        // be treated as suspect, and a text file does the job just as well.
        public ICommand OpenLogViewerCommand { get; }

        public MainWindowVM()
        {
            // ViewModels persistent erzeugen
            var itemService = new Services.Adapters.ItemServiceAdapter(_itemDB);
            var fileService = new Services.Adapters.FileServiceAdapter(_fileDB);
            var formIdService = new Services.Adapters.FormIdServiceAdapter(_formIDDB);
            var enchantmentService = new Services.Adapters.EnchantmentServiceAdapter(_itemDB);
            var importExportService = new Services.Adapters.ImportExportServiceAdapter(_itemDB);

            // shared services
            var keywordService = new Services.KeywordService();
            var cacheManager = new Services.CacheManager(itemService, formIdService);

            ContentVM = new MainContentVM(itemService, fileService, formIdService, cacheManager, null, keywordService, importExportService);
            EnchantVM = new EnchantmentMenuVM(_itemDB, keywordService, new List<PluginInfo>(), enchantmentService, cacheManager, importExportService);
            // The edit store writes straight to item.db rather than through the item save pipeline -
            // see the note on NpcEditStore for why NPCs need no debouncer.
            var npcDbPath = System.IO.Path.Combine(
                Model.GlobalState.Tool.InputFolder, "Item", "item.db");

            NpcVM = new NpcMenuVM(
                itemService,
                new Services.NpcEditStore(npcDbPath),
                // The keyword catalogue the item tab already builds - one list, both tabs.
                () => ContentVM?.AllAvailableKeywords ?? new List<Model.FormIDRecord>());

            // The Templates tab, over the SAME view model: a template is an NPC record, and 1.073 of
            // the 1.372 templates in the load order are NPCs this tab already holds. One instance
            // means one set of records and one editor - an edit on either tab is the edit the other
            // one shows.
            TemplateVM = new TemplateTabVM(NpcVM);

            // Same database file as the NPC edit store, a different set of tables: the group tables
            // hold predicates, not records, and nothing in them is written by a scan.
            NpcGroupsVM = new NpcGroupVM(itemService, new Services.NpcGroupStore(npcDbPath));

            // Following the template link moves to the Templates tab. Without this the editor
            // changed while the NPC tree kept showing the record that was open before, which looks
            // exactly like a button that does nothing.
            NpcVM.TemplateOpened += () => CurrentView = TemplateVM;

            // "Jump to enchantment" on an item (Prio 9). View first, then the target - same order as
            // the template link above, and for the same reason: switching afterwards would leave the
            // user looking at the item tab while the other tab silently changed underneath.
            ContentVM.EnchantmentJumpRequested += key =>
            {
                CurrentView = EnchantVM;
                EnchantVM.ShowEnchantment(key);
            };

            PresetsVM = new PresetsConfigVM(ContentVM);
            SettingsVM = new SettingsVM(ContentVM);

            // EnchantmentMenuVM builds its tree from _itemDB at construction time, before any scan
            // has run (the DB is empty/missing then) — refresh it once real data exists, on both the
            // initial auto-load and every subsequent rescan.
            ContentVM.DataLoaded += () => EnchantVM.RefreshData(ContentVM.ActivePlugins);
            ContentVM.DataLoaded += () => PresetsVM.RefreshReferenceData();
            // A scan rewrites the NPC tables, so whatever the tab is holding is stale. Dropped
            // rather than re-read: the user is usually not on that tab when a scan finishes.
            ContentVM.DataLoaded += () => NpcVM.Invalidate();
            // The group tables survive a scan - a predicate is not scanned data - but the MEMBERS a
            // predicate reaches change with it, which is the whole point of the rescan diff
            // (docs/NPC-Gruppen-Plan.md section 8). So the tab drops what it is holding and resolves
            // again on the next visit.
            ContentVM.DataLoaded += () => NpcGroupsVM.Invalidate();
            ContentVM.DataLoaded += () =>
            {
                IssueHub.Current.Clear("scan");
                IssueHub.Current.Report(new AppIssue(
                    AppIssueSeverity.Info,
                    $"Scan complete - {ContentVM.ActivePlugins.Count} plugins, " +
                    $"{ContentVM.ArmorCache.Count + ContentVM.WeaponCache.Count} items.",
                    Category: "scan"));
            };

            ClearIssuesCommand = new RelayCommand(() => Issues.Clear());

            // Commands
            OpenMainContentCommand = new RelayCommand(() =>
            {
                // Picks up any presets created/edited while the user was on the Presets tab.
                ContentVM.RefreshAvailablePresets();
                CurrentView = ContentVM;
            });
            OpenEnchantmentMenuCommand = new RelayCommand(() => CurrentView = EnchantVM);
            OpenNpcMenuCommand = new RelayCommand(() =>
            {
                // First visit reads the NPC tables; later visits are free.
                NpcVM.EnsureLoaded();
                CurrentView = NpcVM;
            });
            OpenTemplatesCommand = new RelayCommand(() =>
            {
                // Same load as the NPC tab, because it IS the NPC tab data.
                NpcVM.EnsureLoaded();
                CurrentView = TemplateVM;
            });
            OpenNpcGroupsCommand = new RelayCommand(() =>
            {
                // Reads the NPC tables on the first visit, like the NPC tab.
                NpcGroupsVM.EnsureLoaded();
                CurrentView = NpcGroupsVM;
            });
            OpenPresetsConfigCommand = new RelayCommand(() => CurrentView = PresetsVM);
            OpenSettingsCommand = new RelayCommand(() => CurrentView = SettingsVM);

            OpenLogViewerCommand = new RelayCommand(() =>
            {
                var window = new View.LogViewerWindow
                {
                    Owner = System.Windows.Application.Current?.MainWindow,
                };
                window.ShowDialog();
            });

            // Startansicht
            CurrentView = ContentVM;
        }

        // Called from MainWindow.Closing so a still-debounced autosave (350ms window) is written
        // out before the process exits, rather than silently lost.
        public async Task FlushAllPendingSavesAsync()
        {
            await ContentVM.FlushPendingSavesAsync();
            await PresetsVM.FlushPendingSavesAsync();
            await EnchantVM.FlushPendingSavesAsync();
        }
    }
}
