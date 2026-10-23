using SkyrimCraftingTool.Model;
using SkyrimCraftingTool.Services.ModularPresets;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;

namespace SkyrimCraftingTool.ViewModel
{
    // One armor item in the dry-run: what the pre-fill read, and what the engine would make of it.
    // Everything is pre-formatted text - the window only shows it, and a few thousand cards of
    // ready strings scroll better than converters running per field.
    public sealed class ModularDryRunRow
    {
        public string Item { get; init; } = "";
        public string Plugin { get; init; } = "";
        public string Type { get; init; } = "";
        public string Material { get; init; } = "";
        public string Slots { get; init; } = "";
        public string Unassigned { get; init; } = "";
        public string Addons { get; init; } = "";
        public string ArmorRating { get; init; } = "";
        public string Value { get; init; } = "";
        public string Weight { get; init; } = "";

        // The values line of the item card - armor: rating, value, weight; weapons: damage, crit, ...
        public string Stats { get; init; } = "";
        public string Keywords { get; init; } = "";
        public string Craft { get; init; } = "";
        public string Notes { get; init; } = "";
        public bool HasNotes { get; init; }

        // Anything the patch would write differently (values, keywords, type).
        public bool HasChanges { get; init; }

        // Plain names: the plugin card's tally ("Steel 12, Iron 4") and the item card's choices.
        internal string TypeName { get; init; } = "";
        internal string MaterialName { get; init; } = "";
        internal IReadOnlyList<string> SlotNames { get; init; } = Array.Empty<string>();
        internal IReadOnlyList<string> AddonNames { get; init; } = Array.Empty<string>();

        // What "(auto)" means on the cards: detected only (plugin card), detected + plugin card (item card).
        internal string DetectedType { get; init; } = "";
        internal string DetectedMaterial { get; init; } = "";
        internal string AutoType { get; init; } = "";
        internal string AutoMaterial { get; init; } = "";
        internal string AutoSlots { get; init; } = "";

        // Not shown - what "Apply" writes to.
        internal ItemNodeVM Node { get; init; } = null!;
    }

    // The modular preset dry-run (docs/ItemPresets-Plan.md, P4): pre-fill + engine over every armor
    // item the tree currently shows, nothing written. One card per plugin (load order, with what the
    // presets make of it as a whole), one card per item inside. Both can be switched off - red, left
    // out of every Apply - and both can get their own preset
    // combination on top of the detection. All of it is remembered (PatchPluginSelection).
    public sealed class ModularDryRunVM : ViewModelBase
    {
        internal const string Auto = "(auto)";
        internal const string NoMaterial = "(none)";

        // "(auto)" with what it gives: "(auto: Steel)". The first entry of every card's list.
        internal static string AutoLabel(string value) => $"(auto: {(value.Length == 0 ? "none" : value)})";
        // A rebuilt list (new "(auto: …)" label) briefly sets the combo to null - the setters ignore
        // that, or a chosen material would fall back to auto.
        internal static bool IsAuto(string? value) => value == null || value.StartsWith("(auto", StringComparison.Ordinal);
        internal static IReadOnlyList<string> WithAuto(IReadOnlyList<string> options, string autoLabel)
            => new[] { autoLabel }.Concat(options.Where(o => o != Auto)).ToList();

        private readonly MainContentVM _main;
        private readonly PatchPluginSelection _armorSelection = new();
        private readonly PatchPluginSelection _weaponSelection = new(PatchPluginSelection.WeaponsPath);
        private ModularCatalog? _catalog;
        private WeaponCatalog? _weaponCatalog;
        private string _armorProblem = "";
        private string _weaponProblem = "";
        private HashSet<string> _weaponMaterialKeywords = new();
        private Dictionary<string, string> _keywordNames = new();
        private Dictionary<string, string> _materialNames = new();
        private HashSet<string> _materialKeywords = new();

        public ObservableCollection<DryRunPluginCardVM> PluginCards { get; } = new();
        public ICollectionView PluginCardsView { get; }

        // What the cards' combo boxes offer - names from the catalog.
        public IReadOnlyList<string> TypeOptions { get; private set; } = new[] { Auto };
        public IReadOnlyList<string> MaterialOptions { get; private set; } = new[] { Auto };
        public IReadOnlyList<string> SlotOptions { get; private set; } = new[] { Auto };
        public IReadOnlyList<string> AddonOptions { get; private set; } = Array.Empty<string>();

        private string _summary = "";
        public string Summary { get => _summary; private set => SetProperty(ref _summary, value); }

        public string CatalogPath => ShowWeapons ? ModularCatalogStore.WeaponsRoot : ModularCatalogStore.ArmorRoot;

        // Armor or weapons - one window, two catalogs, two remembered selections.
        private bool _showWeapons;
        public bool ShowWeapons
        {
            get => _showWeapons;
            set
            {
                if (!SetProperty(ref _showWeapons, value)) return;
                OnPropertyChanged(nameof(ShowArmor));
                OnPropertyChanged(nameof(ShowSlots));
                OnPropertyChanged(nameof(CatalogPath));
                Build();
            }
        }
        public bool ShowArmor { get => !_showWeapons; set => ShowWeapons = !value; }

        // Weapons have no slots: the item card hides that box.
        public bool ShowSlots => !_showWeapons;

        private string Kind => _showWeapons ? "weapon" : "armor";

        private string _search = "";
        // Matches plugin names and item names - a plugin stays visible when one of its items matches.
        public string Search
        {
            get => _search;
            set { if (SetProperty(ref _search, value ?? "")) RefreshFilters(); }
        }

        private bool _onlyWithNotes;
        public bool OnlyWithNotes
        {
            get => _onlyWithNotes;
            set { if (SetProperty(ref _onlyWithNotes, value)) RefreshFilters(); }
        }

