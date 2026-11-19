using System.Windows.Input;

namespace SkyrimCraftingTool.ViewModel
{
    public class KeywordSelectionVM : ViewModelBase
    {
        public string Key { get; set; } = "";
        public string Name { get; set; } = "";

        private bool _isSelected;
        public bool IsSelected
        {
            get => _isSelected;
            set => SetProperty(ref _isSelected, value);
        }

        private bool _isReadOnlyOverride = false;
        public bool IsReadOnly
        {
            get
            {
                // Default: WeapType keywords are always ReadOnly
                if (!string.IsNullOrEmpty(Name) && Name.StartsWith("WeapType", System.StringComparison.OrdinalIgnoreCase))
                    return true;

                // Optional: read-only set by rules
                return _isReadOnlyOverride;
            }
            set => SetProperty(ref _isReadOnlyOverride, value);
        }

        public ICommand ToggleSelectedCommand { get; }

        public KeywordSelectionVM()
        {
            ToggleSelectedCommand = new RelayCommand(() =>
            {
                if (IsReadOnly) return;

                IsSelected = !IsSelected;
            });
        }

        public KeywordSelectionVM(string key, string name, bool isSelected = false, Action<KeywordSelectionVM>? onSelectedChanged = null)
        {
            Key = key;
            Name = name;
            _isSelected = isSelected;

            ToggleSelectedCommand = new RelayCommand(() =>
            {
                if (IsReadOnly) return;

                IsSelected = !IsSelected;

                onSelectedChanged?.Invoke(this);
            });
        }
    }
}
