using SkyrimCraftingTool.Model;
using SkyrimCraftingTool.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Data;
using System.Windows.Input;

namespace SkyrimCraftingTool.ViewModel
{
    // Bulk editor for the slot/type multi-selection in the Presets tree (see
    // PresetsConfigVM.SelectedSlots), the Presets-side counterpart of MultiSelectDetailVM.
    //
    // It holds one throwaway "template" PresetSlotConfig that the user fills in right here - the same
    // Values / Keywords / Crafting / Temper fields the single-slot editor exposes, reusing the exact
    // same editor VMs (PresetRecipeVM) and the same "Enabled" checkbox = "include this field"
    // convention (see Model.FieldValue<T>). Nothing is written until an "Apply to selection" button
    // is pressed; each button then copies its field group onto every selected slot via
    // PresetSlotNodeVM.ApplyBulkTemplate and force-saves each distinct affected preset file
    // (PresetsConfigVM.SavePresetImmediate) - the shared save debouncer can't be trusted for a burst
    // of writes across several files.
    //
    // A mixed Armor + Weapon selection is allowed: ArmorRating only lands on Armor slots,
    // Damage/Speed/Reach/Stagger only on Weapon types, everything else on all of them (the split is
    // enforced in ApplyBulkTemplate, keyed off each node's IsArmor).
    public class PresetMultiSelectVM : ViewModelBase
    {
        private static readonly string[] KeywordPrefixes =
        {
            "Armor", "Clothing", "Jewelry", "VendorItemArmor", "VendorItemWeapon", "Vendor",
            "Material", "DamageType", "Weap", "Weapon"
        };

        private readonly PresetsConfigVM _owner;
        private readonly PresetSlotConfig _template = new();

        public ObservableCollection<PresetSlotNodeVM> SelectedSlots => _owner.SelectedSlots;

        private string? _statusMessage;
        public string? StatusMessage
        {
            get => _statusMessage;
            private set => SetProperty(ref _statusMessage, value);
        }

        public ICommand ApplyValuesCommand { get; }
        public ICommand ApplyKeywordsCommand { get; }
        public ICommand ApplyCraftingCommand { get; }
        public ICommand ApplyTemperCommand { get; }

        public PresetMultiSelectVM(PresetsConfigVM owner)
        {
            _owner = owner;

            var main = owner.Main;
            var allKeywords = main?.AllAvailableKeywords ?? new List<FormIDRecord>();
            var allWorkbenches = main?.AllAvailableWorkbenches ?? new List<FormIDRecord>();
            var allMaterials = main?.AllAvailableMaterials ?? new List<FormIDRecord>();
            var allPerks = main?.AllAvailablePerks ?? new List<FormIDRecord>();
            var allQuests = main?.AllAvailableQuests ?? new List<FormIDRecord>();

            CraftRecipe = new PresetRecipeVM(_template.CraftRecipe, true, allWorkbenches, allMaterials, allPerks, allQuests, () => { }, main?.References);
            TemperRecipe = new PresetRecipeVM(_template.TemperRecipe, false, allWorkbenches, allMaterials, allPerks, allQuests, () => { }, main?.References);

            _allKeywordVMs = new ObservableCollection<KeywordSelectionVM>(
                allKeywords.Select(k => new KeywordSelectionVM(k.Key, k.Name, false, OnKeywordToggled)));

            try { BindingOperations.EnableCollectionSynchronization(_allKeywordVMs, new object()); }
            catch { /* already enabled */ }

            _keywordViewSource = new CollectionViewSource { Source = _allKeywordVMs };
            _keywordViewSource.Filter += KeywordFilter;

            _selectedKeywordViewSource = new CollectionViewSource { Source = _allKeywordVMs };
            _selectedKeywordViewSource.Filter += (s, e) => e.Accepted = e.Item is KeywordSelectionVM kw && kw.IsSelected;

            ApplyValuesCommand = new RelayCommand(ApplyValues);
            ApplyKeywordsCommand = new RelayCommand(() => Apply(PresetBulkFields.Keywords, "Keywords"));
            ApplyCraftingCommand = new RelayCommand(() => Apply(PresetBulkFields.CraftRecipe, "Crafting recipe"));
            ApplyTemperCommand = new RelayCommand(() => Apply(PresetBulkFields.TemperRecipe, "Temper recipe"));
        }

        // --------------------
        // Values
        // --------------------
        public bool WeightEnabled
        {
            get => _template.Weight.Enabled;
            set { if (_template.Weight.Enabled == value) return; _template.Weight.Enabled = value; OnPropertyChanged(); }
        }
        public double Weight
        {
            get => _template.Weight.Value;
            set { if (_template.Weight.Value == value) return; _template.Weight.Value = value; OnPropertyChanged(); }
        }

        public bool ValueEnabled
        {
            get => _template.Value.Enabled;
            set { if (_template.Value.Enabled == value) return; _template.Value.Enabled = value; OnPropertyChanged(); }
        }
        public int Value
        {
            get => _template.Value.Value;
            set { if (_template.Value.Value == value) return; _template.Value.Value = value; OnPropertyChanged(); }
        }

        public bool ArmorRatingEnabled
        {
            get => _template.ArmorRating.Enabled;
            set { if (_template.ArmorRating.Enabled == value) return; _template.ArmorRating.Enabled = value; OnPropertyChanged(); }
        }
        public double ArmorRating
        {
            get => _template.ArmorRating.Value;
            set { if (_template.ArmorRating.Value == value) return; _template.ArmorRating.Value = value; OnPropertyChanged(); }
        }

