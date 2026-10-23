using SkyrimCraftingTool.Model;
using SkyrimCraftingTool.Services.ModularPresets;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;

namespace SkyrimCraftingTool.ViewModel
{
    // The editor for the modular preset building blocks (docs/ItemPresets-Plan.md, P6): Types,
    // Materials, Slot definitions, Keyword addons, Rules - each a list with a detail pane.
    //
    // The edit VMs write straight into the catalog's model objects; Save writes the five files. A
    // rename of a type, material or slot definition is carried into every rule that names it, and a
    // delete takes it out of them - rules refer by NAME, so a stale name would silently stop the rule
    // from firing.
    //
    // WEAPON MODE (docs/WeaponPresets-Plan.md, W5): the same editor over the weapon catalog. Addons,
    // rules and extras are the armor classes, so their lists here ARE the weapon catalog's lists; the
    // weapon types and materials get their own tabs. "Types" for a rule and "slots" for an addon's
    // "only on" are the weapon types then.
    public sealed class ModularCatalogEditorVM : ViewModelBase
    {
        private readonly ModularCatalogStore _store;
        private readonly ModularCatalog _catalog;
        private readonly WeaponCatalog? _weapons;
        private readonly WeaponCatalogStore? _weaponStore;

        public bool IsWeapons => _weapons != null;
        public bool IsArmor => _weapons == null;

        // Weapon mode only (empty otherwise).
        public EditList<WeaponTypeEditVM> WeaponTypes { get; }
        public EditList<WeaponMaterialEditVM> WeaponMaterials { get; }

        internal Func<string, string> KeywordName { get; }
        internal Func<string, string> MaterialName { get; }
        internal Func<string, string> PerkName { get; }
        internal IReadOnlyList<FormIDRecord> AllKeywords { get; }
        internal IReadOnlyList<FormIDRecord> AllMaterials { get; }
        internal IReadOnlyList<FormIDRecord> AllPerks { get; }
        internal IReadOnlyList<FormIDRecord> AllEnchantments { get; }

        // The load order's plugin file names, as picker records (Key = Name = file name).
        internal IReadOnlyList<FormIDRecord> AllPlugins { get; }
        internal List<ContainerRecord> AllContainers { get; }
        internal Func<string, string> EnchantmentName { get; }
        public IReadOnlyList<FormIDRecord> Workbenches { get; }

        public EditList<TypeEditVM> Types { get; }
        public EditList<MaterialEditVM> Materials { get; }
        public EditList<SlotEditVM> Slots { get; }
        public EditList<AddonEditVM> Addons { get; }
        public EditList<RuleEditVM> Rules { get; }
        public EditList<ExtraEditVM> Extras { get; }

        public string CatalogPath { get; }

        private bool _isDirty;
        public bool IsDirty { get => _isDirty; private set => SetProperty(ref _isDirty, value); }

        private string _status = "";
        public string Status { get => _status; set => SetProperty(ref _status, value); }

        public RelayCommand SaveCommand { get; }

        // P7: every slot preset in Output/Presets becomes a material (unsaved until Save).
        public RelayCommand ImportSlotPresetsCommand => new(() =>
        {
            var presets = new List<PresetFile>();
            foreach (var path in Services.PresetFileStore.FindAllPresetFiles())
            {
                try
                {
                    var p = Services.PresetFileStore.ReadPreset(path);
                    if (p != null) presets.Add(p);
                }
                catch (Exception ex)
                {
                    Status = $"Could not read {System.IO.Path.GetFileName(path)}: {ex.Message}";
                    return;
                }
            }
            ImportSlotPresets(presets);
        });

        // A material named like the preset is left alone (re-importing changes nothing you edited);
        // to take a preset over again, remove its material first.
        internal void ImportSlotPresets(IEnumerable<PresetFile> presets)
        {
            double bodyShare = _catalog.Slots.FirstOrDefault(s => s.Bits.Count == 1 && s.Bits[0] == 2)?.Share ?? 40;

            var imported = new List<MaterialModule>();
            var present = new List<string>();
            var skipped = new List<string>();
            foreach (var preset in SlotPresetImporter.OrderByTier(presets))
            {
                if (_catalog.Materials.Any(m => Same(m.Name, preset.PresetName))) { present.Add(preset.PresetName); continue; }
                var material = SlotPresetImporter.ToMaterial(preset, bodyShare, out var why);
                if (material == null) { skipped.Add($"{preset.PresetName} ({why})"); continue; }
                imported.Add(material);
            }

            foreach (var m in SlotPresetImporter.OrderForMatching(imported))
            {
                _catalog.Materials.Add(m);
                Materials.Items.Add(new MaterialEditVM(this, m));
            }
            if (imported.Count > 0)
            {
                Materials.Selected = Materials.Items.FirstOrDefault(vm => ReferenceEquals(vm.Model, imported[0])) ?? Materials.Selected;
                MarkDirty();
            }

            var parts = new List<string> { $"Imported {imported.Count} material(s) from slot presets (body share {bodyShare} %)." };
            if (present.Count > 0) parts.Add($"{present.Count} already there.");
            if (skipped.Count > 0) parts.Add("Skipped: " + string.Join(", ", skipped) + ".");
            if (imported.Count > 0) parts.Add("Not saved yet.");
            Status = string.Join(" ", parts);
        }

        public ModularCatalogEditorVM(MainContentVM main, bool weapons = false)
            : this(main.AllAvailableKeywords, main.AllAvailableMaterials, main.AllAvailablePerks, main.AllAvailableWorkbenches,
                   weapons ? ModularCatalogStore.WeaponsRoot : ModularCatalogStore.ArmorRoot, main.AllAvailableEnchantments, main.AllContainers,
                   main.ActivePlugins.Select(p => p.FileName), weapons)
        {
        }

