using SkyrimCraftingTool.Model;
using SkyrimCraftingTool.ViewModel;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace SkyrimCraftingTool.Services
{
    public class KeywordService : IKeywordService
    {
        public ObservableCollection<KeywordSelectionVM> GlobalKeywords { get; } = new();

        public void InitializeFrom(List<FormIDRecord> keywords)
        {
            GlobalKeywords.Clear();
            if (keywords == null) return;

            foreach (var kw in keywords.OrderBy(k => k.Name))
            {
                GlobalKeywords.Add(new KeywordSelectionVM
                {
                    Key = kw.Key,
                    Name = kw.Name,
                    IsSelected = false,
                    ParentItem = null
                });
            }
        }

        public void SetSelectionForActiveItem(IEnumerable<string> activeKeywordKeys)
        {
            var set = activeKeywordKeys?.ToHashSet() ?? new HashSet<string>();

            foreach (var g in GlobalKeywords)
            {
                g.IsSelected = set.Contains(g.Key);
            }
        }
    }
}
