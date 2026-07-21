using SkyrimCraftingTool.Model;
using SkyrimCraftingTool.ViewModel;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace SkyrimCraftingTool.Services
{
    public class CacheSnapshot
    {
        public Dictionary<string, ArmorRecord> Armor { get; set; } = new();
        public Dictionary<string, WeaponRecord> Weapons { get; set; } = new();
        public Dictionary<string, List<COBJRecord>> RecipesByCreatedItem { get; set; } = new();

        public List<FormIDRecord> Keywords { get; set; } = new();
        public List<FormIDRecord> Materials { get; set; } = new();
        public List<FormIDRecord> Perks { get; set; } = new();
        public List<ContainerRecord> Containers { get; set; } = new();
        public List<MagicEffectsRecords> MagicEffects { get; set; } = new();
    }
}
