using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mutagen.Bethesda.Plugins;
using SkyrimCraftingTool.Model;

namespace SkyrimCraftingTool.Services.PatchGen
{
    // Ordered plugin list - the same one the scan uses (FileDBHandler.OrderWithVanillaFirst: active
    // '*' lines of plugins.txt, vanilla masters in front, Skyrim.ccc's Creation Club plugins right
    // after them). Orders the generated ESP's master list (Mutagen discovers *which* masters via
    // MastersListContentOption.Iterate).
    public static class LoadOrderReader
    {
        public static IReadOnlyList<ModKey> Read(string? pluginsTxtPath = null)
        {
            var path = pluginsTxtPath ?? GlobalState.PluginsFilePath;

            var lines = !string.IsNullOrEmpty(path) && File.Exists(path)
                ? File.ReadAllLines(path)
                : Array.Empty<string>();
            var names = FileDBHandler.OrderWithVanillaFirst(lines, CreationClubPlugins.Read(GlobalState.GameDataPath));

            var keys = new List<ModKey>();
            foreach (var n in names)
            {
                if (ModKey.TryFromFileName(n, out var mk))
                    keys.Add(mk);
            }
            return keys;
        }
    }
}
