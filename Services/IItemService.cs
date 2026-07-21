using SkyrimCraftingTool.Model;
using SkyrimCraftingTool.ViewModel;
using System.Collections.Generic;

namespace SkyrimCraftingTool.Services
{
    public interface IItemService
    {
        // Build or refresh the item DB from the provided plugins
        void PutIntoDataBank(List<PluginInfo> plugins);

        // Query helpers used by ViewModels
        IEnumerable<ArmorRecord> GetArmorByPlugin(string pluginFileName);
        IEnumerable<WeaponRecord> GetWeaponsByPlugin(string pluginFileName);
        IEnumerable<COBJRecord> GetCOBJByPlugin(string pluginFileName);

        // Enchantments
        List<EnchantmentRecord> GetAllEnchantments();

        // COBJ helpers (create/save recipes)
        COBJRecord CreateNewCOBJRecordForItem(ItemNodeVM item, bool isTemper);
        void SaveCOBJ(COBJRecord rec);

        // Armor
        void UpdateArmorName(string key, string name);
        void UpdateArmorWeight(string key, double weight);
        void UpdateArmorValue(string key, int value);
        void UpdateArmorRating(string key, double armorRating);
        void UpdateArmorBodySlotMask(string key, long bodySlotMask);
        void UpdateArmorKeywords(string key, List<string> keywordKeys);
        void UpdateArmorContainerString(string key, string containerString);

        // Weapon
        void UpdateWeaponName(string key, string name);
        void UpdateWeaponWeight(string key, double weight);
        void UpdateWeaponValue(string key, int value);
        void UpdateWeaponDamage(string key, double damage);
        void UpdateWeaponSpeed(string key, double speed);
        void UpdateWeaponReach(string key, double reach);
        void UpdateWeaponStagger(string key, double stagger);
        void UpdateWeaponKeywords(string key, List<string> keywordKeys);
        void UpdateWeaponContainerString(string key, string containerString);
        System.Collections.Generic.IList<object> SearchByType(string v);
    }
}
