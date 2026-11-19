using System.Windows;
using System.Windows.Controls;
using SkyrimCraftingTool.ViewModel;

namespace SkyrimCraftingTool.View
{
    // For a tree whose branches and leaves sit in the same item list (NPC tree: a node's Items are
    // its child nodes followed by its NPCs). One container style for all rows would bind IsExpanded
    // on every leaf too, and a leaf has no such property - one binding error per NPC row. Branches
    // get BranchStyle, everything else LeafStyle. Child levels inherit the selector from the TreeView
    // as long as the HierarchicalDataTemplate sets no container style of its own.
    public sealed class ExpandableNodeStyleSelector : StyleSelector
    {
        public Style? BranchStyle { get; set; }
        public Style? LeafStyle { get; set; }

        public override Style? SelectStyle(object item, DependencyObject container)
            => item is ExpandableNodeVM ? BranchStyle : LeafStyle;
    }
}
