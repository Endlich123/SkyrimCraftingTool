using SkyrimCraftingTool.Model;
using SkyrimCraftingTool.ViewModel;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SkyrimCraftingTool.Services.EnchantmentFilter
{
    public static class MagicEffectFilter
    {
        public static bool IsValidEffectForEnchantment(MagicEffectsRecords mgef, EnchantmentCategory category)
        {
            string id = mgef.EditorID?.ToUpper() ?? "";
            string name = mgef.Name?.ToUpper() ?? "";

            // Global blacklist
            if (id.StartsWith("ALCH") || id.StartsWith("INGR") || id.StartsWith("DISE") || id.Contains("POTION"))
                return false;

            if (category == EnchantmentCategory.Armor)
            {
                // Armor effects: no magnitude, no duration
                if (!mgef.HasMagnitude && !mgef.HasDuration)
                    return true;

                // Resist effects
                if (name.Contains("RESIST"))
                    return true;

                // Fortify effects
                if (name.Contains("FORTIFY"))
                    return true;

                return false;
            }

            if (category == EnchantmentCategory.Weapon)
            {
                // Weapon effects: magnitude present
                if (mgef.HasMagnitude)
                    return true;

                // Damage effects
                if (name.Contains("DAMAGE"))
                    return true;

                // Absorb effects
                if (name.Contains("ABSORB"))
                    return true;

                // Drain effects
                if (name.Contains("DRAIN"))
                    return true;

                // Elemental effects
                if (name.Contains("FIRE") || name.Contains("FROST") || name.Contains("SHOCK"))
                    return true;

                return false;
            }

            return true;
        }

    }

}
