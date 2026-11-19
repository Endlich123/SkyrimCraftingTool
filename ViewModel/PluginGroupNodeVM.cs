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
    public sealed class PluginGroupNodeVM : ExpandableNodeVM
    {
        public PluginGroupNodeVM(string pluginName, IEnumerable<object> rows)
        {
            PluginName = pluginName;
            Rows = new ObservableCollection<object>(rows);
        }

        public string PluginName { get; }

        public ObservableCollection<object> Rows { get; }

        // `expansion`: the open plugins, kept across regrouping - see TreeExpansion. Null = a plain
        // field per node, as before.
        public static List<PluginGroupNodeVM> Group<T>(
            IEnumerable<T> rows, Func<T, string> keyOf,
            IReadOnlyList<string>? loadOrder = null,
            TreeExpansion? expansion = null)
        {
            // No list, no order: every plugin ranks the same and the name decides.
            Func<string, int> rank = loadOrder == null || loadOrder.Count == 0
                ? _ => 0
                : Services.PluginTreeOrder.Ranker(loadOrder);

            return rows
                .GroupBy(r => PluginOf(keyOf(r)), StringComparer.OrdinalIgnoreCase)
                .OrderBy(g => rank(g.Key))
                .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                .Select(g => new PluginGroupNodeVM(g.Key, g.Cast<object>())
                {
                    Expansion = expansion, ExpansionKey = g.Key,
                })
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