        internal ModularCatalogEditorVM(IEnumerable<FormIDRecord>? keywords, IEnumerable<FormIDRecord>? materials,
            IEnumerable<FormIDRecord>? perks, IEnumerable<FormIDRecord>? workbenches, string root,
            IEnumerable<FormIDRecord>? enchantments = null, List<ContainerRecord>? containers = null,
            IEnumerable<string>? plugins = null, bool weapons = false)
        {
            AllPlugins = (plugins ?? Enumerable.Empty<string>())
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Select(p => new FormIDRecord { Key = p, Name = p })
                .ToList();
            // The "(no enchantment)" row is how an extra strips one; make sure it is there even when
            // the caller's list is bare (tests, or before the first scan).
            AllEnchantments = (enchantments ?? Enumerable.Empty<FormIDRecord>())
                .Where(e => !string.IsNullOrEmpty(e.Key))
                .Prepend(MainContentVM.NoEnchantmentChoice())
                .ToList();
            AllContainers = containers ?? new List<ContainerRecord>();
            EnchantmentName = Namer(AllEnchantments);
            CatalogPath = root;
            _store = new ModularCatalogStore(root);
            if (weapons)
            {
                _weaponStore = new WeaponCatalogStore(root);
                try
                {
                    _weapons = _weaponStore.Read();
                }
                catch (Exception ex)
                {
                    _weapons = new WeaponCatalog();
                    Status = $"Could not read the weapon building blocks ({ex.Message}). Saving will replace the files.";
                }
                // The shared kinds edit the weapon catalog's own lists.
                _catalog = new ModularCatalog { Addons = _weapons.Addons, Rules = _weapons.Rules, Extras = _weapons.Extras };
            }
            else
            {
                try
                {
                    _catalog = _store.Read();
                }
                catch (Exception ex)
                {
                    // Better an empty editor that says why than a crash; Save would overwrite the
                    // unreadable files, so it says that too.
                    _catalog = new ModularCatalog();
                    Status = $"Could not read the building blocks ({ex.Message}). Saving will replace the files.";
                }
            }

            AllKeywords = (keywords ?? Enumerable.Empty<FormIDRecord>()).ToList();
            AllMaterials = (materials ?? Enumerable.Empty<FormIDRecord>()).ToList();
            AllPerks = (perks ?? Enumerable.Empty<FormIDRecord>()).ToList();
            Workbenches = new[] { new FormIDRecord { Key = "", Name = "(none)" } }
                .Concat(workbenches ?? Enumerable.Empty<FormIDRecord>()).ToList();

            KeywordName = Namer(AllKeywords);
            MaterialName = Namer(AllMaterials);
            PerkName = Namer(AllPerks);

            Types = new EditList<TypeEditVM>(
                _catalog.Types.Select(t => new TypeEditVM(this, t)),
                () => { var m = new TypeModule { Name = UniqueName("New type", Types!.Items.Select(x => x.Name)), ArmorType = "HeavyArmor" }; _catalog.Types.Add(m); return new TypeEditVM(this, m); },
                vm => { _catalog.Types.Remove(vm.Model); ForEachRule(r => r.Types.RemoveAll(n => Same(n, vm.Name))); },
                MarkDirty);

            Materials = new EditList<MaterialEditVM>(
                _catalog.Materials.Select(m => new MaterialEditVM(this, m)),
                () => { var m = new MaterialModule { Name = UniqueName("New material", Materials!.Items.Select(x => x.Name)) }; _catalog.Materials.Add(m); return new MaterialEditVM(this, m); },
                vm => { _catalog.Materials.Remove(vm.Model); ForEachRule(r => r.Materials.RemoveAll(n => Same(n, vm.Name))); },
                MarkDirty);

            Slots = new EditList<SlotEditVM>(
                _catalog.Slots.Select(s => new SlotEditVM(this, s)),
                () => { var m = new SlotDefinition { Name = UniqueName("New slot", Slots!.Items.Select(x => x.Name)) }; _catalog.Slots.Add(m); return new SlotEditVM(this, m); },
                vm =>
                {
                    _catalog.Slots.Remove(vm.Model);
                    ForEachRule(r => { r.Slots.RemoveAll(n => Same(n, vm.Name)); r.ExcludeSlots.RemoveAll(n => Same(n, vm.Name)); });
                    foreach (var a in _catalog.Addons) a.OnlySlots?.RemoveAll(n => Same(n, vm.Name));
                    foreach (var m in _catalog.Materials) m.SlotOverrides?.RemoveAll(o => Same(o.Slot, vm.Name));
                },
                MarkDirty);

            Addons = new EditList<AddonEditVM>(
                _catalog.Addons.Select(a => new AddonEditVM(this, a)),
                () => { var m = new KeywordAddon { Name = UniqueName("New addon", Addons!.Items.Select(x => x.Name)) }; _catalog.Addons.Add(m); return new AddonEditVM(this, m); },
                vm =>
                {
                    _catalog.Addons.Remove(vm.Model);
                    foreach (var a in _catalog.Addons) a.AlsoAddons?.RemoveAll(n => Same(n, vm.Name));
                    ForEachRule(r => { r.Addons.RemoveAll(n => Same(n, vm.Name)); r.ExcludeAddons.RemoveAll(n => Same(n, vm.Name)); });
                },
                MarkDirty);

            Rules = new EditList<RuleEditVM>(
                _catalog.Rules.Select(r => new RuleEditVM(this, r)),
                () => { var m = new CombinationRule { Name = UniqueName("New rule", Rules!.Items.Select(x => x.Name)) }; _catalog.Rules.Add(m); return new RuleEditVM(this, m); },
                vm => _catalog.Rules.Remove(vm.Model),
                MarkDirty);

            Extras = new EditList<ExtraEditVM>(
                _catalog.Extras.Select(x => new ExtraEditVM(this, x)),
                () => { var m = new ExtraModule { Name = UniqueName("New extra", Extras!.Items.Select(x => x.Name)) }; _catalog.Extras.Add(m); return new ExtraEditVM(this, m); },
                vm => _catalog.Extras.Remove(vm.Model),
                MarkDirty);

            var wcat = _weapons ?? new WeaponCatalog();
            WeaponTypes = new EditList<WeaponTypeEditVM>(
                wcat.Types.Select(t => new WeaponTypeEditVM(this, t)),
                () => { var m = new WeaponTypeModule { Name = UniqueName("New weapon type", WeaponTypes!.Items.Select(x => x.Name)), WeaponClass = "OneHanded" }; wcat.Types.Add(m); return new WeaponTypeEditVM(this, m); },
                vm =>
                {
                    wcat.Types.Remove(vm.Model);
                    ForEachRule(r => r.Types.RemoveAll(n => Same(n, vm.Name)));
                    foreach (var a in _catalog.Addons) a.OnlySlots?.RemoveAll(n => Same(n, vm.Name));
                    foreach (var m in wcat.Materials) m.TypeOverrides?.RemoveAll(o => Same(o.WeaponType, vm.Name));
                },
                MarkDirty);

            WeaponMaterials = new EditList<WeaponMaterialEditVM>(
                wcat.Materials.Select(m => new WeaponMaterialEditVM(this, m)),
                () => { var m = new WeaponMaterialModule { Name = UniqueName("New material", WeaponMaterials!.Items.Select(x => x.Name)) }; wcat.Materials.Add(m); return new WeaponMaterialEditVM(this, m); },
                vm => { wcat.Materials.Remove(vm.Model); ForEachRule(r => r.Materials.RemoveAll(n => Same(n, vm.Name))); },
                MarkDirty);
            WeaponTypes.Items.CollectionChanged += (_, _) => { Rules.Selected?.RefreshChoices(); Addons.Selected?.RefreshChoices(); };
            WeaponMaterials.Items.CollectionChanged += (_, _) => Rules.Selected?.RefreshChoices();

            SaveCommand = new RelayCommand(() => Save());

            // The rule shown needs the CURRENT names: refresh it when another rule is picked and
            // whenever a type, slot or material is added or removed on another tab.
            Rules.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(EditList<RuleEditVM>.Selected)) Rules.Selected?.RefreshChoices();
            };
            Types.Items.CollectionChanged += (_, _) => Rules.Selected?.RefreshChoices();
            Slots.Items.CollectionChanged += (_, _) => { Rules.Selected?.RefreshChoices(); Addons.Selected?.RefreshChoices(); };
            Addons.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(EditList<AddonEditVM>.Selected)) Addons.Selected?.RefreshChoices();
            };
            Addons.Items.CollectionChanged += (_, _) => Addons.Selected?.RefreshChoices();
            Addons.Selected?.RefreshChoices();
            Materials.Items.CollectionChanged += (_, _) => Rules.Selected?.RefreshChoices();
            Rules.Selected?.RefreshChoices();
        }

        internal void MarkDirty() => IsDirty = true;

        public bool Save()
        {
            var problem = Validate();
            if (problem != null)
            {
                Status = problem;
                return false;
            }

            try
            {
                if (_weapons != null) _weaponStore!.Write(_weapons);
                else _store.Write(_catalog);
            }
            catch (Exception ex)
            {
                Status = $"Saving failed: {ex.Message}";
                return false;
            }

            IsDirty = false;
            Status = _weapons != null
                ? $"Saved ({_weapons.Types.Count} weapon types, {_weapons.Materials.Count} materials, {_catalog.Addons.Count} addons, {_catalog.Rules.Count} rules, {_catalog.Extras.Count} extras)."
                : $"Saved ({_catalog.Types.Count} types, {_catalog.Materials.Count} materials, {_catalog.Slots.Count} slots, {_catalog.Addons.Count} addons, {_catalog.Rules.Count} rules, {_catalog.Extras.Count} extras).";
            return true;
        }

        // Names are how rules and the pre-fill find things - an empty or doubled one would make a
        // rule fire for the wrong entry, or never.
        internal string? Validate()
        {
            foreach (var (kind, names) in new (string, IEnumerable<string>)[]
            {
                ("type", _weapons?.Types.Select(t => t.Name) ?? _catalog.Types.Select(t => t.Name)),
                ("material", _weapons?.Materials.Select(m => m.Name) ?? _catalog.Materials.Select(m => m.Name)),
                ("slot definition", _catalog.Slots.Select(s => s.Name)),
                ("addon", _catalog.Addons.Select(a => a.Name)),
                ("rule", _catalog.Rules.Select(r => r.Name)),
                ("extra", _catalog.Extras.Select(x => x.Name)),
            })
            {
                var list = names.ToList();
                if (list.Any(string.IsNullOrWhiteSpace)) return $"A {kind} has no name.";
                var dup = list.GroupBy(n => n.Trim(), StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
                if (dup != null) return $"Two {kind}s are called '{dup.Key}'.";
            }
            return null;
        }

        // Rules refer by name: carry a rename into each of them.
        internal void RenameReferences(string kind, string oldName, string newName)
        {
            if (string.IsNullOrWhiteSpace(oldName) || Same(oldName, newName)) return;
            ForEachRule(r =>
            {
                var lists = kind switch
                {
                    "type" => new[] { r.Types },
                    "material" => new[] { r.Materials },
                    "slot" => new[] { r.Slots, r.ExcludeSlots },
                    _ => Array.Empty<List<string>>(),
                };
                foreach (var list in lists)
                    for (int i = 0; i < list.Count; i++)
                        if (Same(list[i], oldName)) list[i] = newName;
            });
            // Weapon mode: an addon's "only on" and a material's exceptions name weapon types.
            if (kind == "type" && _weapons != null)
            {
                foreach (var a in _catalog.Addons)
                    for (int i = 0; i < a.OnlySlots.Count; i++)
                        if (Same(a.OnlySlots[i], oldName)) a.OnlySlots[i] = newName;
                foreach (var m in _weapons.Materials)
                    foreach (var o in m.TypeOverrides ?? new())
                        if (Same(o.WeaponType, oldName)) o.WeaponType = newName;
            }
            // Addons name slot definitions too ("only on these slots").
            if (kind == "slot")
                foreach (var a in _catalog.Addons)
                    for (int i = 0; i < a.OnlySlots.Count; i++)
                        if (Same(a.OnlySlots[i], oldName)) a.OnlySlots[i] = newName;
            // ... and so do the materials' exceptions per slot.
            if (kind == "slot")
                foreach (var m in _catalog.Materials)
                    foreach (var o in m.SlotOverrides ?? new())
                        if (Same(o.Slot, oldName)) o.Slot = newName;
            foreach (var rule in Rules.Items) rule.RefreshChoices();
            Addons.Selected?.RefreshChoices();
        }

        internal IEnumerable<string> TypeNames => _weapons?.Types.Select(t => t.Name) ?? _catalog.Types.Select(t => t.Name);
        internal IEnumerable<string> MaterialNames => _weapons?.Materials.Select(m => m.Name) ?? _catalog.Materials.Select(m => m.Name);

        // What an addon's "only on" lists: slot definitions, or the weapon types.
        internal IEnumerable<string> SlotNames => _weapons?.Types.Select(t => t.Name) ?? _catalog.Slots.Select(s => s.Name);

        // The weapon classes in use (types and materials), plus the four the starter catalog knows.
        internal IEnumerable<string> WeaponClasses => new[] { "OneHanded", "TwoHanded", "Bow", "Crossbow" }
            .Concat(_weapons?.Types.Select(t => t.WeaponClass) ?? Enumerable.Empty<string>())
            .Concat(_weapons?.Materials.SelectMany(m => m.Classes.Keys) ?? Enumerable.Empty<string>())
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Distinct(StringComparer.OrdinalIgnoreCase);
        internal IEnumerable<string> AddonNames => _catalog.Addons.Select(a => a.Name);
        internal IEnumerable<TypeModule> TypeModules => _catalog.Types;

        // Addons refer to each other by name ("also adds").
        internal void RenameAddonReferences(string oldName, string newName)
        {
            if (string.IsNullOrWhiteSpace(oldName) || Same(oldName, newName)) return;
            foreach (var a in _catalog.Addons)
                for (int i = 0; i < a.AlsoAddons.Count; i++)
                    if (Same(a.AlsoAddons[i], oldName)) a.AlsoAddons[i] = newName;
            ForEachRule(r =>
            {
                foreach (var list in new[] { r.Addons, r.ExcludeAddons })
                    for (int i = 0; i < list.Count; i++)
                        if (Same(list[i], oldName)) list[i] = newName;
            });
            foreach (var rule in Rules.Items) rule.RefreshChoices();
        }

        private void ForEachRule(Action<CombinationRule> action)
        {
            foreach (var r in _catalog.Rules) action(r);
        }

        internal static bool Same(string? a, string? b) => string.Equals(a?.Trim(), b?.Trim(), StringComparison.OrdinalIgnoreCase);

        private static string UniqueName(string stem, IEnumerable<string> taken)
        {
            var set = new HashSet<string>(taken, StringComparer.OrdinalIgnoreCase);
            if (!set.Contains(stem)) return stem;
            for (int i = 2; ; i++)
                if (!set.Contains($"{stem} {i}")) return $"{stem} {i}";
        }

        private static Func<string, string> Namer(IEnumerable<FormIDRecord> records)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in records)
                if (!string.IsNullOrEmpty(r.Key) && !map.ContainsKey(r.Key))
                    map[r.Key] = string.IsNullOrWhiteSpace(r.Name) ? r.Key : r.Name;
            return key => string.IsNullOrEmpty(key) ? "" : map.TryGetValue(key, out var n) ? n : key;
        }
    }

    // One catalog list: the entries, the selected one, add and remove.
    public sealed class EditList<T> : ViewModelBase where T : class
    {
        private readonly Func<T> _create;
        private readonly Action<T> _remove;
        private readonly Action _changed;

        public ObservableCollection<T> Items { get; }

        private T? _selected;
        public T? Selected { get => _selected; set => SetProperty(ref _selected, value); }

        public RelayCommand AddCommand { get; }
        public RelayCommand RemoveCommand { get; }

        public EditList(IEnumerable<T> items, Func<T> create, Action<T> remove, Action changed)
        {
            Items = new ObservableCollection<T>(items);
            _create = create;
            _remove = remove;
            _changed = changed;
            Selected = Items.FirstOrDefault();

            AddCommand = new RelayCommand(() =>
            {
                var item = _create();
                Items.Add(item);
                Selected = item;
                _changed();
            });
            RemoveCommand = new RelayCommand(() =>
            {
                if (Selected == null) return;
                var gone = Selected;
                int index = Items.IndexOf(gone);
                _remove(gone);
                Items.Remove(gone);
                Selected = Items.Count == 0 ? null : Items[Math.Min(index, Items.Count - 1)];
                _changed();
            });
        }
    }

    // A search box over a FormID catalogue (keywords, materials, perks) with an Add button.
    // Capped at 200 matches - the full keyword list is ~4,000 entries and nobody scrolls those.
    public sealed class RecordPickerVM : ViewModelBase
    {
        private readonly IReadOnlyList<FormIDRecord> _all;
        private readonly Action<FormIDRecord> _pick;

        public ObservableCollection<FormIDRecord> Matches { get; } = new();

        private string _search = "";
        public string Search
        {
            get => _search;
            set { if (SetProperty(ref _search, value ?? "")) Refresh(); }
        }

        private FormIDRecord? _selected;
        public FormIDRecord? Selected { get => _selected; set => SetProperty(ref _selected, value); }

        public RelayCommand AddCommand { get; }

        // allowFreeText: with nothing selected and no single match, Add takes what was typed - for
        // plugin patterns like "TAWOBA*" that name no single file.
        public RecordPickerVM(IReadOnlyList<FormIDRecord> all, Action<FormIDRecord> pick, bool allowFreeText = false)
        {
            _all = all;
            _pick = pick;
            AddCommand = new RelayCommand(() =>
            {
                var pick = Selected ?? (Matches.Count == 1 ? Matches[0] : null);
                if (pick == null && allowFreeText && !string.IsNullOrWhiteSpace(Search))
                    pick = new FormIDRecord { Key = Search.Trim(), Name = Search.Trim() };
                if (pick != null) _pick(pick);
            });
        }

        // The match list is only shown while there is something in it: empty - and disabled with
        // its picker - WPF paints the list in the system's white instead of the theme (2026-10-20).
        public bool HasMatches => Matches.Count > 0;

        private void Refresh()
        {
            Matches.Clear();
            var term = _search.Trim();
            if (term.Length >= 2)
            {
                foreach (var r in _all.Where(r =>
                             (r.Name?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false) ||
                             (r.Key?.Contains(term, StringComparison.OrdinalIgnoreCase) ?? false)).Take(200))
                    Matches.Add(r);
            }
            OnPropertyChanged(nameof(HasMatches));
        }
    }

    // A key shown by name, removable.
    public sealed class KeyEntryVM
    {
        public string Key { get; }
        public string Display { get; }
        public RelayCommand RemoveCommand { get; }

        public KeyEntryVM(string key, string display, Action<KeyEntryVM> remove)
        {
            Key = key;
            Display = display == key ? key : $"{display}  ({key})";
            RemoveCommand = new RelayCommand(() => remove(this));
        }
    }

    // A name in a rule condition list (types / slots / materials), ticked or not.
    public sealed class NameChoiceVM : ViewModelBase
    {
        private readonly Action<string, bool> _toggled;
        public string Name { get; }

        private bool _isChecked;
        public bool IsChecked
        {
            get => _isChecked;
            set { if (SetProperty(ref _isChecked, value)) _toggled(Name, value); }
        }

        public NameChoiceVM(string name, bool isChecked, Action<string, bool> toggled)
        {
            Name = name;
            _isChecked = isChecked;
            _toggled = toggled;
        }
    }

    // --- Types ---

    public sealed class TypeEditVM : ViewModelBase
    {
        private readonly ModularCatalogEditorVM _owner;
        internal TypeModule Model { get; }

        public IReadOnlyList<string> ArmorTypeChoices { get; } = new[] { "HeavyArmor", "LightArmor", "Clothing" };

        public RecipeEditVM FallbackCraft { get; }
        public RecipeEditVM FallbackTemper { get; }
        public RecipeEditVM FallbackBreakdown { get; }

        public TypeEditVM(ModularCatalogEditorVM owner, TypeModule model)
        {
            _owner = owner;
            Model = model;
            // Older files have no fallback recipes; the model's initializers only run for new objects.
            model.FallbackCraft ??= new MaterialRecipe();
            model.FallbackTemper ??= new MaterialRecipe();
            model.FallbackBreakdown ??= new MaterialRecipe();
            FallbackCraft = new RecipeEditVM(owner, model.FallbackCraft, "Fallback crafting", showsWorkbench: true,
                "For pieces of this type without a material (or whose material lists no crafting recipe). Same scaling as a material: each slot definition multiplies the amounts by its factor.");
            FallbackTemper = new RecipeEditVM(owner, model.FallbackTemper, "Fallback tempering", showsWorkbench: false,
                "Note: the game does not temper clothing. The workbench is derived when the recipe is created.");
            FallbackBreakdown = new RecipeEditVM(owner, model.FallbackBreakdown, "Fallback breakdown", showsWorkbench: true,
                "What breaking the item down PRODUCES. Only the first entry with an amount above 0 is used.", breakdown: true);
        }

        public string Name
        {
            get => Model.Name;
            set
            {
                var old = Model.Name;
                if (old == value) return;
                // An emptied name would lose every rule reference to it (Validate blocks saving it anyway).
                if (string.IsNullOrWhiteSpace(value)) { OnPropertyChanged(); return; }
                Model.Name = value ?? "";
                _owner.RenameReferences("type", old, Model.Name);
                OnPropertyChanged();
                _owner.MarkDirty();
            }
        }

        public string ArmorType
        {
            get => Model.ArmorType;
            set { if (Model.ArmorType == value) return; Model.ArmorType = value ?? ""; OnPropertyChanged(); _owner.MarkDirty(); }
        }

        // Off = no fallback (armor without a material keeps its values, clothing gets 0).
        // 0 = no rounding (2 / 3 decimals).
        public double ArmorRatingStep
        {
            get => Model.ArmorRatingStep;
            set { if (value < 0 || Model.ArmorRatingStep == value) return; Model.ArmorRatingStep = value; OnPropertyChanged(); _owner.MarkDirty(); }
        }

        public double WeightStep
        {
            get => Model.WeightStep;
            set { if (value < 0 || Model.WeightStep == value) return; Model.WeightStep = value; OnPropertyChanged(); _owner.MarkDirty(); }
        }

        public bool HasFallback
        {
            get => Model.FallbackTotals != null;
            set
            {
                if (value == HasFallback) return;
                Model.FallbackTotals = value ? new SetTotals() : null;
                OnPropertyChanged();
                OnPropertyChanged(nameof(FallbackArmorRating));
                OnPropertyChanged(nameof(FallbackValue));
                OnPropertyChanged(nameof(FallbackWeight));
                _owner.MarkDirty();
            }
        }

        public double FallbackArmorRating
        {
            get => Model.FallbackTotals?.ArmorRating ?? 0;
            set { var t = Model.FallbackTotals; if (t == null || t.ArmorRating == value) return; t.ArmorRating = value; OnPropertyChanged(); _owner.MarkDirty(); }
        }

        public int FallbackValue
        {
            get => Model.FallbackTotals?.Value ?? 0;
            set { var t = Model.FallbackTotals; if (t == null || t.Value == value) return; t.Value = value; OnPropertyChanged(); _owner.MarkDirty(); }
        }

        public double FallbackWeight
        {
            get => Model.FallbackTotals?.Weight ?? 0;
            set { var t = Model.FallbackTotals; if (t == null || t.Weight == value) return; t.Weight = value; OnPropertyChanged(); _owner.MarkDirty(); }
        }
    }

    // --- Materials ---

    public sealed class TotalsRowVM : ViewModelBase
    {
        private readonly MaterialModule _material;
        private readonly Action _changed;
        public string ArmorType { get; }

        public TotalsRowVM(MaterialModule material, string armorType, Action changed)
        {
            _material = material;
            ArmorType = armorType;
            _changed = changed;
        }

        private SetTotals? Totals => _material.Totals.TryGetValue(ArmorType, out var t) ? t : null;

        // Off = no entry for this type, which the engine reads as "keep the item's own values"
        // (clothing: 0).
        public bool Enabled
        {
            get => Totals != null;
            set
            {
                if (value == Enabled) return;
                if (value) _material.Totals[ArmorType] = new SetTotals();
                else _material.Totals.Remove(ArmorType);
                OnPropertyChanged();
                OnPropertyChanged(nameof(ArmorRating));
                OnPropertyChanged(nameof(Value));
                OnPropertyChanged(nameof(Weight));
                _changed();
            }
        }

        public double ArmorRating
        {
            get => Totals?.ArmorRating ?? 0;
            set { var t = Totals; if (t == null || t.ArmorRating == value) return; t.ArmorRating = value; OnPropertyChanged(); _changed(); }
        }

        public int Value
        {
            get => Totals?.Value ?? 0;
            set { var t = Totals; if (t == null || t.Value == value) return; t.Value = value; OnPropertyChanged(); _changed(); }
        }

        public double Weight
        {
            get => Totals?.Weight ?? 0;
            set { var t = Totals; if (t == null || t.Weight == value) return; t.Weight = value; OnPropertyChanged(); _changed(); }
        }
    }

    public sealed class RecipeEntryEditVM : ViewModelBase
    {
        private readonly MaterialEntry _entry;
        private readonly Action _changed;
        public string Display { get; }
        public RelayCommand RemoveCommand { get; }

        public RecipeEntryEditVM(MaterialEntry entry, string display, Action changed, Action<RecipeEntryEditVM> remove)
        {
            _entry = entry;
            _changed = changed;
            Display = display;
            RemoveCommand = new RelayCommand(() => remove(this));
        }

        internal MaterialEntry Model => _entry;

        public double Count
        {
            get => _entry.Count;
            set { if (_entry.Count == value) return; _entry.Count = value; OnPropertyChanged(); _changed(); }
        }

        public bool Fixed
        {
            get => _entry.Fixed;
            set { if (_entry.Fixed == value) return; _entry.Fixed = value; OnPropertyChanged(); _changed(); }
        }
    }

    // One of Craft / Temper / Breakdown. Conditions: the editor handles HasPerk (by far the common
    // case); conditions of any other type in the file are kept untouched and counted.
    public sealed class RecipeEditVM : ViewModelBase
    {
        private readonly ModularCatalogEditorVM _owner;
        private readonly MaterialRecipe _recipe;

        public string Title { get; }
        public bool ShowsWorkbench { get; }
        public string Hint { get; }
        public IReadOnlyList<FormIDRecord> Workbenches { get; }

        public ObservableCollection<RecipeEntryEditVM> Entries { get; } = new();
        public ObservableCollection<KeyEntryVM> Perks { get; } = new();
        public RecordPickerVM MaterialPicker { get; }
        public RecordPickerVM PerkPicker { get; }

        // breakdown: only the smelter and the tanning rack - the two benches a breakdown recipe can
        // sit at, the same two the item's own breakdown editor offers (BreakdownRecipeVM.Benches).
        public RecipeEditVM(ModularCatalogEditorVM owner, MaterialRecipe recipe, string title, bool showsWorkbench, string hint,
            bool breakdown = false)
        {
            _owner = owner;
            _recipe = recipe;
            Title = title;
            ShowsWorkbench = showsWorkbench;
            Hint = hint;
            Workbenches = breakdown
                ? owner.Workbenches.Take(1).Concat(BreakdownRecipeVM.Benches).ToList()
                : owner.Workbenches;

            foreach (var e in recipe.Materials) Entries.Add(EntryVM(e));
            foreach (var c in recipe.Conditions.Where(IsPerk)) Perks.Add(PerkVM(c.Target));

            MaterialPicker = new RecordPickerVM(owner.AllMaterials, r =>
            {
                var entry = new MaterialEntry { Key = r.Key, Count = 1 };
                _recipe.Materials.Add(entry);
                Entries.Add(EntryVM(entry));
                _owner.MarkDirty();
            });
            PerkPicker = new RecordPickerVM(owner.AllPerks, r =>
            {
                if (_recipe.Conditions.Any(c => IsPerk(c) && ModularCatalogEditorVM.Same(c.Target, r.Key))) return;
                _recipe.Conditions.Add(new ConditionEntry { ConditionType = "HasPerk", Target = r.Key, Value = "1", RunOn = "Subject" });
                Perks.Add(PerkVM(r.Key));
                _owner.MarkDirty();
            });
        }

        public FormIDRecord? Workbench
        {
            get => Workbenches.FirstOrDefault(w => ModularCatalogEditorVM.Same(w.Key, _recipe.WorkbenchKey)) ?? Workbenches[0];
            set
            {
                var key = value?.Key ?? "";
                if (ModularCatalogEditorVM.Same(key, _recipe.WorkbenchKey)) return;
                _recipe.WorkbenchKey = key;
                OnPropertyChanged();
                _owner.MarkDirty();
            }
        }

        public string OtherConditions
        {
            get
            {
                int n = _recipe.Conditions.Count(c => !IsPerk(c));
                return n == 0 ? "" : $"{n} other condition(s) from the file are kept as they are.";
            }
        }

        private static bool IsPerk(ConditionEntry c) => string.Equals(c.ConditionType, "HasPerk", StringComparison.OrdinalIgnoreCase);

        private RecipeEntryEditVM EntryVM(MaterialEntry e) => new(e, _owner.MaterialName(e.Key), _owner.MarkDirty, vm =>
        {
            _recipe.Materials.Remove(vm.Model);
            Entries.Remove(vm);
            _owner.MarkDirty();
        });

        private KeyEntryVM PerkVM(string key) => new(key, _owner.PerkName(key), vm =>
        {
            _recipe.Conditions.RemoveAll(c => IsPerk(c) && ModularCatalogEditorVM.Same(c.Target, vm.Key));
            Perks.Remove(vm);
            _owner.MarkDirty();
        });
    }

    public sealed class MaterialEditVM : ViewModelBase
    {
        private readonly ModularCatalogEditorVM _owner;
        internal MaterialModule Model { get; }

        public IReadOnlyList<TotalsRowVM> Totals { get; }
        public RecipeEditVM Craft { get; }
        public RecipeEditVM Temper { get; }
        public RecipeEditVM Breakdown { get; }
        public RecordPickerVM KeywordPicker { get; }
        public RelayCommand ClearKeywordCommand { get; }

        public MaterialEditVM(ModularCatalogEditorVM owner, MaterialModule model)
        {
            _owner = owner;
            Model = model;
            Totals = new[] { "HeavyArmor", "LightArmor", "Clothing" }
                .Select(t => new TotalsRowVM(model, t, owner.MarkDirty)).ToList();
            Craft = new RecipeEditVM(owner, model.Craft, "Crafting", showsWorkbench: true,
                "Base amounts for a whole set piece of share 1; each slot definition multiplies them by its factor. Fixed = same amount on every piece (catalysts).");
            Temper = new RecipeEditVM(owner, model.Temper, "Tempering", showsWorkbench: false,
                "The workbench is derived when the recipe is created. Usually one fixed primary material.");
            Breakdown = new RecipeEditVM(owner, model.Breakdown, "Breakdown", showsWorkbench: true,
                "What breaking the item down PRODUCES. Only the first entry with an amount above 0 is used.", breakdown: true);
            KeywordPicker = new RecordPickerVM(owner.AllKeywords, r => MaterialKeyword = r.Key);
            ClearKeywordCommand = new RelayCommand(() => MaterialKeyword = "");

            model.SlotOverrides ??= new();
            foreach (var o in model.SlotOverrides) Overrides.Add(RowFor(o));
            SlotChoices = owner.SlotNames.ToList();
            _newSlot = SlotChoices.FirstOrDefault() ?? "";
            AddOverrideCommand = new RelayCommand(() =>
            {
                if (string.IsNullOrWhiteSpace(NewOverrideType) || string.IsNullOrWhiteSpace(NewOverrideSlot)) return;
                if (Model.OverrideFor(NewOverrideType, NewOverrideSlot) != null) return;
                var o = new MaterialSlotOverride { ArmorType = NewOverrideType, Slot = NewOverrideSlot };
                Model.SlotOverrides.Add(o);
                Overrides.Add(RowFor(o));
                _owner.MarkDirty();
            });
        }

        // Exceptions to the slot shares for this material (Daedric helmet weighs 30 % of its set).
        public ObservableCollection<MaterialOverrideRowVM> Overrides { get; } = new();
        public IReadOnlyList<string> ArmorTypeChoices { get; } = new[] { "HeavyArmor", "LightArmor", "Clothing" };
        public IReadOnlyList<string> SlotChoices { get; }
        private string _newType = "HeavyArmor";
        public string NewOverrideType { get => _newType; set => SetProperty(ref _newType, value ?? ""); }
        private string _newSlot;
        public string NewOverrideSlot { get => _newSlot; set => SetProperty(ref _newSlot, value ?? ""); }
        public RelayCommand AddOverrideCommand { get; }

        private MaterialOverrideRowVM RowFor(MaterialSlotOverride o) => new(o, _owner.MarkDirty, row =>
        {
            Model.SlotOverrides.Remove(o);
            Overrides.Remove(row);
            _owner.MarkDirty();
        });

        public string Name
        {
            get => Model.Name;
            set
            {
                var old = Model.Name;
                if (old == value) return;
                // An emptied name would lose every rule reference to it (Validate blocks saving it anyway).
                if (string.IsNullOrWhiteSpace(value)) { OnPropertyChanged(); return; }
                Model.Name = value ?? "";
                _owner.RenameReferences("material", old, Model.Name);
                OnPropertyChanged();
                _owner.MarkDirty();
            }
        }

        public string MaterialKeyword
        {
            get => Model.MaterialKeyword;
            set
            {
                if (Model.MaterialKeyword == value) return;
                Model.MaterialKeyword = value ?? "";
                OnPropertyChanged();
                OnPropertyChanged(nameof(MaterialKeywordDisplay));
                _owner.MarkDirty();
            }
        }

        public string MaterialKeywordDisplay => Model.MaterialKeyword.Length == 0
            ? "(none)"
            : $"{_owner.KeywordName(Model.MaterialKeyword)}  ({Model.MaterialKeyword})";

        public string NamePatterns
        {
            get => Model.NamePatterns;
            set { if (Model.NamePatterns == value) return; Model.NamePatterns = value ?? ""; OnPropertyChanged(); _owner.MarkDirty(); }
        }

        public string NameExclusions
        {
            get => Model.NameExclusions;
            set { if (Model.NameExclusions == value) return; Model.NameExclusions = value ?? ""; OnPropertyChanged(); _owner.MarkDirty(); }
        }
    }

    // --- Slot definitions ---

    public sealed class BitChoiceVM : ViewModelBase
    {
        private readonly Action<int, bool> _toggled;
        public int Bit { get; }
        public string Label { get; }

        private bool _isChecked;
        public bool IsChecked
        {
            get => _isChecked;
            set { if (SetProperty(ref _isChecked, value)) _toggled(Bit, value); }
        }

        public BitChoiceVM(int bit, bool isChecked, Action<int, bool> toggled)
        {
            Bit = bit;
            _isChecked = isChecked;
            _toggled = toggled;
            // "32 Body" - the game slot number first, the way xEdit and modders say it.
            Label = $"{30 + bit} {Enum.GetName(typeof(ArmorSlotMask), 1u << bit)}";
        }
    }

    public sealed class SlotEditVM : ViewModelBase
    {
        private readonly ModularCatalogEditorVM _owner;
        internal SlotDefinition Model { get; }

        public IReadOnlyList<BitChoiceVM> Bits { get; }

        public SlotEditVM(ModularCatalogEditorVM owner, SlotDefinition model)
        {
            _owner = owner;
            Model = model;
            Bits = Enumerable.Range(0, 32)
                .Select(b => new BitChoiceVM(b, model.Bits.Contains(b), (bit, on) =>
                {
                    Model.Bits.Remove(bit);
                    if (on) { Model.Bits.Add(bit); Model.Bits.Sort(); }
                    OnPropertyChanged(nameof(BitsSummary));
                    _owner.MarkDirty();
                }))
                .ToList();
            model.TypeShares ??= new();
            _factorsText = FormatFactors(model.RecipeFactors);
            foreach (var t in owner.TypeModules.Where(t => !string.IsNullOrWhiteSpace(t.ArmorType)))
                TypeShares.Add(new TypeShareRowVM(t.Name, t.ArmorType, model, owner.MarkDirty));
        }

        // One row per type: own armor/value/weight shares, empty = the defaults above.
        public ObservableCollection<TypeShareRowVM> TypeShares { get; } = new();

        public string Name
        {
            get => Model.Name;
            set
            {
                var old = Model.Name;
                if (old == value) return;
                // An emptied name would lose every rule reference to it (Validate blocks saving it anyway).
                if (string.IsNullOrWhiteSpace(value)) { OnPropertyChanged(); return; }
                Model.Name = value ?? "";
                _owner.RenameReferences("slot", old, Model.Name);
                OnPropertyChanged();
                _owner.MarkDirty();
            }
        }

        public string BitsSummary => Model.Bits.Count == 0
            ? "No slot ticked - claims items that have no slot at all"
            : "Slots " + string.Join(" + ", Model.Bits.Select(b => (30 + b).ToString(CultureInfo.InvariantCulture)));

        public double Share
        {
            get => Model.Share;
            set { if (Model.Share == value) return; Model.Share = value; OnPropertyChanged(); _owner.MarkDirty(); }
        }

        // Empty = same as the armor-rating share.
        public string ValueShareText
        {
            get => Model.ValueShare?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "";
            set { var v = ParseShare(value); if (Model.ValueShare == v) return; Model.ValueShare = v; OnPropertyChanged(); _owner.MarkDirty(); }
        }

        public string WeightShareText
        {
            get => Model.WeightShare?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "";
            set { var v = ParseShare(value); if (Model.WeightShare == v) return; Model.WeightShare = v; OnPropertyChanged(); _owner.MarkDirty(); }
        }

        private static double? ParseShare(string? text)
            => double.TryParse((text ?? "").Trim().Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : null;

        public bool CarriesMaterialKeyword
        {
            get => Model.CarriesMaterialKeyword;
            set { if (Model.CarriesMaterialKeyword == value) return; Model.CarriesMaterialKeyword = value; OnPropertyChanged(); _owner.MarkDirty(); }
        }

        // "1, 0.5, 0" - one factor per position of a material's recipe list. Kept as typed while it
        // does not parse, so a half-typed "0." is not thrown away.
        private string _factorsText;
        public string FactorsText
        {
            get => _factorsText;
            set
            {
                if (!SetProperty(ref _factorsText, value ?? "")) return;
                var parsed = ParseFactors(_factorsText);
                if (parsed == null) { FactorsError = "Numbers separated by commas, e.g. 1, 0.5, 0"; return; }
                FactorsError = "";
                Model.RecipeFactors = parsed;
                _owner.MarkDirty();
            }
        }

        private string _factorsError = "";
        public string FactorsError { get => _factorsError; private set => SetProperty(ref _factorsError, value); }

        internal static List<double>? ParseFactors(string text)
        {
            var list = new List<double>();
            foreach (var raw in text.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var s = raw.Trim();
                if (s.Length == 0) continue;
                if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) || v < 0) return null;
                list.Add(v);
            }
            return list;
        }

        internal static string FormatFactors(IEnumerable<double> factors)
            => string.Join(", ", factors.Select(f => f.ToString("0.###", CultureInfo.InvariantCulture)));
    }

    // --- Addons ---

    public sealed class AddonEditVM : ViewModelBase
    {
        private readonly ModularCatalogEditorVM _owner;
        internal KeywordAddon Model { get; }

        public ObservableCollection<KeyEntryVM> Keywords { get; } = new();
        public RecordPickerVM KeywordPicker { get; }

        public AddedIngredientsVM CraftAdditions { get; }
        public AddedIngredientsVM TemperAdditions { get; }
        public PluginListVM OnlyPlugins { get; }
        public PluginListVM ExcludePlugins { get; }

        // "Only on these slot definitions" - rebuilt from the catalog's current names when shown.
        public ObservableCollection<NameChoiceVM> SlotChoices { get; } = new();

        // "Also adds these addons" - every other addon, ticked or not.
        public ObservableCollection<NameChoiceVM> AlsoChoices { get; } = new();

        public void RefreshChoices()
        {
            SlotChoices.Clear();
            foreach (var name in _owner.SlotNames)
                SlotChoices.Add(new NameChoiceVM(name, Model.OnlySlots.Any(s => ModularCatalogEditorVM.Same(s, name)), (n, on) =>
                {
                    Model.OnlySlots.RemoveAll(s => ModularCatalogEditorVM.Same(s, n));
                    if (on) Model.OnlySlots.Add(n);
                    _owner.MarkDirty();
                }));

            AlsoChoices.Clear();
            foreach (var name in _owner.AddonNames.Where(n => !ModularCatalogEditorVM.Same(n, Model.Name)))
                AlsoChoices.Add(new NameChoiceVM(name, Model.AlsoAddons.Any(s => ModularCatalogEditorVM.Same(s, name)), (n, on) =>
                {
                    Model.AlsoAddons.RemoveAll(s => ModularCatalogEditorVM.Same(s, n));
                    if (on) Model.AlsoAddons.Add(n);
                    _owner.MarkDirty();
                }));
        }

        public AddonEditVM(ModularCatalogEditorVM owner, KeywordAddon model)
        {
            _owner = owner;
            Model = model;
            // Older files have no addition lists; initializers only run for new objects.
            model.CraftAdditions ??= new List<IngredientEntry>();
            model.TemperAdditions ??= new List<IngredientEntry>();
            model.OnlySlots ??= new List<string>();
            model.OnlyPlugins ??= new List<string>();
            model.ExcludePlugins ??= new List<string>();
            model.AlsoAddons ??= new List<string>();
            OnlyPlugins = new PluginListVM(owner, model.OnlyPlugins, "Only from these plugins (empty = any):");
            ExcludePlugins = new PluginListVM(owner, model.ExcludePlugins, "Never from these plugins:");
            CraftAdditions = new AddedIngredientsVM(owner, model.CraftAdditions, "Adds to crafting");
            TemperAdditions = new AddedIngredientsVM(owner, model.TemperAdditions, "Adds to tempering");
            foreach (var k in model.Keywords) Keywords.Add(KeyVM(k));
            KeywordPicker = new RecordPickerVM(owner.AllKeywords, r =>
            {
                if (Model.Keywords.Any(k => ModularCatalogEditorVM.Same(k, r.Key))) return;
                Model.Keywords.Add(r.Key);
                Keywords.Add(KeyVM(r.Key));
                _owner.MarkDirty();
            });
        }

        public string Name
        {
            get => Model.Name;
            set
            {
                var old = Model.Name;
                if (old == value) return;
                // An emptied name would lose every "also adds" reference to it.
                if (string.IsNullOrWhiteSpace(value)) { OnPropertyChanged(); return; }
                Model.Name = value;
                _owner.RenameAddonReferences(old, value);
                OnPropertyChanged();
                _owner.MarkDirty();
            }
        }

        public string NamePatterns
        {
            get => Model.NamePatterns;
            set { if (Model.NamePatterns == value) return; Model.NamePatterns = value ?? ""; OnPropertyChanged(); _owner.MarkDirty(); }
        }

        public string NameExclusions
        {
            get => Model.NameExclusions;
            set { if (Model.NameExclusions == value) return; Model.NameExclusions = value ?? ""; OnPropertyChanged(); _owner.MarkDirty(); }
        }

        // Negative makes no sense for a multiplier; such an entry is ignored rather than stored.
        public double ArmorRatingFactor
        {
            get => Model.ArmorRatingFactor;
            set { if (value < 0 || Model.ArmorRatingFactor == value) return; Model.ArmorRatingFactor = value; OnPropertyChanged(); _owner.MarkDirty(); }
        }

        // Weapon catalogs: damage instead of armor rating; "only on" lists weapon types.
        public bool IsWeapons => _owner.IsWeapons;
        public bool IsArmor => _owner.IsArmor;
        public string OnlySlotsLabel => _owner.IsWeapons
            ? "Only on items of one of these weapon types (none ticked = any):"
            : "Only on items with one of these slot definitions (none ticked = any):";

        public double DamageFactor
        {
            get => Model.DamageFactor;
            set { if (value < 0 || Model.DamageFactor == value) return; Model.DamageFactor = value; OnPropertyChanged(); _owner.MarkDirty(); }
        }

        public string EditorIdPatterns
        {
            get => Model.EditorIdPatterns;
            set { if (Model.EditorIdPatterns == value) return; Model.EditorIdPatterns = value ?? ""; OnPropertyChanged(); _owner.MarkDirty(); }
        }

        public bool LeaveUntouched
        {
            get => Model.LeaveUntouched;
            set { if (Model.LeaveUntouched == value) return; Model.LeaveUntouched = value; OnPropertyChanged(); _owner.MarkDirty(); }
        }

        public bool KeepValues
        {
            get => Model.KeepValues;
            set { if (Model.KeepValues == value) return; Model.KeepValues = value; OnPropertyChanged(); _owner.MarkDirty(); }
        }

        public bool KeepRecipes
        {
            get => Model.KeepRecipes;
            set { if (Model.KeepRecipes == value) return; Model.KeepRecipes = value; OnPropertyChanged(); _owner.MarkDirty(); }
        }

        public bool KeepKeywords
        {
            get => Model.KeepKeywords;
            set { if (Model.KeepKeywords == value) return; Model.KeepKeywords = value; OnPropertyChanged(); _owner.MarkDirty(); }
        }

        public double ValueFactor
        {
            get => Model.ValueFactor;
            set { if (value < 0 || Model.ValueFactor == value) return; Model.ValueFactor = value; OnPropertyChanged(); _owner.MarkDirty(); }
        }

        public double WeightFactor
        {
            get => Model.WeightFactor;
            set { if (value < 0 || Model.WeightFactor == value) return; Model.WeightFactor = value; OnPropertyChanged(); _owner.MarkDirty(); }
        }

        private KeyEntryVM KeyVM(string key) => new(key, _owner.KeywordName(key), vm =>
        {
            Model.Keywords.RemoveAll(k => ModularCatalogEditorVM.Same(k, vm.Key));
            Keywords.Remove(vm);
            _owner.MarkDirty();
        });
    }

    // A list of plugin names or patterns ("TAWOBA*"), picked from the load order or typed.
    public sealed class PluginListVM : ViewModelBase
    {
        public string Title { get; }
        public ObservableCollection<KeyEntryVM> Rows { get; } = new();
        public RecordPickerVM Picker { get; }

        public PluginListVM(ModularCatalogEditorVM owner, List<string> list, string title)
        {
            Title = title;
            KeyEntryVM Row(string p) => new(p, p, vm =>
            {
                list.RemoveAll(x => ModularCatalogEditorVM.Same(x, vm.Key));
                Rows.Remove(vm);
                owner.MarkDirty();
            });

            foreach (var p in list) Rows.Add(Row(p));
            Picker = new RecordPickerVM(owner.AllPlugins, r =>
            {
                if (list.Any(x => ModularCatalogEditorVM.Same(x, r.Key))) return;
                list.Add(r.Key);
                Rows.Add(Row(r.Key));
                owner.MarkDirty();
            }, allowFreeText: true);
        }
    }

    // An addon's flat ingredient additions for one recipe kind.
    public sealed class AddedIngredientRowVM : ViewModelBase
    {
        private readonly IngredientEntry _entry;
        private readonly Action _changed;
        public string Display { get; }
        public RelayCommand RemoveCommand { get; }
        internal IngredientEntry Model => _entry;

        public AddedIngredientRowVM(IngredientEntry entry, string display, Action changed, Action<AddedIngredientRowVM> remove)
        {
            _entry = entry;
            _changed = changed;
            Display = display;
            RemoveCommand = new RelayCommand(() => remove(this));
        }

        // At least 1 - a row with 0 adds nothing and only confuses.
        public int Count
        {
            get => _entry.Count;
            set { var v = Math.Max(1, value); if (_entry.Count == v) return; _entry.Count = v; OnPropertyChanged(); _changed(); }
        }
    }

    public sealed class AddedIngredientsVM : ViewModelBase
    {
        public string Title { get; }
        public ObservableCollection<AddedIngredientRowVM> Rows { get; } = new();
        public RecordPickerVM MaterialPicker { get; }

        public AddedIngredientsVM(ModularCatalogEditorVM owner, List<IngredientEntry> list, string title)
        {
            Title = title;
            AddedIngredientRowVM Row(IngredientEntry e) => new(e, owner.MaterialName(e.Key), owner.MarkDirty, vm =>
            {
                list.Remove(vm.Model);
                Rows.Remove(vm);
                owner.MarkDirty();
            });

            foreach (var e in list) Rows.Add(Row(e));
            MaterialPicker = new RecordPickerVM(owner.AllMaterials, r =>
            {
                var e = new IngredientEntry { Key = r.Key, Count = 1 };
                list.Add(e);
                Rows.Add(Row(e));
                owner.MarkDirty();
            });
        }
    }

    // --- Extras ---

    // An enchantment with its charge, and leveled-list placements. Also the HOST for the shared
    // Styles/ContainerPlacementPanel.xaml (its PlacementTarget is this extra itself), so the
    // placement editor looks and behaves exactly like the item's.
    public sealed class ExtraEditVM : ViewModelBase
    {
        private readonly ModularCatalogEditorVM _owner;
        internal ExtraModule Model { get; }

        public RecordPickerVM EnchantmentPicker { get; }
        public ContainerSelectionVM ContainerSelection { get; }
        public ObservableCollection<ContainerEntryVM> CatalogContainers { get; } = new();

        public ExtraEditVM(ModularCatalogEditorVM owner, ExtraModule model)
        {
            _owner = owner;
            Model = model;
            EnchantmentPicker = new RecordPickerVM(owner.AllEnchantments, r =>
            {
                Model.EnchantmentKey = r.Key ?? "";
                OnPropertyChanged(nameof(SetsEnchantment));
                OnPropertyChanged(nameof(EnchantmentDisplay));
                _owner.MarkDirty();
            });

            ContainerSelection = new ContainerSelectionVM(owner.AllContainers);
            ContainerSelection.OwnerInfo = () => ("", Model.Name);
            ContainerSelection.LoadFromString(string.IsNullOrWhiteSpace(model.Container) ? "{}" : model.Container);
            foreach (var c in owner.AllContainers)
                CatalogContainers.Add(new ContainerEntryVM(c)
                {
                    IsSelected = ContainerSelection.SelectedContainers.Any(sc => sc.ContainerKey == c.ContainerKey),
                });
            ContainerSelection.SelectedContainers.CollectionChanged += (_, _) => SyncContainer();
            ContainerSelection.LevelChanged += SyncContainer;

            ToggleContainerForSelectedItemCommand = new RelayCommand<string>(key =>
            {
                if (string.IsNullOrEmpty(key)) return;
                ContainerSelection.ToggleContainer(key);
                var row = CatalogContainers.FirstOrDefault(c => c.ContainerKey == key);
                if (row != null) row.IsSelected = ContainerSelection.SelectedContainers.Any(sc => sc.ContainerKey == key);
            });
            ClearContainerSelectionCommand = new RelayCommand(() =>
            {
                ContainerSelection.Clear();
                foreach (var c in CatalogContainers) c.IsSelected = false;
            });
            ToggleExpertContainersCommand = new RelayCommand(() => ShowExpertContainers = !ShowExpertContainers);
        }

        public string Name
        {
            get => Model.Name;
            set { if (Model.Name == value || string.IsNullOrWhiteSpace(value)) { OnPropertyChanged(); return; } Model.Name = value; OnPropertyChanged(); _owner.MarkDirty(); }
        }

        // Off = the item keeps its enchantment. On with "(no enchantment)" = strip it.
        public bool SetsEnchantment
        {
            get => Model.EnchantmentKey != null;
            set
            {
                if (value == SetsEnchantment) return;
                Model.EnchantmentKey = value ? "" : null;
                OnPropertyChanged();
                OnPropertyChanged(nameof(EnchantmentDisplay));
                _owner.MarkDirty();
            }
        }

        public string EnchantmentDisplay => Model.EnchantmentKey == null
            ? "(left as it is)"
            : Model.EnchantmentKey.Length == 0
                ? "(no enchantment) - strips the item's enchantment"
                : $"{_owner.EnchantmentName(Model.EnchantmentKey)}  ({Model.EnchantmentKey})";

        public bool SetsAmount
        {
            get => Model.EnchantmentAmount.HasValue;
            set
            {
                if (value == SetsAmount) return;
                Model.EnchantmentAmount = value ? 0 : null;
                OnPropertyChanged();
                OnPropertyChanged(nameof(EnchantmentAmount));
                _owner.MarkDirty();
            }
        }

        public int EnchantmentAmount
        {
            get => Model.EnchantmentAmount ?? 0;
            set { if (!SetsAmount || Model.EnchantmentAmount == value) return; Model.EnchantmentAmount = value; OnPropertyChanged(); _owner.MarkDirty(); }
        }

        private void SyncContainer()
        {
            var built = ContainerSelection.BuildString();
            if (built == Model.Container) return;
            Model.Container = built;
            _owner.MarkDirty();
        }

        // --- What Styles/ContainerPlacementPanel.xaml asks its host for ---

        public ExtraEditVM PlacementTarget => this;
        public string ExistingPlacementSummary => "Every item this extra is applied to gets these placements added to its own.";
        public bool HasChangedLists => false;
        public string ChangedListsButtonText => "";
        public RelayCommand ShowChangedListsCommand { get; } = new(() => { });

        private string _containerSearchText = "";
        public string ContainerSearchText
        {
            get => _containerSearchText;
            set { if (SetProperty(ref _containerSearchText, value ?? "")) OnPropertyChanged(nameof(FilteredContainers)); }
        }

        private bool _showExpertContainers;
        public bool ShowExpertContainers
        {
            get => _showExpertContainers;
            set { if (SetProperty(ref _showExpertContainers, value)) OnPropertyChanged(nameof(FilteredContainers)); }
        }

        public IEnumerable<ContainerEntryVM> FilteredContainers => CatalogContainers
            .Where(c => ContainerEntryVM.IsShown(c, ShowExpertContainers))
            .Where(c => string.IsNullOrWhiteSpace(ContainerSearchText) || c.Name.Contains(ContainerSearchText, StringComparison.OrdinalIgnoreCase));

        public RelayCommand ToggleExpertContainersCommand { get; }
        public RelayCommand<string> ToggleContainerForSelectedItemCommand { get; }
        public RelayCommand ClearContainerSelectionCommand { get; }
    }

    // --- Rules ---

    public sealed class RuleEditVM : ViewModelBase
    {
        private readonly ModularCatalogEditorVM _owner;
        internal CombinationRule Model { get; }

        public ObservableCollection<NameChoiceVM> TypeChoices { get; } = new();
        public ObservableCollection<NameChoiceVM> SlotChoices { get; } = new();

        // Weapons have no slots: the slot groups are hidden there (Types are the weapon types).
        public bool ShowsSlots => _owner.IsArmor;
        public string TypeLabel => _owner.IsWeapons ? "Weapon type:" : "Type:";
        public ObservableCollection<NameChoiceVM> MaterialChoices { get; } = new();
        public ObservableCollection<NameChoiceVM> ExcludeChoices { get; } = new();
        public ObservableCollection<NameChoiceVM> AddonChoices { get; } = new();
        public ObservableCollection<NameChoiceVM> ExcludeAddonChoices { get; } = new();
        public ObservableCollection<KeyEntryVM> Keywords { get; } = new();
        public RecordPickerVM KeywordPicker { get; }

        public RuleEditVM(ModularCatalogEditorVM owner, CombinationRule model)
        {
            _owner = owner;
            Model = model;
            foreach (var k in model.Keywords) Keywords.Add(KeyVM(k));
            KeywordPicker = new RecordPickerVM(owner.AllKeywords, r =>
            {
                if (Model.Keywords.Any(k => ModularCatalogEditorVM.Same(k, r.Key))) return;
                Model.Keywords.Add(r.Key);
                Keywords.Add(KeyVM(r.Key));
                _owner.MarkDirty();
            });
        }

        public string Name
        {
            get => Model.Name;
            set { if (Model.Name == value) return; Model.Name = value ?? ""; OnPropertyChanged(); _owner.MarkDirty(); }
        }

        public string ConditionSummary
        {
            get
            {
                string Part(List<string> l, string any) => l.Count == 0 ? any : string.Join(" | ", l);
                var s = _owner.IsWeapons
                    ? Part(Model.Types, "any weapon type")
                    : $"{Part(Model.Types, "any type")} + {Part(Model.Slots, "any slot")}";
                if (Model.Materials.Count > 0) s += $" + {Part(Model.Materials, "")}";
                if (Model.ExcludeSlots.Count > 0) s += $", not with {string.Join(" | ", Model.ExcludeSlots)}";
                if (Model.Addons.Count > 0) s += $" + addon {string.Join(" | ", Model.Addons)}";
                if (Model.ExcludeAddons.Count > 0) s += $", not with addon {string.Join(" | ", Model.ExcludeAddons)}";
                return s;
            }
        }

        // Rebuilt from the catalog's CURRENT names each time the rule is shown - a type added or
        // renamed on another tab must show up here.
        public void RefreshChoices()
        {
            Fill(TypeChoices, _owner.TypeNames, Model.Types);
            // In weapon mode SlotNames are the weapon types (for addons) - a rule has no slots there.
            Fill(SlotChoices, _owner.IsArmor ? _owner.SlotNames : Enumerable.Empty<string>(), Model.Slots);
            Fill(MaterialChoices, _owner.MaterialNames, Model.Materials);
            Fill(ExcludeChoices, _owner.IsArmor ? _owner.SlotNames : Enumerable.Empty<string>(), Model.ExcludeSlots);
            Fill(AddonChoices, _owner.AddonNames, Model.Addons);
            Fill(ExcludeAddonChoices, _owner.AddonNames, Model.ExcludeAddons);
            OnPropertyChanged(nameof(ConditionSummary));
        }

        private void Fill(ObservableCollection<NameChoiceVM> target, IEnumerable<string> names, List<string> selected)
        {
            target.Clear();
            foreach (var name in names)
                target.Add(new NameChoiceVM(name, selected.Any(s => ModularCatalogEditorVM.Same(s, name)), (n, on) =>
                {
                    selected.RemoveAll(s => ModularCatalogEditorVM.Same(s, n));
                    if (on) selected.Add(n);
                    OnPropertyChanged(nameof(ConditionSummary));
                    _owner.MarkDirty();
                }));
        }

        private KeyEntryVM KeyVM(string key) => new(key, _owner.KeywordName(key), vm =>
        {
            Model.Keywords.RemoveAll(k => ModularCatalogEditorVM.Same(k, vm.Key));
            Keywords.Remove(vm);
            _owner.MarkDirty();
        });
    }
}

