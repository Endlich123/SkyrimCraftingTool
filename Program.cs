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
using DynamicData.Kernel;

namespace SkyrimCraftingTool;

public class Program
{

    public static string loadOrderFile;

    public static readonly HashSet<string> skipMods = new(StringComparer.OrdinalIgnoreCase)
    {
        // Filter problematic mods
        "Skyrim Extended Cut - Saints and Seducers.esp",
        "MoreNastyCritters.esp",
    };

    // SkyrimEsm UpdateEsm
    private static ISkyrimModGetter? _skyrimEsm;
    private static ISkyrimModGetter? _updateEsm;

    [STAThread]
    public static void Handler()
    {
        Bench.Start("Benchmark");

        var settings = FolderSettings.LoadSavedSettings();
        var folders = new FolderStructure(settings);
        folders.CheckFoldersAndLog();

        Console.WriteLine(">>> Version of 10. Februar 2026 <<<");

        string modFolder = AppContext.BaseDirectory;
        string inputFolder = Path.Combine(modFolder, "Input");
        Directory.CreateDirectory(inputFolder);
        string outputFolder = Path.Combine(modFolder, "Output");
        Directory.CreateDirectory(outputFolder);

        // LoadOrder 
        var loadOrderListings = LoadStockGameLoadOrder();

        // Skyrim.esm
        _skyrimEsm = SkyrimMod.CreateFromBinary(FolderStructure.Current.SkyrimEsmPath, SkyrimRelease.SkyrimSE);

        foreach (var misc in _skyrimEsm.MiscItems)
        {
            var editorID = misc.EditorID ?? "Unbenannt";
            GlobalState.MaterialMap[misc.FormKey] = editorID;
            GlobalState.MaterialMapReverse[editorID] = misc.FormKey;
        }


        // Mod-Data 
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


        // all mods
        Bench.Start("LoadAllMods");
        var loadedMods = MutagenSafeLoader.LoadAllMods(
            loadOrderListings.Select(lo => lo.ModKey),
            FindModFile
        );
        Bench.End("LoadAllMods");

        GlobalState.LoadedMods = loadedMods;

        // to LinkCache 
        GlobalState.LinkCache = loadedMods.ToImmutableLinkCache();

        // Keyword-Mapping
        GlobalState.KeywordMap.Clear();
        GlobalState.WorkbenchKeywords.Clear();
        GlobalState.VendorKeywords.Clear();

        // Keywords from SkyrimESM
        foreach (var keyword in _skyrimEsm.Keywords)
        {
            var edid = keyword.EditorID ?? "Unbenannt";

            GlobalState.KeywordMap[keyword.FormKey] = edid;
            GlobalState.KeywordMapReverse[edid] = keyword.FormKey;

            if (edid.Contains("Craft", StringComparison.OrdinalIgnoreCase))
                GlobalState.WorkbenchKeywords.Add(edid);

            if (edid.Contains("Vendor", StringComparison.OrdinalIgnoreCase))
            {
                GlobalState.VendorKeywords.Add(edid);
                GlobalState.KeywordMap[keyword.FormKey] = edid; // EditorID für GUI
            }

        }

        // Keywords from Update.esm 
        if (_updateEsm != null && _updateEsm.Keywords != null)
        {
            foreach (var keyword in _updateEsm.Keywords)
            {
                var edid = keyword.EditorID ?? "Unbenannt";

                GlobalState.KeywordMap[keyword.FormKey] = edid;
                GlobalState.KeywordMapReverse[edid] = keyword.FormKey;

                if (edid.Contains("Craft", StringComparison.OrdinalIgnoreCase))
                    GlobalState.WorkbenchKeywords.Add(edid);

                if (edid.Contains("Vendor", StringComparison.OrdinalIgnoreCase))
                    GlobalState.VendorKeywords.Add(edid);
            }
        }
        else
        {
            //Console.WriteLine("WARNUNG: update.esm couldn't be loaded.");
        }


        // Keywords from Mods
        foreach (var mod in GlobalState.LoadedMods)
        {
            foreach (var keyword in mod.Keywords)
            {
                var edid = keyword.EditorID ?? "Unbenannt";
                Debug.WriteLine("Keywords!!!!!!!!!!!!!!!!!");

                GlobalState.KeywordMap[keyword.FormKey] = edid;
                GlobalState.KeywordMapReverse[edid] = keyword.FormKey;

                foreach (var kv in GlobalState.KeywordMapReverse)
                    Debug.WriteLine($"{kv.Key} → {kv.Value}");

                if (edid.Contains("Craft", StringComparison.OrdinalIgnoreCase))
                    GlobalState.WorkbenchKeywords.Add(edid);

                if (edid.Contains("Vendor", StringComparison.OrdinalIgnoreCase))
                    GlobalState.VendorKeywords.Add(edid);
            }
        }

        //DebugConditionsOnSomeCOBJs();
        LoadAllPerks();
        //foreach (var fk in GlobalState.SmithingPerks) // how access the Formkeys
        //{
        //    if (GlobalState.PerkMap.TryGetValue(fk, out var edid))
        //    {
        //        Debug.WriteLine($"{edid}  →  {fk}");
        //    }
        //    else
        //    {
        //        Debug.WriteLine($"UNKNOWN → {fk}");
        //    }
        //}

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
            // skip filter
            if (skipMods.Contains(mod.ModKey.FileName))
                continue;

            // filter for relevant tags
            if (!MutagenSafeLoader.HasRelevantContent(mod))
                continue;

            List<ArmorRecord> armorItems = new();
            List<WeaponRecord> weaponItems = new();

            string sourcePath = FindModFile(mod.ModKey.FileName) ?? string.Empty;

            // weapon
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

                // Keyword-Handling
                if (weapon.Keywords != null)
                {
                    foreach (var kw in weapon.Keywords)
                    {
                        var edid = TryGetKeywordEdid(kw, linkCache);
                        if (string.IsNullOrWhiteSpace(edid))
                        {
                            //Console.WriteLine($"[KW-UNRESOLVED] Could not resolve keyword link for item {record.EditorID} in {mod.ModKey.FileName}");
                            continue;
                        }

                        record.Keywords.Add(edid);

                        // VendorKeywords
                        if (GlobalState.VendorKeywords.Contains(edid))
                        {
                            if (!record.Vendor.Contains(edid, StringComparer.OrdinalIgnoreCase))
                                record.Vendor.Add(edid);
                            //Console.WriteLine($"[VendorAdd] Mod={mod.ModKey.FileName} Item={record.EditorID} VendorEdid='{edid}'");
                        }
                    }
                }


                // ConstructibleObjects
                foreach (var cobj in mod.ConstructibleObjects)
                {
                    if (cobj.CreatedObject.FormKey != record.FormKey)
                        continue;

                    var wbKey = cobj.WorkbenchKeyword?.FormKey;

                    if (cobj.Items == null)
                        continue;

                    foreach (var entry in cobj.Items)
                    {
                        var itemKey = entry.Item.Item.FormKey;
                        int count = entry.Item.Count;

                        if (GlobalState.MaterialMap.TryGetValue(itemKey, out var matName) &&
                            GlobalState.KeywordMap.TryGetValue(wbKey ?? default, out var wbName))
                        {
                            record.Materials[matName] = count;
                            record.Workbench = wbName;
                        }
                    }
                }


                weaponItems.Add(record);
            }

