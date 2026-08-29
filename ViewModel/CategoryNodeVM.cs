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
        /// Filters this category. pluginMatched = true when the plugin already matches the search text.
        /// </summary>
        public CategoryNodeVM FilterReference(string text, bool pluginMatches, bool onlyEdited = false)
        {
            bool categoryMatches = string.IsNullOrWhiteSpace(text) ||
                                   CategoryName.Contains(text, StringComparison.OrdinalIgnoreCase);

            var filtered = new CategoryNodeVM { CategoryName = this.CategoryName };

            foreach (var item in Items)
            {
                if (onlyEdited && !item.IsEdited)
                    continue;

                bool itemMatches =
                    string.IsNullOrWhiteSpace(text) ||
                    item.Name.Contains(text, StringComparison.OrdinalIgnoreCase) ||
                    item.EditorID.Contains(text, StringComparison.OrdinalIgnoreCase) ||
                    item.Key.Contains(text, StringComparison.OrdinalIgnoreCase);


                if (itemMatches || categoryMatches || pluginMatches)
                    filtered.Items.Add(item); // REFERENCE, not a copy
            }

            return filtered.Items.Count > 0 ? filtered : null;
        }
    }
}