        private bool _onlyChanging;
        public bool OnlyChanging
        {
            get => _onlyChanging;
            set { if (SetProperty(ref _onlyChanging, value)) RefreshFilters(); }
        }

        public RelayCommand ApplyCommand { get; }

        // On / off / open / close for every plugin card the filter shows.
        public RelayCommand AllPluginsOnCommand => new(() => SetVisiblePlugins(true));
        public RelayCommand AllPluginsOffCommand => new(() => SetVisiblePlugins(false));
        // Back to on + "(auto)" for every card shown (plugins and all their items). The items' own
        // data is not touched - that is the item view's Reset.
        public RelayCommand ResetAllCommand => new(() =>
        {
            var cards = VisibleCards().ToList();
            if (cards.Count == 0) return;
            int itemCount = cards.Sum(c => c.Items.Count);
            int choices = cards.Count(c => c.HasOwnChoice || !c.IsIncluded)
                        + cards.Sum(c => c.Items.Count(i => i.HasOwnChoice || !i.IsIncluded));
            if (choices == 0)
            {
                MessageBox.Show("Nothing to reset - every card shown is switched on and set to (auto).", "Reset all", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var answer = MessageBox.Show(
                $"Reset {choices} card(s) among the {cards.Count} plugin(s) and {itemCount} item(s) shown?\n\n"
                + "Every preset combination goes back to (auto) and everything switched off is switched on again. "
                + "The items themselves are not changed.",
                "Reset all", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes) return;

            Selection.Reset(cards.Select(c => c.Name), cards.SelectMany(c => c.Items).Select(i => i.Key));
            Build();
        });

        public RelayCommand ExpandAllCommand => new(() => { foreach (var c in VisibleCards()) c.IsExpanded = true; });
        public RelayCommand CollapseAllCommand => new(() => { foreach (var c in VisibleCards()) c.IsExpanded = false; });

        // Edit the blocks, then run again with what was saved - the whole point of the dry run is
        // trying a combination and seeing it.
        public RelayCommand EditCatalogCommand => new(() =>
        {
            View.ModularCatalogWindow.Show(Application.Current?.Windows.OfType<View.ModularDryRunWindow>().FirstOrDefault(), _main, ShowWeapons);
            if (ShowWeapons) _weaponCatalog = ReadWeaponCatalog(ModularCatalogStore.WeaponsRoot, out _weaponProblem);
            else _catalog = ReadCatalog(ModularCatalogStore.ArmorRoot, out _armorProblem);
            Build();
        });

        public ModularDryRunVM(MainContentVM main)
        {
            _main = main;
            PluginCardsView = CollectionViewSource.GetDefaultView(PluginCards);
            PluginCardsView.Filter = o => o is DryRunPluginCardVM c && c.HasVisibleItems;
            ApplyCommand = new RelayCommand(async () => await ApplyAsync());

            _catalog = ReadCatalog(ModularCatalogStore.ArmorRoot, out _armorProblem);
            _weaponCatalog = ReadWeaponCatalog(ModularCatalogStore.WeaponsRoot, out _weaponProblem);
            Build();
        }

        internal static WeaponCatalog? ReadWeaponCatalog(string path, out string problem)
        {
            problem = "";
            WeaponCatalog catalog;
            try
            {
                catalog = new WeaponCatalogStore(path).Read();
            }
            catch (Exception ex)
            {
                problem = $"The weapon building blocks could not be read: {ex.Message}";
                return null;
            }

            if (catalog.Types.Count == 0)
            {
                problem = "No weapon building blocks yet (weapon types are needed).";
                return null;
            }
            return catalog;
        }

        // The catalog, or null with the reason.
        internal static ModularCatalog? ReadCatalog(string path, out string problem)
        {
            problem = "";
            ModularCatalog catalog;
            try
            {
                catalog = new ModularCatalogStore(path).Read();
            }
            catch (Exception ex)
            {
                problem = $"The building blocks could not be read: {ex.Message}";
                return null;
            }

            if (catalog.Types.Count == 0 || catalog.Slots.Count == 0)
            {
                problem = "No building blocks yet (types and slot definitions are needed).";
                return null;
            }
            return catalog;
        }

        // Every armor item of the tree as it is filtered now, in the tree's (load) order.
        internal static List<(ItemNodeVM Item, string Plugin)> VisibleArmor(MainContentVM main)
        {
            var list = new List<(ItemNodeVM, string)>();
            foreach (var plugin in main.FilteredTree)
                foreach (var category in plugin.Categories)
                    foreach (var item in category.Items)
                        if (item.IsArmor) list.Add((item, plugin.PluginName));
            return list;
        }

        // The same for weapons - or armor, by the window's switch.
        internal static List<(ItemNodeVM Item, string Plugin)> VisibleItems(MainContentVM main, bool weapons)
        {
            if (!weapons) return VisibleArmor(main);
            var list = new List<(ItemNodeVM, string)>();
            foreach (var plugin in main.FilteredTree)
                foreach (var category in plugin.Categories)
                    foreach (var item in category.Items)
                        if (item.IsWeapon) list.Add((item, plugin.PluginName));
            return list;
        }

        private void Build()
        {
            // No catalog for the side shown: say why, show nothing.
            if (ShowWeapons ? _weaponCatalog == null : _catalog == null)
            {
                PluginCards.Clear();
                Summary = ShowWeapons ? _weaponProblem : _armorProblem;
                return;
            }

            // Open cards stay open across a recompute (Apply, edited building blocks).
            var expanded = new HashSet<string>(PluginCards.Where(c => c.IsExpanded).Select(c => c.Name), StringComparer.OrdinalIgnoreCase);
            var itemsOpen = new HashSet<string>(PluginCards.Where(c => c.ItemsExpanded).Select(c => c.Name), StringComparer.OrdinalIgnoreCase);
            var itemCardsOpen = new HashSet<string>(PluginCards.SelectMany(c => c.Items).Where(i => i.IsExpanded).Select(i => i.Key), StringComparer.OrdinalIgnoreCase);
            PluginCards.Clear();
            _keywordNames = Lookup(_main.AllAvailableKeywords);
            _materialNames = Lookup(_main.AllAvailableMaterials);
            _materialKeywords = ModularPresetEngine.MaterialKeywordsOf(_main.AllAvailableKeywords);
            _weaponMaterialKeywords = WeaponPresetEngine.MaterialKeywordsOf(_main.AllAvailableKeywords);

            if (ShowWeapons)
            {
                TypeOptions = new[] { Auto }.Concat(_weaponCatalog!.Types.Select(t => t.Name)).ToList();
                MaterialOptions = new[] { Auto, NoMaterial }.Concat(_weaponCatalog.Materials.Select(m => m.Name)).ToList();
                SlotOptions = new[] { Auto };
                AddonOptions = _weaponCatalog.Addons.Select(a => a.Name).ToList();
            }
            else
            {
                TypeOptions = new[] { Auto }.Concat(_catalog!.Types.Select(t => t.Name)).ToList();
                MaterialOptions = new[] { Auto, NoMaterial }.Concat(_catalog.Materials.Select(m => m.Name)).ToList();
                SlotOptions = new[] { Auto }.Concat(_catalog.Slots.Select(s => s.Name)).ToList();
                AddonOptions = _catalog.Addons.Select(a => a.Name).ToList();
            }
            OnPropertyChanged(nameof(TypeOptions));
            OnPropertyChanged(nameof(MaterialOptions));
            OnPropertyChanged(nameof(SlotOptions));
            OnPropertyChanged(nameof(AddonOptions));

            // GroupBy keeps the order of first appearance - the tree's, which is the load order.
            foreach (var g in VisibleItems(_main, ShowWeapons).GroupBy(x => x.Plugin, StringComparer.OrdinalIgnoreCase))
            {
                var card = new DryRunPluginCardVM(this, g.Key, g.Select(x => x.Item).ToList())
                {
                    IsExpanded = expanded.Contains(g.Key),
                    ItemsExpanded = itemsOpen.Contains(g.Key),
                };
                foreach (var i in card.Items) i.IsExpanded = itemCardsOpen.Contains(i.Key);
                PluginCards.Add(card);
            }
            RefreshFilters();
        }

        internal PatchPluginSelection Selection => _showWeapons ? _weaponSelection : _armorSelection;

        // Pre-fill, then the plugin's and the item's own choice, then the engine.
        internal ModularDryRunRow RowFor(ItemNodeVM item, string plugin)
        {
            Func<ModularSelection, ModularSelection> pluginStage = detected => Selection.PluginChoice(plugin)?.Resolve(detected) ?? detected;
            Func<ModularSelection, ModularSelection> itemStage = withPlugin => Selection.ItemChoice(item.Key)?.Resolve(withPlugin) ?? withPlugin;
            return ShowWeapons
                ? BuildWeaponRow(_weaponCatalog!, item, plugin, _main, _keywordNames, _materialNames, _weaponMaterialKeywords, pluginStage, itemStage)
                : BuildRow(_catalog!, item, plugin, _main, _keywordNames, _materialNames, _materialKeywords, pluginStage, itemStage);
        }

        internal bool ItemVisible(DryRunItemCardVM item)
            => (!_onlyWithNotes || item.Row.HasNotes)
            && (!_onlyChanging || item.Row.HasChanges)
            && (_search.Length == 0
                || item.Row.Item.Contains(_search, StringComparison.OrdinalIgnoreCase)
                || item.Row.Plugin.Contains(_search, StringComparison.OrdinalIgnoreCase));

        private void RefreshFilters()
        {
            foreach (var c in PluginCards) c.RefreshItems();
            PluginCardsView.Refresh();
            UpdateSummary();
        }

        internal void Changed() => UpdateSummary();

        private IEnumerable<DryRunPluginCardVM> VisibleCards() => PluginCardsView.Cast<DryRunPluginCardVM>().ToList();

        private void SetVisiblePlugins(bool on)
        {
            var cards = VisibleCards().ToList();
            Selection.SetMany(cards.Select(c => c.Name), on);
            foreach (var c in cards) c.SetIncludedSilently(on);
            UpdateSummary();
        }

        // What Apply writes to: every item the filter shows whose plugin AND card are on.
        private List<DryRunItemCardVM> Targets()
            => VisibleCards().Where(c => c.IsIncluded)
                .SelectMany(c => c.VisibleItems.Where(i => i.IsIncluded))
                .ToList();

        private void UpdateSummary()
        {
            int items = PluginCards.Sum(c => c.Items.Count);
            if (items == 0)
            {
                Summary = $"The tree shows no {(ShowWeapons ? "weapons" : "armor")}. Clear the search or the filters to test more items.";
                return;
            }
            int targets = Targets().Count;
            int pluginsOff = PluginCards.Count(c => !c.IsIncluded);
            int itemsOff = PluginCards.Where(c => c.IsIncluded).Sum(c => c.Items.Count(i => !i.IsIncluded));
            int ownChoices = PluginCards.Count(c => c.HasOwnChoice) + PluginCards.Sum(c => c.Items.Count(i => i.HasOwnChoice));
            Summary = $"{items} {Kind} item(s) in {PluginCards.Count} plugin(s) from the tree, in load order. "
                    + $"Apply would patch {targets} - {pluginsOff} plugin(s) and {itemsOff} single item(s) switched off, {ownChoices} card(s) with their own preset combination. Nothing has been changed.";
        }

        private async Task ApplyAsync()
        {
            if (ShowWeapons ? _weaponCatalog == null : _catalog == null) return;
            var targets = Targets();
            if (targets.Count == 0) return;

            var answer = MessageBox.Show(
                $"Apply the building blocks to the {targets.Count} {Kind} item(s) that are switched on and shown?\n\nEvery item can be reverted afterwards with its Reset button.",
                "Preset patch", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes) return;

            var pluginOf = targets.ToDictionary(t => t.Row.Node, t => t.Row.Plugin);
            var nodes = targets.Select(t => t.Row.Node).ToList();
            var selection = Selection;
            ModularSelection Resolve(ItemNodeVM item, ModularSelection detected) => selection.Resolve(pluginOf[item], item.Key, detected);
            var report = ShowWeapons
                ? await WeaponPresetApplier.ApplyAsync(_main, _weaponCatalog!, nodes, Resolve)
                : await ModularPresetApplier.ApplyAsync(_main, _catalog!, nodes, Resolve);
            Build();
            Summary = $"Applied: {report.Changed} of {report.Items} item(s) changed{(report.SkippedNoName > 0 ? $", {report.SkippedNoName} skipped (no name)" : "")}. The cards now show the state after the patch.";
        }

        private static ModularDryRunRow BuildRow(ModularCatalog catalog, ItemNodeVM item, string plugin, MainContentVM main,
            Dictionary<string, string> keywordNames, Dictionary<string, string> materialNames, HashSet<string> materialKeywords,
            Func<ModularSelection, ModularSelection> pluginStage, Func<ModularSelection, ModularSelection> itemStage)
        {
            if (ModularPresetApplier.IsSkipped(item))
                return new ModularDryRunRow
                {
                    Node = item,
                    Item = item.EditorID ?? item.Key,
                    Plugin = plugin,
                    Type = "-", Material = "-", Slots = "-", Keywords = "-", Craft = "-",
                    ArmorRating = "-", Value = "-", Weight = "-",
                    Notes = "Skipped - the item has no name.",
                    HasNotes = true,
                };

            // Same state the patch builds (keywords from the cache for never-clicked items, the
            // enchantment flag that holds back new recipes).
            var state = ModularPresetApplier.StateOf(main, item);

            var prefill = ModularPrefill.Prefill(catalog, state);
            var auto = pluginStage(prefill.Selection);     // what "(auto)" gives on the item card
            var selection = itemStage(auto);
            var result = ModularPresetEngine.Compute(catalog, selection, state, materialKeywords);

            var before = new HashSet<string>(state.Keywords, StringComparer.OrdinalIgnoreCase);
            var after = new HashSet<string>(result.Keywords, StringComparer.OrdinalIgnoreCase);
            var added = result.Keywords.Where(k => !before.Contains(k)).Select(k => "+" + Name(keywordNames, k));
            var removed = state.Keywords.Where(k => !after.Contains(k)).Select(k => "-" + Name(keywordNames, k));
            var keywordChange = string.Join("  ", added.Concat(removed));

            var notes = prefill.Notes.Concat(result.Warnings).ToList();

            // Where a value comes from: detected by the pre-fill, or chosen on a card.
            string Source(string chosen, string detected, string how)
                => string.Equals(chosen, detected, StringComparison.OrdinalIgnoreCase) ? how : "chosen";

            string material = selection.MaterialName.Length == 0
                ? "-"
                : $"{selection.MaterialName} ({Source(selection.MaterialName, prefill.Selection.MaterialName, prefill.MaterialSource)})";

            string type = selection.TypeName.Length == 0 ? "-" : selection.TypeName;
            bool typeChanges = !string.Equals(result.ArmorType, state.ArmorType, StringComparison.OrdinalIgnoreCase);
            if (typeChanges)
                type += $" ({state.ArmorType} → {result.ArmorType})";

            bool valuesChange = Differs(item.ArmorRating, result.ArmorRating, 0.005)
                             || Differs(item.Value, result.Value, 0.5)
                             || Differs(item.Weight, result.Weight, 0.0005);

            return new ModularDryRunRow
            {
                Node = item,
                Item = string.IsNullOrWhiteSpace(item.Name) ? item.EditorID ?? item.Key : item.Name,
                Plugin = plugin,
                Type = type,
                Material = material,
                Slots = selection.SlotNames.Count == 0 ? "-" : string.Join(" + ", selection.SlotNames),
                Unassigned = prefill.UnassignedBits == 0 ? "" : ModularPrefill.DescribeBits(prefill.UnassignedBits),
                Addons = string.Join(", ", selection.AddonNames.Select(a => prefill.AddonSources.TryGetValue(a, out var why) ? $"{a} ({why})" : $"{a} (chosen)")),
                ArmorRating = Change(item.ArmorRating, result.ArmorRating, "0.##"),
                Value = Change(item.Value, result.Value, "0"),
                Weight = Change(item.Weight, result.Weight, "0.###"),
                Stats = $"Armor {Change(item.ArmorRating, result.ArmorRating, "0.##")}   Value {Change(item.Value, result.Value, "0")}   Weight {Change(item.Weight, result.Weight, "0.###")}",
                Keywords = keywordChange.Length == 0 ? "(unchanged)" : keywordChange,
                Craft = result.Craft == null
                    ? "-"
                    : string.Join(", ", result.Craft.Ingredients.Select(i => $"{i.Count}× {Name(materialNames, i.Key)}")),
                Notes = string.Join(" ", notes),
                HasNotes = notes.Count > 0,
                HasChanges = valuesChange || typeChanges || keywordChange.Length > 0,
                TypeName = selection.TypeName,
                MaterialName = selection.MaterialName,
                SlotNames = selection.SlotNames.ToList(),
                AddonNames = selection.AddonNames.ToList(),
                DetectedType = prefill.Selection.TypeName,
                DetectedMaterial = prefill.Selection.MaterialName,
                AutoType = auto.TypeName,
                AutoMaterial = auto.MaterialName,
                AutoSlots = string.Join(" + ", auto.SlotNames),
            };
        }

        // The weapon card: the same shape as the armor one, with damage, crit and handling where armor
        // has its rating, and no slots.
        private static ModularDryRunRow BuildWeaponRow(WeaponCatalog catalog, ItemNodeVM item, string plugin, MainContentVM main,
            Dictionary<string, string> keywordNames, Dictionary<string, string> materialNames, HashSet<string> materialKeywords,
            Func<ModularSelection, ModularSelection> pluginStage, Func<ModularSelection, ModularSelection> itemStage)
        {
            if (ModularPresetApplier.IsSkipped(item))
                return new ModularDryRunRow
                {
                    Node = item,
                    Item = item.EditorID ?? item.Key,
                    Plugin = plugin,
                    Type = "-", Material = "-", Slots = "-", Keywords = "-", Craft = "-", Stats = "-",
                    Notes = "Skipped - the item has no name.",
                    HasNotes = true,
                };

            var state = WeaponPresetApplier.StateOf(main, item);
            var prefill = WeaponPrefill.Prefill(catalog, state);
            var auto = pluginStage(prefill.Selection);
            var selection = itemStage(auto);
            var result = WeaponPresetEngine.Compute(catalog, selection, state, materialKeywords);

            var before = new HashSet<string>(state.Keywords, StringComparer.OrdinalIgnoreCase);
            var after = new HashSet<string>(result.Keywords, StringComparer.OrdinalIgnoreCase);
            var keywordChange = string.Join("  ", result.Keywords.Where(k => !before.Contains(k)).Select(k => "+" + Name(keywordNames, k))
                .Concat(state.Keywords.Where(k => !after.Contains(k)).Select(k => "-" + Name(keywordNames, k))));

            var notes = prefill.Notes.Concat(result.Warnings).ToList();

            string Source(string chosen, string detected, string how)
                => string.Equals(chosen, detected, StringComparison.OrdinalIgnoreCase) ? how : "chosen";
            string type = selection.TypeName.Length == 0
                ? "-"
                : $"{selection.TypeName} ({Source(selection.TypeName, prefill.Selection.TypeName, prefill.TypeSource)})";
            string material = selection.MaterialName.Length == 0
                ? "-"
                : $"{selection.MaterialName} ({Source(selection.MaterialName, prefill.Selection.MaterialName, prefill.MaterialSource)})";

            bool valuesChange = Differs(item.Damage, result.Damage, 0.5)
                             || Differs(item.CritDamage, result.CritDamage, 0.5)
                             || Differs(item.Value, result.Value, 0.5)
                             || Differs(item.Weight, result.Weight, 0.0005)
                             || Differs(item.Speed, result.Speed, 0.0005)
                             || Differs(item.Reach, result.Reach, 0.0005)
                             || Differs(item.Stagger, result.Stagger, 0.0005)
                             || Differs(item.CritPercentMult, result.CritPercentMult, 0.0005);

            string Int(double before, double? after) => Change(before, after, "0");
            string Dec(double before, double? after) => Change(before, after, "0.##");

            return new ModularDryRunRow
            {
                Node = item,
                Item = string.IsNullOrWhiteSpace(item.Name) ? item.EditorID ?? item.Key : item.Name,
                Plugin = plugin,
                Type = type,
                Material = material,
                Slots = "-",
                Addons = string.Join(", ", selection.AddonNames.Select(a => prefill.AddonSources.TryGetValue(a, out var why) ? $"{a} ({why})" : $"{a} (chosen)")),
                Value = Int(item.Value, result.Value),
                Weight = Change(item.Weight, result.Weight, "0.###"),
                Stats = $"Damage {Int(item.Damage, result.Damage)}   Crit {Int(item.CritDamage, result.CritDamage)}   Value {Int(item.Value, result.Value)}   "
                      + $"Weight {Change(item.Weight, result.Weight, "0.###")}   Speed {Dec(item.Speed, result.Speed)}   Reach {Dec(item.Reach, result.Reach)}   "
                      + $"Stagger {Dec(item.Stagger, result.Stagger)}   Crit chance {Dec(item.CritPercentMult, result.CritPercentMult)}",
                Keywords = keywordChange.Length == 0 ? "(unchanged)" : keywordChange,
                Craft = result.Craft == null
                    ? "-"
                    : string.Join(", ", result.Craft.Ingredients.Select(i => $"{i.Count}× {Name(materialNames, i.Key)}")),
                Notes = string.Join(" ", notes),
                HasNotes = notes.Count > 0,
                HasChanges = valuesChange || keywordChange.Length > 0,
                TypeName = selection.TypeName,
                MaterialName = selection.MaterialName,
                AddonNames = selection.AddonNames.ToList(),
                DetectedType = prefill.Selection.TypeName,
                DetectedMaterial = prefill.Selection.MaterialName,
                AutoType = auto.TypeName,
                AutoMaterial = auto.MaterialName,
            };
        }

        private static bool Differs(double before, double? after, double tolerance)
            => after != null && Math.Abs(after.Value - before) > tolerance;

        // Null = the engine leaves the value alone (armor without a material).
        private static string Change(double before, double? after, string format)
        {
            var b = before.ToString(format, CultureInfo.InvariantCulture);
            if (after == null) return b + " (kept)";
            var a = after.Value.ToString(format, CultureInfo.InvariantCulture);
            return b == a ? a : $"{b} → {a}";
        }

        private static Dictionary<string, string> Lookup(IEnumerable<FormIDRecord>? records)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in records ?? Enumerable.Empty<FormIDRecord>())
                if (!string.IsNullOrEmpty(r.Key) && !map.ContainsKey(r.Key))
                    map[r.Key] = string.IsNullOrWhiteSpace(r.Name) ? r.Key : r.Name;
            return map;
        }

