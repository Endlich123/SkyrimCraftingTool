using Mutagen.Bethesda.Skyrim;

namespace SkyrimCraftingTool.ViewModel
{
    // EPTemperingItemIsEnchanted: "is the item being tempered enchanted?" - 1 if so. No parameter,
    // only meaningful in a temper recipe. On its own it is NOT a perk check: vanilla pairs it as
    //
    //     EPTemperingItemIsEnchanted != 1  [OR]
    //     HasPerk ArcaneBlacksmith   == 1
    //
    // i.e. "not enchanted, or has Arcane Blacksmith". 5,540 of the 5,542 on the real modlist are
    // exactly that. Editable since 2026-10-24 so a temper recipe the tool creates can carry the gate
    // (ItemNodeVM.CreateTemperRecipe adds the pair by default).
    public class TemperEnchantedConditionViewModel : BaseConditionViewModel
    {
        public TemperEnchantedConditionViewModel()
        {
            Type = CustomConditionType.EPTemperingItemIsEnchanted;
            // A new one starts as the vanilla half of the pair. The operator is stored explicitly
            // because the builder's fallback for a missing one is "==".
            StoredCompareOperator = "NotEqualTo";
            StoredFlags = "OR";
        }

        // Skyrim.esm's Arcane Blacksmith perk - the other half of the gate.
        public const string ArcaneBlacksmithKey = "Skyrim.esm|05218E";

        private float _value = 1f;
        public override float ComparisonValue
        {
            get => _value;
            set => SetProperty(ref _value, value);
        }

        public override ConditionFloat ToMutagenCondition() => new ConditionFloat
        {
            CompareOperator = CompareOperator.NotEqualTo,
            ComparisonValue = ComparisonValue,
            Data = new EPTemperingItemIsEnchantedConditionData(),
        };
    }
}
