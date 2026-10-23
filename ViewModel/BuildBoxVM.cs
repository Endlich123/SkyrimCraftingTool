using SkyrimCraftingTool.Model;
using SkyrimCraftingTool.Services.ModularPresets;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;

namespace SkyrimCraftingTool.ViewModel
{
    // One "+ addon" / "+ extra" row: a dropdown of the catalog's names and a remove button.
    public sealed class PickRowVM : ViewModelBase
    {
        private readonly Action _changed;
        public IReadOnlyList<string> Choices { get; }

        private string? _selected;
        public string? Selected
        {
            get => _selected;
            set { if (SetProperty(ref _selected, value)) _changed(); }
        }

        public RelayCommand RemoveCommand { get; }

        // Why the pre-fill put this row here ("keyword", "name: gold*"); empty for a row added by hand.
        public string Hint { get; }
        public bool HasHint => Hint.Length > 0;

        public PickRowVM(IReadOnlyList<string> choices, string? selected, Action changed, Action<PickRowVM> remove, string hint = "")
        {
            Choices = choices;
            _selected = selected;
            _changed = changed;
            Hint = hint;
            RemoveCommand = new RelayCommand(() => remove(this));
        }
    }

    // The build box (docs/ItemPresets-Plan.md, "Umbau"): where the Items tab used to offer
    // "preset + Apply". Type, material, slot definitions, keyword addons and extras, put together
    // and applied through the same engine as the dry run and the preset patch.
    //
    // THREE LEVELS. On a single item every field is that item's own, pre-filled from it, and the
    // slot definitions can be ticked. On a multi-selection or a whole plugin the slots cannot be
    // chosen - every item has its own - and type and material start on "(detected)", meaning each
    // item keeps what the pre-fill finds for it; picking a value forces it on all of them. Addons
    // picked there come ON TOP of each item's detected ones.
    public sealed class BuildBoxVM : ViewModelBase
    {
        public const string Detected = "(detected)";
        public const string NoMaterial = "(none)";

        private readonly MainContentVM _main;
        private readonly Func<IReadOnlyList<ItemNodeVM>> _targets;
        private readonly ModularCatalog? _catalog;
        private readonly string _scopeLabel;
        private bool _filling;

        public bool IsSingle { get; }
        public bool IsMulti => !IsSingle;
        public bool HasCatalog => _catalog != null;
        public bool HasCatalogProblem => _catalog == null;
        public string CatalogProblem { get; }

        public IReadOnlyList<string> TypeChoices { get; }
        public IReadOnlyList<string> MaterialChoices { get; }
        private readonly IReadOnlyList<string> _addonChoices;
        private readonly IReadOnlyList<string> _extraChoices;

        public ObservableCollection<NameChoiceVM> SlotChoices { get; } = new();
        public ObservableCollection<PickRowVM> Addons { get; } = new();
        public ObservableCollection<PickRowVM> Extras { get; } = new();

        public RelayCommand AddAddonCommand { get; }
        public RelayCommand AddExtraCommand { get; }
        public RelayCommand ApplyCommand { get; }

        private string? _type;
        public string? SelectedType
        {
            get => _type;
            set { if (SetProperty(ref _type, value)) Changed(); }
        }

        private string? _material;
        public string? SelectedMaterial
        {
            get => _material;
            set { if (SetProperty(ref _material, value)) Changed(); }
        }

        private string _preview = "";
        public string Preview { get => _preview; private set => SetProperty(ref _preview, value); }

        private string _status = "";
        public string Status { get => _status; private set => SetProperty(ref _status, value); }

        // scopeLabel: "the 12 selected items", "all items in Foo.esp" - for the confirmation.
        public BuildBoxVM(MainContentVM main, Func<IReadOnlyList<ItemNodeVM>> targets, bool isSingle, string scopeLabel = "")
        {
            _main = main;
            _targets = targets;
            _scopeLabel = scopeLabel;
            IsSingle = isSingle;
            _catalog = ModularCatalogCache.Current;
            CatalogProblem = _catalog == null ? ModularCatalogCache.Problem : "";

            var types = _catalog?.Types.Select(t => t.Name).ToList() ?? new List<string>();
            var materials = _catalog?.Materials.Select(m => m.Name).ToList() ?? new List<string>();
            TypeChoices = isSingle ? types : types.Prepend(Detected).ToList();
            MaterialChoices = (isSingle ? materials.Prepend(NoMaterial) : materials.Prepend(NoMaterial).Prepend(Detected)).ToList();
            _addonChoices = _catalog?.Addons.Select(a => a.Name).ToList() ?? new List<string>();
            _extraChoices = _catalog?.Extras.Select(x => x.Name).ToList() ?? new List<string>();

            AddAddonCommand = new RelayCommand(() => { Addons.Add(Row(_addonChoices, _addonChoices.FirstOrDefault(), Addons)); Changed(); });
            AddExtraCommand = new RelayCommand(() => { Extras.Add(Row(_extraChoices, _extraChoices.FirstOrDefault(), Extras)); Changed(); });
            ApplyCommand = new RelayCommand(async () => await ApplyAsync());

            Fill();
        }

