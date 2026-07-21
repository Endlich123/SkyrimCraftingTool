using SkyrimCraftingTool.Model;
using System.Collections.Generic;
using System.ComponentModel;
using System.Windows.Data;

namespace SkyrimCraftingTool.ViewModel
{
    public class IngredientEntryVM : ViewModelBase
    {
        private readonly ItemNodeVM _parentItem;
        private readonly bool _isTemper;

        public IngredientEntryVM(ItemNodeVM parentItem, bool isTemper = false)
        {
            _parentItem = parentItem;
            _isTemper = isTemper;
        }

        private string _key;
        private string _materialName;
        private int _count;
        private FormIDRecord _selectedMaterial;
        private string _searchText;
        private ICollectionView _localMaterialsView;

        public string Key
        {
            get => _key;
            set
            {
                if (SetProperty(ref _key, value))
                    NotifyParent();
            }
        }

        public string MaterialName
        {
            get => _materialName;
            set
            {
                if (SetProperty(ref _materialName, value))
                    NotifyParent();
            }
        }

        public int Count
        {
            get => _count;
            set
            {
                if (SetProperty(ref _count, value))
                    NotifyParent();
            }
        }

        public string SearchText
        {
            get => _searchText;
            set
            {
                if (SetProperty(ref _searchText, value))
                {
                    _localMaterialsView?.Refresh();
                }
            }
        }

        public ICollectionView LocalMaterialsView
        {
            get => _localMaterialsView;
            private set
            {
                _localMaterialsView = value;
                OnPropertyChanged(nameof(LocalMaterialsView));
            }
        }


        public FormIDRecord SelectedMaterial
        {
            get => _selectedMaterial;
            set
            {
                if (SetProperty(ref _selectedMaterial, value) && value != null)
                {
                    Key = value.Key;
                    MaterialName = value.Name;

                    _searchText = value.Name;
                    OnPropertyChanged(nameof(SearchText));

                    NotifyParent();
                }
            }
        }

        private void NotifyParent()
        {
            if (_parentItem.IsLoading)
                return;

            if (_isTemper)
                _parentItem.NotifyFieldChanged(nameof(ItemNodeVM.TemperIngredients));
            else
                _parentItem.NotifyFieldChanged(nameof(ItemNodeVM.CraftingIngredients));
        }

        public void SetSelectedMaterialSilent(FormIDRecord value)
        {
            _selectedMaterial = value;
            _key = value?.Key;
            _materialName = value?.Name;

            OnPropertyChanged(nameof(SelectedMaterial));
            OnPropertyChanged(nameof(Key));
            OnPropertyChanged(nameof(MaterialName));
            OnPropertyChanged(nameof(SearchText));
        }

        public void InitializeMaterials(List<FormIDRecord> allMaterials)
        {
            if (allMaterials == null) return;

            LocalMaterialsView = new ListCollectionView(allMaterials);

            LocalMaterialsView.Filter = obj =>
            {
                if (string.IsNullOrWhiteSpace(SearchText) ||
                    (SelectedMaterial != null && SearchText == SelectedMaterial.Name))
                    return true;

                var mat = obj as FormIDRecord;
                if (mat == null) return false;

                return mat.Name.Contains(SearchText, System.StringComparison.OrdinalIgnoreCase);
            };
        }
    }
}