namespace SkyrimCraftingTool.ViewModel
{
    public sealed class TypeShareRowVM : ViewModelBase
    {
        private readonly string _armorType;
        private readonly SlotDefinition _slot;
        private readonly Action _dirty;

        public TypeShareRowVM(string label, string armorType, SlotDefinition slot, Action dirty)
        {
            Label = label;
            _armorType = armorType;
            _slot = slot;
            _dirty = dirty;
        }

        public string Label { get; }

        public string ArmorRatingText { get => Show(Current?.ArmorRating); set => Set(value, (o, v) => o.ArmorRating = v); }
        public string ValueText { get => Show(Current?.Value); set => Set(value, (o, v) => o.Value = v); }
        public string WeightText { get => Show(Current?.Weight); set => Set(value, (o, v) => o.Weight = v); }

        // Empty = the slot's recipe factors. LostFocus binding, so a half-typed list never lands.
        public string RecipeFactorsText
        {
            get => Current?.RecipeFactors is { } f ? SlotEditVM.FormatFactors(f) : "";
            set
            {
                var parsed = string.IsNullOrWhiteSpace(value) ? null : SlotEditVM.ParseFactors(value);
                if (parsed == null && !string.IsNullOrWhiteSpace(value)) { OnPropertyChanged(); return; }
                Update(o => o.RecipeFactors = parsed);
            }
        }

