using System;
using System.Collections.Generic;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using SkyrimCraftingTool.Model;
using SkyrimCraftingTool.Services;

namespace SkyrimCraftingTool.ViewModel
{
    // GetStage: the quest's CURRENT stage compared against the value - "Quest >= 20" unlocks a recipe
    // once the quest got that far. Not GetStageDone (QuestStageConditionViewModel), which asks whether
    // one particular stage was ever set and always compares to 1.
    //
    // 227 on the real modlist (2026-10-24), 23 plugins; 147 of them ">=", the rest "==", "<", ">".
    // So the stage is the Value column and the operator matters, unlike GetStageDone.
    public class QuestCurrentStageConditionViewModel : BaseConditionViewModel
    {
        public QuestCurrentStageConditionViewModel()
        {
            Type = CustomConditionType.GetStage;
        }

        private FormKey _questFormKey;
        public FormKey QuestFormKey
        {
            get => _questFormKey;
            set
            {
                if (SetProperty(ref _questFormKey, value))
                    OnPropertyChanged(nameof(IsDeadReference));
            }
        }

        private FormIDRecord? _selectedQuest;
        public FormIDRecord? SelectedQuest
        {
            get => _selectedQuest;
            set
            {
                if (SetProperty(ref _selectedQuest, value))
                {
                    if (value != null)
                        QuestFormKey = KeyFactory.ParseFormKey(value.Key);
                    OnPropertyChanged(nameof(IsDeadReference));
                    OnPropertyChanged(nameof(StagesHint));
                }
            }
        }

        // A quest is set, but it is not in the active quest list (its plugin is disabled or gone).
        public bool IsDeadReference => !QuestFormKey.IsNull && SelectedQuest == null;

        // The stages the quest defines, as a hint next to the picker - the value is typed in the
        // Value column, where the operator sits too.
        public string StagesHint => SelectedQuest?.Stages is { Count: > 0 } s
            ? "Stages: " + string.Join(", ", s)
            : "";

        private float _stage = 10f;
        public override float ComparisonValue
        {
            get => _stage;
            set => SetProperty(ref _stage, value);
        }

        public override ConditionFloat ToMutagenCondition()
        {
            var data = new GetStageConditionData();
            data.Quest.Link.SetTo(QuestFormKey);
            return new ConditionFloat
            {
                CompareOperator = CompareOperator.GreaterThanOrEqualTo,
                ComparisonValue = ComparisonValue,
                Data = data,
            };
        }
    }
}
