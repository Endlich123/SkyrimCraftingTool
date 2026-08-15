using SkyrimCraftingTool.Model;
using System.Collections.Generic;

namespace SkyrimCraftingTool.Services.Adapters
{
    public class EnchantmentServiceAdapter : IEnchantmentService
    {
        private readonly ItemDBHandler _handler;

        public EnchantmentServiceAdapter(ItemDBHandler handler)
        {
            _handler = handler;
        }

        public List<EnchantmentRecord> GetAllEnchantments()
            => _handler.GetAllEnchantments();

        public void UpdateEnchantmentName(string key, string name)
            => ItemDBHandler.UpdateEnchantmentName(key, name);

        public void UpdateEnchantmentEditorID(string key, string editorID)
            => ItemDBHandler.UpdateEnchantmentEditorID(key, editorID);

        public void UpdateEnchantmentCastType(string key, string castType)
            => ItemDBHandler.UpdateEnchantmentCastType(key, castType);

        public void UpdateEnchantmentTargetType(string key, string targetType)
            => ItemDBHandler.UpdateEnchantmentTargetType(key, targetType);

        public void UpdateEnchantmentCost(string key, float cost)
            => ItemDBHandler.UpdateEnchantmentCost(key, cost);

        public void UpdateEnchantmentWornRestrictionListKey(string key, string listKey)
            => ItemDBHandler.UpdateEnchantmentWornRestrictionListKey(key, listKey);

        public void SaveEnchantmentEffects(string enchantmentKey, List<EnchantmentEffectRecord> effects)
            => ItemDBHandler.SaveEnchantmentEffects(enchantmentKey, effects);

        public void SaveEnchantmentWornRestrictionKeywords(string listKey, List<string> keywordKeys)
            => ItemDBHandler.SaveWornRestrictionKeywords(listKey, keywordKeys);
    }
}
