using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace SkyrimCraftingTool;

public class MainViewModel : INotifyPropertyChanged
{
    
    public ObservableCollection<ItemCardVM> SelectedItems { get; } = new();

    public static List<MaterialOption> MaterialOptions =
        GlobalState.MaterialMap.Select(kvp => new MaterialOption
        {
            KeyString = kvp.Key.ToString(),
            DisplayName = kvp.Value
        }).ToList();

    public static List<string> MaterialNames =>
        GlobalState.MaterialMapReverse.Keys.ToList();

    public ObservableCollection<string> CraftingCategories { get; }
    = new ObservableCollection<string>();


    public ICommand SaveJsonCommand { get; }
    public ICommand LoadJsonCommand { get; }
    public ICommand WriteEspCommand { get; }
    public ICommand OpenSettingsCommand { get; }



    public event PropertyChangedEventHandler? PropertyChanged;

    private string? _selectedEsp;

    public Dictionary<string, List<IGameRecord>> EspItemData { get; }

    public string? SelectedEsp
    {
        get => _selectedEsp;
        set
        {
            if (_selectedEsp == value)
                return;

            _selectedEsp = value;
            OnPropertyChanged();
            LoadSelectedItems();
        }
    }

    public MainViewModel()
    {
        EspItemData = Program.LoadItemRecordsFromESPs();

        SaveJsonCommand = new RelayCommand(_ => SaveJson());
        LoadJsonCommand = new RelayCommand(_ => LoadJson());
        WriteEspCommand = new RelayCommand(_ => WriteEsp());
        OpenSettingsCommand = new RelayCommand(_ => OpenSettings());

        LoadCraftingCategories();

        // Default-Wert setzen
        SelectedCraftingCategory = "Random";

        GlobalState.CraftingSettings.SettingsChanged += OnSettingsChanged;


    }

    private void OnSettingsChanged(string key)
    {
        ApplySettingsToItems();
    }


    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private IGameRecord? _selectedItem;
    public IGameRecord? SelectedItem
    {
        get => _selectedItem;
        set
        {
            if (_selectedItem == value)
                return;

            _selectedItem = value;
            OnPropertyChanged();
        }
    }

    // 
    private void LoadSelectedItems()
    {
        SelectedItems.Clear();

        if (SelectedEsp == null)
            return;

        if (!EspItemData.TryGetValue(SelectedEsp, out var list))
            return;

        foreach (var record in list)
        {
            SelectedItems.Add(new ItemCardVM(record, GlobalState.VendorKeywords));
        }
    }

    // Crafting Categories Choicebox
    private void LoadCraftingCategories()
    {
        CraftingCategories.Clear();

        // 1. Dein eigener Eintrag
        CraftingCategories.Add("Random");

        // 2. Smithing-Perks hinzufügen
        foreach (var perkName in GlobalState.SmithingPerkEditorIDs)
        {
            CraftingCategories.Add(perkName);
        }
    }

    private string? _selectedCraftingCategory;
    public string? SelectedCraftingCategory
    {
        get => _selectedCraftingCategory;
        set
        {
            if (_selectedCraftingCategory == value)
                return;

            _selectedCraftingCategory = value;
            OnPropertyChanged();

            ApplySettingsToItems();
        }
    }

    private void ApplySettingsToItems()
    {
        if (SelectedCraftingCategory == null)
            return;

        foreach (var item in SelectedItems)
        {
            // Category + Slot 
            string category = SelectedCraftingCategory;
            string slot = item.IsArmor ? item.ArmorSlot : item.WeaponType;

            // SlotSettings 
            var slotSettings = GlobalState.CraftingSettings.GetSlot(category, slot);

            // Values
            item.Value = (int)slotSettings.Cost;
            item.Weight = slotSettings.Weight;

            if (item.IsArmor)
                item.ArmorRating = (int)slotSettings.ArmorRating;

            if (item.IsWeapon)
                item.Damage = (int)slotSettings.Damage;

            // Vendors
            item.SelectedVendors = slotSettings.Vendors.ToList();
            item.VendorPanel = new VendorPanelVM(GlobalState.VendorKeywords, item.SelectedVendors);

            // Workbench
            item.SelectedWorkbench = slotSettings.SelectedWorkbench;

            // Materials
            item.MaterialList = new ObservableCollection<MaterialEntry>(
                slotSettings.Materials.Select(m => new MaterialEntry
                {
                    Material = m.Material,
                    Amount = m.Amount
                })
            );
        }
    }



