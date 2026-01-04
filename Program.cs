using System;
using System.Linq;
using System.Collections.Generic;
using System.IO;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Skyrim;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Order;
using Noggog;
using System.Reactive;
using Mutagen.Bethesda.Plugins.Binary.Parameters;
using Mutagen.Bethesda.Plugins.Cache;
using System.Diagnostics;

namespace SkyrimCraftingTool;

public class Program
{
    public static Dictionary<FormKey, string> materialMap = new Dictionary<FormKey, string>();
    public static Dictionary<string, FormKey> materialMapReverse = new();
    //workbench
    public static HashSet<string> WorkbenchKeywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    public static List<string> WorkbenchTypes
    => WorkbenchKeywords.ToList();

    //
    //public static HashSet<string> VendorKeywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    public static Dictionary<FormKey, string> keywordMap = new Dictionary<FormKey, string>();

    public static Dictionary<string, FormKey> keywordMapReverse = new();
    // GLOBALES KEYWORD-VERZEICHNIS (falls du es als Fallback nutzen willst)
    public static Dictionary<FormKey, string> AllKeywords = new Dictionary<FormKey, string>();

    public static string loadOrderFile;

    public static readonly HashSet<string> skipMods = new(StringComparer.OrdinalIgnoreCase)
    {
        "Skyrim Extended Cut - Saints and Seducers.esp",
        "MoreNastyCritters.esp",
        // weitere Problem-Mods kannst du hier eintragen
    };

    // >>> Optional: skyrim.esm und update.esm als statische Cache-Felder,
    // damit sie nicht mehrfach geladen werden müssen.
    private static ISkyrimModGetter? _skyrimEsm;
    private static ISkyrimModGetter? _updateEsm;

    [STAThread]
    public static void Handler()
    {
        Bench.Start("Benchmark");

        var settings = FolderSettings.LoadSavedSettings();
        var folders = new FolderStructure(settings);
        folders.CheckFoldersAndLog();

        Console.WriteLine(">>> Version of 20. December 2025 <<<");

        string modFolder = AppContext.BaseDirectory;
        string inputFolder = Path.Combine(modFolder, "Input");
        Directory.CreateDirectory(inputFolder);
        string outputFolder = Path.Combine(modFolder, "Output");
        Directory.CreateDirectory(outputFolder);

        // LoadOrder nur einmal lesen
        var loadOrderListings = LoadStockGameLoadOrder();

        // Skyrim.esm einmal laden
        _skyrimEsm = SkyrimMod.CreateFromBinary(FolderStructure.Current.SkyrimEsmPath, SkyrimRelease.SkyrimSE);

        foreach (var misc in _skyrimEsm.MiscItems)
        {
            var editorID = misc.EditorID ?? "Unbenannt";
            materialMap[misc.FormKey] = editorID;
            materialMapReverse[editorID] = misc.FormKey;
        }

        // Mod-Dateien einmal scannen
        GlobalState.ModFilePathMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in Directory.GetFiles(
            FolderStructure.Current.ModDirectory,
            "*.es*",
            SearchOption.AllDirectories))
        {
            var fileName = Path.GetFileName(file);

            // Falls der Key schon existiert → überspringen
            if (!GlobalState.ModFilePathMap.ContainsKey(fileName))
            {
                GlobalState.ModFilePathMap[fileName] = file;
            }
        }


        // Alle Mods laden
        Bench.Start("LoadAllMods");
        var loadedMods = MutagenSafeLoader.LoadAllMods(
            loadOrderListings.Select(lo => lo.ModKey),
            FindModFile
        );
        Bench.End("LoadAllMods");

        GlobalState.LoadedMods = loadedMods;

        // EIN globaler LinkCache
        GlobalState.LinkCache = loadedMods.ToImmutableLinkCache();

        // Keyword-Mapping aus ALLEN geladenen Mods

        // Keyword-Mapping zurücksetzen
        keywordMap.Clear();
        WorkbenchKeywords.Clear();
        GlobalState.VendorKeywords.Clear();

        // Workbench-Keywords aus Skyrim.esm
        foreach (var keyword in _skyrimEsm.Keywords)
        {
            var edid = keyword.EditorID ?? "Unbenannt";

            keywordMap[keyword.FormKey] = edid;
            keywordMapReverse[edid] = keyword.FormKey;

            if (edid.Contains("Craft", StringComparison.OrdinalIgnoreCase))
                WorkbenchKeywords.Add(edid);

            if (edid.Contains("Vendor", StringComparison.OrdinalIgnoreCase))
            {
                GlobalState.VendorKeywords.Add(edid);
                keywordMap[keyword.FormKey] = edid; // EditorID für GUI
            }

        }

