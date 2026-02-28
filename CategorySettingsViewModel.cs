using System.Collections.ObjectModel;

namespace SkyrimCraftingTool;

public class CategorySettingsViewModel
{
    private readonly CategorySettings _model;

    public string CategoryName => _model.CategoryName;

    public ObservableCollection<SlotSettingsViewModel> ArmorSlots { get; } = new();
    public ObservableCollection<SlotSettingsViewModel> WeaponTypes { get; } = new();

    public CategorySettingsViewModel(string category)
    {
        _model = SettingsStorage.LoadCategory(category);

        // Falls Datei leer → Slots generieren
        if (_model.Slots.Count == 0)
        {
            foreach (var slot in GlobalState.AllArmorSlots)
                _model.Slots.Add(new SlotSettingsData
                {
                    SlotName = slot.ToString(),
                    IsWeapon = false
                });

            foreach (var weapon in GlobalState.AllWeaponTypes)
                _model.Slots.Add(new SlotSettingsData
                {
                    SlotName = weapon.ToString(),
                    IsWeapon = true
                });

            SettingsStorage.SaveCategory(_model);
        }

        // Slots laden
        foreach (var slot in _model.Slots)
        {
            var vm = new SlotSettingsViewModel(CategoryName, slot);

            if (slot.IsWeapon)
                WeaponTypes.Add(vm);
            else
                ArmorSlots.Add(vm);
        }
    }

    public void Save()
    {
        _model.Slots = ArmorSlots
            .Concat(WeaponTypes)
            .Select(vm => vm.ToModel())
            .ToList();

        SettingsStorage.SaveCategory(_model);
    }
}
