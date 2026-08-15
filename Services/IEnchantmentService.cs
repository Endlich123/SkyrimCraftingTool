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
        void UpdateEnchantmentEditorID(string key, string editorID);
        void UpdateEnchantmentCastType(string key, string castType);
        void UpdateEnchantmentTargetType(string key, string targetType);
        void UpdateEnchantmentCost(string key, float cost);

        // Worn restriction list (FLST)
        void UpdateEnchantmentWornRestrictionListKey(string key, string listKey);

        // Effects (full replace)
        void SaveEnchantmentEffects(string enchantmentKey, List<EnchantmentEffectRecord> effects);

        // Worn restriction keywords (full replace)
        void SaveEnchantmentWornRestrictionKeywords(string listKey, List<string> keywordKeys);
    }
}
