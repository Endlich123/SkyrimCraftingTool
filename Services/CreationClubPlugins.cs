using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SkyrimCraftingTool.Model;

namespace SkyrimCraftingTool.Services
{
    // The Creation Club plugins the game loads on its own. Skyrim.ccc (next to SkyrimSE.exe, one file
    // name per line) lists them, plugins.txt does not, and the game puts every one that is installed
    // right after the five vanilla masters, in the order of the .ccc file.
    //
    // For a long time the scan never read them. Their records reached the database only where a mod
    // overrides them (USCCCP, CC Open Helmets ...), 290 armors on NEFARAM, and whatever no mod
    // touches was missing outright: 232 armors (ccBGSSSE058_ArmorBladesShield,
    // ccEDHSSE003_RedguardEliteGlovesAgent ...) plus weapons, books and effects. Found through the KID
    // log, which counted those items where the database had nothing (2026-10-27).
    public static class CreationClubPlugins
    {
        // Listed AND present in Data. An Anniversary Edition's .ccc names all 74 files whether they
        // were bought or not; the game skips the missing ones, so does this.
        public static IReadOnlyList<string> Read(string? gameDataPath)
        {
            if (string.IsNullOrWhiteSpace(gameDataPath) || !Directory.Exists(gameDataPath))
                return Array.Empty<string>();

            var game = Path.GetDirectoryName(gameDataPath.TrimEnd('\\', '/'));
            var ccc = game == null ? null : Path.Combine(game, "Skyrim.ccc");
            if (ccc == null || !File.Exists(ccc)) return Array.Empty<string>();

            try
            {
                return File.ReadAllLines(ccc)
                    .Select(l => l.Trim())
                    .Where(n => n.Length > 0 && File.Exists(Path.Combine(gameDataPath, n)))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            catch (Exception ex)
            {
                // An unreadable .ccc costs the Creation Club records, not the whole scan.
                AppLogger.LogWarning($"Creation Club: reading {ccc} failed ({ex.Message})");
                return Array.Empty<string>();
            }
        }
    }
}
