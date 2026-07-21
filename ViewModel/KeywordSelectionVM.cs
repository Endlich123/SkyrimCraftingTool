using System.Windows.Input;

namespace SkyrimCraftingTool.ViewModel
{
    public class KeywordSelectionVM : ViewModelBase
    {
        public string Key { get; set; }
        public string Name { get; set; }

        private bool _isSelected;
        public bool IsSelected
        {
            get => _isSelected;
            set => SetProperty(ref _isSelected, value);
        }

        public ICommand ToggleSelectedCommand { get; }

        // Read-only flag for keywords that should not be changed by the user
        public bool IsReadOnly => !string.IsNullOrEmpty(Name) &&
                                  Name.StartsWith("WeapType", System.StringComparison.OrdinalIgnoreCase);

        public KeywordSelectionVM()
        {
            ToggleSelectedCommand = new RelayCommand(() =>
            {
                if (IsReadOnly) return;

                IsSelected = !IsSelected;
            });
        }

        public KeywordSelectionVM(string key, string name, bool isSelected = false, Action<KeywordSelectionVM> onSelectedChanged = null)
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
