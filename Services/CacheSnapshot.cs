using SkyrimCraftingTool.Model;
using System.Collections.Generic;

namespace SkyrimCraftingTool.Services
{
    public class CacheSnapshot
    {
        public Dictionary<string, ArmorRecord> Armor { get; set; } = new();
        public Dictionary<string, WeaponRecord> Weapons { get; set; } = new();
        public Dictionary<string, List<COBJRecord>> RecipesByCreatedItem { get; set; } = new();

        public List<FormIDRecord> Keywords { get; set; } = new();
        public List<FormIDRecord> Materials { get; set; } = new();
    }
}
