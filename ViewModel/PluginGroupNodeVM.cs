using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace SkyrimCraftingTool.ViewModel
{
    // A plugin row in the trees on the left of the Container/LeveledList sub-tabs.
    //
    // TWO LEVELS, AND THAT IS THE WHOLE POINT. The item tree has three - plugin, category, item -
    // because an item is one of two record types and the tree is the only place to tell them apart.
    // Here the tab IS the category: everything in "Books" is a book. So this carries a plugin name
    // and the rows under it, and nothing else.
    //
    // NOT PluginNodeVM, which looks like the obvious reuse: that one holds a MainContentVM
    // back-reference for the per-plugin export/import/preset buttons and answers whether anything
    // below it is edited. None of that exists on these tabs, and taking it would tie them to the
    // item editor's view model for the sake of one string.
    //
    // Rows are typed `object` so one node serves both shapes of sub-tab: OwnerRowVM for containers
    // and leveled lists, IPlaceable for the placement tabs. What a leaf looks like comes from each
    // view's own HierarchicalDataTemplate.ItemTemplate rather than from a DataType-keyed template,
    // which is also why IPlaceable being an interface does not matter here.
    public sealed class PluginGroupNodeVM : ViewModelBase
    {
        public PluginGroupNodeVM(string pluginName, IEnumerable<object> rows)
        {
            PluginName = pluginName;
            Rows = new ObservableCollection<object>(rows);
        }

        public string PluginName { get; }

        public ObservableCollection<object> Rows { get; }

        private bool _isExpanded;
        public bool IsExpanded
        {
            get => _isExpanded;
            set => SetProperty(ref _isExpanded, value);
        }

        // Grouping lives here rather than in each sub-tab: both kinds key their rows the same way,
        // and two copies of this would drift apart the moment one of them gets a sort.
        //
        // LOAD ORDER, like the item tree. It was alphabetical at first, on the reasoning that these
        // view models see nothing but the rows' keys and that handing each one the plugin list ties
        // a tab to the scanner for the sake of row order. That trade was wrong: the order a load
        // order is read in IS the information - which mod overrides which - and a tool that shows
        // the same plugins in two different orders on two screens makes the reader translate
        // between them.
        //
        // `loadOrder` is the plugin file names as the game loads them. Anything not in it sorts
        // after the rest, by name, rather than vanishing - a key whose plugin has left the load
        // order is exactly the row someone is looking for. Without a list it stays alphabetical,
        // which is what the tests and any caller that has no scanner get.
        //
        // `expanded` is for the filtered tree: a search that leaves the matches inside collapsed
        // plugin rows looks like a search that found nothing.
        public static List<PluginGroupNodeVM> Group<T>(
            IEnumerable<T> rows, Func<T, string> keyOf, bool expanded = false,
            IReadOnlyList<string>? loadOrder = null)
        {
            var position = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < (loadOrder?.Count ?? 0); i++)
                position.TryAdd(loadOrder![i], i);

            return rows
                .GroupBy(r => PluginOf(keyOf(r)), StringComparer.OrdinalIgnoreCase)
                .OrderBy(g => position.TryGetValue(g.Key, out var i) ? i : int.MaxValue)
                .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                .Select(g => new PluginGroupNodeVM(g.Key, g.Cast<object>()) { IsExpanded = expanded })
                .ToList();
        }

        // Split('|')[0] is the idiom the rest of the tool uses for this (TreeBuilderService,
        // EnchantmentRecord.Plugin) and it cannot throw - Split always yields at least one element.
        // KeyFactory.SplitMasterKey would, on a key that somehow carries no pipe.
        private static string PluginOf(string key)
        {
            if (string.IsNullOrWhiteSpace(key)) return "(unknown plugin)";

            var plugin = key.Split('|')[0];
            return string.IsNullOrWhiteSpace(plugin) ? "(unknown plugin)" : plugin;
        }
    }
}
