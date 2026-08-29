using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using SkyrimCraftingTool.Model;
using SkyrimCraftingTool.Services;

namespace SkyrimCraftingTool.ViewModel
{
    public class SimpleFlags : IFormLinkOrIndexFlagGetter
    {
        public bool UseAliases => false;
        public bool UsePackageData => false;
    }

    public class PerkConditionViewModel : BaseConditionViewModel
    {
        public PerkConditionViewModel()
        {
            Type = CustomConditionType.HasPerk;
        }

        private FormKey _perkFormKey;
        public FormKey PerkFormKey
        {
            get => _perkFormKey;
            set
            {
                if (SetProperty(ref _perkFormKey, value))
                    OnPropertyChanged(nameof(IsDeadReference));
            }
        }

        private FormIDRecord? _selectedPerk;
        public FormIDRecord? SelectedPerk
        {
            get => _selectedPerk;
            set
            {
                if (SetProperty(ref _selectedPerk, value))
                {
                    if (value != null)
                        PerkFormKey = KeyFactory.ParseFormKey(value.Key);
                    OnPropertyChanged(nameof(IsDeadReference));
                }
            }
        }

        // A perk FormKey is set, but it didn't match anything in the active perk list at load time
        // (the mod it came from is disabled/removed).
        public bool IsDeadReference => !PerkFormKey.IsNull && SelectedPerk == null;

        private string _perkSearchText = string.Empty;
        public string PerkSearchText
        {
            get => _perkSearchText;
            set => SetProperty(ref _perkSearchText, value);
        }

        private bool _mustHavePerk = true;
        public bool MustHavePerk
        {
            get => _mustHavePerk;
            set => SetProperty(ref _mustHavePerk, value);
        }

        public override float ComparisonValue
        {
            get => MustHavePerk ? 1f : 0f;
            set => MustHavePerk = value != 0f;
        }

        public override ConditionFloat ToMutagenCondition() => new ConditionFloat
        {
            CompareOperator = CompareOperator.EqualTo,
            ComparisonValue = MustHavePerk ? 1f : 0f,
            Data = new HasPerkConditionData
            {
                Perk = new FormLinkOrIndex<IPerkGetter>(new SimpleFlags(), PerkFormKey)
            }
        };
    }
}
