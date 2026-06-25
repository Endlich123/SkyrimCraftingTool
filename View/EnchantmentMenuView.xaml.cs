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

        private void TreeView_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            if (e.NewValue is EnchantmentTreeNode node && node.Enchantment != null)
            {
                if (DataContext is EnchantmentMenuVM vm)
                    vm.SelectedEnchantment = node.Enchantment;
            }
        }

    }
}
