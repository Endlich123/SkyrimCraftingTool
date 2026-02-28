using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace SkyrimCraftingTool;

public class SlotSettingsViewModel : INotifyPropertyChanged
{
    public string Category { get; }
    public string SlotName { get; }
    private bool _isLoading = false;

    public IEnumerable<string> AllMaterialNames => GlobalState.MaterialMap.Values;

    public bool IsWeapon => Enum.TryParse<WeaponTypes>(SlotName, out _);
    public bool IsArmor => !IsWeapon;

    public IEnumerable<string> Workbenches => GlobalState.WorkbenchTypes;

    private float _cost;
    public float Cost
    {
        get => _cost;
        set { if (_cost != value) { _cost = value; OnPropertyChanged(); if (!_isLoading) Save(); } }
    }

    private float _weight;
    public float Weight
    {
        get => _weight;
        set { if (_weight != value) { _weight = value; OnPropertyChanged(); if (!_isLoading) Save(); } }
    }

    private float _damage;
    public float Damage
    {
        get => _damage;
        set { if (_damage != value) { _damage = value; OnPropertyChanged(); if (!_isLoading) Save(); } }
    }

    private float _armorRating;
    public float ArmorRating
    {
        get => _armorRating;
        set { if (_armorRating != value) { _armorRating = value; OnPropertyChanged(); if (!_isLoading) Save(); } }
    }

    private string _selectedWorkbench;
    public string SelectedWorkbench
    {
        get => _selectedWorkbench;
        set { if (_selectedWorkbench != value) { _selectedWorkbench = value; OnPropertyChanged(); if (!_isLoading) Save(); } }
    }

    public List<string> Vendors { get; set; } = new();
    public ObservableCollection<VendorKeywordVM> VendorOptions { get; } = new();

    public ObservableCollection<MaterialEntry> Materials { get; } = new();

    public ICommand AddMaterialCommand { get; }
    public ICommand RemoveMaterialCommand { get; }

    // ---------------------------------------------------------
    //  Konstruktor für neue Slots (Default)
    // ---------------------------------------------------------
    public SlotSettingsViewModel(string category, string slotName)
    {
        _isLoading = true;

        Category = category;
        SlotName = slotName;

        Cost = 0;
        Weight = 0;
        Damage = 0;
        ArmorRating = 0;

        SelectedWorkbench = GlobalState.WorkbenchTypes.FirstOrDefault();

        Vendors = new List<string>();

        foreach (var v in GlobalState.VendorKeywords)
            VendorOptions.Add(new VendorKeywordVM(v, false, this));

        _isLoading = false;
    }


    // ---------------------------------------------------------
    //  Konstruktor für geladene Daten
    // ---------------------------------------------------------
    public SlotSettingsViewModel(string category, SlotSettingsData data)
    {
        _isLoading = true;

        Category = category;
        SlotName = data.SlotName;

        Cost = data.Cost;
        Weight = data.Weight;
        Damage = data.Damage;
        ArmorRating = data.ArmorRating;
        SelectedWorkbench = data.Workbench;

        Vendors = data.Vendors.ToList();

        foreach (var v in GlobalState.VendorKeywords)
            VendorOptions.Add(new VendorKeywordVM(v, Vendors.Contains(v), this));

        foreach (var m in data.Materials)
            Materials.Add(new MaterialEntry(this) { Material = m.Material, Amount = m.Amount });

        _isLoading = false;
    }


    // ---------------------------------------------------------
    //  Material hinzufügen / entfernen
    // ---------------------------------------------------------
    private void AddMaterial()
    {
        Materials.Add(new MaterialEntry(this) { Material = "", Amount = 1 });
        if (!_isLoading) Save();
    }

    private void RemoveMaterial(MaterialEntry entry)
    {
        if (entry != null)
        {
            Materials.Remove(entry);
            if (!_isLoading) Save();
        }
    }

    // ---------------------------------------------------------
    //  Model erzeugen
    // ---------------------------------------------------------
    public SlotSettingsData ToModel()
    {
        return new SlotSettingsData
        {
            SlotName = SlotName,
            Cost = Cost,
            Weight = Weight,
            Damage = Damage,
            ArmorRating = ArmorRating,
            Workbench = SelectedWorkbench,
            Vendors = Vendors.ToList(),
            Materials = Materials.Select(m => new SlotSettingsData.MaterialEntryData
            {
                Material = m.Material,
                Amount = m.Amount
            }).ToList()
        };
    }

    // ---------------------------------------------------------
    //  Auto-Save
    // ---------------------------------------------------------
    public void Save()
    {
        var category = SettingsStorage.LoadCategory(Category);

        // Falls Datei leer → alle Slots erzeugen
        if (category.Slots.Count == 0)
        {
            foreach (var armor in GlobalState.AllArmorSlots)
                category.Slots.Add(new SlotSettingsData { SlotName = armor.ToString(), IsWeapon = false });

            foreach (var weapon in GlobalState.AllWeaponTypes)
                category.Slots.Add(new SlotSettingsData { SlotName = weapon.ToString(), IsWeapon = true });
        }

        var slot = category.Slots.First(s => s.SlotName == SlotName);

        Vendors = VendorOptions
            .Where(v => v.IsSelected)
            .Select(v => v.Keyword)
            .ToList();

        var model = ToModel();

        slot.Cost = model.Cost;
        slot.Weight = model.Weight;
        slot.Damage = model.Damage;
        slot.ArmorRating = model.ArmorRating;
        slot.Workbench = model.Workbench;
        slot.Vendors = model.Vendors;
        slot.Materials = model.Materials;

        SettingsStorage.SaveCategory(category);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