        private static string Name(Dictionary<string, string> names, string key)
            => names.TryGetValue(key, out var n) ? n : key;
    }

    // One plugin: switch, its own preset combination for all its items, the tally of what the
    // presets make of it, its item cards.
    public sealed class DryRunPluginCardVM : ViewModelBase
    {
        private readonly ModularDryRunVM _owner;
        private bool _isIncluded;

        public DryRunPluginCardVM(ModularDryRunVM owner, string name, List<ItemNodeVM> items)
        {
            _owner = owner;
            Name = name;
            _isIncluded = owner.Selection.IsIncluded(name);
            Items = items.Select(i => new DryRunItemCardVM(owner, this, i)).ToList();

            var choice = owner.Selection.PluginChoice(name);
            _type = choice?.TypeName ?? ModularDryRunVM.Auto;
            _material = choice?.MaterialName == null ? ModularDryRunVM.Auto : choice.MaterialName.Length == 0 ? ModularDryRunVM.NoMaterial : choice.MaterialName;
            foreach (var a in choice?.AdditionalAddons ?? new List<string>())
                AddedAddons.Add(new DryRunChipVM(a, RemoveAddon));

            AddAddonCommand = new RelayCommand(() =>
            {
                if (string.IsNullOrWhiteSpace(AddonToAdd) || AddedAddons.Any(c => c.Name == AddonToAdd)) return;
                AddedAddons.Add(new DryRunChipVM(AddonToAdd, RemoveAddon));
                SaveChoice();
            });
            ResetChoiceCommand = new RelayCommand(() =>
            {
                _type = ModularDryRunVM.Auto;
                _material = ModularDryRunVM.Auto;
                AddedAddons.Clear();
                OnPropertyChanged(nameof(SelectedType));
                OnPropertyChanged(nameof(SelectedMaterial));
                SaveChoice();
            });
            Tally();
        }

