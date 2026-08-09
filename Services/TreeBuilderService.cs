using SkyrimCraftingTool.Model;
using SkyrimCraftingTool.ViewModel;
using System.Collections.Generic;
using System.Linq;

namespace SkyrimCraftingTool.Services
{
    public class TreeBuilderService : ITreeBuilderService
    {
        public List<PluginNodeVM> BuildTreeFromCache(List<PluginInfo> activePlugins,
            IDictionary<string, ArmorRecord> armorCache,
            IDictionary<string, WeaponRecord> weaponCache,
            MainContentVM parent)
        {
            var result = new List<PluginNodeVM>();

            var armorByPlugin = armorCache.Values
                .GroupBy(a => a.Key.Split('|')[0].ToLowerInvariant())
                .ToDictionary(g => g.Key, g => g.ToList());

            var weaponByPlugin = weaponCache.Values
                .GroupBy(w => w.Key.Split('|')[0].ToLowerInvariant())
                .ToDictionary(g => g.Key, g => g.ToList());

            foreach (var plugin in activePlugins)
            {
                var pluginKey = plugin.FileName.ToLowerInvariant();
                var pluginNode = new PluginNodeVM { PluginName = plugin.FileName };

                var armorCategory = new CategoryNodeVM { CategoryName = "Armor" };
                var weaponCategory = new CategoryNodeVM { CategoryName = "Weapons" };

                if (armorByPlugin.TryGetValue(pluginKey, out var armorList))
                {
                    foreach (var armor in armorList)
                        armorCategory.Items.Add(new ItemNodeVM(armor, parent));
                }

                if (weaponByPlugin.TryGetValue(pluginKey, out var weaponList))
                {
                    foreach (var weapon in weaponList)
                        weaponCategory.Items.Add(new ItemNodeVM(weapon, parent));
                }

                if (armorCategory.Items.Count > 0)
                    pluginNode.Categories.Add(armorCategory);

                if (weaponCategory.Items.Count > 0)
                    pluginNode.Categories.Add(weaponCategory);

                if (pluginNode.Categories.Count > 0)
                    result.Add(pluginNode);
            }

            return result;
        }
    }
}
