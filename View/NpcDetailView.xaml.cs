using System.Windows.Controls;

namespace SkyrimCraftingTool.View
{
    // The NPC editor pane. Its DataContext is an NpcMenuVM; both the NPC tab and the Templates tab
    // put the same instance behind it, so an edit made in one is the edit the other shows.
    public partial class NpcDetailView : UserControl
    {
        public NpcDetailView()
        {
            InitializeComponent();
        }
    }
}
