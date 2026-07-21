using SkyrimCraftingTool.ViewModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;

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

        private void Slider_ThumbDragCompleted(object sender, DragCompletedEventArgs e)
        {
            if (sender is Slider s)
            {
                // push binding explicitly
                BindingExpression be = s.GetBindingExpression(Slider.ValueProperty);
                be?.UpdateSource();

                // After updating the LVLi VM, rebuild the container string for the selected item
                if (DataContext is MainContentVM vm && vm.SelectedNode is ItemNodeVM item)
                {
                    item.ContainerString = item.ContainerSelection.BuildString();
                    // sync left-hand selection flags
                    vm.UpdateAllContainerSelectionFlags(item);
                }
            }
        }
    }
}