        public bool CanAddAddon => _addonChoices.Count > 0;
        public bool CanAddExtra => _extraChoices.Count > 0;

        private PickRowVM Row(IReadOnlyList<string> choices, string? selected, ObservableCollection<PickRowVM> owner, string hint = "")
            => new(choices, selected, Changed, r => { owner.Remove(r); Changed(); }, hint);

        // Single item: its own pre-fill. Several: everything on "(detected)", no rows.
        private void Fill()
        {
            _filling = true;
            try
            {
                SlotChoices.Clear();
                Addons.Clear();
                Extras.Clear();

                if (_catalog == null) return;

                if (!IsSingle)
                {
                    SelectedType = Detected;
                    SelectedMaterial = Detected;
                    return;
                }

                var item = _targets().FirstOrDefault();
                if (item == null) return;

                var state = ModularPresetApplier.StateOf(_main, item);
                var prefill = ModularPrefill.Prefill(_catalog, state);
                SelectedType = prefill.Selection.TypeName.Length > 0 ? prefill.Selection.TypeName : TypeChoices.FirstOrDefault();
                SelectedMaterial = prefill.Selection.MaterialName.Length > 0 ? prefill.Selection.MaterialName : NoMaterial;

                foreach (var name in _catalog.Slots.Select(s => s.Name))
                    SlotChoices.Add(new NameChoiceVM(name, prefill.Selection.SlotNames.Contains(name, StringComparer.OrdinalIgnoreCase), (_, _) => Changed()));
                foreach (var a in prefill.Selection.AddonNames)
                    Addons.Add(Row(_addonChoices, a, Addons, prefill.AddonSources.TryGetValue(a, out var why) ? why : ""));
            }
            finally
            {
                _filling = false;
            }
            Changed();
        }

        // The ticked slot definitions in one line - the full list of ~36 sits in an expander.
        public string SlotSummary
        {
            get
            {
                var ticked = SlotChoices.Where(c => c.IsChecked).Select(c => c.Name).ToList();
                return ticked.Count == 0 ? "no slot definition - the item keeps its slots" : string.Join(" + ", ticked);
            }
        }

        private void Changed()
        {
            if (_filling) return;
            Status = "";
            OnPropertyChanged(nameof(SlotSummary));
            Preview = IsSingle ? SinglePreview() : MultiPreview();
        }