        public string Name { get; }
        public IReadOnlyList<DryRunItemCardVM> Items { get; }

        public IReadOnlyList<string> TypeOptions => ModularDryRunVM.WithAuto(_owner.TypeOptions, AutoTally(r => r.DetectedType));
        public IReadOnlyList<string> MaterialOptions => ModularDryRunVM.WithAuto(_owner.MaterialOptions, AutoTally(r => r.DetectedMaterial));
        public IReadOnlyList<string> AddonOptions => _owner.AddonOptions;

        // The plugin's own combination - "(auto)" = what each item's pre-fill detected.
        private string _type;
        public string SelectedType
        {
            get => _type == ModularDryRunVM.Auto ? TypeOptions[0] : _type;
            set { if (value == null) return; var v = ModularDryRunVM.IsAuto(value) ? ModularDryRunVM.Auto : value; if (v == _type) return; _type = v; OnPropertyChanged(); SaveChoice(); }
        }

        private string _material;
        public string SelectedMaterial
        {
            get => _material == ModularDryRunVM.Auto ? MaterialOptions[0] : _material;
            set { if (value == null) return; var v = ModularDryRunVM.IsAuto(value) ? ModularDryRunVM.Auto : value; if (v == _material) return; _material = v; OnPropertyChanged(); SaveChoice(); }
        }

