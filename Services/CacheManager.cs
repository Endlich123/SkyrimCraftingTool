using SkyrimCraftingTool.Model;
using System.Collections.Generic;
using System.Linq;
using System.Security.Policy;
using System.Threading.Tasks;

namespace SkyrimCraftingTool.Services
{
    public class CacheManager : ICacheManager
    {
        private readonly IItemService _itemService;
        private readonly IFormIdService _formIdService;
        private CacheSnapshot _snapshot = new();
        private readonly object _armorLock = new();
        private readonly object _weaponLock = new();
        private readonly object _recipeLock = new();

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
            snapshot.Perks = _formIdService.SearchByType("Perk")
                .GroupBy(p => p.Key)          
                .Select(g => g.First())
                .ToList();
            snapshot.Containers = _itemService.SearchByType("Container").Cast<ContainerRecord>().ToList();
            snapshot.MagicEffects = _itemService.SearchByType("MagicEffect").Cast<MagicEffectsRecords>().ToList();

            // store snapshot for runtime updates
            _snapshot = snapshot;
            return snapshot;
        }

        // Armor updates
        public void UpdateArmorName(string key, string name)
        {
            lock (_armorLock)
            {
                if (_snapshot.Armor.TryGetValue(key, out var rec))
                    rec.Name = name;
                else
                    _snapshot.Armor[key] = new ArmorRecord { Key = key, Name = name };
            }
        }

        public void UpdateArmorValue(string key, int value)
        {
            lock (_armorLock)
            {
                if (_snapshot.Armor.TryGetValue(key, out var rec))
                    rec.Value = value;
                else
                    _snapshot.Armor[key] = new ArmorRecord { Key = key, Value = value };
            }
        }

        public void UpdateArmorWeight(string key, double weight)
        {
            lock (_armorLock)
            {
                if (_snapshot.Armor.TryGetValue(key, out var rec))
                    rec.Weight = (float)weight;
                else
                    _snapshot.Armor[key] = new ArmorRecord { Key = key, Weight = (float)weight };
            }
        }

        public void UpdateArmorRating(string key, double armorRating)
        {
            lock (_armorLock)
            {
                if (_snapshot.Armor.TryGetValue(key, out var rec))
                    rec.ArmorRating = (float)armorRating;
                else
                    _snapshot.Armor[key] = new ArmorRecord { Key = key, ArmorRating = (float)armorRating };
            }
        }

        public void UpdateArmorBodySlotMask(string key, long bodySlotMask)
        {
            lock (_armorLock)
            {
                if (_snapshot.Armor.TryGetValue(key, out var rec))
                    rec.BodySlotMask = (uint)bodySlotMask;
                else
                    _snapshot.Armor[key] = new ArmorRecord { Key = key, BodySlotMask = (uint)bodySlotMask };
            }
        }

        public void UpdateArmorKeywords(string key, List<string> keywordKeys)
        {
            lock (_armorLock)
            {
                if (_snapshot.Armor.TryGetValue(key, out var rec))
                    rec.Keywords = new List<string>(keywordKeys);
                else
                    _snapshot.Armor[key] = new ArmorRecord { Key = key, Keywords = new List<string>(keywordKeys) };
            }
        }

        public void UpdateArmorContainerString(string key, string containerString)
        {
            lock (_armorLock)
            {
                if (_snapshot.Armor.TryGetValue(key, out var rec))
                    rec.ContainerString = containerString;
                else
                    _snapshot.Armor[key] = new ArmorRecord { Key = key, ContainerString = containerString };
            }
        }
        // Weapon updates
        public void UpdateWeaponName(string key, string name)
        {
            lock (_weaponLock)
            {
                if (_snapshot.Weapons.TryGetValue(key, out var rec))
                    rec.Name = name;
                else
                    _snapshot.Weapons[key] = new WeaponRecord { Key = key, Name = name };
            }
        }

        public void UpdateWeaponValue(string key, int value)
        {
            lock (_weaponLock)
            {
                if (_snapshot.Weapons.TryGetValue(key, out var rec))
                    rec.Value = value;
                else
                    _snapshot.Weapons[key] = new WeaponRecord { Key = key, Value = value };
            }
        }

        public void UpdateWeaponWeight(string key, double weight)
        {
            lock (_weaponLock)
            {
                if (_snapshot.Weapons.TryGetValue(key, out var rec))
                    rec.Weight = (float)weight;
                else
                    _snapshot.Weapons[key] = new WeaponRecord { Key = key, Weight = (float)weight };
            }
        }

        public void UpdateWeaponDamage(string key, double damage)
        {
            lock (_weaponLock)
            {
                if (_snapshot.Weapons.TryGetValue(key, out var rec))
                    rec.Damage = (int)damage;
                else
                    _snapshot.Weapons[key] = new WeaponRecord { Key = key, Damage = (int)damage };
            }
        }

        public void UpdateWeaponSpeed(string key, double speed)
        {
            lock (_weaponLock)
            {
                if (_snapshot.Weapons.TryGetValue(key, out var rec))
                    rec.Speed = (float)speed;
                else
                    _snapshot.Weapons[key] = new WeaponRecord { Key = key, Speed = (float)speed };
            }
        }

        public void UpdateWeaponReach(string key, double reach)
        {
            lock (_weaponLock)
            {
                if (_snapshot.Weapons.TryGetValue(key, out var rec))
                    rec.Reach = (float)reach;
                else
                    _snapshot.Weapons[key] = new WeaponRecord { Key = key, Reach = (float)reach };
            }
        }

        public void UpdateWeaponStagger(string key, double stagger)
        {
            lock (_weaponLock)
            {
                if (_snapshot.Weapons.TryGetValue(key, out var rec))
                    rec.Stagger = (float)stagger;
                else
                    _snapshot.Weapons[key] = new WeaponRecord { Key = key, Stagger = (float)stagger };
            }
        }

        public void UpdateWeaponKeywords(string key, List<string> keywordKeys)
        {
            lock (_weaponLock)
            {
                if (_snapshot.Weapons.TryGetValue(key, out var rec))
                    rec.Keywords = new List<string>(keywordKeys);
                else
                    _snapshot.Weapons[key] = new WeaponRecord { Key = key, Keywords = new List<string>(keywordKeys) };
            }
        }

        public void UpdateWeaponContainerString(string key, string containerString)
        {
            lock (_weaponLock)
            {
                if (_snapshot.Weapons.TryGetValue(key, out var rec))
                    rec.ContainerString = containerString;
                else
                    _snapshot.Weapons[key] = new WeaponRecord { Key = key, ContainerString = containerString };
            }
        }

        // Recipe updates
        public void UpdateRecipe(COBJRecord rec)
        {
            if (rec == null) return;

            lock (_recipeLock)
            {
                if (!_snapshot.RecipesByCreatedItem.TryGetValue(rec.CreatedItemKey, out var list))
                {
                    list = new List<COBJRecord>();
                    _snapshot.RecipesByCreatedItem[rec.CreatedItemKey] = list;
                }

                var idx = list.FindIndex(r => r.Key == rec.Key);
                if (idx >= 0)
                    list[idx] = rec;
                else
                    list.Add(rec);
            }
        }
    }
}
