using System;
using System.Collections.Generic;

namespace SkyrimCraftingTool.ViewModel
{
    // Which branches of ONE tree are open, by a path of display names ("Skyrim.esm",
    // "Skyrim.esm|Armor"). Owned by the tree's view model, so it outlives every node.
    //
    // Why not a field on the node: every tree in this tool throws its nodes away far more often than
    // it looks - a search hands the TreeView filtered COPIES, a reset re-runs the filter, a create or
    // reload builds the tree from the database again. A field on the node was gone after each of
    // those and the tree came back collapsed. A path survives all of them, a rescan included,
    // because it names the branch rather than the object.
    //
    // Session only, on purpose: after a restart the plugins and categories can be different ones,
    // and a tree that opens half-expanded on stale paths helps nobody.
    public sealed class TreeExpansion
    {
        private readonly HashSet<string> _open = new(StringComparer.OrdinalIgnoreCase);

        public bool IsOpen(string key) => _open.Contains(key);

        // True when the state actually changed.
        public bool Set(string key, bool open) => open ? _open.Add(key) : _open.Remove(key);

        public static string Child(string parentKey, string name) => parentKey + "|" + name;
    }

    // A branch whose IsExpanded lives in a TreeExpansion when it has one, and in a plain field when
    // it does not - which keeps nodes built without one (tests, leaves) working as before.
    //
    // A filtered copy carries the SAME Expansion and ExpansionKey as its original, so opening the
    // copy opens the original too, and the next filter run, reset or rebuild finds it open.
    public abstract class ExpandableNodeVM : ViewModelBase
    {
        public TreeExpansion? Expansion { get; set; }
        public string? ExpansionKey { get; set; }

        private bool _isExpanded;
        public bool IsExpanded
        {
            get => Expansion != null && ExpansionKey != null ? Expansion.IsOpen(ExpansionKey) : _isExpanded;
            set
            {
                if (Expansion != null && ExpansionKey != null)
                {
                    if (Expansion.Set(ExpansionKey, value)) OnPropertyChanged();
                }
                else
                {
                    SetProperty(ref _isExpanded, value);
                }
            }
        }

        // For the copy a filter makes: same memory, same key, so both answer the same.
        protected void CopyExpansionTo(ExpandableNodeVM copy)
        {
            copy.Expansion = Expansion;
            copy.ExpansionKey = ExpansionKey;
            if (Expansion == null || ExpansionKey == null) copy._isExpanded = _isExpanded;
        }
    }
}
