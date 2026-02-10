using DynamicData;
using Noggog;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace SkyrimCraftingTool;

public class CategorySettingsViewModel
{
    public string CategoryName { get; }

    public ObservableCollection<SlotSettingsViewModel> ArmorSlots { get; } = new();
    public ObservableCollection<SlotSettingsViewModel> WeaponTypes { get; } = new();

    public CategorySettingsViewModel(string category)
    {
        CategoryName = category;

        foreach (var slot in GlobalState.AllArmorSlots)
            ArmorSlots.Add(new SlotSettingsViewModel(category, slot.ToString()));

        foreach (var weapon in GlobalState.AllWeaponTypes)
            WeaponTypes.Add(new SlotSettingsViewModel(category, weapon.ToString()));
    }
}