            // armor
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

                //keyword-Handling
                if (armor.Keywords != null)
                {
                    foreach (var kw in armor.Keywords)
                    {
                        var edid = TryGetKeywordEdid(kw, linkCache);
                        if (string.IsNullOrWhiteSpace(edid))
                        {
                            //Console.WriteLine($"[KW-UNRESOLVED] Could not resolve keyword link for item {record.EditorID} in {mod.ModKey.FileName}");
                            continue;
                        }

                        record.Keywords.Add(edid);

                        // VendorKeywords
                        if (GlobalState.VendorKeywords.Contains(edid))
                        {
                            if (!record.Vendor.Contains(edid, StringComparer.OrdinalIgnoreCase))
                                record.Vendor.Add(edid);
                            //Console.WriteLine($"[VendorAdd] Mod={mod.ModKey.FileName} Item={record.EditorID} VendorEdid='{edid}'");
                        }
                    }
                }



                // Slots
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
                            // place slot
                            record.SelectedSlot = (ArmorSlot)slotValue;
                        }

                        string slotName = Enum.GetName(typeof(ArmorSlot), slotValue) ?? $"Unbekannter Slot {slotValue}";
                        record.Slots.Add($"{slotValue}:{slotName}");
                    }
                }


                // materials from ConstructibleObjects
                foreach (var cobj in mod.ConstructibleObjects)
                {
                    if (cobj.CreatedObject.FormKey != record.FormKey)
                        continue;

                    var wbKey = cobj.WorkbenchKeyword?.FormKey;

                    if (cobj.Items == null)
                        continue;

                    foreach (var entry in cobj.Items)
                    {
                        var itemKey = entry.Item.Item.FormKey;
                        int count = entry.Item.Count;

                        if (GlobalState.MaterialMap.TryGetValue(itemKey, out var matName) &&
                            GlobalState.KeywordMap.TryGetValue(wbKey ?? default, out var wbName))
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
        if (GlobalState.KeywordMap.TryGetValue(kwLink.FormKey, out var edidFromMap) && !string.IsNullOrWhiteSpace(edidFromMap))
            return edidFromMap.Trim();

        // 3) Optional: fallback to global AllKeywords if vorhanden
        if (GlobalState.AllKeywords.TryGetValue(kwLink.FormKey, out var edidAll) && !string.IsNullOrWhiteSpace(edidAll))
            return edidAll.Trim();

        return null;
    }

    //public static void DebugConditionsOnSomeCOBJs()
    //{
    //    if (_skyrimEsm == null)
    //        return;

    //    int counter = 0;

    //    foreach (var cobj in _skyrimEsm.ConstructibleObjects)
    //    {
    //        if (cobj.Conditions == null || cobj.Conditions.Count == 0)
    //            continue;

    //        Debug.WriteLine($"COBJ: {cobj.EditorID ?? cobj.FormKey.ToString()}");
    //        foreach (var cond in cobj.Conditions)
    //        {
    //            var data = cond.Data;
    //            Debug.WriteLine($"  Condition Data Type: {data?.GetType().FullName ?? "null"}");
    //            Debug.WriteLine($"  Condition ToString(): {data}");
    //        } 

    //            counter++;
    //        if (counter > 20) break; // nicht alles fluten
    //    }
    //}

    //public static void readperksfromitems() //dont delete can be used later
    //{
    //    if (_skyrimEsm == null)
    //        return;

    //    // Lokale Variablen holen
    //    var loadedMods = GlobalState.LoadedMods;
    //    var linkCache = GlobalState.LinkCache;
    //    var modKey = _skyrimEsm.ModKey;

    //    // Skyrim.esm + Update.esm + Mods in EINEN Cache packen
    //    var allMods = new List<ISkyrimModGetter>();
    //    allMods.Add(_skyrimEsm);

    //    if (_updateEsm != null)
    //        allMods.Add(_updateEsm);

    //    allMods.AddRange(loadedMods);

    //    // new LinkCache
    //    linkCache = allMods.ToImmutableLinkCache();

    //    foreach (var cobj in _skyrimEsm.ConstructibleObjects)
    //    {
    //        if (cobj.Conditions == null)
    //            continue;

    //        foreach (var cond in cobj.Conditions)
    //        {
    //            if (cond.Data is HasPerkConditionData perkData)
    //            {
    //                var perkItem = perkData.Perk;

    //                // Try Link
    //                var link = perkItem.Link;
    //                if (link != null && link.TryResolve<IPerkGetter>(linkCache, out var perk))
    //                {
    //                    Debug.WriteLine($"PERK FOUND (Link): {perk.EditorID} ({perk.FormKey})");
    //                    continue;
    //                }

    //                // Index
    //                if (perkItem.Index is uint index)
    //                {
    //                    var fk = new FormKey(_skyrimEsm.ModKey, index);
    //                    var linkFromIndex = fk.ToLink<IPerkGetter>();

    //                    if (linkFromIndex.TryResolve<IPerkGetter>(linkCache, out var resolved))
    //                    {
    //                        Debug.WriteLine($"PERK FOUND via Index: {resolved.EditorID} ({resolved.FormKey})");
    //                    }
    //                    else
    //                    {
    //                        Debug.WriteLine($"PERK unresolved (Index): {fk}");
    //                    }

    //                    continue;
    //                }

    //                // neither Link nor Index
    //                Debug.WriteLine("PERK unresolved (neither Link nor Index)");
    //            }
    //        }
    //    }
    //}

    public static void LoadAllPerks()
    {
        if (_skyrimEsm == null)
            return;

        var loadedMods = GlobalState.LoadedMods;
        var allMods = new List<ISkyrimModGetter>();

        allMods.Add(_skyrimEsm);
        if (_updateEsm != null)
            allMods.Add(_updateEsm);
        allMods.AddRange(loadedMods);

        var linkCache = allMods.ToImmutableLinkCache();

        // Globale Maps clear
        GlobalState.PerkMap.Clear();
        GlobalState.PerkMapReverse.Clear();

        // load all perks
        var allPerks = linkCache.PriorityOrder
            .SelectMany(mod => mod.EnumerateMajorRecords<IPerkGetter>())
            .ToList();

        foreach (var perk in allPerks)
        {
            var edid = perk.EditorID ?? $"PERK_{perk.FormKey}";

            GlobalState.PerkMap[perk.FormKey] = edid;
            GlobalState.PerkMapReverse[edid] = perk.FormKey;

            //Debug.WriteLine($"PERK: {edid} ({perk.FormKey})");
        }

        GlobalState.SmithingPerks.Clear();

        foreach (var perk in allPerks)
        {
            var edid = perk.EditorID ?? $"PERK_{perk.FormKey}";

            GlobalState.PerkMap[perk.FormKey] = edid;
            GlobalState.PerkMapReverse[edid] = perk.FormKey;

            if (GlobalState.SmithingPerkEditorIDs.Contains(edid))
                GlobalState.SmithingPerks.Add(perk.FormKey);
        }


    }
}