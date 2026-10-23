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

        // Drives the dot on a COLLAPSED category row: an edited item marks itself, but folded away
        // that marker is invisible and the edit looks like it is not there.
        //
        // Computed, not cached, and that is deliberate - FilterReference hands out category copies
        // that SHARE the item instances, so a copy answers this correctly without any bookkeeping of
        // its own. What it does need is a nudge, which MainContentVM gives it (RaiseTreeEditedFlags).
        public bool HasEditedItems
        {
            get
            {
                foreach (var item in Items)
                    if (item.IsEdited) return true;
                return false;
            }
        }

        internal void RaiseHasEditedItems() => OnPropertyChanged(nameof(HasEditedItems));

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