        internal BuildChoice Choice()
        {
            string? type = SelectedType == Detected ? null : SelectedType;
            string? material = SelectedMaterial == Detected ? null : SelectedMaterial == NoMaterial ? "" : SelectedMaterial;
            var addons = Addons.Select(r => r.Selected).Where(s => !string.IsNullOrEmpty(s)).Select(s => s!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var extras = Extras.Select(r => r.Selected).Where(s => !string.IsNullOrEmpty(s)).Select(s => s!).ToList();

            return IsSingle
                ? new BuildChoice
                {
                    TypeName = type,
                    MaterialName = material,
                    SlotNames = SlotChoices.Where(c => c.IsChecked).Select(c => c.Name).ToList(),
                    AddonNames = addons,
                    ExtraNames = extras,
                }
                : new BuildChoice
                {
                    TypeName = type,
                    MaterialName = material,
                    AdditionalAddons = addons,
                    ExtraNames = extras,
                };
        }

        // What Apply would do to this item, in one line - the reason to build here rather than blind.
        private string SinglePreview()
        {
            var item = _targets().FirstOrDefault();
            if (_catalog == null || item == null) return "";
            if (ModularPresetApplier.IsSkipped(item)) return "This item has no name - it is skipped for safety and Apply does nothing.";

            var state = ModularPresetApplier.StateOf(_main, item);
            var detected = ModularPrefill.Prefill(_catalog, state).Selection;
            var result = ModularPresetEngine.Compute(_catalog, Choice().Resolve(detected), state,
                ModularPresetEngine.MaterialKeywordsOf(_main.AllAvailableKeywords));

            var parts = new List<string>
            {
                $"Armor {Change(item.ArmorRating, result.ArmorRating, "0.##")}",
                $"Value {Change(item.Value, result.Value, "0")}",
                $"Weight {Change(item.Weight, result.Weight, "0.###")}",
            };
            if (!string.Equals(item.ArmorType, result.ArmorType, StringComparison.OrdinalIgnoreCase))
                parts.Add($"Type {item.ArmorType} → {result.ArmorType}");
            if (item.BodySlotMask != result.BodySlotMask)
                parts.Add($"Slots {ModularPrefill.DescribeBits(item.BodySlotMask)} → {ModularPrefill.DescribeBits(result.BodySlotMask)}");

            var names = NameMap(_main.AllAvailableKeywords);
            string Kw(string k) => names.TryGetValue(k, out var n) ? n : k;
            var before = new HashSet<string>(state.Keywords, StringComparer.OrdinalIgnoreCase);
            var after = new HashSet<string>(result.Keywords, StringComparer.OrdinalIgnoreCase);
            var kw = result.Keywords.Where(k => !before.Contains(k)).Select(k => "+" + Kw(k))
                .Concat(state.Keywords.Where(k => !after.Contains(k)).Select(k => "−" + Kw(k))).ToList();
            if (kw.Count > 0) parts.Add("Keywords " + string.Join(" ", kw));

            if (result.Craft != null)
            {
                var mats = NameMap(_main.AllAvailableMaterials);
                parts.Add("Craft " + string.Join(", ", result.Craft.Ingredients.Select(i => $"{i.Count}× {(mats.TryGetValue(i.Key, out var n) ? n : i.Key)}")));
            }
            if (result.EnchantmentKey != null) parts.Add(result.EnchantmentKey.Length == 0 ? "Enchantment removed" : "Enchantment set");
            if (result.EnchantmentAmount.HasValue) parts.Add($"Charge {result.EnchantmentAmount}");
            if (result.Containers.Count > 0) parts.Add($"{result.Containers.Count} placement set(s) added");

            var text = string.Join("  ·  ", parts);
            return result.Warnings.Count == 0 ? text : text + Environment.NewLine + "⚠ " + string.Join(" ", result.Warnings);
        }

        private string MultiPreview()
        {
            int armor = _targets().Count(i => i.IsArmor && !ModularPresetApplier.IsSkipped(i));
            int nameless = _targets().Count(i => i.IsArmor && ModularPresetApplier.IsSkipped(i));
            var forced = new List<string>();
            if (SelectedType != Detected) forced.Add($"type {SelectedType}");
            if (SelectedMaterial != Detected) forced.Add($"material {SelectedMaterial}");
            string how = forced.Count == 0 ? "each with its own detected type and material" : "forcing " + string.Join(" and ", forced);
            var skipped = nameless > 0 ? $" {nameless} without a name are skipped." : "";
            return $"{armor} armor item(s), {how}. Slots always come from each item.{skipped}";
        }

        // Not ToDictionary: a key can exist in two casings (see the key-case-variants note), and
        // the comparer below would make that a duplicate and throw.
        private static Dictionary<string, string> NameMap(IEnumerable<FormIDRecord>? records)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in records ?? Enumerable.Empty<FormIDRecord>())
                if (!string.IsNullOrEmpty(r.Key)) map.TryAdd(r.Key, string.IsNullOrWhiteSpace(r.Name) ? r.Key : r.Name);
            return map;
        }

        private static string Change(double before, double? after, string format)
        {
            var b = before.ToString(format, CultureInfo.InvariantCulture);
            if (after == null) return b + " (kept)";
            var a = after.Value.ToString(format, CultureInfo.InvariantCulture);
            return b == a ? a : $"{b} → {a}";
        }

        private async Task ApplyAsync()
        {
            if (_catalog == null) return;
            var targets = _targets().Where(i => i.IsArmor).ToList();
            if (targets.Count == 0) { Status = "Nothing to apply to - the building blocks are for armor."; return; }

            if (!IsSingle)
            {
                var answer = MessageBox.Show(
                    $"Apply the build to {_scopeLabel} - {targets.Count} armor item(s)?\n\nEvery item can be reverted with its Reset button.",
                    "Build", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (answer != MessageBoxResult.Yes) return;
            }

            var report = await ModularPresetApplier.ApplyAsync(_main, _catalog, targets, Choice());
            Status = report.Changed == 0
                ? "No change - the items already looked like this."
                : $"Applied to {report.Changed} of {report.Items} item(s).";
            if (report.SkippedNoName > 0) Status += $" {report.SkippedNoName} skipped (no name).";

            // The single item's own values just changed: show the box against them.
            if (IsSingle) Preview = SinglePreview();
        }
    }
}
