using SkyrimCraftingTool.Model;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace SkyrimCraftingTool.Services.ModularPresets
{
    // One JSON file per catalog under Output/Presets/Modules/<Armor|Weapons>/, so a user can hand
    // someone their materials without their slot layout (or the other way round), and armor without
    // weapons. The subfolder keeps these out of PresetFileStore.FindAllPresetFiles, which only lists
    // the top level.
    public class ModularCatalogStore
    {
        public const int CurrentSchemaVersion = 1;

        public static string ModulesRoot => Path.Combine(PresetFileStore.PresetsRoot, "Modules");

        // Armor and weapons are separate catalogs (decided 2026-10-23): each folder is complete on its
        // own - its own materials, addons, rules and extras - and can be shared alone.
        public static string ArmorRoot => Path.Combine(ModulesRoot, "Armor");
        public static string WeaponsRoot => Path.Combine(ModulesRoot, "Weapons");

        private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

        private readonly string _root;

        public ModularCatalogStore(string root) { _root = root; }

        public ModularCatalog Read()
        {
            return new ModularCatalog
            {
                Types = ReadFile<TypeModule>("Types.json"),
                Materials = ReadFile<MaterialModule>("Materials.json"),
                Slots = ReadFile<SlotDefinition>("Slots.json"),
                Addons = ReadFile<KeywordAddon>("Addons.json"),
                Rules = ReadFile<CombinationRule>("Rules.json"),
                Extras = ReadFile<ExtraModule>("Extras.json"),
            };
        }

        public void Write(ModularCatalog catalog)
        {
            Directory.CreateDirectory(_root);
            WriteFile("Types.json", catalog.Types);
            WriteFile("Materials.json", catalog.Materials);
            WriteFile("Slots.json", catalog.Slots);
            WriteFile("Addons.json", catalog.Addons);
            WriteFile("Rules.json", catalog.Rules);
            WriteFile("Extras.json", catalog.Extras);
            Saved?.Invoke();
        }

        public static readonly string[] FileNames =
            { "Types.json", "Materials.json", "Slots.json", "Addons.json", "Rules.json", "Extras.json" };

        // Before 2026-10-23 the armor catalog lay directly in Modules/. Moved once into Modules/Armor/
        // - only when Armor/ has no catalog yet, so it can never overwrite one. Backups next to the
        // files (Rules.json.bak-...) move along; anything else in Modules/ stays where it is.
        public static bool MoveLegacyArmorCatalog(string modulesRoot)
        {
            try
            {
                var armorRoot = Path.Combine(modulesRoot, "Armor");
                if (!FileNames.Any(f => File.Exists(Path.Combine(modulesRoot, f)))) return false;
                if (FileNames.Any(f => File.Exists(Path.Combine(armorRoot, f)))) return false;

                Directory.CreateDirectory(armorRoot);
                foreach (var path in Directory.GetFiles(modulesRoot))
                {
                    var name = Path.GetFileName(path);
                    if (FileNames.Any(f => name.Equals(f, System.StringComparison.OrdinalIgnoreCase)
                                           || name.StartsWith(f + ".", System.StringComparison.OrdinalIgnoreCase)))
                        File.Move(path, Path.Combine(armorRoot, name));
                }
                return true;
            }
            catch (System.Exception ex)
            {
                // Not moved = the armor tabs start empty and the old files are still there to move by
                // hand. Never a failed start.
                AppLogger.LogError("Preset catalog: the armor catalog could not be moved to Modules/Armor", ex);
                return false;
            }
        }

        // The catalog that ships with the tool (PresetDefaults/Modules/<kind>, embedded): vanilla
        // Skyrim, so a fresh install starts with working blocks instead of empty tabs.
        //
        // Only into a folder with NO catalog file at all. A user's own catalog - even one file of it -
        // is never touched, and an update never overwrites what someone built. Someone who deletes
        // every file gets the default back on the next start, which is the way to reset it.
        public static bool SeedDefaultsIfEmpty(string root, string kind = "Armor", string[]? fileNames = null)
        {
            fileNames ??= FileNames;
            try
            {
                if (fileNames.Any(f => File.Exists(Path.Combine(root, f)))) return false;

                var asm = typeof(ModularCatalogStore).Assembly;
                int written = 0;
                foreach (var fileName in fileNames)
                {
                    using var stream = asm.GetManifestResourceStream($"SkyrimCraftingTool.PresetDefaults.{kind}.{fileName}");
                    if (stream == null) continue;
                    Directory.CreateDirectory(root);
                    using var target = File.Create(Path.Combine(root, fileName));
                    stream.CopyTo(target);
                    written++;
                }
                return written > 0;
            }
            catch (System.Exception ex)
            {
                // A missing default catalog is empty tabs, never a failed start.
                AppLogger.LogError("Preset catalog: the default catalog could not be written", ex);
                return false;
            }
        }

        // The Presets tab saves; the build boxes on the Items tab read. One process-wide signal so
        // they pick up a saved change without anyone polling the files.
        public static event System.Action? Saved;

        // A missing file is an empty catalog, not an error - a fresh install has none of them.
        private List<T> ReadFile<T>(string fileName) => ReadEntries<T>(_root, fileName);

        private void WriteFile<T>(string fileName, List<T> entries) => WriteEntries(_root, fileName, entries);

        // Shared with WeaponCatalogStore: the same file format in both folders.
        internal static List<T> ReadEntries<T>(string root, string fileName)
        {
            var path = Path.Combine(root, fileName);
            if (!File.Exists(path)) return new List<T>();
            var file = JsonSerializer.Deserialize<CatalogFile<T>>(File.ReadAllText(path));
            return file?.Entries ?? new List<T>();
        }

        internal static void WriteEntries<T>(string root, string fileName, List<T> entries)
        {
            var file = new CatalogFile<T> { SchemaVersion = CurrentSchemaVersion, Entries = entries };
            File.WriteAllText(Path.Combine(root, fileName), JsonSerializer.Serialize(file, Indented));
        }

        private class CatalogFile<T>
        {
            public int SchemaVersion { get; set; }
            public List<T> Entries { get; set; } = new();
        }
    }
}
