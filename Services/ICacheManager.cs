using SkyrimCraftingTool.Model;
using System.Collections.Generic;

namespace SkyrimCraftingTool.Services
{
    public interface ICacheManager
    {
        CacheSnapshot BuildCachesFromDB(List<PluginInfo> activePlugins);

        // Armor updates
        void UpdateArmorName(string key, string name);
        void UpdateArmorValue(string key, int value);
        void UpdateArmorWeight(string key, double weight);
        void UpdateArmorRating(string key, double armorRating);
        void UpdateArmorBodySlotMask(string key, long bodySlotMask);
        void UpdateArmorKeywords(string key, List<string> keywordKeys);
        void UpdateArmorContainerString(string key, string containerString);

        // Weapon updates
        void UpdateWeaponName(string key, string name);
        void UpdateWeaponValue(string key, int value);
        void UpdateWeaponWeight(string key, double weight);
        void UpdateWeaponDamage(string key, double damage);
        void UpdateWeaponSpeed(string key, double speed);
        void UpdateWeaponReach(string key, double reach);
        void UpdateWeaponStagger(string key, double stagger);
        void UpdateWeaponKeywords(string key, List<string> keywordKeys);
        void UpdateWeaponContainerString(string key, string containerString);

        // Recipe updates
        void UpdateRecipe(COBJRecord rec);
        void UpdateRecipeConditions(string cobjKey, List<COBJConditionRecord> conditions);

        // Enchantment updates
        void UpdateEnchantmentName(string key, string name);
        void UpdateEnchantmentEditorID(string key, string editorID);
        void UpdateEnchantmentCastType(string key, string castType);
        void UpdateEnchantmentTargetType(string key, string targetType);
        void UpdateEnchantmentCost(string key, float cost);
        void UpdateEnchantmentWornRestrictionListKey(string key, string listKey);
        void SaveEnchantmentEffects(string key, List<EnchantmentEffectRecord> effects);
        void SaveEnchantmentWornRestrictionKeywords(string listKey, List<string> keywordKeys);

    }
}
