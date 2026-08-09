using Mutagen.Bethesda.Skyrim;
using System.Collections.Generic;

namespace SkyrimCraftingTool.ViewModel
{
    public class ActorValueConditionViewModel : BaseConditionViewModel
    {
        public ActorValueConditionViewModel()
        {
            Type = CustomConditionType.GetActorValue;
        }

        public IEnumerable<ActorValue> ActorValueWhitelist { get; } = new[]
        {
            ActorValue.Smithing,
            ActorValue.Alchemy,
            ActorValue.Enchanting
        };

        private ActorValue _selectedSkill = ActorValue.Alchemy;
        public ActorValue SelectedSkill
        {
            get => _selectedSkill;
            set => SetProperty(ref _selectedSkill, value);
        }

        private float _requiredValue = 25f;
        public float RequiredValue
        {
            get => _requiredValue;
            set => SetProperty(ref _requiredValue, value);
        }

        public override float ComparisonValue
        {
            get => RequiredValue;
            set => RequiredValue = value;
        }

        public override ConditionFloat ToMutagenCondition() => new ConditionFloat
        {
            CompareOperator = CompareOperator.GreaterThanOrEqualTo,
            ComparisonValue = RequiredValue,
            Data = new GetActorValueConditionData { ActorValue = SelectedSkill }
        };
    }
}
