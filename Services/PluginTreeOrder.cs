using System;
using System.Collections.Generic;
using SkyrimCraftingTool.Model;

namespace SkyrimCraftingTool.Services
{
    // The ONE order every record tree puts its plugins in: the item tree's, which is the load order
    // (MainContentVM.ActivePlugins). The Enchantments and Spells tabs each had their own copy of the
    // rule, and the two disagreed about plugins missing from that list - one put them first, one last.
    //
    // MISSING PLUGINS are almost all Creation Club masters: the game loads them through Skyrim.ccc,
    // plugins.txt never names them, and records keyed to them still turn up through mods that
    // override them. They go straight after the five vanilla masters, which is where the game loads
    // them - not at the top of the tree, ahead of Skyrim.esm.
    public static class PluginTreeOrder
    {
        private static readonly string[] VanillaMasters =
            { "Skyrim.esm", "Update.esm", "Dawnguard.esm", "HearthFires.esm", "Dragonborn.esm" };

        // Sort key for a plugin name. Known plugins rank by load order; unknown ones right after the
        // last vanilla master present (ties between unknown ones are the caller's ThenBy).
        public static Func<string, int> Ranker(IReadOnlyList<PluginInfo> loadOrder)
        {
            var names = new List<string>();
            foreach (var p in loadOrder ?? Array.Empty<PluginInfo>()) names.Add(p?.FileName ?? "");
            return Ranker(names);
        }

        // The same rule over bare file names - what the Container/LeveledList tab's trees are handed.
        public static Func<string, int> Ranker(IReadOnlyList<string> loadOrder)
        {
            var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            int lastVanilla = -1;

            for (int i = 0; i < (loadOrder?.Count ?? 0); i++)
            {
                var name = loadOrder![i] ?? "";
                if (!index.ContainsKey(name)) index[name] = i;
                if (Array.Exists(VanillaMasters, v => v.Equals(name, StringComparison.OrdinalIgnoreCase)))
                    lastVanilla = i;
            }

            // Doubled, so an unknown plugin can sit between two known ones. The tool's own
            // pseudo-plugin (records the user created) is not a master of anything and always last,
            // whether or not the list carries it - after a rescan it does not.
            //
            // A list without any vanilla master gives "after the vanilla masters" no place to be, so
            // an unknown plugin then goes after everything instead of in front of it.
            int unknownRank = lastVanilla >= 0 ? lastVanilla * 2 + 1 : int.MaxValue - 1;

            return plugin =>
                string.Equals(plugin, KeyFactory.UserPluginName, StringComparison.OrdinalIgnoreCase) ? int.MaxValue :
                index.TryGetValue(plugin ?? "", out var i) ? i * 2 : unknownRank;
        }
    }
}
