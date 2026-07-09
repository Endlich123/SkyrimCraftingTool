using SkyrimCraftingTool.Model;
using System.Windows.Input;

namespace SkyrimCraftingTool.ViewModel
{
    public class MainWindowVM : ViewModelBase
    {
        // Services (einmal erzeugt, für alle Views)
        private readonly ItemDBHandler _itemDB = new();
        private readonly FileDBHandler _fileDB = new();
        private readonly FormIDDBHandler _formIDDB = new();

        // Persistente ViewModels
        public MainContentVM ContentVM { get; }
        public EnchantmentMenuVM EnchantVM { get; }
        public CategoriesConfigVM CategoriesVM { get; }

        // Aktuelle Ansicht
        private object _currentView;
        public object CurrentView
        {
            get => _currentView;
            set => SetProperty(ref _currentView, value);
        }

        // Commands
        public ICommand OpenMainContentCommand { get; }
        public ICommand OpenEnchantmentMenuCommand { get; }
        public ICommand OpenCategoriesConfigCommand { get; }

        public MainWindowVM()
        {
            // ViewModels persistent erzeugen
            var itemService = new Services.Adapters.ItemServiceAdapter(_itemDB);
            var fileService = new Services.Adapters.FileServiceAdapter(_fileDB);
            var formIdService = new Services.Adapters.FormIdServiceAdapter(_formIDDB);

            ContentVM = new MainContentVM(itemService, fileService, formIdService);
            EnchantVM = new EnchantmentMenuVM(_itemDB);
            CategoriesVM = new CategoriesConfigVM();

            // Commands
            OpenMainContentCommand = new RelayCommand(() => CurrentView = ContentVM);
            OpenEnchantmentMenuCommand = new RelayCommand(() => CurrentView = EnchantVM);
            OpenCategoriesConfigCommand = new RelayCommand(() => CurrentView = CategoriesVM);

            // Startansicht
            CurrentView = ContentVM;
        }
    }
}
