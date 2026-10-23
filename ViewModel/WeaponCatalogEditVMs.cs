using SkyrimCraftingTool.Model;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;

namespace SkyrimCraftingTool.ViewModel
{
    // The weapon tabs of the building-block editor (docs/WeaponPresets-Plan.md, W5).
    //
    // Most weapon values are OPTIONAL - empty = the weapon keeps its own (decided 2026-10-23) - so
    // they are text fields: "" is a real answer, which a numeric box bound to a double cannot give.
    internal static class OptionalNumber
    {
        public static string Show(double? v) => v?.ToString(CultureInfo.InvariantCulture) ?? "";
        public static string Show(int? v) => v?.ToString(CultureInfo.InvariantCulture) ?? "";

        // False = not a number (and not empty): the field keeps its old value.
        public static bool TryParse(string? text, out double? value)
        {
            value = null;
            var t = (text ?? "").Trim().Replace(',', '.');
            if (t.Length == 0) return true;
            if (!double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) || d < 0) return false;
            value = d;
            return true;
        }

        public static bool TryParseInt(string? text, out int? value)
        {
            value = null;
            if (!TryParse(text, out var d)) return false;
            if (d.HasValue) value = (int)Math.Round(d.Value, MidpointRounding.AwayFromZero);
            return true;
        }
    }

    public sealed class WeaponTypeEditVM : ViewModelBase
    {
        private readonly ModularCatalogEditorVM _owner;
        internal WeaponTypeModule Model { get; }

        public ObservableCollection<KeyEntryVM> Keywords { get; } = new();
        public RecordPickerVM KeywordPicker { get; }
        public IReadOnlyList<string> ClassChoices => _owner.WeaponClasses.ToList();

        public WeaponTypeEditVM(ModularCatalogEditorVM owner, WeaponTypeModule model)
        {
            _owner = owner;
            Model = model;
            model.Keywords ??= new();
            model.RecipeFactors ??= new();
            foreach (var k in model.Keywords) Keywords.Add(KeywordVM(k));
            KeywordPicker = new RecordPickerVM(owner.AllKeywords, r =>
            {
                if (Model.Keywords.Any(k => ModularCatalogEditorVM.Same(k, r.Key))) return;
                Model.Keywords.Add(r.Key);
                Keywords.Add(KeywordVM(r.Key));
                _owner.MarkDirty();
            });
        }

        private KeyEntryVM KeywordVM(string key) => new(key, _owner.KeywordName(key), vm =>
        {
            Model.Keywords.RemoveAll(k => ModularCatalogEditorVM.Same(k, vm.Key));
            Keywords.Remove(vm);
            _owner.MarkDirty();
        });

        public string Name
        {
            get => Model.Name;
            set
            {
                var old = Model.Name;
                if (old == value) return;
                if (string.IsNullOrWhiteSpace(value)) { OnPropertyChanged(); return; }
                Model.Name = value ?? "";
                _owner.RenameReferences("type", old, Model.Name);
                OnPropertyChanged();
                _owner.MarkDirty();
            }
        }

        // Editable: a mod type can get a class of its own ("Spear").
        public string WeaponClass
        {
            get => Model.WeaponClass;
            set { if (Model.WeaponClass == value) return; Model.WeaponClass = (value ?? "").Trim(); OnPropertyChanged(); _owner.MarkDirty(); }
        }

        public string NamePatterns { get => Model.NamePatterns; set => SetText(Model.NamePatterns, value, v => Model.NamePatterns = v); }
        public string NameExclusions { get => Model.NameExclusions; set => SetText(Model.NameExclusions, value, v => Model.NameExclusions = v); }
        public string EditorIdPatterns { get => Model.EditorIdPatterns; set => SetText(Model.EditorIdPatterns, value, v => Model.EditorIdPatterns = v); }

        public string BaseDamageText { get => OptionalNumber.Show(Model.BaseDamage); set => SetNumber(value, v => Model.BaseDamage = v); }
        public string BaseWeightText { get => OptionalNumber.Show(Model.BaseWeight); set => SetNumber(value, v => Model.BaseWeight = v); }
        public string ValueFactorText { get => OptionalNumber.Show(Model.ValueFactor); set => SetNumber(value, v => Model.ValueFactor = v); }
        public string SpeedText { get => OptionalNumber.Show(Model.Speed); set => SetNumber(value, v => Model.Speed = v); }
        public string ReachText { get => OptionalNumber.Show(Model.Reach); set => SetNumber(value, v => Model.Reach = v); }
        public string StaggerText { get => OptionalNumber.Show(Model.Stagger); set => SetNumber(value, v => Model.Stagger = v); }
        public string CritFactorText { get => OptionalNumber.Show(Model.CritFactor); set => SetNumber(value, v => Model.CritFactor = v); }
        public string CritChanceText { get => OptionalNumber.Show(Model.CritChance); set => SetNumber(value, v => Model.CritChance = v); }

        // "2, 1" - one factor per position of the material's recipe. Empty = the base amounts as they are.
        public string RecipeFactorsText
        {
            get => SlotEditVM.FormatFactors(Model.RecipeFactors);
            set
            {
                var parsed = string.IsNullOrWhiteSpace(value) ? new List<double>() : SlotEditVM.ParseFactors(value);
                if (parsed == null) { OnPropertyChanged(); return; }
                Model.RecipeFactors = parsed;
                OnPropertyChanged();
                _owner.MarkDirty();
            }
        }

        public double WeightStep
        {
            get => Model.WeightStep;
            set { if (value < 0 || Model.WeightStep == value) return; Model.WeightStep = value; OnPropertyChanged(); _owner.MarkDirty(); }
        }

        private void SetText(string current, string? value, Action<string> set)
        {
            if (current == value) return;
            set(value ?? "");
            _owner.MarkDirty();
        }

        private void SetNumber(string? text, Action<double?> set, [System.Runtime.CompilerServices.CallerMemberName] string? property = null)
        {
            if (!OptionalNumber.TryParse(text, out var v)) { OnPropertyChanged(property); return; }
            set(v);
            OnPropertyChanged(property);
            _owner.MarkDirty();
        }
    }

    public sealed class WeaponMaterialEditVM : ViewModelBase
    {
        private readonly ModularCatalogEditorVM _owner;
        internal WeaponMaterialModule Model { get; }

        public RecipeEditVM Craft { get; }
        public RecipeEditVM Temper { get; }
        public RecipeEditVM Breakdown { get; }
        public RecordPickerVM KeywordPicker { get; }
        public RelayCommand ClearKeywordCommand { get; }

        // One row per weapon class: every class the catalog uses, plus any this material lists.
        public ObservableCollection<WeaponClassRowVM> Classes { get; } = new();

        public ObservableCollection<WeaponOverrideRowVM> Overrides { get; } = new();
        public IReadOnlyList<string> TypeChoices => _owner.TypeNames.ToList();
        private string _newOverrideType = "";
        public string NewOverrideType { get => _newOverrideType; set => SetProperty(ref _newOverrideType, value ?? ""); }
        public RelayCommand AddOverrideCommand { get; }

        public WeaponMaterialEditVM(ModularCatalogEditorVM owner, WeaponMaterialModule model)
        {
            _owner = owner;
            Model = model;
            model.Classes ??= new();
            model.TypeOverrides ??= new();
            model.Craft ??= new();
            model.Temper ??= new();
            model.Breakdown ??= new();

            Craft = new RecipeEditVM(owner, model.Craft, "Crafting", showsWorkbench: true,
                "Base amounts; each weapon type multiplies them by its recipe factors (an iron sword takes 2 ingots, a dagger 1). Fixed = same amount on every weapon (catalysts like a Daedra Heart).");
            Temper = new RecipeEditVM(owner, model.Temper, "Tempering", showsWorkbench: false,
                "The grindstone is set when the recipe is created. Usually one fixed primary material.");
            Breakdown = new RecipeEditVM(owner, model.Breakdown, "Breakdown", showsWorkbench: true,
                "What breaking the weapon down PRODUCES. Only the first entry with an amount above 0 is used.", breakdown: true);
            KeywordPicker = new RecordPickerVM(owner.AllKeywords, r => MaterialKeyword = r.Key);
            ClearKeywordCommand = new RelayCommand(() => MaterialKeyword = "");

            foreach (var c in owner.WeaponClasses.Concat(model.Classes.Keys).Distinct(StringComparer.OrdinalIgnoreCase))
                Classes.Add(new WeaponClassRowVM(model, c, owner.MarkDirty));
            foreach (var o in model.TypeOverrides) Overrides.Add(RowFor(o));
            _newOverrideType = TypeChoices.FirstOrDefault() ?? "";

            AddOverrideCommand = new RelayCommand(() =>
            {
                if (string.IsNullOrWhiteSpace(NewOverrideType) || Model.OverrideFor(NewOverrideType) != null) return;
                var o = new WeaponTypeOverride { WeaponType = NewOverrideType };
                Model.TypeOverrides.Add(o);
                Overrides.Add(RowFor(o));
                _owner.MarkDirty();
            });
        }

        private WeaponOverrideRowVM RowFor(WeaponTypeOverride o) => new(o, _owner.MarkDirty, row =>
        {
            Model.TypeOverrides.Remove(o);
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

        public string BaseValueText
        {
            get => OptionalNumber.Show(Model.BaseValue);
            set
            {
                if (!OptionalNumber.TryParseInt(value, out var v)) { OnPropertyChanged(); return; }
                Model.BaseValue = v;
                OnPropertyChanged();
                _owner.MarkDirty();
            }
        }
    }

    // One weapon class of a material: its damage and weight bonus, and (bows) its speed. All three
    // empty = no entry - the class adds nothing.
    public sealed class WeaponClassRowVM : ViewModelBase
    {
        private readonly WeaponMaterialModule _material;
        private readonly Action _dirty;
        public string WeaponClass { get; }

        public WeaponClassRowVM(WeaponMaterialModule material, string weaponClass, Action dirty)
        {
            _material = material;
            WeaponClass = weaponClass;
            _dirty = dirty;
        }

        private WeaponClassBonus? Current => _material.BonusFor(WeaponClass);

        public string DamageText { get => Current == null ? "" : OptionalNumber.Show(Current.Damage); set => Update(value, (b, v) => b.Damage = v ?? 0); }
        public string WeightText { get => Current == null ? "" : OptionalNumber.Show(Current.Weight); set => Update(value, (b, v) => b.Weight = v ?? 0); }
        public string SpeedText { get => OptionalNumber.Show(Current?.Speed); set => Update(value, (b, v) => b.Speed = v); }

        private void Update(string? text, Action<WeaponClassBonus, double?> apply)
        {
            if (!OptionalNumber.TryParse(text, out var v)) { Raise(); return; }
            var key = _material.Classes.Keys.FirstOrDefault(k => string.Equals(k, WeaponClass, StringComparison.OrdinalIgnoreCase)) ?? WeaponClass;
            if (!_material.Classes.TryGetValue(key, out var bonus))
            {
                if (v == null) { Raise(); return; }   // nothing there, nothing typed
                bonus = new WeaponClassBonus();
                _material.Classes[key] = bonus;
            }
            apply(bonus, v);
            // A row emptied completely is no entry at all.
            if (string.IsNullOrWhiteSpace(text) && bonus.Damage == 0 && bonus.Weight == 0 && bonus.Speed == null)
                _material.Classes.Remove(key);
            Raise();
            _dirty();
        }

        private void Raise()
        {
            OnPropertyChanged(nameof(DamageText));
            OnPropertyChanged(nameof(WeightText));
            OnPropertyChanged(nameof(SpeedText));
        }
    }

    // A material's exception for one weapon type - absolute values, empty = the formula.
    public sealed class WeaponOverrideRowVM : ViewModelBase
    {
        private readonly WeaponTypeOverride _o;
        private readonly Action _dirty;

        public WeaponOverrideRowVM(WeaponTypeOverride o, Action dirty, Action<WeaponOverrideRowVM> remove)
        {
            _o = o;
            _dirty = dirty;
            RemoveCommand = new RelayCommand(() => remove(this));
        }

        public string WeaponType => _o.WeaponType;
        public RelayCommand RemoveCommand { get; }

        public string DamageText { get => OptionalNumber.Show(_o.Damage); set => SetInt(value, v => _o.Damage = v); }
        public string WeightText { get => OptionalNumber.Show(_o.Weight); set => SetDouble(value, v => _o.Weight = v); }
        public string ValueText { get => OptionalNumber.Show(_o.Value); set => SetInt(value, v => _o.Value = v); }
        public string SpeedText { get => OptionalNumber.Show(_o.Speed); set => SetDouble(value, v => _o.Speed = v); }
        public string ReachText { get => OptionalNumber.Show(_o.Reach); set => SetDouble(value, v => _o.Reach = v); }
        public string StaggerText { get => OptionalNumber.Show(_o.Stagger); set => SetDouble(value, v => _o.Stagger = v); }
        public string CritText { get => OptionalNumber.Show(_o.CritDamage); set => SetInt(value, v => _o.CritDamage = v); }
        public string CritChanceText { get => OptionalNumber.Show(_o.CritChance); set => SetDouble(value, v => _o.CritChance = v); }

        public string RecipeFactorsText
        {
            get => _o.RecipeFactors is { } f ? SlotEditVM.FormatFactors(f) : "";
            set
            {
                var parsed = string.IsNullOrWhiteSpace(value) ? null : SlotEditVM.ParseFactors(value);
                if (parsed == null && !string.IsNullOrWhiteSpace(value)) { OnPropertyChanged(); return; }
                _o.RecipeFactors = parsed;
                OnPropertyChanged();
                _dirty();
            }
        }

        private void SetInt(string? text, Action<int?> set, [System.Runtime.CompilerServices.CallerMemberName] string? property = null)
        {
            if (OptionalNumber.TryParseInt(text, out var v)) { set(v); _dirty(); }
            OnPropertyChanged(property);
        }

        private void SetDouble(string? text, Action<double?> set, [System.Runtime.CompilerServices.CallerMemberName] string? property = null)
        {
            if (OptionalNumber.TryParse(text, out var v)) { set(v); _dirty(); }
            OnPropertyChanged(property);
        }
    }
}
