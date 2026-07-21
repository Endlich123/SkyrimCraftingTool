using SkyrimCraftingTool.Model;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SkyrimCraftingTool.ViewModel
{
    public enum EnchantmentCategory
    {
        Weapon,
        Armor,
        Other
    }

    public static class EnchantmentCategoryHelper
    {
        public static EnchantmentCategory Classify(EnchantmentRecord ench)
        {
            // 1) Armor: ConstantEffect
            if (ench.CastType == "ConstantEffect")
                return EnchantmentCategory.Armor;

            // 2) Armor: WornRestrictions present
            //if (!string.IsNullOrEmpty(ench.WornRestrictionListKey))
            //    return EnchantmentCategory.Armor;

            // 3) Weapon: FireAndForget + Contact
            if (ench.CastType == "FireAndForget")
                return EnchantmentCategory.Weapon;

            // 4) Fallback
            return EnchantmentCategory.Other;
        }
    }
}
