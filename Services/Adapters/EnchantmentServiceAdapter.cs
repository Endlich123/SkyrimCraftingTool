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

        public void UpdateEnchantmentCastType(string key, string castType)
            => ItemDBHandler.UpdateEnchantmentCastType(key, castType);

        public void UpdateEnchantmentTargetType(string key, string targetType)
            => ItemDBHandler.UpdateEnchantmentTargetType(key, targetType);

        public void UpdateEnchantmentCost(string key, float cost)
            => ItemDBHandler.UpdateEnchantmentCost(key, cost);

        public void UpdateEnchantmentWornRestrictionListKey(string key, string listKey)
            => ItemDBHandler.UpdateEnchantmentWornRestrictionListKey(key, listKey);

        public List<(string ListKey, List<string> KeywordKeys, bool IsEdited)> GetKnownWornRestrictionLists()
            => ItemDBHandler.GetKnownWornRestrictionLists();

        public List<string> GetWornRestrictionKeywordsForList(string listKey)
            => ItemDBHandler.GetWornRestrictionKeywordsForList(listKey);

        public Dictionary<string, string> GetFormListNamesByKey()
            => _handler.GetFormListNamesByKey();

        public bool IsWornRestrictionListEdited(string listKey)
            => ItemDBHandler.IsWornRestrictionListEdited(listKey);

        public int CountEnchantmentsUsingWornRestrictionList(string listKey)
            => ItemDBHandler.CountEnchantmentsUsingWornRestrictionList(listKey);

        public void SaveEnchantmentEffects(string enchantmentKey, List<EnchantmentEffectRecord> effects)
            => ItemDBHandler.SaveEnchantmentEffects(enchantmentKey, effects);

        public void SaveEnchantmentWornRestrictionKeywords(string listKey, List<string> keywordKeys)
            => ItemDBHandler.SaveWornRestrictionKeywords(listKey, keywordKeys);

        public EnchantmentRecord GetOriginalEnchantment(string key) => ItemDBHandler.GetOriginalEnchantment(key);
        public void ResetEnchantmentEdits(string key) => ItemDBHandler.ResetEnchantmentEdits(key);

        public List<EnchantmentEffectRecord> GetOriginalEnchantmentEffects(string enchantmentKey) => ItemDBHandler.GetOriginalEnchantmentEffects(enchantmentKey);
        public void ResetEnchantmentEffects(string enchantmentKey) => ItemDBHandler.ResetEnchantmentEffects(enchantmentKey);
        public List<string> GetOriginalWornRestrictionKeywords(string listKey) => ItemDBHandler.GetOriginalWornRestrictionKeywords(listKey);
        public void ResetWornRestrictionKeywords(string listKey) => ItemDBHandler.ResetWornRestrictionKeywords(listKey);
    }
}
