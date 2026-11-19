using SkyrimCraftingTool.Model;
using SkyrimCraftingTool.ViewModel;
using System.Collections.Generic;

namespace SkyrimCraftingTool.Services
{
    // Bridges the Preset JSON's ConditionEntry POCO and the BaseConditionViewModel editor already
    // used for item COBJ conditions, by reusing ConditionMapper against a throwaway
    // COBJConditionRecord (Id/COBJKey are irrelevant here). Every field goes across, operator and
    // OR flag included - see ConditionEntry.
    public static class PresetConditionMapper
    {
        public static BaseConditionViewModel ToViewModel(
            ConditionEntry entry,
            IEnumerable<FormIDRecord> allPerks,
            IEnumerable<FormIDRecord> allQuests)
            => ConditionMapper.ToViewModel(entry.ToRecord(), allPerks, allQuests);

        public static ConditionEntry ToEntry(BaseConditionViewModel vm)
            => ConditionEntry.FromRecord(ConditionMapper.ToRecord(vm, cobjKey: ""));
    }
}
