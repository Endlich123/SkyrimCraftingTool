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

        public void SaveCOBJ(COBJRecord rec) => _handler.SaveCOBJ(rec);
    }
}
