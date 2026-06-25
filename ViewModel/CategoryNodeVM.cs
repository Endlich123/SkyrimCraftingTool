using System.Collections.ObjectModel;

namespace SkyrimCraftingTool.ViewModel
{
    public class CategoryNodeVM : ViewModelBase
    {
        public string CategoryName { get; set; }

        private bool _isExpanded;
        public bool IsExpanded
        {
            get => _isExpanded;
            set => SetProperty(ref _isExpanded, value);
        }

        public ObservableCollection<ItemNodeVM> Items { get; set; }
            = new ObservableCollection<ItemNodeVM>();

        /// <summary>
        /// Filtert diese Kategorie. pluginMatched = true, wenn das Plugin bereits auf den Suchtext matcht.
        /// </summary>
        public CategoryNodeVM FilterReference(string text, bool pluginMatches)
        {
            bool categoryMatches = string.IsNullOrWhiteSpace(text) ||
                                   CategoryName.Contains(text, StringComparison.OrdinalIgnoreCase);

            var filtered = new CategoryNodeVM { CategoryName = this.CategoryName };

            foreach (var item in Items)
            {
                bool itemMatches = string.IsNullOrWhiteSpace(text) ||
                                   item.Name.Contains(text, StringComparison.OrdinalIgnoreCase);

                if (itemMatches || categoryMatches || pluginMatches)
                    filtered.Items.Add(item); // REFERENZ, keine Kopie
            }

            return filtered.Items.Count > 0 ? filtered : null;
        }
    }
}