        // Update.esm nur laden, wenn vorhanden
        if (_updateEsm != null && _updateEsm.Keywords != null)
        {
            foreach (var keyword in _updateEsm.Keywords)
            {
                var edid = keyword.EditorID ?? "Unbenannt";

                keywordMap[keyword.FormKey] = edid;
                keywordMapReverse[edid] = keyword.FormKey;

                if (edid.Contains("Craft", StringComparison.OrdinalIgnoreCase))
                    WorkbenchKeywords.Add(edid);

                if (edid.Contains("Vendor", StringComparison.OrdinalIgnoreCase))
                    GlobalState.VendorKeywords.Add(edid);
            }
        }
        else
        {
            //Console.WriteLine("WARNUNG: update.esm konnte nicht geladen werden oder enthält keine Keywords.");
        }


        // Mods ergänzen (falls sie neue Keywords definieren)
        foreach (var mod in GlobalState.LoadedMods)
        {
            foreach (var keyword in mod.Keywords)
            {
                var edid = keyword.EditorID ?? "Unbenannt";
                Debug.WriteLine("Keywords!!!!!!!!!!!!!!!!!");

                keywordMap[keyword.FormKey] = edid;
                keywordMapReverse[edid] = keyword.FormKey;

                foreach (var kv in Program.keywordMapReverse)
                    Debug.WriteLine($"{kv.Key} → {kv.Value}");

                if (edid.Contains("Craft", StringComparison.OrdinalIgnoreCase))
                    WorkbenchKeywords.Add(edid);

                if (edid.Contains("Vendor", StringComparison.OrdinalIgnoreCase))
                    GlobalState.VendorKeywords.Add(edid);
            }
        }

