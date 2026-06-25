using SkyrimCraftingTool.Model;
using System.ComponentModel;
using System.Windows.Data;

namespace SkyrimCraftingTool.ViewModel
{
    public class IngredientEntryVM : ViewModelBase
    {
        private string _key;
        private string _materialName;
        private int _count;
        private FormIDRecord _selectedMaterial;
        private string _searchText;
        private ICollectionView _localMaterialsView;
        private bool _isVisible = true;
        public bool IsVisible
        {
            get => _isVisible;
            set => SetProperty(ref _isVisible, value);
        }

        private bool _isExpanded;
        public bool IsExpanded
        {
            get => _isExpanded;
            set => SetProperty(ref _isExpanded, value);
        }


        public string Key { get => _key; set => SetProperty(ref _key, value); }
        public string MaterialName { get => _materialName; set => SetProperty(ref _materialName, value); }
        public int Count { get => _count; set => SetProperty(ref _count, value); }

        // Der Suchtext für GENAU DIESE Zeile
        public string SearchText
        {
            get => _searchText;
            set
            {
                if (SetProperty(ref _searchText, value))
                {
                    // Erzwingt, dass der Filter-Delegat (oben definiert) neu ausgeführt wird
                    _localMaterialsView?.Refresh();
                    // Optional: Öffnet die Liste beim Tippen, falls sie zugegangen ist
                    OnPropertyChanged(nameof(LocalMaterialsView));
                }
            }
        }


        // Die gefilterte Liste für GENAU DIESE Zeile
        public ICollectionView LocalMaterialsView => _localMaterialsView;

        public FormIDRecord SelectedMaterial
        {
            get => _selectedMaterial;
            set
            {
                if (SetProperty(ref _selectedMaterial, value) && value != null)
                {
                    Key = value.Key;
                    MaterialName = value.Name;
                    // Wenn ein Material gewählt wurde, setzen wir den Suchtext auf den Namen
                    _searchText = value.Name;
                    OnPropertyChanged(nameof(SearchText));
                }
            }
        }

        // Initialisierung mit der globalen Materialliste
        public void InitializeMaterials(List<FormIDRecord> allMaterials)
        {
            if (allMaterials == null) return;

            // WICHTIG: Wir erstellen eine NEUE Instanz der View, 
            // damit jede Zeile ihren eigenen Filter-Zustand hat!
            _localMaterialsView = new ListCollectionView(allMaterials);

            _localMaterialsView.Filter = obj =>
            {
                // 1. Wenn kein Suchtext da ist oder er exakt dem Namen des gewählten Items entspricht: Alles zeigen
                if (string.IsNullOrWhiteSpace(SearchText) || (SelectedMaterial != null && SearchText == SelectedMaterial.Name))
                    return true;

                var mat = obj as FormIDRecord;
                if (mat == null) return false;

                // 2. Filtern nach Namen
                return mat.Name.Contains(SearchText, StringComparison.OrdinalIgnoreCase);
            };
        }

    }
}