        public bool DamageEnabled
        {
            get => _template.Damage.Enabled;
            set { if (_template.Damage.Enabled == value) return; _template.Damage.Enabled = value; OnPropertyChanged(); }
        }
        public int Damage
        {
            get => _template.Damage.Value;
            set { if (_template.Damage.Value == value) return; _template.Damage.Value = value; OnPropertyChanged(); }
        }

        public bool SpeedEnabled
        {
            get => _template.Speed.Enabled;
            set { if (_template.Speed.Enabled == value) return; _template.Speed.Enabled = value; OnPropertyChanged(); }
        }
        public double Speed
        {
            get => _template.Speed.Value;
            set { if (_template.Speed.Value == value) return; _template.Speed.Value = value; OnPropertyChanged(); }
        }

        public bool ReachEnabled
        {
            get => _template.Reach.Enabled;
            set { if (_template.Reach.Enabled == value) return; _template.Reach.Enabled = value; OnPropertyChanged(); }
        }
        public double Reach
        {
            get => _template.Reach.Value;
            set { if (_template.Reach.Value == value) return; _template.Reach.Value = value; OnPropertyChanged(); }
        }

        public bool StaggerEnabled
        {
            get => _template.Stagger.Enabled;
            set { if (_template.Stagger.Enabled == value) return; _template.Stagger.Enabled = value; OnPropertyChanged(); }
        }
        public double Stagger
        {
            get => _template.Stagger.Value;
            set { if (_template.Stagger.Value == value) return; _template.Stagger.Value = value; OnPropertyChanged(); }
        }

        // --------------------
        // Keywords
        // --------------------
        public bool KeywordsEnabled
        {
            get => _template.Keywords.Enabled;
            set { if (_template.Keywords.Enabled == value) return; _template.Keywords.Enabled = value; OnPropertyChanged(); }
        }

        private readonly ObservableCollection<KeywordSelectionVM> _allKeywordVMs;
        public ObservableCollection<KeywordSelectionVM> AllKeywords => _allKeywordVMs;

        private readonly CollectionViewSource _keywordViewSource;
        private readonly CollectionViewSource _selectedKeywordViewSource;

        public System.ComponentModel.ICollectionView FilteredKeywordsView => _keywordViewSource.View;
        public System.ComponentModel.ICollectionView SelectedKeywordsView => _selectedKeywordViewSource.View;

        private string _searchText = "";
        public string SearchText
        {
            get => _searchText;
            set { if (SetProperty(ref _searchText, value)) _keywordViewSource?.View?.Refresh(); }
        }

        private bool _showAllKeywords;
        public bool ShowAllKeywords
        {
            get => _showAllKeywords;
            set { if (SetProperty(ref _showAllKeywords, value)) _keywordViewSource?.View?.Refresh(); }
        }

        private void OnKeywordToggled(KeywordSelectionVM kw)
        {
            KeywordRuleEngine.ApplyExclusivityRules(_allKeywordVMs, kw);
            _template.Keywords.Value = _allKeywordVMs.Where(k => k.IsSelected).Select(k => k.Key).ToList();
            _keywordViewSource.View.Refresh();
            _selectedKeywordViewSource.View.Refresh();
        }

        // Union of the Armor and Weapon relevance prefixes (see PresetSlotNodeVM.KeywordFilter) -
        // the selection can hold both slot kinds at once. "All (Expert)" drops the relevance gate.
        private void KeywordFilter(object sender, FilterEventArgs e)
        {
            if (e.Item is not KeywordSelectionVM kw) { e.Accepted = false; return; }

            if (!ShowAllKeywords)
            {
                bool relevant = kw.IsSelected
                    || KeywordPrefixes.Any(p => kw.Name.StartsWith(p, StringComparison.OrdinalIgnoreCase));
                if (!relevant) { e.Accepted = false; return; }
            }

            e.Accepted = string.IsNullOrWhiteSpace(SearchText)
                || kw.Name.Contains(SearchText, StringComparison.OrdinalIgnoreCase);
        }

        // --------------------
        // Recipes
        // --------------------
        public PresetRecipeVM CraftRecipe { get; }
        public PresetRecipeVM TemperRecipe { get; }

        // --------------------
        // Apply
        // --------------------
        private void ApplyValues()
        {
            bool anyField = WeightEnabled || ValueEnabled || ArmorRatingEnabled
                || DamageEnabled || SpeedEnabled || ReachEnabled || StaggerEnabled;
            if (!anyField)
            {
                StatusMessage = "Tick at least one value field (its checkbox) before applying.";
                return;
            }
            Apply(PresetBulkFields.Values, "Values");
        }

        private void Apply(PresetBulkFields fields, string label)
        {
            var slots = SelectedSlots.ToList();
            if (slots.Count == 0)
            {
                StatusMessage = "No slots selected.";
                return;
            }

            foreach (var slot in slots)
                slot.ApplyBulkTemplate(_template, fields);

            var files = slots.Select(s => _owner.GetOwnerFile(s)).Where(f => f != null).Distinct().ToList();
            foreach (var file in files)
                _owner.SavePresetImmediate(file);

            StatusMessage = $"{label} applied to {slots.Count} slot(s)"
                + (files.Count > 1 ? $" across {files.Count} presets." : ".");
        }
    }
}
