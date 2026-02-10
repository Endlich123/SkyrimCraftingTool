using DynamicData;
using Noggog;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using static SkyrimCraftingTool.GlobalState;

namespace SkyrimCraftingTool;

public class SlotSettingsViewModel : INotifyPropertyChanged
{
    private bool _isLoading = false;

    public string Category { get; }
    public string SlotName { get; }

    public IEnumerable<string> AllMaterialNames => GlobalState.MaterialMap.Values;
    public IEnumerable<string> Workbenches => GlobalState.WorkbenchTypes;

    private float _cost;
    public float Cost
    {
        get => _cost;
        set
        {
            if (_cost != value)
            {
                _cost = value;
                OnPropertyChanged();
                Save();
            }
        }
    }

    private float _weight;
    public float Weight
    {
        get => _weight;
        set
        {
            if (_weight != value)
            {
                _weight = value;
                OnPropertyChanged();
                Save();
            }
        }
    }

    private float _damage;
    public float Damage
    {
        get => _damage;
        set
        {
            if (_damage != value)
            {
                _damage = value;
                OnPropertyChanged();
                Save();
            }
        }
    }

    private float _armorRating;
    public float ArmorRating
    {
        get => _armorRating;
        set
        {
            if (_armorRating != value)
            {
                _armorRating = value;
                OnPropertyChanged();
                Save();
            }
        }
    }

    private string _selectedWorkbench;
    public string SelectedWorkbench
    {
        get => _selectedWorkbench;
        set
        {
            if (_selectedWorkbench != value)
            {
                _selectedWorkbench = value;
                OnPropertyChanged();
                Save();
            }
        }
    }

    public List<string> Vendors { get; set; } = new();
    public ObservableCollection<VendorKeywordVM> VendorOptions { get; } = new();

    public bool IsWeapon => Enum.TryParse<WeaponTypes>(SlotName, out _);
    public bool IsArmor => !IsWeapon;

    public ICommand AddMaterialCommand { get; }
    public ICommand RemoveMaterialCommand { get; }

    // ViewModel list for UI binding
    public ObservableCollection<MaterialEntry> Materials { get; } = new();

    public SlotSettingsViewModel(string category, string slotName)
    {
        Category = category;
        SlotName = slotName;

        AddMaterialCommand = new RelayCommand(_ => AddMaterial());
        RemoveMaterialCommand = new RelayCommand(m => RemoveMaterial((MaterialEntry)m));

        LoadFromGlobalState();
    }

    private void AddMaterial()
    {
        Materials.Add(new MaterialEntry(this) { Material = "", Amount = 1 });
    }

    private void RemoveMaterial(MaterialEntry entry)
    {
        Materials.Remove(entry);
        Save();
    }

    private void LoadFromGlobalState()
    {
        _isLoading = true;

        VendorOptions.Clear();
        Materials.Clear();

        string key = $"{Category}:{SlotName}";

        if (GlobalState.LoadedSettings.Settings.TryGetValue(key, out var data))
        {
            Cost = data.Cost;
            Weight = data.Weight;
            Damage = data.Damage;
            ArmorRating = data.ArmorRating;
            Vendors = data.Vendors;
            SelectedWorkbench = data.Workbench;

            foreach (var v in GlobalState.VendorKeywords)
                VendorOptions.Add(new VendorKeywordVM(v, data.Vendors.Contains(v), this));

            foreach (var m in data.Materials)
            {
                Materials.Add(new MaterialEntry(this)
                {
                    Material = m.Material,
                    Amount = m.Amount
                });
            }
        }
        else
        {
            Vendors = new List<string>(GlobalState.VendorKeywords);
            SelectedWorkbench = GlobalState.WorkbenchTypes.FirstOrDefault();

            foreach (var v in GlobalState.VendorKeywords)
                VendorOptions.Add(new VendorKeywordVM(v, true, this));
        }

        _isLoading = false;
    }


    public void Save()
    {
        if (_isLoading)
            return;

        string key = $"{Category}:{SlotName}";

        Vendors = VendorOptions
            .Where(v => v.IsSelected)
            .Select(v => v.Keyword)
            .ToList();

        GlobalState.LoadedSettings.Settings[key] = new SlotSettingsData
        {
            Cost = Cost,
            Weight = Weight,
            Damage = Damage,
            ArmorRating = ArmorRating,

            Materials = Materials
                .Select(m => new SlotSettingsData.MaterialEntryData
                {
                    Material = m.Material,
                    Amount = m.Amount
                })
                .ToList(),

            Vendors = Vendors,
            Workbench = SelectedWorkbench
        };

        GlobalState.Save();
    }


    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
