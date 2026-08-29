using SkyrimCraftingTool.Model;
using System.Collections.Generic;

namespace SkyrimCraftingTool.Services
{
    public interface IEnchantmentService
    {
        // Query
        List<EnchantmentRecord> GetAllEnchantments();

        // Basic fields
        void UpdateEnchantmentName(string key, string name);
        void UpdateEnchantmentCastType(string key, string castType);
        void UpdateEnchantmentTargetType(string key, string targetType);
        void UpdateEnchantmentCost(string key, float cost);

        // Worn restriction list (FLST)
        void UpdateEnchantmentWornRestrictionListKey(string key, string listKey);

        // Effects (full replace)
        void SaveEnchantmentEffects(string enchantmentKey, List<EnchantmentEffectRecord> effects);

        // Worn restriction keywords (full replace)
        void SaveEnchantmentWornRestrictionKeywords(string listKey, List<string> keywordKeys);

        // Reset: pristine (pre-edit) Name/Cost + clearing their shadow columns
        EnchantmentRecord GetOriginalEnchantment(string key);
        void ResetEnchantmentEdits(string key);

        // Reset: Effects / Worn Restriction Keywords (lazy _Original snapshot tables - see
        // Model/ItemDBHandler.cs's COBJ_Conditions_Original schema comment for the pattern)
        List<EnchantmentEffectRecord> GetOriginalEnchantmentEffects(string enchantmentKey);
        void ResetEnchantmentEffects(string enchantmentKey);
        List<string> GetOriginalWornRestrictionKeywords(string listKey);
        void ResetWornRestrictionKeywords(string listKey);
    }
}
