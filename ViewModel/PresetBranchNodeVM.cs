using System.Collections.ObjectModel;

namespace SkyrimCraftingTool.ViewModel
{
    // Fixed "Armor" / "Weapon" branch under a PresetNodeVM, holding that branch's Slot/Type leaves.
    public class PresetBranchNodeVM : ViewModelBase
    {
        public string Label { get; }
        public ObservableCollection<PresetSlotNodeVM> Children { get; } = new();

        public PresetBranchNodeVM(string label)
        {
            Label = label;
        }

        public override string ToString() => Label;
    }
}
