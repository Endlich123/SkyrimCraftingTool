using System;
using System.Collections.Generic;
using System.Linq;

namespace SkyrimCraftingTool.ViewModel
{
    public sealed class PluginTree<T> where T : class
    {
        private readonly Func<T, string> _keyOf;
        private readonly Func<T, string, bool> _matches;
        private readonly Func<IReadOnlyList<string>>? _loadOrder;

        private List<PluginGroupNodeVM> _all = new();
        private List<PluginGroupNodeVM>? _filtered;

        private IReadOnlyList<T> _rows = Array.Empty<T>();
        private IReadOnlyList<string>? _order;
        private string _search = "";

        // `loadOrder` is a FUNCTION, not a list, for one reason: MainContentVM REPLACES its
        // ActivePlugins on every scan. A list captured when the tab was built would still be the
        // load order from before a rescan, and these trees are built lazily - possibly long after.
        // Read at Rebuild, it is whatever the load order is when the tree is drawn.
        //
        // Optional, because not every caller has one: without it the grouping falls back to
        // alphabetical, which is what PluginGroupNodeVM.Group does with no list.
        public PluginTree(Func<T, string> keyOf, Func<T, string, bool> matches,
                          Func<IReadOnlyList<string>>? loadOrder = null)
        {
            _keyOf = keyOf;
            _matches = matches;
            _loadOrder = loadOrder;
        }

        public IEnumerable<PluginGroupNodeVM> Nodes => _filtered ?? _all;

        // A second condition on top of the search - "only edited" on the Container/LeveledList tab.
        // Null means none. Applied by the next Filter() call; the owner calls Filter after setting it.
        public Func<T, bool>? RowFilter { get; set; }

        // The rows as they are drawn, for a Shift range - see MultiSelectState, which spans between
        // two of them and therefore needs the order on screen rather than the order they were read.
        public List<T> RowsInScreenOrder() => Nodes.SelectMany(n => n.Rows).OfType<T>().ToList();

        public void Rebuild(IEnumerable<T> rows)
        {
            _rows = rows?.ToList() ?? (IReadOnlyList<T>)Array.Empty<T>();

            // Taken once per rebuild and kept, so the filtered tree below is ordered by the same
            // list the full tree was: re-reading it per keystroke would let a scan finishing
            // mid-search reorder the plugins under the box.
            _order = _loadOrder?.Invoke();

            _all = PluginGroupNodeVM.Group(_rows, _keyOf, loadOrder: _order, expansion: _expansion);
            Filter(_search);
        }

        // Which plugins are open, by name. The full and the filtered nodes read the same entry, so a
        // plugin opened in either survives a search, a keystroke, a cleared box and a Rebuild alike.
        // Rebuild used to lose all of it: a reset regrouped the rows and the tree came back closed.
        private readonly TreeExpansion _expansion = new();

        public void Filter(string search)
        {
            _search = search ?? "";

            bool searching = !string.IsNullOrWhiteSpace(_search);
            if (!searching && RowFilter == null)
            {
                _filtered = null;
                return;
            }

            _filtered = PluginGroupNodeVM.Group(
                _rows.Where(r => (RowFilter == null || RowFilter(r)) && (!searching || _matches(r, _search))),
                _keyOf, loadOrder: _order, expansion: _expansion);
        }
    }
}
