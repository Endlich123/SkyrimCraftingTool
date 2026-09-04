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
        public PresetsConfigVM PresetsVM { get; }

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
                }
            }
        }

        // Active status of the nav tabs, for the button highlight in MainWindow.xaml
        public bool IsMainContentActive => CurrentView == ContentVM;
        public bool IsEnchantmentActive => CurrentView == EnchantVM;
        public bool IsPresetsActive => CurrentView == PresetsVM;

        // Non-blocking issue collector, shown in the status strip at the bottom of MainWindow.
        public IssueService Issues => IssueHub.Current;
        public ICommand ClearIssuesCommand { get; }

        // Commands
        public ICommand OpenMainContentCommand { get; }
        public ICommand OpenEnchantmentMenuCommand { get; }
        public ICommand OpenPresetsConfigCommand { get; }

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
            PresetsVM = new PresetsConfigVM(ContentVM);

            // EnchantmentMenuVM builds its tree from _itemDB at construction time, before any scan
            // has run (the DB is empty/missing then) — refresh it once real data exists, on both the
            // initial auto-load and every subsequent rescan.
            ContentVM.DataLoaded += () => EnchantVM.RefreshData(ContentVM.ActivePlugins);
            ContentVM.DataLoaded += () => PresetsVM.RefreshReferenceData();
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
            OpenPresetsConfigCommand = new RelayCommand(() => CurrentView = PresetsVM);

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