        // Addons added to every item of the plugin, on top of what each detected.
        public ObservableCollection<DryRunChipVM> AddedAddons { get; } = new();

        private string _addonToAdd = "";
        public string AddonToAdd { get => _addonToAdd; set => SetProperty(ref _addonToAdd, value ?? ""); }

        public RelayCommand AddAddonCommand { get; }
        public RelayCommand ResetChoiceCommand { get; }

        public bool HasOwnChoice => _owner.Selection.PluginChoice(Name) != null;

        private void RemoveAddon(DryRunChipVM chip)
        {
            AddedAddons.Remove(chip);
            SaveChoice();
        }

        private void SaveChoice()
        {
            _owner.Selection.SetPluginChoice(Name, new BuildChoice
            {
                TypeName = _type == ModularDryRunVM.Auto ? null : _type,
                MaterialName = _material == ModularDryRunVM.Auto ? null : _material == ModularDryRunVM.NoMaterial ? "" : _material,
                AdditionalAddons = AddedAddons.Select(c => c.Name).ToList(),
            });
            OnPropertyChanged(nameof(HasOwnChoice));
            foreach (var i in Items) i.Recompute();
            Tally();
            RefreshItems();
            _owner.Changed();
        }

        // The plugin's preset assignment, tallied over its items.
        public string Counts { get; private set; } = "";
        public string TypeTally { get; private set; } = "";
        public string MaterialTally { get; private set; } = "";
        public string AddonTally { get; private set; } = "";

