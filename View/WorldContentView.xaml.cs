using System.Windows.Controls;

namespace SkyrimCraftingTool.View
{
    // No handlers left here either - see PlacementListView. The ticks in the contents list are
    // properties on the rows, which the view model subscribes to itself
    // (OwnerContentsVM.WatchSelection), and the tree's clicks are MultiSelectTreeBehavior's.
    public partial class WorldContentView : UserControl
    {
        public WorldContentView()
        {
            InitializeComponent();
        }
    }
}
