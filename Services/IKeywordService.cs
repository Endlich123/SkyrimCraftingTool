using SkyrimCraftingTool.ViewModel;
using SkyrimCraftingTool.Model;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace SkyrimCraftingTool.Services
{
    public interface IKeywordService
    {
        ObservableCollection<KeywordSelectionVM> GlobalKeywords { get; }

        void InitializeFrom(List<FormIDRecord> keywords);

        void ApplySelectionFromItem(ItemNodeVM item);

        IEnumerable<KeywordSelectionVM> FilterByItemType(ItemNodeVM item);
        IEnumerable<KeywordSelectionVM> FilterByEnchantmentCategory(EnchantmentCategory category);

        IEnumerable<KeywordSelectionVM> FilterBySearch(string search);

    }
}