        // "(auto: Steel)" when every item detects the same, else "(auto: mixed - Steel 8 · Iron 4)".
        private string AutoTally(Func<ModularDryRunRow, string> pick)
        {
            var groups = Items.Select(i => pick(i.Row)).GroupBy(v => v).OrderByDescending(g => g.Count()).ToList();
            if (groups.Count == 0) return ModularDryRunVM.AutoLabel("");
            if (groups.Count == 1) return ModularDryRunVM.AutoLabel(groups[0].Key);
            return "(auto: mixed - " + string.Join(" · ", groups.Take(3).Select(g => $"{(g.Key.Length == 0 ? "none" : g.Key)} {g.Count()}")) + (groups.Count > 3 ? " …" : "") + ")";
        }

        internal void Tally()
        {
            var rows = Items.Select(i => i.Row).ToList();
            string Count(IEnumerable<string> names, string none)
                => string.Join(" · ", names.Select(n => n.Length == 0 ? none : n)
                    .GroupBy(n => n).OrderByDescending(g => g.Count()).ThenBy(g => g.Key)
                    .Select(g => $"{g.Key} {g.Count()}"));
            TypeTally = Count(rows.Select(r => r.TypeName), "no type");
            MaterialTally = Count(rows.Select(r => r.MaterialName), "no material");
            var addons = rows.SelectMany(r => r.AddonNames).ToList();
            AddonTally = addons.Count == 0 ? "none" : Count(addons, "");
            Counts = $"{rows.Count} item(s) · {rows.Count(r => r.HasChanges)} change · {rows.Count(r => r.HasNotes)} with notes";
            OnPropertyChanged(nameof(TypeTally));
            OnPropertyChanged(nameof(MaterialTally));
            OnPropertyChanged(nameof(AddonTally));
            OnPropertyChanged(nameof(Counts));
            OnPropertyChanged(nameof(TypeOptions));
            OnPropertyChanged(nameof(MaterialOptions));
            OnPropertyChanged(nameof(SelectedType));
            OnPropertyChanged(nameof(SelectedMaterial));
        }

