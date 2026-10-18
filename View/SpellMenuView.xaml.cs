using System.Windows;
using SkyrimCraftingTool.ViewModel;

namespace SkyrimCraftingTool.View
{
    // The Spells tab (docs/Spells-Plan.md, S-P2).
    public partial class SpellMenuView : System.Windows.Controls.UserControl
    {
        public SpellMenuView()
        {
            InitializeComponent();
        }

        // The node goes through untouched, as in the Enchantments and Items trees: the detail side
        // picks its template from the node's TYPE, so filtering folder rows out here would stop a
        // plugin row from ever reaching a panel.
        private void TreeView_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            if (DataContext is SpellMenuVM vm)
                vm.SelectedNode = e.NewValue;
        }
    }
}
