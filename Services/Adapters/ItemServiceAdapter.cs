using SkyrimCraftingTool.Model;
using SkyrimCraftingTool.ViewModel;
using System.Collections.Generic;

namespace SkyrimCraftingTool.Services.Adapters
{
    public class ItemServiceAdapter : SkyrimCraftingTool.Services.IItemService
    {
        private readonly ItemDBHandler _handler;

        public ItemServiceAdapter(ItemDBHandler handler)
        {
            _handler = handler;
        }

        public void PutIntoDataBank(List<PluginInfo> plugins) => _handler.PutIntoDataBank(plugins);

        public IEnumerable<ArmorRecord> GetArmorByPlugin(string pluginFileName) => _handler.GetArmorByPlugin(pluginFileName);
        public IEnumerable<WeaponRecord> GetWeaponsByPlugin(string pluginFileName) => _handler.GetWeaponsByPlugin(pluginFileName);
        public IEnumerable<COBJRecord> GetCOBJByPlugin(string pluginFileName) => _handler.GetCOBJByPlugin(pluginFileName);

        public List<EnchantmentRecord> GetAllEnchantments() => _handler.GetAllEnchantments();

        public COBJRecord CreateNewCOBJRecordForItem(ItemNodeVM item, bool isTemper) => _handler.CreateNewCOBJRecordForItem(item, isTemper);
        public System.Collections.Generic.IList<object> SearchByType(string type) => _handler.SearchByType(type);

        public void SaveCOBJ(COBJRecord rec) => _handler.SaveCOBJ(rec);

        // Armor updates
        public void UpdateArmorName(string key, string name) => ItemDBHandler.UpdateArmorName(key, name);
        public void UpdateArmorWeight(string key, double weight) => ItemDBHandler.UpdateArmorWeight(key, weight);
        public void UpdateArmorValue(string key, int value) => ItemDBHandler.UpdateArmorValue(key, value);
        public void UpdateArmorRating(string key, double armorRating) => ItemDBHandler.UpdateArmorRating(key, armorRating);
        public void UpdateArmorBodySlotMask(string key, long bodySlotMask) => ItemDBHandler.UpdateArmorBodySlotMask(key, bodySlotMask);
        public void UpdateArmorKeywords(string key, List<string> keywordKeys)
        {
            var col = new System.Collections.ObjectModel.ObservableCollection<SkyrimCraftingTool.ViewModel.KeywordSelectionVM>();
            foreach (var k in keywordKeys)
                col.Add(new SkyrimCraftingTool.ViewModel.KeywordSelectionVM(k, "", true, null));

            ItemDBHandler.UpdateArmorKeywords(key, col);
        }
        public void UpdateArmorContainerString(string key, string containerString) => ItemDBHandler.UpdateArmorContainerString(key, containerString);

        // Weapon updates
        public void UpdateWeaponName(string key, string name) => ItemDBHandler.UpdateWeaponName(key, name);
        public void UpdateWeaponWeight(string key, double weight) => ItemDBHandler.UpdateWeaponWeight(key, weight);
        public void UpdateWeaponValue(string key, int value) => ItemDBHandler.UpdateWeaponValue(key, value);
        public void UpdateWeaponDamage(string key, double damage) => ItemDBHandler.UpdateWeaponDamage(key, damage);
        public void UpdateWeaponSpeed(string key, double speed) => ItemDBHandler.UpdateWeaponSpeed(key, speed);
        public void UpdateWeaponReach(string key, double reach) => ItemDBHandler.UpdateWeaponReach(key, reach);
        public void UpdateWeaponStagger(string key, double stagger) => ItemDBHandler.UpdateWeaponStagger(key, stagger);
        public void UpdateWeaponKeywords(string key, List<string> keywordKeys)
        {
            var col = new System.Collections.ObjectModel.ObservableCollection<SkyrimCraftingTool.ViewModel.KeywordSelectionVM>();
            foreach (var k in keywordKeys)
                col.Add(new SkyrimCraftingTool.ViewModel.KeywordSelectionVM(k, "", true, null));

            ItemDBHandler.UpdateWeaponKeywords(key, col);
        }
        public void UpdateWeaponContainerString(string key, string containerString) => ItemDBHandler.UpdateWeaponContainerString(key, containerString);
    }
}
