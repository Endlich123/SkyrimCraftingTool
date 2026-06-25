using System.Collections.ObjectModel;

namespace SkyrimCraftingTool.Model
{
    public class EnchantmentRecord
    {
        // Plugin|FormID
        public string Key { get; set; } = "";

        public string Name { get; set; } = "";
        public string CastType { get; set; } = "";
        public float EnchantmentCost { get; set; }
        public string TargetType { get; set; } = "";

        // Plugin|FormID der FLST
        public string WornRestrictionListKey { get; set; } = "";

        public ObservableCollection<EnchantmentEffectRecord> Effects { get; set; }
            = new ObservableCollection<EnchantmentEffectRecord>();

        public ObservableCollection<string> WornRestrictionKeywords { get; set; }
            = new ObservableCollection<string>();


        public string Plugin => Key.Split('|')[0];
        public string FormID => Key.Split('|')[1];

        public bool IsWeaponEnchantment =>
            CastType == "FireAndForget" && TargetType == "Contact";

        public bool IsArmorEnchantment =>
            CastType == "ConstantEffect";
    }
}

