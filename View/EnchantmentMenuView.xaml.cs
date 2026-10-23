using SkyrimCraftingTool.ViewModel;
using System.Windows;

namespace SkyrimCraftingTool.View
{
    /// <summary>
    /// Interaction logic for EnchantmentMenuWindow.xaml
    /// </summary>
    public partial class EnchantmentMenuView : System.Windows.Controls.UserControl
    {
        public EnchantmentMenuView()
        {
            InitializeComponent();
        }

        // Hands the node through untouched, exactly like MainContentView does - the detail side is a
        // ContentPresenter now, and it needs the NODE to pick a template by type. Filtering folder
        // rows out here (what this used to do) would mean a plugin click could never reach a panel.
        private void TreeView_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            if (DataContext is EnchantmentMenuVM vm)
                vm.SelectedNode = e.NewValue;
        }

    }
}
