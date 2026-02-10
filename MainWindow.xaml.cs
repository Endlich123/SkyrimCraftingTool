using System.Windows;
using System.Windows.Controls;
using WpfMessageBox = System.Windows.MessageBox;


namespace SkyrimCraftingTool

{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
            DataContext = new MainViewModel();
        }

    }
}


