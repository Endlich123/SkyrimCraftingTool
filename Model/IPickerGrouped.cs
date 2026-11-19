using System.Collections.Generic;

namespace SkyrimCraftingTool.Model
{
    // Something a picker can sort into groups for its filter chips (ViewModel.PickerFilter): a perk
    // into its skill tree(s), a material into its kind. Empty when the record has no group.
    public interface IPickerGrouped
    {
        IEnumerable<string> PickerGroups { get; }
    }
}
