using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Aspects;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Noggog;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace SkyrimCraftingTool;

public class ESPWriter
{
    private SkyrimMod _patchMod;
    private readonly List<PluginInfo> _items = new();

    // Cache der geladenen Quell-ESPs (Key = voller Pfad)
    private readonly Dictionary<string, SkyrimMod> _loadedSourceMods = new(StringComparer.OrdinalIgnoreCase);

    public void AddItem(PluginInfo item)
    {
        if (item == null) return;
        _items.Add(item);
    }

    public void AddItems(IEnumerable<PluginInfo> pluginItems)
    {
        if (pluginItems == null) return;
        foreach (var item in pluginItems)
            AddItem(item);
    }

    public void WriteToEsp(string outputPath)
    {
        if (string.IsNullOrWhiteSpace(outputPath))
            throw new ArgumentException("Output path must not be empty.", nameof(outputPath));

        if (_items.Count == 0)
        {
            //Console.WriteLine("[ESPWriter] Keine Items zum Schreiben vorhanden. Abbruch.");
            return;
        }

        // Patch-Mod initialisieren
        var modKey = ModKey.FromNameAndExtension(Path.GetFileName(outputPath));
        _patchMod = new SkyrimMod(modKey, SkyrimRelease.SkyrimSE);

        // 1. Alle gültigen EspPaths einsammeln
        var validItems = _items.Where(i => !string.IsNullOrWhiteSpace(i.EspPath)).ToList();
        if (validItems.Count == 0)
        {
            //Console.WriteLine("[ESPWriter] Keine Items mit gültigem EspPath. Abbruch.");
            return;
        }

        var espPaths = validItems
            .Select(i => i.EspPath)
            .Where(p => !string.IsNullOrWhiteSpace(p) && File.Exists(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (espPaths.Count == 0)
        {
            //Console.WriteLine("[ESPWriter] Keine existierenden ESP-Dateien gefunden. Abbruch.");
            return;
        }

        // 2. Master-Referenzen aus Dateinamen
        var masterNames = espPaths
            .Select(p => Path.GetFileName(p))
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var master in masterNames)
        {
            _patchMod.ModHeader.MasterReferences.Add(new MasterReference
            {
                Master = ModKey.FromNameAndExtension(master!)
            });
        }

        // 3. Alle benötigten ESPs laden
        foreach (var espPath in espPaths)
        {
            try
            {
                var sourceMod = SkyrimMod.CreateFromBinary(espPath, SkyrimRelease.SkyrimSE);
                _loadedSourceMods[espPath] = sourceMod;
                Console.WriteLine($"[ESPWriter] Loaded source mod: {espPath}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ESPWriter] Fehler beim Laden von '{espPath}': {ex.Message}");
            }
        }

        // 4. Items verarbeiten
        foreach (var item in validItems)
        {
            if (!_loadedSourceMods.TryGetValue(item.EspPath, out var sourceMod))
            {
                //Console.WriteLine($"[ESPWriter] Quelle '{item.EspPath}' für Item '{item.ItemName}' konnte nicht geladen werden. Übersprungen.");
                continue;
            }

            if (!FormKey.TryFactory(item.FormKey, out var formKey))
            {
                //Console.WriteLine($"[ESPWriter] Ungültiger FormKey '{item.FormKey}' für Item '{item.ItemName}'. Übersprungen.");
                continue;
            }

            WriteArmorWeaponOrMisc(item, sourceMod, formKey);
        }

        // 5. Patch-ESP schreiben
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        _patchMod.WriteToBinary(outputPath);
        Console.WriteLine($"[ESPWriter] Patch geschrieben: {outputPath}");
    }

    private void WriteArmorWeaponOrMisc(PluginInfo item, SkyrimMod sourceMod, FormKey formKey)
    {
        // Robust: Typ anhand des tatsächlichen Records erkennen
        var originalArmor = sourceMod.Armors.FirstOrDefault(a => a.FormKey == formKey);
        if (originalArmor != null)
        {
            WriteArmor(item, sourceMod, originalArmor);
            return;
        }

        var originalWeapon = sourceMod.Weapons.FirstOrDefault(w => w.FormKey == formKey);
        if (originalWeapon != null)
        {
            WriteWeapon(item, sourceMod, originalWeapon);
            return;
        }

        // Fallback: irgendwas anderes
        WriteMiscItem(item);
    }

    // -------------------------
    // Armor-Schreiben
    // -------------------------
    private void WriteArmor(PluginInfo item, SkyrimMod sourceMod, IArmorGetter originalArmor)
    {
        var armorOverride = _patchMod.Armors.GetOrAddAsOverride(originalArmor);

        // Basiswerte
        armorOverride.Value = (uint)item.ItemValue;
        armorOverride.Weight = item.ItemWeight;
        armorOverride.ArmorRating = item.ArmorRating;

        // Vendor Keywords
        if (item.Vendors != null && item.Vendors.Count > 0)
        {
            armorOverride.Keywords ??= new();
            ReplaceVendorKeywords(armorOverride.Keywords, item.Vendors);
        }

        // Slot-Maske setzen (ArmorSlot ist bei dir aktuell der Enum-Name, z. B. "Body")
        if (TryBuildBipedFlagFromArmorSlot(item.ArmorSlot, out var bipedFlag))
        {
            armorOverride.BodyTemplate ??= new BodyTemplate();
            armorOverride.BodyTemplate.FirstPersonFlags = bipedFlag;
        }

        // COBJ (Rezept) suchen / erstellen
        var existingRecipe = sourceMod.ConstructibleObjects
            .FirstOrDefault(cobj => cobj.CreatedObject.FormKey == originalArmor.FormKey);

        if (existingRecipe != null)
        {
            WriteArmorRecipeOverride(item, existingRecipe);
        }
        else
        {
            WriteArmorRecipeNew(item, armorOverride);
        }
    }

    private void WriteArmorRecipeOverride(PluginInfo item, IConstructibleObjectGetter existingRecipeGetter)
    {
        var recipeOverride = _patchMod.ConstructibleObjects.GetOrAddAsOverride(existingRecipeGetter);

        // Workbench
        if (!string.IsNullOrEmpty(item.Workbench) &&
            Program.keywordMapReverse.TryGetValue(item.Workbench, out var workbenchKeyword))
        {
            recipeOverride.WorkbenchKeyword = new FormLinkNullable<IKeywordGetter>(workbenchKeyword);
        }

        // Items neu setzen
        recipeOverride.Items?.Clear();
        recipeOverride.Items ??= new();

        if (item.Materials != null)
        {
            foreach (var mat in item.Materials)
            {
                if (!Program.materialMapReverse.TryGetValue(mat.Key, out var materialFormKey))
                {
                    Console.WriteLine($"[ESPWriter] Material '{mat.Key}' nicht in materialMapReverse gefunden.");
                    continue;
                }

                var containerItem = new ContainerItem
                {
                    Item = materialFormKey.ToLink<IItemGetter>(),
                    Count = (short)mat.Value
                };

                recipeOverride.Items.Add(new ContainerEntry { Item = containerItem });
            }
        }
    }

    private void WriteArmorRecipeNew(PluginInfo item, Armor armorOverride)
    {
        var newRecipe = _patchMod.ConstructibleObjects.AddNew();
        newRecipe.Items ??= new();

        newRecipe.CreatedObject.SetTo(armorOverride);
        newRecipe.CreatedObjectCount = 1;

        if (!string.IsNullOrEmpty(item.Workbench) &&
            Program.keywordMapReverse.TryGetValue(item.Workbench, out var workbenchKeyword))
        {
            newRecipe.WorkbenchKeyword = new FormLinkNullable<IKeywordGetter>(workbenchKeyword);
        }

        if (item.Materials != null)
        {
            foreach (var mat in item.Materials)
            {
                if (!Program.materialMapReverse.TryGetValue(mat.Key, out var materialFormKey))
                {
                    Console.WriteLine($"[ESPWriter] Material '{mat.Key}' nicht in materialMapReverse gefunden.");
                    continue;
                }

                var containerItem = new ContainerItem
                {
                    Item = materialFormKey.ToLink<IItemGetter>(),
                    Count = (short)mat.Value
                };

                newRecipe.Items.Add(new ContainerEntry { Item = containerItem });
            }
        }
    }

    // -------------------------
    // Weapon-Schreiben (Grundgerüst)
    // -------------------------
    private void WriteWeapon(PluginInfo item, SkyrimMod sourceMod, IWeaponGetter originalWeapon)
    {
        Console.WriteLine($"[ESPWriter] Weapon override: {item.ItemName} ({item.FormKey})");

        var weaponOverride = _patchMod.Weapons.GetOrAddAsOverride(originalWeapon);

        weaponOverride.BasicStats ??= new();

        weaponOverride.BasicStats.Value = (uint)item.ItemValue;
        weaponOverride.BasicStats.Weight = item.ItemWeight;
        weaponOverride.BasicStats.Damage = (ushort)item.Damage;

        // Vendor Keywords
        if (item.Vendors != null && item.Vendors.Count > 0)
        {
            weaponOverride.Keywords ??= new();
            ReplaceVendorKeywords(weaponOverride.Keywords, item.Vendors);
        }

        // COBJ suchen / schreiben
        var existingRecipe = sourceMod.ConstructibleObjects
            .FirstOrDefault(cobj => cobj.CreatedObject.FormKey == originalWeapon.FormKey);

        if (existingRecipe != null)
        {
            WriteWeaponRecipeOverride(item, existingRecipe);
        }
        else
        {
            WriteWeaponRecipeNew(item, weaponOverride);
        }
    }


    private void WriteWeaponRecipeOverride(PluginInfo item, IConstructibleObjectGetter existingRecipeGetter)
    {
        var recipeOverride = _patchMod.ConstructibleObjects.GetOrAddAsOverride(existingRecipeGetter);

        if (!string.IsNullOrEmpty(item.Workbench))
        {
            if (Program.keywordMapReverse.TryGetValue(item.Workbench, out var workbenchKeyword))
            {
                Console.WriteLine($"[ESPWriter] Workbench gesetzt: {item.Workbench} → {workbenchKeyword}");
                recipeOverride.WorkbenchKeyword = new FormLinkNullable<IKeywordGetter>(workbenchKeyword);
            }
            else
            {
                Console.WriteLine($"[ESPWriter] Workbench NICHT gefunden: '{item.Workbench}'");
            }
        }


        recipeOverride.Items?.Clear();
        recipeOverride.Items ??= new();

        if (item.Materials != null)
        {
            foreach (var mat in item.Materials)
            {
                if (!Program.materialMapReverse.TryGetValue(mat.Key, out var materialFormKey))
                    continue;

                var containerItem = new ContainerItem
                {
                    Item = materialFormKey.ToLink<IItemGetter>(),
                    Count = (short)mat.Value
                };

                recipeOverride.Items.Add(new ContainerEntry { Item = containerItem });
            }
        }
    }

    private void WriteWeaponRecipeNew(PluginInfo item, Weapon weaponOverride)
    {
        var newRecipe = _patchMod.ConstructibleObjects.AddNew();
        newRecipe.Items ??= new();

        newRecipe.CreatedObject.SetTo(weaponOverride);
        newRecipe.CreatedObjectCount = 1;

        if (!string.IsNullOrEmpty(item.Workbench))
        {
            if (Program.keywordMapReverse.TryGetValue(item.Workbench, out var workbenchKeyword))
            {
                Console.WriteLine($"[ESPWriter] Workbench gesetzt: {item.Workbench} → {workbenchKeyword}");
                newRecipe.WorkbenchKeyword = new FormLinkNullable<IKeywordGetter>(workbenchKeyword);
            }
            else
            {
                Console.WriteLine($"[ESPWriter] Workbench NICHT gefunden: '{item.Workbench}'");
            }
        }


        if (item.Materials != null)
        {
            foreach (var mat in item.Materials)
            {
                if (!Program.materialMapReverse.TryGetValue(mat.Key, out var materialFormKey))
                    continue;

                var containerItem = new ContainerItem
                {
                    Item = materialFormKey.ToLink<IItemGetter>(),
                    Count = (short)mat.Value
                };

                newRecipe.Items.Add(new ContainerEntry { Item = containerItem });
            }
        }
    }

    // -------------------------
    // Misc (Fallback)
    // -------------------------
    private void WriteMiscItem(PluginInfo item)
    {
        Console.WriteLine($"[ESPWriter] Misc oder nicht erkannter Record-Typ: {item.ItemName} ({item.FormKey})");
        // Hier könntest du später z.B. MiscItems unterstützen, wenn gewünscht.
    }

    // -------------------------
    // ArmorSlot -> BipedObjectFlag
    // -------------------------
    private bool TryBuildBipedFlagFromArmorSlot(string armorSlot, out BipedObjectFlag flag)
    {
        flag = 0;

        if (string.IsNullOrWhiteSpace(armorSlot))
            return false;

        // Bei dir ist ArmorSlot aktuell der Enum-Name (z. B. "Body").
        if (!Enum.TryParse<ArmorSlot>(armorSlot, out var slotEnum))
            return false;

        int slotValue = (int)slotEnum;   // z. B. 32 für Body
        int bitIndex = slotValue - 30;   // dein Offset in Program.cs

        if (bitIndex < 0 || bitIndex >= 64)
            return false;

        flag = (BipedObjectFlag)(1UL << bitIndex);
        return true;
    }

    //clean VendorKeyword:
    private void ReplaceVendorKeywords(ExtendedList<IFormLinkGetter<IKeywordGetter>> keywords, List<string> newVendorNames)
    {
        //Debug.WriteLine("=== DEBUG keywordMapReverse ===");
        //foreach (var kv in Program.keywordMapReverse)
        //    Debug.WriteLine($"{kv.Key} → {kv.Value}");

        if (keywords == null)
            return;

        // 1. Alle Vendor‑Keywords entfernen
        var vendorLinks = keywords
            .Where(link =>
                link != null &&
                Program.keywordMap.TryGetValue(link.FormKey, out var edid) &&
                edid.StartsWith("VendorItem", StringComparison.OrdinalIgnoreCase))
            .ToList();

        foreach (var vendorLink in vendorLinks)
            keywords.Remove(vendorLink);

        // 2. Neue Vendor‑Keywords hinzufügen
        foreach (var vendorName in newVendorNames.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (Program.keywordMapReverse.TryGetValue(vendorName, out var keywordFormKey))
            {
                var newLink = keywordFormKey.ToLink<IKeywordGetter>();
                if (!keywords.Any(k => k.FormKey == keywordFormKey))
                {
                    //Debug.WriteLine($"[ESPWriter] Füge VendorKeyword hinzu: {vendorName} → {keywordFormKey}");
                    keywords.Add(newLink);
                }
            }
            else
            {
                //Debug.WriteLine($"[ESPWriter] VendorKeyword NICHT gefunden: '{vendorName}'");
            }
        }

    }
}
