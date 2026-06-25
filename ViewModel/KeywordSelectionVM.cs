using System.Windows.Input;

namespace SkyrimCraftingTool.ViewModel
{
    public class KeywordSelectionVM : ViewModelBase
    {
        public string Key { get; set; }      // Plugin|FormID
        public string Name { get; set; }     // EditorID
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


        private bool _isSelected;
        public bool IsSelected
        {
            get => _isSelected;
            set => SetProperty(ref _isSelected, value);
        }

        public ItemNodeVM ParentItem { get; set; }

        public void Toggle()
        {
            IsSelected = !IsSelected;

            ParentItem?.SelectedKeywordsView.Refresh();
            ParentItem?.FilteredKeywordsView.Refresh();
        }

        public ICommand ToggleSelectedCommand { get; }

        public KeywordSelectionVM()
        {
            ToggleSelectedCommand = new RelayCommand(Toggle);
        }


    }

}
