using SkyrimCraftingTool.ViewModel;
using System.Windows;

namespace SkyrimCraftingTool.View
{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
            DataContext = new MainWindowVM();
        }

    }
}
