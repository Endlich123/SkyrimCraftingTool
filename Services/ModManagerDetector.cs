using System;
using System.IO;
using SkyrimCraftingTool.Model;

namespace SkyrimCraftingTool.Services
{
    public static class ModManagerDetector
    {
        // MO2 keeps plugins.txt and modlist.txt side by side in profiles\<profile>\. Deriving one
        // from the other also pins them to the SAME profile, which hand-picking two files does not:
        // a modlist.txt from the wrong profile would silently apply the wrong mod priority.
        public static string ModListPathFor(string pluginsFilePath)
        {
            if (string.IsNullOrWhiteSpace(pluginsFilePath)) return "";

            var folder = Path.GetDirectoryName(pluginsFilePath);
            return string.IsNullOrWhiteSpace(folder) ? "" : Path.Combine(folder, "modlist.txt");
        }

        // Vortex and a manual install both keep plugins.txt here; Skyrim SE itself writes it.
        public static string DefaultPluginsFilePath()
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return string.IsNullOrWhiteSpace(localAppData)
                ? ""
                : Path.Combine(localAppData, "Skyrim Special Edition", "plugins.txt");
        }

        // A modlist.txt next to plugins.txt means MO2 and nothing else does - no other manager
        // writes that file. Used for the silent classification of a settings.json from before
        // ManagerKind existed, so nobody has to re-answer a question on an update.
        public static bool LooksLikeMo2(string pluginsFilePath)
        {
            var modList = ModListPathFor(pluginsFilePath);
            return !string.IsNullOrWhiteSpace(modList) && File.Exists(modList);
        }

        public static bool TryInferForExistingSettings(FolderSettings settings, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out FolderSettings? inferred)
        {
            inferred = null;
            if (settings == null) return false;
            if (settings.ManagerKind != ModManagerKind.Unknown) return false;

            var isMo2 = LooksLikeMo2(settings.PluginsFilePath);

            inferred = new FolderSettings
            {
                GameDataPath = settings.GameDataPath,
                ModDirectoryPath = settings.ModDirectoryPath,
                PluginsFilePath = settings.PluginsFilePath,
                ManagerKind = isMo2 ? ModManagerKind.ModOrganizer2 : ModManagerKind.Other,
                ModListFilePath = isMo2 ? ModListPathFor(settings.PluginsFilePath) : "",
            };
            return true;
        }
    }
}
