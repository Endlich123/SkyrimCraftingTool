using SkyrimCraftingTool.Model;
using SkyrimCraftingTool.ViewModel;
using System.Collections.Generic;

namespace SkyrimCraftingTool.Services
{
    // Bridges the Preset JSON's ConditionEntry POCO and the BaseConditionViewModel editor already
    // used for item COBJ conditions, by reusing ConditionMapper against a throwaway
    // COBJConditionRecord (Id/COBJKey are irrelevant here — only ConditionType/Target/Value/RunOn).
    public static class PresetConditionMapper
    {
        public static BaseConditionViewModel ToViewModel(
            ConditionEntry entry,
            IEnumerable<FormIDRecord> allPerks,
            IEnumerable<FormIDRecord> allQuests)
        {
            var rec = new COBJConditionRecord
            {
                ConditionType = entry.ConditionType,
                Target = entry.Target,
                Value = entry.Value,
                RunOn = entry.RunOn
            };

            return ConditionMapper.ToViewModel(rec, allPerks, allQuests);
        }

        public static ConditionEntry ToEntry(BaseConditionViewModel vm)
        {
            var rec = ConditionMapper.ToRecord(vm, cobjKey: "");
            return new ConditionEntry
            {
                ConditionType = rec.ConditionType,
                Target = rec.Target,
                Value = rec.Value,
                RunOn = rec.RunOn
            };
        }
    }
}
