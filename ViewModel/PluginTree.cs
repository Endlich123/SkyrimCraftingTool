using System;
using System.Collections.Generic;
using System.Linq;

namespace SkyrimCraftingTool.ViewModel
{
    // The tree on the left of every sub-tab of the Container/LeveledList tab: a plugin, the rows it
    // defines, and a search box over them.
    //
    // ONE COPY, because there were two and they were the same forty-five lines with different field
    // names - the containers/lists pane and the placement tabs. What they genuinely differ in is
    // what a row IS and what counts as a match, and those are the two functions this is built with.
    //
    // TWO COLLECTIONS, NOT ONE, and that is the part worth keeping: _all is every row grouped, built
    // once per reload, and it keeps whatever the user expanded. _filtered exists only while
    // something is typed in the box. Clearing the search drops it and the full tree comes back with
    // its expansion intact - one rebuilt collection would collapse every plugin the moment the box
    // is emptied.
    //
    // Not a ViewModelBase: it raises nothing. The sub-tab that owns it knows which of ITS properties
    // changed, and a second notification path into the same view is how two of them get out of step.
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

        // The rows as they are drawn, for a Shift range - see MultiSelectState, which spans between
        // two of them and therefore needs the order on screen rather than the order they were read.
        public List<T> RowsInScreenOrder() => Nodes.SelectMany(n => n.Rows).OfType<T>().ToList();

        // Rebuilt from the given rows, with whatever is in the search box still applied: a reload
        // must not quietly widen the filter the user is looking through.
        public void Rebuild(IEnumerable<T> rows)
        {
            _rows = rows?.ToList() ?? (IReadOnlyList<T>)Array.Empty<T>();

            // Taken once per rebuild and kept, so the filtered tree below is ordered by the same
            // list the full tree was: re-reading it per keystroke would let a scan finishing
            // mid-search reorder the plugins under the box.
            _order = _loadOrder?.Invoke();

            _all = PluginGroupNodeVM.Group(_rows, _keyOf, loadOrder: _order);
            Filter(_search);
        }

        // Filtered in a plain pass rather than through a CollectionView: the filter is one Contains
        // over a few thousand rows, and the rest of this tool already pays for a debouncer wherever
        // that is not true. If this turns out to stutter on a big load order it becomes a
        // BackgroundFilterRunner like the enchantment tree.
        //
        // TYPING DOES NOT OPEN THE PLUGINS (user, 2026-10-12). It used to: the filtered nodes were
        // built expanded, on the reasoning that matches inside collapsed rows look like no matches
        // at all. On a real load order that turns one keystroke into a few thousand visible rows and
        // the plugin a search is supposed to narrow down to disappears in them - and the item tree,
        // which the user reads first, never did this. The count beside each plugin name already says
        // where the matches are; opening the one that matters is a click.
        //
        // Each plugin therefore keeps the state the user left it in. Expanding one WHILE a filter is
        // active is remembered too, by folding it back into _all before the rebuild - without that,
        // the next keystroke would snap it shut again.
        public void Filter(string search)
        {
            RememberExpansion();

            _search = search ?? "";

            if (string.IsNullOrWhiteSpace(_search))
            {
                _filtered = null;
                return;
            }

            _filtered = PluginGroupNodeVM.Group(
                _rows.Where(r => _matches(r, _search)), _keyOf, loadOrder: _order);

            var expanded = _all.Where(n => n.IsExpanded)
                               .Select(n => n.PluginName)
                               .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var node in _filtered)
                node.IsExpanded = expanded.Contains(node.PluginName);
        }

        // The filtered nodes are new objects on every keystroke, so what the user opened in them
        // only survives by being written back to the full tree - the one collection that outlives a
        // filter pass. A plugin in _filtered that _all no longer has (a reload between keystrokes)
        // is simply dropped; there is nothing left to remember it on.
        private void RememberExpansion()
        {
            if (_filtered == null) return;

            foreach (var node in _filtered)
            {
                var full = _all.FirstOrDefault(
                    n => string.Equals(n.PluginName, node.PluginName, StringComparison.OrdinalIgnoreCase));
                if (full != null) full.IsExpanded = node.IsExpanded;
            }
        }
    }
}
