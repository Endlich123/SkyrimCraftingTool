using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using System.Collections.Generic;

namespace SkyrimCraftingTool.ViewModel
{
    public enum Gender { Male, Female }

    public class SexConditionViewModel : BaseConditionViewModel
    {
        public SexConditionViewModel()
        {
            Type = CustomConditionType.GetIsSex;
        }

        public IEnumerable<Gender> Genders { get; } = new[] { Gender.Male, Gender.Female };

        private Gender _targetGender = Gender.Female;
        public Gender TargetGender
        {
            get => _targetGender;
            set => SetProperty(ref _targetGender, value);
        }

        private float _comparisonValue = 1f;
        public override float ComparisonValue
        {
            get => _comparisonValue;
            set => SetProperty(ref _comparisonValue, value);
        }

        public override ConditionFloat ToMutagenCondition()
        {
            var data = new GetIsSexConditionData
            {
                MaleFemaleGender = TargetGender == Gender.Male
                    ? MaleFemaleGender.Male
                    : MaleFemaleGender.Female
            };

            return new ConditionFloat
            {
                CompareOperator = CompareOperator.EqualTo,
                ComparisonValue = ComparisonValue,
                Data = data
            };
        }
    }
}
