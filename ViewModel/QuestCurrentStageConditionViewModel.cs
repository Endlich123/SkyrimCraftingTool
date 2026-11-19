using System;
using System.Collections.Generic;
using System.Globalization;
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
                    OnPropertyChanged(nameof(StageOptions));
                    OnPropertyChanged(nameof(StageToolTip));
                }
            }
        }

        // A quest is set, but it is not in the active quest list (its plugin is disabled or gone).
        public bool IsDeadReference => !QuestFormKey.IsNull && SelectedQuest == null;

        // The stage box next to the quest - a second way into the Value column, nothing of its own:
        // picking a stage writes the Value, typing a Value shows in the box. The operator stays in
        // the row, so ">= 20" still reads "at least stage 20".
        public IReadOnlyList<QuestStageOption> StageOptions => QuestStageOption.For(SelectedQuest);

        public string StageToolTip => QuestStageOption.ToolTipFor(SelectedQuest, ComparisonValue);

        // The box's text, bound on every keystroke so a picked stage reaches the Value box at once.
        // Not raised back while it is being typed into - "1." would turn into "1" under the caret.
        private bool _typingStage;
        public string StageText
        {
            // Invariant, like the Value box's binding (WPF converts with en-US unless told otherwise).
            get => ComparisonValue.ToString(CultureInfo.InvariantCulture);
            set
            {
                if (!float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                    return;

                _typingStage = true;
                try { ComparisonValue = v; }
                finally { _typingStage = false; }
            }
        }

        private float _stage = 10f;
        public override float ComparisonValue
        {
            get => _stage;
            set
            {
                if (!SetProperty(ref _stage, value)) return;
                if (!_typingStage) OnPropertyChanged(nameof(StageText));
                OnPropertyChanged(nameof(StageToolTip));
            }
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
