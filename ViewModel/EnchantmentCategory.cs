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
        Staff,
        Other
    }

    public static class EnchantmentCategoryHelper
    {
        public const string StaffEnchantType = "StaffEnchantment";

        public static EnchantmentCategory Classify(EnchantmentRecord ench)
        {
            // 1) Staff, BEFORE the cast type is looked at - that ordering is the whole point.
            //
            //    Cast type alone cannot tell a staff from a weapon: measured across the load order,
            //    of 443 staff enchantments 355 are FireAndForget (and so used to land among the
            //    weapons) and 88 are Concentration (which fell through to "Other"). Those 88 were
            //    the ENTIRE content of "Other" - it was never a category, just the hole the
            //    channelled staves fell into.
            //
            //    EnchantType says it outright, and only it does.
            if (string.Equals(ench.EnchantType, StaffEnchantType, StringComparison.OrdinalIgnoreCase))
                return EnchantmentCategory.Staff;

            // 2) Armor: ConstantEffect
            if (ench.CastType == "ConstantEffect")
                return EnchantmentCategory.Armor;

            // 3) Weapon: FireAndForget + Contact
            if (ench.CastType == "FireAndForget")
                return EnchantmentCategory.Weapon;

            // 4) Fallback. Expected to be empty on a freshly scanned database - but EnchantType is a
            //    new column, so before the first rescan it is NULL for every scanned record and the
            //    channelled staves land here again, exactly as they did before. Keeping the branch
            //    is what stops them disappearing from the tree in that window.
            return EnchantmentCategory.Other;
        }
    }
}
