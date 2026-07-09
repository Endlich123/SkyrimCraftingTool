using SkyrimCraftingTool.Model;
using SkyrimCraftingTool.ViewModel;
using System.Collections.Generic;

namespace SkyrimCraftingTool.Services
{
    public interface ITreeBuilderService
    {
        List<PluginNodeVM> BuildTreeFromCache(List<PluginInfo> activePlugins,
            IDictionary<string, ArmorRecord> armorCache,
            IDictionary<string, WeaponRecord> weaponCache,
            MainContentVM parent);
    }
}
