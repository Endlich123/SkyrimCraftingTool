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
    }
}