    // ---------------------------------------------------------
    //  JSON EXPORT
    // ---------------------------------------------------------
    public List<PluginInfo> ExtractPluginInfoFromSelectedItems()
    {
        var list = new List<PluginInfo>();

        foreach (var vm in SelectedItems)
        {
            list.Add(new PluginInfo
            {
                EspName = SelectedEsp ?? "",
                EspPath = vm.Record.SourceEspPath,
                ItemName = vm.EditorID,
                FormKey = vm.Record.FormKey.ToString(),

                ItemValue = vm.Value,
                ItemWeight = vm.Weight,

                Vendors = vm.SelectedVendors.ToList(),
                Workbench = vm.SelectedWorkbench,
                Materials = vm.MaterialList.ToDictionary(m => m.Material, m => m.Amount),

                ArmorRating = vm.ArmorRating,
                ArmorSlot = vm.ArmorSlot,

                Damage = vm.Damage,
                WeaponType = vm.WeaponType,
            });
        }

        return list;
    }

    // ---------------------------------------------------------
    //  JSON IMPORT
    // ---------------------------------------------------------
    public void ApplyPluginInfoToGui(List<PluginInfo> items)
    {
        foreach (var info in items)
        {
            var vm = SelectedItems
                .FirstOrDefault(x => x.Record.FormKey.ToString() == info.FormKey);

            if (vm == null)
                continue;

            vm.EditorID = info.ItemName;
            vm.Value = info.ItemValue;
            vm.Weight = info.ItemWeight;

            // Vendors
            vm.SelectedVendors = info.Vendors;
            vm.VendorPanel = new VendorPanelVM(GlobalState.VendorKeywords, info.Vendors);

            // Workbench
            vm.SelectedWorkbench = info.Workbench;

            // Materials
            vm.MaterialList = new ObservableCollection<MaterialEntry>(
                info.Materials.Select(kvp => new MaterialEntry
                {
                    Material = kvp.Key,
                    Amount = kvp.Value
                })
            );

            // Armor
            vm.ArmorRating = info.ArmorRating;
            vm.ArmorSlot = info.ArmorSlot;

            // Weapon
            vm.Damage = info.Damage;

            //  norm WeaponType 
            if (Enum.TryParse<WeaponTypes>(info.WeaponType, out var parsed))
                vm.WeaponType = parsed.ToString();
            else
                vm.WeaponType = nameof(WeaponTypes.Sword);
        }
    }

    // ---------------------------------------------------------
    //  SAVE JSON
    // ---------------------------------------------------------
    public void SaveJson()
    {
        string baseDir = AppContext.BaseDirectory;
        string inputDir = Path.Combine(baseDir, "Input");
        Directory.CreateDirectory(inputDir);

        string fileName = $"{SelectedEsp}.json";
        string fullPath = Path.Combine(inputDir, fileName);

        var data = ExtractPluginInfoFromSelectedItems();
        JsonTranslator.Save(fullPath, data);
    }

    // ---------------------------------------------------------
    //  LOAD JSON
    // ---------------------------------------------------------
    public void LoadJson()
    {
        string baseDir = AppContext.BaseDirectory;
        string inputDir = Path.Combine(baseDir, "Input");
        Directory.CreateDirectory(inputDir);

        string fileName = $"{SelectedEsp}.json";
        string fullPath = Path.Combine(inputDir, fileName);

        if (!File.Exists(fullPath))
            return;

        var data = JsonTranslator.Load(fullPath);
        ApplyPluginInfoToGui(data);
    }

    // ---------------------------------------------------------
    //  Write ESP
    // ---------------------------------------------------------
    private void WriteEsp()
    {
        try
        {
            string baseDir = AppContext.BaseDirectory;
            string outputDir = Path.Combine(baseDir, "Output");
            Directory.CreateDirectory(outputDir);

            string outputPath = Path.Combine(outputDir, "SkyrimCraftingToolOutput.esp");

            var writer = new ESPWriter();
            writer.AddItems(ExtractPluginInfoFromSelectedItems());
            writer.WriteToEsp(outputPath);

            System.Windows.MessageBox.Show("ESP erfolgreich geschrieben:\n" + outputPath);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show("Fehler beim Schreiben der ESP:\n" + ex.Message);
        }
    }

    // ---------------------------------------------------------
    //  Settings
    // ---------------------------------------------------------
    private void OpenSettings()
    {
        var win = new SettingsWindow();
        win.ShowDialog();
    }


}