        public bool IsIncluded
        {
            get => _isIncluded;
            set
            {
                if (!SetProperty(ref _isIncluded, value)) return;
                _owner.Selection.Set(Name, value);
                _owner.Changed();
            }
        }

        // All on / all off: saved in one go by the window.
        internal void SetIncludedSilently(bool value)
        {
            _isIncluded = value;
            OnPropertyChanged(nameof(IsIncluded));
        }

        // The card itself open (preset, tally, items list) - and, inside, the items list open.
        private bool _isExpanded;
        public bool IsExpanded { get => _isExpanded; set => SetProperty(ref _isExpanded, value); }

        private bool _itemsExpanded;
        public bool ItemsExpanded { get => _itemsExpanded; set => SetProperty(ref _itemsExpanded, value); }

        // The item cards the window's filter leaves; the plugin card hides when none are left.
        private List<DryRunItemCardVM> _visibleItems = new();
        public IReadOnlyList<DryRunItemCardVM> VisibleItems => _visibleItems;
        public bool HasVisibleItems => _visibleItems.Count > 0;
        public string ItemsHeader => _visibleItems.Count == Items.Count
            ? $"Items ({Items.Count})"
            : $"Items ({_visibleItems.Count} of {Items.Count} shown)";

        internal void RefreshItems()
        {
            _visibleItems = Items.Where(_owner.ItemVisible).ToList();
            OnPropertyChanged(nameof(VisibleItems));
            OnPropertyChanged(nameof(HasVisibleItems));
            OnPropertyChanged(nameof(ItemsHeader));
        }
    }

    // One item: switch, its own preset combination, what changes.
    public sealed class DryRunItemCardVM : ViewModelBase
    {
        private readonly ModularDryRunVM _owner;
        private readonly DryRunPluginCardVM _plugin;
        private readonly ItemNodeVM _item;
        private bool _isIncluded;

        public DryRunItemCardVM(ModularDryRunVM owner, DryRunPluginCardVM plugin, ItemNodeVM item)
        {
            _owner = owner;
            _plugin = plugin;
            _item = item;
            _isIncluded = owner.Selection.IsItemIncluded(item.Key);
            Row = owner.RowFor(item, plugin.Name);

            var choice = owner.Selection.ItemChoice(item.Key);
            _type = choice?.TypeName ?? ModularDryRunVM.Auto;
            _material = choice?.MaterialName == null ? ModularDryRunVM.Auto : choice.MaterialName.Length == 0 ? ModularDryRunVM.NoMaterial : choice.MaterialName;
            _slot = choice?.SlotNames is { Count: > 0 } s ? string.Join(" + ", s) : ModularDryRunVM.Auto;
            _addonsOwn = choice?.AddonNames != null;
            FillAddonChips();

            AddAddonCommand = new RelayCommand(() =>
            {
                if (string.IsNullOrWhiteSpace(AddonToAdd) || Addons.Any(c => c.Name == AddonToAdd)) return;
                _addonsOwn = true;
                Addons.Add(new DryRunChipVM(AddonToAdd, RemoveAddon));
                SaveChoice();
            });
            ResetChoiceCommand = new RelayCommand(() =>
            {
                _type = ModularDryRunVM.Auto;
                _material = ModularDryRunVM.Auto;
                _slot = ModularDryRunVM.Auto;
                _addonsOwn = false;
                OnPropertyChanged(nameof(SelectedType));
                OnPropertyChanged(nameof(SelectedMaterial));
                OnPropertyChanged(nameof(SelectedSlot));
                SaveChoice();
            });
        }

        public ModularDryRunRow Row { get; private set; }

        internal string Key => _item.Key;

        private bool _isExpanded;
        public bool IsExpanded { get => _isExpanded; set => SetProperty(ref _isExpanded, value); }

        // The collapsed card's one-liner: what changes, in short.
        public string Short => (Row.HasChanges ? Stats : "no change") + (Row.HasNotes ? "  ·  has notes" : "");

