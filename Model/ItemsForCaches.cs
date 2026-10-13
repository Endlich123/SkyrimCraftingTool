using System.Collections.Generic;

namespace SkyrimCraftingTool.Model
{
    // What CacheManager.BuildCachesFromDB takes from the item cache: armor and weapons of the active
    // plugins by key, and their recipes by the item they create (conditions attached). Built in one
    // pass by ItemDBHandler.GroupForCaches (docs/Performance-Plan.md, P-P7).
    public sealed record ItemsForCaches(
        Dictionary<string, ArmorRecord> Armor,
        Dictionary<string, WeaponRecord> Weapons,
        Dictionary<string, List<COBJRecord>> RecipesByCreatedItem);
}
