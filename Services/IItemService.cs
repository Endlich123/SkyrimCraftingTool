using SkyrimCraftingTool.Model;
using SkyrimCraftingTool.ViewModel;
using System.Collections.Generic;

namespace SkyrimCraftingTool.Services
{
    public interface IItemService
    {
        // Build or refresh the item DB from the provided plugins
        void PutIntoDataBank(List<PluginInfo> plugins);

        // Forces the in-memory cache to load (if not already loaded) on the calling thread.
        // Call this once before fanning out parallel per-plugin queries so they all hit an
        // already-warm cache instead of racing/blocking on the internal load lock.
        void EnsureCacheLoaded();

        // Query helpers used by ViewModels
        IEnumerable<ArmorRecord> GetArmorByPlugin(string pluginFileName);
        IEnumerable<WeaponRecord> GetWeaponsByPlugin(string pluginFileName);
        IEnumerable<COBJRecord> GetCOBJByPlugin(string pluginFileName);

        // Enchantments
        List<EnchantmentRecord> GetAllEnchantments();

        // COBJ helpers (create/save recipes)
        COBJRecord CreateNewCOBJRecordForItem(ItemNodeVM item, bool isTemper);
        void SaveCOBJ(COBJRecord rec);

        // COBJ Conditions
        List<COBJConditionRecord> GetCOBJConditions(string cobjKey);
        void SaveCOBJConditions(string cobjKey, List<COBJConditionRecord> conditions);

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

        // Reset: pristine (pre-edit) base values + clearing the shadow columns Reset reverts
        ArmorRecord GetOriginalArmor(string key);
        WeaponRecord GetOriginalWeapon(string key);
        void ResetArmorEdits(string key);
        void ResetWeaponEdits(string key);

        // Orphaned edits: Armor/Weapons rows edited but no longer in the scanned load order.
        List<OrphanedEdit> GetOrphanedItemEdits();
        void DeleteItemRow(string table, string key);
        COBJRecord GetOriginalCOBJ(string key);
        void ResetCOBJEdits(string key);
        void DeleteCOBJ(string key);
        List<COBJConditionRecord> GetOriginalCOBJConditions(string cobjKey);
        void ResetCOBJConditions(string cobjKey);
    }
}
