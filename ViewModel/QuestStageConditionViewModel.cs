using System;
using System.Collections.Generic;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using SkyrimCraftingTool.Model;
using SkyrimCraftingTool.Services;

namespace SkyrimCraftingTool.ViewModel
{
    public class QuestStageConditionViewModel : BaseConditionViewModel
    {
        public QuestStageConditionViewModel()
        {
            Type = CustomConditionType.GetStageDone;
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
                    OnPropertyChanged(nameof(AvailableStages));
                    OnPropertyChanged(nameof(HasStageList));
                }
            }
        }

        // A quest FormKey is set, but it didn't match anything in the active quest list at load time.
        public bool IsDeadReference => !QuestFormKey.IsNull && SelectedQuest == null;

        // Stage indices defined by the selected quest, for the stage picker. Empty when no quest is
        // picked or the QUST record defines no stages (the ComboBox stays editable so a value can
        // still be typed).
        public IReadOnlyList<int> AvailableStages => SelectedQuest?.Stages ?? Array.Empty<int>();
        public bool HasStageList => AvailableStages.Count > 0;

        private int _stage = 100;
        public int Stage
        {
            get => _stage;
            set => SetProperty(ref _stage, value);
        }

        public override float ComparisonValue
        {
            get => Stage;
            set => Stage = (int)value;
        }

        public override ConditionFloat ToMutagenCondition() => new ConditionFloat
        {
            CompareOperator = CompareOperator.EqualTo,
            ComparisonValue = 1f,
            Data = new GetStageDoneConditionData
            {
                Quest = new FormLinkOrIndex<IQuestGetter>(new SimpleFlags(), QuestFormKey),
                Stage = Stage
            }
        };
    }
}