        private ShareOverride? Current
            => _slot.TypeShares.FirstOrDefault(kv => string.Equals(kv.Key, _armorType, StringComparison.OrdinalIgnoreCase)).Value;

        private static string Show(double? v) => v?.ToString(CultureInfo.InvariantCulture) ?? "";

        private void Set(string? text, Action<ShareOverride, double?> apply)
        {
            double? v = double.TryParse((text ?? "").Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;
            Update(o => apply(o, v));
        }

        private void Update(Action<ShareOverride> apply)
        {
            var key = _slot.TypeShares.Keys.FirstOrDefault(k => string.Equals(k, _armorType, StringComparison.OrdinalIgnoreCase)) ?? _armorType;
            if (!_slot.TypeShares.TryGetValue(key, out var o)) o = new ShareOverride();
            apply(o);
            // An all-empty row is no override at all - keeps the JSON free of empty entries.
            if (o.ArmorRating == null && o.Value == null && o.Weight == null && o.RecipeFactors == null) _slot.TypeShares.Remove(key);
            else _slot.TypeShares[key] = o;
            OnPropertyChanged(nameof(ArmorRatingText));
            OnPropertyChanged(nameof(ValueText));
            OnPropertyChanged(nameof(WeightText));
            OnPropertyChanged(nameof(RecipeFactorsText));
            _dirty();
        }
    }
}

namespace SkyrimCraftingTool.ViewModel
{
    // One exception row of a material: its own shares / recipe factors for one type + slot.
    public sealed class MaterialOverrideRowVM : ViewModelBase
    {
        private readonly MaterialSlotOverride _o;
        private readonly Action _dirty;

