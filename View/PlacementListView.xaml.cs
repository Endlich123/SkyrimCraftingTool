using System.Windows.Controls;

namespace SkyrimCraftingTool.View
{
    // No handlers. Ctrl/Shift and the single-select navigation are
    // Model.MultiSelectTreeBehavior, which the shared tree style switches on - this view had its own
    // copy of both, as did the containers/lists pane.
    public partial class PlacementListView : UserControl
    {
        public PlacementListView()
        {
            InitializeComponent();
        }
    }
}
