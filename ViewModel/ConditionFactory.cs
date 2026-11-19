using System;

namespace SkyrimCraftingTool.ViewModel
{
    // A fresh editable condition for a type, and what carries over when a row's type is changed.
    //
    // Changing the Type picker only changes an enum on the SAME view model; the Target editor is
    // picked by the view model's CLR type, so every condition list swaps in a new instance of the
    // right subclass (Remove + Insert - see ItemNodeVM.ReplaceConditionForTypeChange). The item
    // editor, the multi-selection and the preset editor all do that; this is the part they share.
    public static class ConditionFactory
    {
        public static BaseConditionViewModel Create(CustomConditionType type) => type switch
        {
            CustomConditionType.HasPerk => new PerkConditionViewModel(),
            CustomConditionType.GetIsSex => new SexConditionViewModel(),
            CustomConditionType.GetActorValue => new ActorValueConditionViewModel(),
            CustomConditionType.GetLevel => new LevelConditionViewModel(),
            CustomConditionType.GetStageDone => new QuestStageConditionViewModel(),
            CustomConditionType.GetItemCount => new ItemCountConditionViewModel(),
            CustomConditionType.EPTemperingItemIsEnchanted => new TemperEnchantedConditionViewModel(),
            CustomConditionType.GetStage => new QuestCurrentStageConditionViewModel(),
            _ => new PerkConditionViewModel(),
        };

        // A new instance for the type `old` was just switched to, with what still makes sense:
        // run-on, the value, and the OR flag - membership in an OR group belongs to the row's place
        // in the list, not to its type. The operator does not carry: each type has its own default.
        public static BaseConditionViewModel ForTypeChange(BaseConditionViewModel old)
        {
            var fresh = Create(old.Type);
            fresh.RunOnPlayer = old.RunOnPlayer;
            try { fresh.ComparisonValue = old.ComparisonValue; } catch { /* not every value means something in every type */ }
            if (old.IsOr) fresh.IsOr = true;
            return fresh;
        }
    }
}