        public IReadOnlyList<string> TypeOptions => ModularDryRunVM.WithAuto(_owner.TypeOptions, ModularDryRunVM.AutoLabel(Row.AutoType));
        public IReadOnlyList<string> MaterialOptions => ModularDryRunVM.WithAuto(_owner.MaterialOptions, ModularDryRunVM.AutoLabel(Row.AutoMaterial));
        public IReadOnlyList<string> SlotOptions => ModularDryRunVM.WithAuto(_owner.SlotOptions, ModularDryRunVM.AutoLabel(Row.AutoSlots));

        // Weapon cards have no slot box. The cards are built again when the window switches.
        public bool ShowSlots => _owner.ShowSlots;
        public IReadOnlyList<string> AddonOptions => _owner.AddonOptions;

        private string _type;
        public string SelectedType
        {
            get => _type == ModularDryRunVM.Auto ? TypeOptions[0] : _type;
            set { if (value == null) return; var v = ModularDryRunVM.IsAuto(value) ? ModularDryRunVM.Auto : value; if (v == _type) return; _type = v; OnPropertyChanged(); SaveChoice(); }
        }

        private string _material;
        public string SelectedMaterial
        {
            get => _material == ModularDryRunVM.Auto ? MaterialOptions[0] : _material;
            set { if (value == null) return; var v = ModularDryRunVM.IsAuto(value) ? ModularDryRunVM.Auto : value; if (v == _material) return; _material = v; OnPropertyChanged(); SaveChoice(); }
        }

        // One slot definition, or "(auto)" for the detected cover. Several at once stay a job for the
        // item's build box; a remembered multi-slot choice shows as "A + B".
        private string _slot;
        public string SelectedSlot
        {
            get => _slot == ModularDryRunVM.Auto ? SlotOptions[0] : _slot;
            set { if (value == null) return; var v = ModularDryRunVM.IsAuto(value) ? ModularDryRunVM.Auto : value; if (v == _slot) return; _slot = v; OnPropertyChanged(); SaveChoice(); }
        }

        // The addons this item ends up with; removing or adding one makes the list the item's own.
        public ObservableCollection<DryRunChipVM> Addons { get; } = new();
        private bool _addonsOwn;

        private string _addonToAdd = "";
        public string AddonToAdd { get => _addonToAdd; set => SetProperty(ref _addonToAdd, value ?? ""); }

        public RelayCommand AddAddonCommand { get; }
        public RelayCommand ResetChoiceCommand { get; }

        public bool HasOwnChoice => _owner.Selection.ItemChoice(_item.Key) != null;

        private void RemoveAddon(DryRunChipVM chip)
        {
            _addonsOwn = true;
            Addons.Remove(chip);
            SaveChoice();
        }

        private void FillAddonChips()
        {
            Addons.Clear();
            foreach (var a in Row.AddonNames) Addons.Add(new DryRunChipVM(a, RemoveAddon));
        }

        private void SaveChoice()
        {
            List<string>? slots = _slot == ModularDryRunVM.Auto
                ? null
                : _slot.Split(" + ", StringSplitOptions.RemoveEmptyEntries).ToList();
            _owner.Selection.SetItemChoice(_item.Key, new BuildChoice
            {
                TypeName = _type == ModularDryRunVM.Auto ? null : _type,
                MaterialName = _material == ModularDryRunVM.Auto ? null : _material == ModularDryRunVM.NoMaterial ? "" : _material,
                SlotNames = slots,
                AddonNames = _addonsOwn ? Addons.Select(c => c.Name).ToList() : null,
            });
            OnPropertyChanged(nameof(HasOwnChoice));
            Recompute();
            _plugin.Tally();
            _owner.Changed();
        }

        internal void Recompute()
        {
            Row = _owner.RowFor(_item, _plugin.Name);
            FillAddonChips();
            OnPropertyChanged(nameof(Row));
            OnPropertyChanged(nameof(TypeOptions));
            OnPropertyChanged(nameof(MaterialOptions));
            OnPropertyChanged(nameof(SlotOptions));
            OnPropertyChanged(nameof(SelectedType));
            OnPropertyChanged(nameof(SelectedMaterial));
            OnPropertyChanged(nameof(SelectedSlot));
            OnPropertyChanged(nameof(Title));
            OnPropertyChanged(nameof(Assignment));
            OnPropertyChanged(nameof(Stats));
            OnPropertyChanged(nameof(Keywords));
            OnPropertyChanged(nameof(Craft));
            OnPropertyChanged(nameof(Notes));
            OnPropertyChanged(nameof(HasNotes));
            OnPropertyChanged(nameof(Short));
        }

        public string Title => Row.Item;
        public string Assignment => string.Join(" · ", new[]
        {
            Row.Type, Row.Material, Row.Slots,
            Row.Unassigned.Length > 0 ? "unassigned " + Row.Unassigned : "",
            Row.Addons.Length > 0 ? "+ " + Row.Addons : "",
        }.Where(s => s.Length > 0 && s != "-"));
        public string Stats => Row.Stats;
        public string Keywords => "Keywords " + Row.Keywords;
        public string Craft => "Craft " + Row.Craft;
        public string Notes => Row.Notes;
        public bool HasNotes => Row.HasNotes;

        public bool IsIncluded
        {
            get => _isIncluded;
            set
            {
                if (!SetProperty(ref _isIncluded, value)) return;
                _owner.Selection.SetItem(_item.Key, value);
                _owner.Changed();
            }
        }
    }

    // A removable name (an addon on a card).
    public sealed class DryRunChipVM
    {
        public DryRunChipVM(string name, Action<DryRunChipVM> remove)
        {
            Name = name;
            RemoveCommand = new RelayCommand(() => remove(this));
        }

        public string Name { get; }
        public RelayCommand RemoveCommand { get; }
    }
}
