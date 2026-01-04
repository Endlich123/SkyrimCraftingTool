using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace SkyrimCraftingTool;

public class ItemCardVM : INotifyPropertyChanged
{
    public IGameRecord Record { get; }

    // -------------------------
    // Panels
    // -------------------------
    private VendorPanelVM _vendorPanel;
    public VendorPanelVM VendorPanel
    {
        get => _vendorPanel;
        set
        {
            if (_vendorPanel == value)
                return;

            // altes Event lösen, neues binden
            if (_vendorPanel != null)
                _vendorPanel.VendorsChanged -= OnVendorsChanged;

            _vendorPanel = value;
            OnPropertyChanged();

            if (_vendorPanel != null)
                _vendorPanel.VendorsChanged += OnVendorsChanged;
        }
    }

    private void OnVendorsChanged(List<string> vendors)
    {
        SelectedVendors = vendors;
    }

    // Workbench
    public IEnumerable<string> Workbenches => Program.WorkbenchTypes;

    // -------------------------
    // MATERIAL LIST
    // -------------------------
    private ObservableCollection<MaterialEntry> _materialList;
    public ObservableCollection<MaterialEntry> MaterialList
    {
        get => _materialList;
        set
        {
            if (_materialList == value)
                return;

            _materialList = value;
            OnPropertyChanged();
        }
    }

    public ICommand AddMaterialCommand { get; }
    public ICommand RemoveMaterialCommand { get; }

    // -------------------------
    // Konstruktor
    // -------------------------
    public ItemCardVM(IGameRecord record, IEnumerable<string> allVendorKeywords)
    {
        Record = record;

        // Vendor Panel + Rückkanal
        VendorPanel = new VendorPanelVM(allVendorKeywords, record.Vendor);

        // Shared
        EditorID = record.EditorID;
        Value = (int)record.Value;
        Weight = record.Weight;
        SelectedWorkbench = record.Workbench;
        SelectedVendors = new List<string>(record.Vendor);

        // Armor
        if (record is ArmorRecord armor)
        {
            ArmorRating = (int)armor.ArmorRating;
            ArmorSlot = armor.SelectedSlot.ToString();
        }

        // Weapon
        if (record is WeaponRecord weapon)
        {
            Damage = (int)weapon.Damage;
            WeaponType = weapon.WeaponType;
        }

        // Materials
        MaterialList = new ObservableCollection<MaterialEntry>(
            record.Materials.Select(kvp => new MaterialEntry
            {
                Material = kvp.Key,
                Amount = kvp.Value
            })
        );

        AddMaterialCommand = new RelayCommand(_ => AddMaterial());
        RemoveMaterialCommand = new RelayCommand(m => RemoveMaterial(m as MaterialEntry));
    }

    // -------------------------
    // Shared Properties
    // -------------------------
    public string FormKey => Record.FormKey.ToString();

    private string _editorID;
    public string EditorID
    {
        get => _editorID;
        set { _editorID = value; OnPropertyChanged(); }
    }

    private int _value;
    public int Value
    {
        get => _value;
        set { _value = value; OnPropertyChanged(); }
    }

    private float _weight;
    public float Weight
    {
        get => _weight;
        set { _weight = value; OnPropertyChanged(); }
    }

    private string _selectedWorkbench;
    public string SelectedWorkbench
    {
        get => _selectedWorkbench;
        set { _selectedWorkbench = value; OnPropertyChanged(); }
    }

    // -------------------------
    // Vendors
    // -------------------------
    private List<string> _selectedVendors;
    public List<string> SelectedVendors
    {
        get => _selectedVendors;
        set { _selectedVendors = value; OnPropertyChanged(); }
    }

    // -------------------------
    // Armor
    // -------------------------
    private int _armorRating;
    public int ArmorRating
    {
        get => _armorRating;
        set { _armorRating = value; OnPropertyChanged(); }
    }

    private string _armorSlot;
    public string ArmorSlot
    {
        get => _armorSlot;
        set { _armorSlot = value; OnPropertyChanged(); }
    }

    public IEnumerable<string> ArmorSlots => Enum.GetNames(typeof(ArmorSlot));

    // -------------------------
    // Weapon
    // -------------------------
    private int _damage;
    public int Damage
    {
        get => _damage;
        set { _damage = value; OnPropertyChanged(); }
    }

    private string _weaponType;
    public string WeaponType
    {
        get => _weaponType;
        set { _weaponType = value; OnPropertyChanged(); }
    }

    public IEnumerable<string> WeaponTypes => Enum.GetNames(typeof(WeaponTypes));

    public bool IsArmor => Record is ArmorRecord;
    public bool IsWeapon => Record is WeaponRecord;

    // -------------------------
    // Material Commands
    // -------------------------
    private void AddMaterial()
    {
        MaterialList.Add(new MaterialEntry
        {
            Material = Program.materialMap.Values.First(), // Default-Material
            Amount = 1
        });
    }

    private void RemoveMaterial(MaterialEntry entry)
    {
        if (entry != null)
            MaterialList.Remove(entry);
    }

    // -------------------------
    // INotifyPropertyChanged
    // -------------------------
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