        Console.WriteLine("Patch successful!");
        Bench.End("Benchmark");
    }


    static string ResolveWeaponType(IWeaponGetter weapon, ILinkCache linkCache)
    {
        var weaponkeywordMap = new Dictionary<string, WeaponTypes>
    {
        { "WeapTypeSword", WeaponTypes.Sword },
        { "WeapTypeGreatsword", WeaponTypes.Greatsword },
        { "WeapTypeWarAxe", WeaponTypes.WarAxe },
        { "WeapTypeBattleaxe", WeaponTypes.Battleaxe },
        { "WeapTypeDagger", WeaponTypes.Dagger },
        { "WeapTypeBow", WeaponTypes.Bow },
        { "WeapTypeCrossbow", WeaponTypes.Crossbow },
        { "WeapTypeStaff", WeaponTypes.Staff },
        { "WeapTypeMace", WeaponTypes.Mace },
        { "WeapTypeWarhammer", WeaponTypes.Warhammer },
        { "WeapTypePickaxe", WeaponTypes.Pickaxe },
        { "WeapTypeWoodAxe", WeaponTypes.WoodAxe },
        { "WeapTypeArtifact", WeaponTypes.Artifact }
    };

        if (weapon.Keywords != null)
        {
            foreach (var kw in weapon.Keywords)
            {
                if (kw == null)
                    continue;

                if (!kw.TryResolve(linkCache, out var resolved))
                    continue;

                if (resolved?.EditorID == null)
                    continue;

                var edid = resolved.EditorID.Trim();

                if (weaponkeywordMap.TryGetValue(edid, out var weaponType))
                    return weaponType.ToString();
            }
        }

        return nameof(WeaponTypes.Sword);
    }


    static List<LoadOrderListing> LoadStockGameLoadOrder()
    {
        List<LoadOrderListing> loadOrderListings = new List<LoadOrderListing>();

        if (File.Exists(FolderStructure.Current.PluginsFilePath))
        {
            string[] lines = File.ReadAllLines(FolderStructure.Current.PluginsFilePath);
            //Console.WriteLine("Stock Game Load Order:");

            foreach (string line in lines)
            {
                if (!line.StartsWith("#") && !string.IsNullOrWhiteSpace(line) && line.EndsWith(".esp"))
                {
                    string modKeyString = line.Trim().TrimStart('*');
                    modKeyString = modKeyString.Substring(0, modKeyString.Length - 4);

                    try
                    {
                        var modKey = ModKey.FromName(modKeyString, ModType.Plugin);
                        loadOrderListings.Add(new LoadOrderListing(modKey, true));
                    }
                    catch (ArgumentException e)
                    {
                        Console.WriteLine($"Fehler beim Verarbeiten von '{modKeyString}': {e.Message}");
                    }
                }
            }
        }
        else
        {
            Console.WriteLine($"Die Datei {FolderStructure.Current.PluginsFilePath} wurde nicht gefunden.");
        }

        return loadOrderListings;
    }

    public static Dictionary<string, List<IGameRecord>> LoadItemRecordsFromESPs()
    {
        Dictionary<string, List<IGameRecord>> espItemData = new();

        if (_skyrimEsm == null)
            _skyrimEsm = SkyrimMod.CreateFromBinary(FolderStructure.Current.SkyrimEsmPath, SkyrimRelease.SkyrimSE);

        if (_updateEsm == null)
            _updateEsm = SkyrimMod.CreateFromBinary(FolderStructure.Current.UpdateEsmPath, SkyrimRelease.SkyrimSE);

        var skyrimEsm = _skyrimEsm;
        var updateEsm = _updateEsm;

        var linkCache = GlobalState.LinkCache;
        var mods = GlobalState.LoadedMods;



        Bench.Start("Filter Mods");

        foreach (var mod in mods)
        {
            // Skip-Liste
            if (skipMods.Contains(mod.ModKey.FileName))
                continue;

            // Nur Mods mit relevanten Gruppen
            if (!MutagenSafeLoader.HasRelevantContent(mod))
                continue;

            List<ArmorRecord> armorItems = new();
            List<WeaponRecord> weaponItems = new();

            string sourcePath = FindModFile(mod.ModKey.FileName) ?? string.Empty;

            // Waffen
            foreach (var weapon in mod.Weapons)
            {
                var record = new WeaponRecord
                {
                    EditorID = weapon.EditorID ?? "Unbenannte Waffe",
                    SourceEspPath = sourcePath,
                    Name = weapon.Name?.String ?? "Kein Name",
                    Weight = weapon.BasicStats.Weight,
                    Value = weapon.BasicStats.Value,
                    Damage = weapon.BasicStats.Damage,
                    WeaponType = ResolveWeaponType(weapon, linkCache),
                    FormKey = weapon.FormKey
                };

                // --- robustes Keyword-Handling (für weapon.Keywords oder armor.Keywords) ---
                // Beispiel für weapon.Keywords (analog für armor.Keywords)
                if (weapon.Keywords != null)
                {
                    foreach (var kw in weapon.Keywords)
                    {
                        var edid = TryGetKeywordEdid(kw, linkCache);
                        if (string.IsNullOrWhiteSpace(edid))
                        {
                            // Optional: nur einmal pro FormKey loggen, um Konsole nicht zu fluten
                            //Console.WriteLine($"[KW-UNRESOLVED] Could not resolve keyword link for item {record.EditorID} in {mod.ModKey.FileName}");
                            continue;
                        }

                        record.Keywords.Add(edid);

                        // VendorKeywords ist HashSet mit OrdinalIgnoreCase → Contains reicht
                        if (GlobalState.VendorKeywords.Contains(edid))
                        {
                            if (!record.Vendor.Contains(edid, StringComparer.OrdinalIgnoreCase))
                                record.Vendor.Add(edid);
                            //Console.WriteLine($"[VendorAdd] Mod={mod.ModKey.FileName} Item={record.EditorID} VendorEdid='{edid}'");
                        }
                    }
                }



                foreach (var cobj in mod.ConstructibleObjects)
                {
                    if (cobj.CreatedObject.FormKey != record.FormKey)
                        continue;

                    var wbKey = cobj.WorkbenchKeyword?.FormKey;

                    // FIX: Items können NULL sein
                    if (cobj.Items == null)
                        continue;

                    foreach (var entry in cobj.Items)
                    {
                        var itemKey = entry.Item.Item.FormKey;
                        int count = entry.Item.Count;

                        if (materialMap.TryGetValue(itemKey, out var matName) &&
                            keywordMap.TryGetValue(wbKey ?? default, out var wbName))
                        {
                            record.Materials[matName] = count;
                            record.Workbench = wbName;
                        }
                    }
                }


                weaponItems.Add(record);
            }

            // Rüstungen
            foreach (var armor in mod.Armors)
            {
                var record = new ArmorRecord
                {
                    EditorID = armor.EditorID ?? "Unbenannte Rüstung",
                    SourceEspPath = sourcePath,
                    Weight = armor.Weight,
                    Value = armor.Value,
                    ArmorRating = armor.ArmorRating,
                    FormKey = armor.FormKey
                };

                // --- robustes Keyword-Handling (für weapon.Keywords oder armor.Keywords) ---
                // Beispiel für weapon.Keywords (analog für armor.Keywords)
                if (armor.Keywords != null)
                {
                    foreach (var kw in armor.Keywords)
                    {
                        var edid = TryGetKeywordEdid(kw, linkCache);
                        if (string.IsNullOrWhiteSpace(edid))
                        {
                            // Optional: nur einmal pro FormKey loggen, um Konsole nicht zu fluten
                            //Console.WriteLine($"[KW-UNRESOLVED] Could not resolve keyword link for item {record.EditorID} in {mod.ModKey.FileName}");
                            continue;
                        }

                        record.Keywords.Add(edid);

                        // VendorKeywords ist HashSet mit OrdinalIgnoreCase → Contains reicht
                        if (GlobalState.VendorKeywords.Contains(edid))
                        {
                            if (!record.Vendor.Contains(edid, StringComparer.OrdinalIgnoreCase))
                                record.Vendor.Add(edid);
                            //Console.WriteLine($"[VendorAdd] Mod={mod.ModKey.FileName} Item={record.EditorID} VendorEdid='{edid}'");
                        }
                    }
                }



                // Konstanten: erster relevanter ArmorSlot-Bitindex in deiner GUI/Enum
                const int ArmorSlotBitOffset = 30;

                var flags = armor.BodyTemplate?.FirstPersonFlags;
                if (flags != null)
                {
                    ulong raw = (ulong)flags.Value;

                    for (int bitIndex = 0; bitIndex < 64; bitIndex++)
                    {
                        if ((raw & (1UL << bitIndex)) == 0)
                            continue;

                        int slotValue = bitIndex + ArmorSlotBitOffset;

                        if (Enum.IsDefined(typeof(ArmorSlot), slotValue))
                        {
                            // Setze den eigentlichen Slot
                            record.SelectedSlot = (ArmorSlot)slotValue;
                        }

                        // Falls du die Liste behalten willst:
                        string slotName = Enum.GetName(typeof(ArmorSlot), slotValue) ?? $"Unbekannter Slot {slotValue}";
                        record.Slots.Add($"{slotValue}:{slotName}");
                    }
                }


                // Materialien
                foreach (var cobj in mod.ConstructibleObjects)
                {
                    if (cobj.CreatedObject.FormKey != record.FormKey)
                        continue;

                    var wbKey = cobj.WorkbenchKeyword?.FormKey;

                    // FIX: Items können NULL sein
                    if (cobj.Items == null)
                        continue;

                    foreach (var entry in cobj.Items)
                    {
                        var itemKey = entry.Item.Item.FormKey;
                        int count = entry.Item.Count;

                        if (materialMap.TryGetValue(itemKey, out var matName) &&
                            keywordMap.TryGetValue(wbKey ?? default, out var wbName))
                        {
                            record.Materials[matName] = count;
                            record.Workbench = wbName;
                        }
                    }
                }


                armorItems.Add(record);
            }

            var combined = new List<IGameRecord>();
            combined.AddRange(armorItems.Where(i => !i.EditorID.Contains("cc")));
            combined.AddRange(weaponItems.Where(i => !i.EditorID.Contains("cc")));

            if (combined.Count > 0)
                espItemData[mod.ModKey.FileName] = combined;
        }

        Bench.End("Filter Mods");
        return espItemData;
    }


    public static string? FindModFile(string modKeyString)
    {
        return GlobalState.ModFilePathMap.TryGetValue(modKeyString, out var path)
            ? path
            : null;
    }

    static string? TryGetKeywordEdid(IFormLinkGetter<IKeywordGetter>? kwLink, ILinkCache linkCache)
    {
        if (kwLink == null)
            return null;

        // 1) TryResolve
        if (kwLink.TryResolve(linkCache, out var resolved) && !string.IsNullOrWhiteSpace(resolved?.EditorID))
            return resolved!.EditorID.Trim();

        // 2) Fallback: lookup in your keywordMap (FormKey -> EditorID)
        if (keywordMap.TryGetValue(kwLink.FormKey, out var edidFromMap) && !string.IsNullOrWhiteSpace(edidFromMap))
            return edidFromMap.Trim();

        // 3) Optional: fallback to global AllKeywords if vorhanden
        if (AllKeywords.TryGetValue(kwLink.FormKey, out var edidAll) && !string.IsNullOrWhiteSpace(edidAll))
            return edidAll.Trim();

        return null;
    }


}
