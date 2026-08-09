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
            set => SetProperty(ref _questFormKey, value);
        }

        private FormIDRecord? _selectedQuest;
        public FormIDRecord? SelectedQuest
        {
            get => _selectedQuest;
            set
            {
                if (SetProperty(ref _selectedQuest, value) && value != null)
                    QuestFormKey = KeyFactory.ParseFormKey(value.Key);
            }
        }

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