        public MaterialOverrideRowVM(MaterialSlotOverride o, Action dirty, Action<MaterialOverrideRowVM> remove)
        {
            _o = o;
            _dirty = dirty;
            RemoveCommand = new RelayCommand(() => remove(this));
        }

        public string Label => $"{_o.ArmorType} / {_o.Slot}";
        public RelayCommand RemoveCommand { get; }

        public string ArmorRatingText { get => Show(_o.ArmorRating); set { _o.ArmorRating = Parse(value); Changed(); } }
        public string ValueText { get => Show(_o.Value); set { _o.Value = Parse(value); Changed(); } }
        public string WeightText { get => Show(_o.Weight); set { _o.Weight = Parse(value); Changed(); } }
        public string RecipeFactorsText
        {
            get => _o.RecipeFactors is { } f ? SlotEditVM.FormatFactors(f) : "";
            set
            {
                var parsed = string.IsNullOrWhiteSpace(value) ? null : SlotEditVM.ParseFactors(value);
                if (parsed == null && !string.IsNullOrWhiteSpace(value)) { OnPropertyChanged(); return; }
                _o.RecipeFactors = parsed;
                Changed();
            }
        }

        private static string Show(double? v) => v?.ToString(CultureInfo.InvariantCulture) ?? "";
        private static double? Parse(string? text)
            => double.TryParse((text ?? "").Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;

        private void Changed()
        {
            OnPropertyChanged(nameof(ArmorRatingText));
            OnPropertyChanged(nameof(ValueText));
            OnPropertyChanged(nameof(WeightText));
            OnPropertyChanged(nameof(RecipeFactorsText));
            _dirty();
        }
    }
}
