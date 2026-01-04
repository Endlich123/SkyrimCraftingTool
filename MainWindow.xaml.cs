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

        //    private Dictionary<string, List<IGameRecord>> espItemData;
        //    private List<string> CraftingMats;

        //    public MainWindow()
        //    {
        //        InitializeComponent();

        //        CraftingMats = Program.materialMap.Values
        //            .Distinct()
        //            .OrderBy(s => s)
        //            .ToList();

        //        espItemData = Program.LoadItemRecordsFromESPs();

        //        EspLoader.ItemsSource = espItemData.Keys.ToList();

        //        CraftingCategory.ItemsSource = new List<string>
        //        {
        //            "Leather","Iron","Steel","Elven","Advanced","Glass",
        //            "Dragon","Dwarven","Orcish","Ebony","Daedric"
        //        };

        //        EspLoader.SelectionChanged += EspLoader_SelectionChanged;
        //        SaveJsonButton.Click += SaveJson_Click;
        //        LoadJsonButton.Click += LoadJson_Click;
        //    }

        //    private void EspLoader_SelectionChanged(object sender, SelectionChangedEventArgs e)
        //    {
        //        MainControlPanel.Children.Clear();

        //        if (EspLoader.SelectedItem is not string esp)
        //            return;

        //        foreach (var record in espItemData[esp])
        //        {
        //            var card = new ItemCard { DataContext = record };
        //            MainControlPanel.Children.Add(card);
        //        }
        //    }

        //    private void SaveJson_Click(object sender, RoutedEventArgs e)
        //    {
        //        string plugin = EspLoader.SelectedItem?.ToString() ?? "UnknownESP";
        //        var items = JsonTranslator.ExtractFromWpf(MainControlPanel, plugin);
        //        JsonTranslator.SaveToJson(items, plugin + ".json");
        //        WpfMessageBox.Show("Daten gespeichert.");
        //    }

        //    private void LoadJson_Click(object sender, RoutedEventArgs e)
        //    {
        //        string plugin = EspLoader.SelectedItem?.ToString() ?? "UnknownESP";
        //        var items = JsonTranslator.LoadFromJson(plugin + ".json");

        //        if (items.Count > 0)
        //        {
        //            JsonTranslator.UpdateWpf(MainControlPanel, items);
        //            WpfMessageBox.Show("Daten geladen.");
        //        }
        //    }

    }
}


