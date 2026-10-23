using Mutagen.Bethesda.Skyrim;

namespace SkyrimCraftingTool.ViewModel
{
    public class LevelConditionViewModel : BaseConditionViewModel
    {
        public LevelConditionViewModel()
        {
            Type = CustomConditionType.GetLevel;
        }

        private float _requiredLevel = 10f;
        public float RequiredLevel
        {
            get => _requiredLevel;
            set => SetProperty(ref _requiredLevel, value);
        }

        public override float ComparisonValue
        {
            get => RequiredLevel;
            set => RequiredLevel = value;
        }

        public override ConditionFloat ToMutagenCondition() => new ConditionFloat
        {
            CompareOperator = CompareOperator.GreaterThanOrEqualTo,
            ComparisonValue = RequiredLevel,
            Data = new GetLevelConditionData()
        };
    }
}
