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
    }
}
