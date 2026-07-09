using SkyrimCraftingTool.Model;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace SkyrimCraftingTool.Services
{
    public class CacheManager : ICacheManager
    {
        private readonly IItemService _itemService;
        private readonly IFormIdService _formIdService;

        public CacheManager(IItemService itemService, IFormIdService formIdService)
        {
            _itemService = itemService;
            _formIdService = formIdService;
        }

        public CacheSnapshot BuildCachesFromDB(List<PluginInfo> activePlugins)
        {
            var armorLocal = new Dictionary<string, ArmorRecord>();
            var weaponLocal = new Dictionary<string, WeaponRecord>();
            var recipeLocal = new Dictionary<string, List<COBJRecord>>();

            Parallel.ForEach(activePlugins, plugin =>
            {
                foreach (var armor in _itemService.GetArmorByPlugin(plugin.FileName))
                    lock (armorLocal) armorLocal[armor.Key] = armor;

                foreach (var weapon in _itemService.GetWeaponsByPlugin(plugin.FileName))
                    lock (weaponLocal) weaponLocal[weapon.Key] = weapon;

                foreach (var recipe in _itemService.GetCOBJByPlugin(plugin.FileName))
                {
                    lock (recipeLocal)
                    {
                        if (!recipeLocal.TryGetValue(recipe.CreatedItemKey, out var list))
                            recipeLocal[recipe.CreatedItemKey] = list = new List<COBJRecord>();
                        list.Add(recipe);
                    }
                }
            });

            var snapshot = new CacheSnapshot();
            snapshot.Armor = armorLocal;
            snapshot.Weapons = weaponLocal;
            snapshot.RecipesByCreatedItem = recipeLocal;

            snapshot.Keywords = _formIdService.SearchByType("Keyword");
            snapshot.Materials = _formIdService.SearchByType("Material");

            return snapshot;
        }
    }
}
