using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mutagen.Bethesda.Plugins;
using SkyrimCraftingTool.Model;

namespace SkyrimCraftingTool.Services.PatchGen
{
    // Ordered plugin list from plugins.txt (same parse as FileDBHandler.GetPluginsFromTxt: active
    // lines are '*'-prefixed, vanilla masters prepended). Used only to order the generated ESP's
    // master list — Mutagen discovers *which* masters via MastersListContentOption.Iterate.
    public static class LoadOrderReader
    {
        private static readonly string[] Vanilla =
            { "Skyrim.esm", "Update.esm", "Dawnguard.esm", "HearthFires.esm", "Dragonborn.esm" };

        public static IReadOnlyList<ModKey> Read(string? pluginsTxtPath = null)
        {
            var path = pluginsTxtPath ?? GlobalState.PluginsFilePath;

            var names = new List<string>();
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
            {
                names = File.ReadAllLines(path)
                    .Where(l => !string.IsNullOrWhiteSpace(l) && l.StartsWith("*"))
                    .Select(l => l.TrimStart('*').Trim())
                    .ToList();
            }

            foreach (var v in Vanilla.Reverse())
                if (!names.Contains(v, StringComparer.OrdinalIgnoreCase))
                    names.Insert(0, v);

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
