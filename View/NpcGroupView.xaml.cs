using System.Windows.Controls;

namespace SkyrimCraftingTool.View
{
    // The NPC group tab. Everything it does lives in NpcGroupVM - there is no click handling to do
    // here: the group list is a plain ListBox with a bound selection, not a tree with modifier keys.
    public partial class NpcGroupView : UserControl
    {
        public NpcGroupView()
        {
            InitializeComponent();
        }
    }
}
