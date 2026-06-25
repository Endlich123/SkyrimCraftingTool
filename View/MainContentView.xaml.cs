using SkyrimCraftingTool.ViewModel;
using System.Windows;

namespace SkyrimCraftingTool.View
{
    public partial class MainContentView : System.Windows.Controls.UserControl
    {
        public MainContentView()
        {
            InitializeComponent();
        }

        private async void MainContentView_Loaded(object sender, RoutedEventArgs e)
        {
            if (DataContext is MainContentVM vm)
                await vm.LoadInitialDataAsync();
        }

        private void MainTreeView_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            if (DataContext is MainContentVM vm)
                vm.SelectedNode = e.NewValue;
        }

    }
}
