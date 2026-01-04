using System;
using System.Collections.Generic;
using System.IO;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Binary.Parameters;
using Mutagen.Bethesda.Skyrim;

namespace SkyrimCraftingTool;

public static class MutagenSafeLoader
{
    public static readonly GroupMask RelevantGroups = new GroupMask()
    {
        Weapons = true,
        Armors = true,
        ConstructibleObjects = true
    };

    private static readonly BinaryReadParameters FastParams = new BinaryReadParameters()
    {
        Parallel = true,
        ThrowOnUnknownSubrecord = false
    };

    public static bool TryLoadMod(string path, out ISkyrimModGetter? mod)
    {
        mod = null;

        if (!File.Exists(path))
            return false;

        try
        {
            mod = SkyrimMod.CreateFromBinary(
                path,
                SkyrimRelease.SkyrimSE,
                FastParams,
                RelevantGroups
            );

            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SafeLoader] Skip {Path.GetFileName(path)} → {ex.GetType().Name}");
            return false;
        }
    }

    public static List<ISkyrimModGetter> LoadAllMods(IEnumerable<ModKey> loadOrder, Func<string, string?> findModFile)
    {
        var result = new List<ISkyrimModGetter>();

        foreach (var modKey in loadOrder)
        {
            string? path = findModFile(modKey.FileName);

            if (path == null)
                continue;

            if (TryLoadMod(path, out var mod) && mod != null)
                result.Add(mod);
        }

        return result;
    }

    public static bool HasRelevantContent(ISkyrimModGetter mod)
    {
        return
            mod.Weapons.Count > 0 ||
            mod.Armors.Count > 0 ||
            mod.ConstructibleObjects.Count > 0;
    }
}
