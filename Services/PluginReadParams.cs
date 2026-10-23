using System.IO;
using Mutagen.Bethesda.Plugins.Binary.Parameters;
using Mutagen.Bethesda.Strings;
using SkyrimCraftingTool.Model;

namespace SkyrimCraftingTool.Services
{
    // Read parameters shared by both plugin scans (item.db and formid.db).
    //
    // Why this exists: localized plugins - every vanilla master and every Creation Club file - keep
    // their display names in .STRINGS files, NOT in the plugin. In Skyrim SE those live inside
    // "Skyrim - Interface.bsa" in the game's Data folder. Mutagen looks for them next to the plugin
    // it is reading, which breaks the moment a plugin is read from anywhere else.
    //
    // That is not exotic: MO2 modlists routinely ship cleaned vanilla masters as a mod, so
    // Skyrim.esm exists both in Data and in "mods\Cleaned Plugins\" - and the latter folder holds
    // only ESMs, no BSAs. Reading that copy returned EMPTY names for 2433 of 2762 vanilla armors,
    // every vanilla enchantment, and so on. EditorIDs survived (they are stored in the plugin),
    // which is what made it look like "names are gone" rather than "the scan failed".
    //
    // Pointing the BSA lookup at the game Data folder fixes it wherever the plugin file happens to
    // sit. Verified against a real setup: the cleaned copy went from 0/400 named armors to 398/400,
    // and reading the Data copy is unaffected.
    public static class PluginReadParams
    {
        public static BinaryReadParameters ForScan()
        {
            var dataFolder = GlobalState.GameDataPath;

            if (string.IsNullOrWhiteSpace(dataFolder) || !Directory.Exists(dataFolder))
                return BinaryReadParameters.Default;

            var stringsParam = new StringsReadParameters
            {
                BsaFolderOverride = dataFolder,
            };

            // Some setups keep loose .STRINGS in Data\Strings instead of (or besides) the BSAs.
            // Only set the override when it actually exists - pointing it at a missing folder would
            // replace Mutagen's own lookup with one that can never succeed.
            var looseStrings = Path.Combine(dataFolder, "Strings");
            if (Directory.Exists(looseStrings))
                stringsParam = stringsParam with { StringsFolderOverride = looseStrings };

            return BinaryReadParameters.Default with { StringsParam = stringsParam };
        }
    }
}
