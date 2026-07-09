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
        void SetSelectionForActiveItem(IEnumerable<string> activeKeywordKeys);
    }
}
