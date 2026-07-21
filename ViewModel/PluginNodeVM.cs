using System.Collections.ObjectModel;

namespace SkyrimCraftingTool.ViewModel
{
    public class PluginNodeVM : ViewModelBase
    {
        public string PluginName { get; set; }

        private bool _isExpanded;
        public bool IsExpanded
        {
            get => _isExpanded;
            set => SetProperty(ref _isExpanded, value);
        }

        public ObservableCollection<CategoryNodeVM> Categories { get; set; }
            = new ObservableCollection<CategoryNodeVM>();

        /// <summary>
        /// Erzeugt eine gefilterte Kopie dieses Plugins.
        /// </summary>
        public PluginNodeVM FilterReference(string text)
        {
            bool pluginMatches = string.IsNullOrWhiteSpace(text) ||
                                 PluginName.Contains(text, StringComparison.OrdinalIgnoreCase);

            var filtered = new PluginNodeVM { PluginName = this.PluginName };

            foreach (var cat in Categories)
            {
                var filteredCat = cat.FilterReference(text, pluginMatches);
                if (filteredCat != null)
                    filtered.Categories.Add(filteredCat);
            }

            return filtered.Categories.Count > 0 ? filtered : null;
        }
    }
}
