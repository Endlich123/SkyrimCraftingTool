using SkyrimCraftingTool.Model;
using SkyrimCraftingTool.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Windows.Data;
using System.Windows.Input;

namespace SkyrimCraftingTool.ViewModel
{
    // Which field groups a bulk "Apply to selected slots" pass should overwrite. See
    // PresetSlotNodeVM.ApplyBulkTemplate and PresetMultiSelectVM.
    [Flags]
    public enum PresetBulkFields
    {
        None = 0,
        Values = 1,
        Keywords = 2,
        CraftRecipe = 4,
        TemperRecipe = 8
    }

    // One Armor-Slot or Weapon-Type leaf node in the Presets tree. Doubles as its own detail view,
    // the same pattern ItemNodeVM uses for the main item tree (tree leaf and detail content are the
    // same object). ArmorRating is only meaningful when IsArmor; Damage/Speed/Reach/Stagger only
    // when IsWeapon — mirrors ItemNodeVM's Armor/Weapon field split.
    //
    // Heavy editor state (the full modlist keyword list -> one KeywordSelectionVM each, both keyword
    // CollectionViewSources, the container catalog, and the two PresetRecipeVM editors) is built
    // lazily on first access via EnsureLoaded() and released again by Unload() when the node stops
    // being the selected detail. The Presets tree eagerly creates one of these per armor slot (32) per
    // preset, so doing that work in the constructor made startup allocate millions of throwaway VMs
    // once a handful of presets existed. The tree only ever shows DisplayName; the rest is needed
    // solely while this node is open in the right-hand pane. Field pass-throughs below read _config
    // directly and stay cheap, so Apply/Save (which work off the PresetFile POCO) are unaffected.
    public class PresetSlotNodeVM : ViewModelBase
    {
        private readonly PresetSlotConfig _config;
        private readonly Action _onChanged;

        private readonly List<FormIDRecord> _allKeywords;
        private readonly List<FormIDRecord> _allWorkbenches;
        private readonly List<FormIDRecord> _allMaterials;
        private readonly List<FormIDRecord> _allPerks;
        private readonly List<FormIDRecord> _allQuests;
        private readonly List<ContainerRecord> _allContainers;
        private readonly Services.IReferenceResolver? _references;

        private bool _loaded;

        public string DisplayName { get; }
        public bool IsArmor { get; }
        public bool IsWeapon => !IsArmor;

        // Tree multi-selection flag (Ctrl/Shift-click in the Presets tree, see
        // PresetsConfigVM.HandleSlotNodeClick) - drives the row highlight in PresetsConfigView.xaml.
        private bool _isSelected;
        public bool IsSelected
        {
            get => _isSelected;
            set => SetProperty(ref _isSelected, value);
        }

        public PresetSlotNodeVM(PresetSlotConfig config, bool isArmor, string displayName,
            List<FormIDRecord> allKeywords, List<FormIDRecord> allWorkbenches,
            List<FormIDRecord> allMaterials, List<FormIDRecord> allPerks, List<FormIDRecord> allQuests,
            List<ContainerRecord> allContainers,
            Action onChanged,
            Services.IReferenceResolver? references = null)
        {
            _config = config;
            IsArmor = isArmor;
            DisplayName = displayName;
            _onChanged = onChanged;
            _references = references;

            _allKeywords = allKeywords ?? new List<FormIDRecord>();
            _allWorkbenches = allWorkbenches ?? new List<FormIDRecord>();
            _allMaterials = allMaterials ?? new List<FormIDRecord>();
            _allPerks = allPerks ?? new List<FormIDRecord>();
            _allQuests = allQuests ?? new List<FormIDRecord>();
            _allContainers = allContainers ?? new List<ContainerRecord>();
        }

        // --------------------
        // Lazy load / unload
        // --------------------

        // Idempotent; safe to call from a binding getter or explicitly when this node becomes the
        // selected detail. Runs on the UI thread (binding evaluation / SelectedNode setter).
        public void EnsureLoaded()
        {
            if (_loaded) return;
            _loaded = true;

            _craftRecipe = new PresetRecipeVM(_config.CraftRecipe, true, _allWorkbenches, _allMaterials, _allPerks, _allQuests, _onChanged, _references);
            _temperRecipe = new PresetRecipeVM(_config.TemperRecipe, false, _allWorkbenches, _allMaterials, _allPerks, _allQuests, _onChanged, _references);

            _allKeywordVMs = new ObservableCollection<KeywordSelectionVM>(
                _allKeywords.Select(k => new KeywordSelectionVM(k.Key, k.Name, false, OnKeywordToggled)));

            var selectedKeys = new HashSet<string>(_config.Keywords.Value ?? new List<string>());
            foreach (var kw in _allKeywordVMs)
                kw.IsSelected = selectedKeys.Contains(kw.Key);

            try { BindingOperations.EnableCollectionSynchronization(_allKeywordVMs, new object()); }
            catch { /* already enabled */ }

            _keywordViewSource = new CollectionViewSource { Source = _allKeywordVMs };
            _keywordViewSource.Filter += KeywordFilter;

            _selectedKeywordViewSource = new CollectionViewSource { Source = _allKeywordVMs };
            _selectedKeywordViewSource.Filter += (s, e) =>
            {
                e.Accepted = e.Item is KeywordSelectionVM kw && kw.IsSelected;
            };

            _containerSelection = new ContainerSelectionVM(_allContainers);
            _containerSelection.LoadFromString(_config.Container.Value ?? "{}");
            SubscribeContainerEvents();

            foreach (var c in _allContainers)
            {
                CatalogContainers.Add(new ContainerEntryVM(c)
                {
                    IsSelected = _containerSelection.SelectedContainers.Any(sc => sc.ContainerKey == c.ContainerKey)
                });
            }

            RaiseLoadedStateChanged();
        }

        // Drops every lazily-built collection/editor so it can be GC'd. Nothing here holds unsaved
        // state — every edit writes straight through to _config — so a later EnsureLoaded() rebuilds
        // an identical view. Called by PresetsConfigVM when this node stops being the selected detail.
        public void Unload()
        {
            if (!_loaded) return;
            _loaded = false;

            if (_containerSelection != null)
            {
                _containerSelection.SelectedContainers.CollectionChanged -= OnSelectedContainersChanged;
                foreach (var c in _containerSelection.SelectedContainers)
                    UnsubscribeContainerEntry(c);
            }

            if (_keywordViewSource != null)
                _keywordViewSource.Filter -= KeywordFilter;

            if (_allKeywordVMs != null)
            {
                try { BindingOperations.DisableCollectionSynchronization(_allKeywordVMs); }
                catch { /* was not enabled */ }
            }

            _keywordViewSource = null;
            _selectedKeywordViewSource = null;
            _allKeywordVMs = null;
            _craftRecipe = null;
            _temperRecipe = null;
            _containerSelection = null;
            CatalogContainers.Clear();
            _searchText = "";
            _showAllKeywords = false;

            RaiseLoadedStateChanged();
        }

        private void RaiseLoadedStateChanged()
        {
            OnPropertyChanged(nameof(CraftRecipe));
            OnPropertyChanged(nameof(TemperRecipe));
            OnPropertyChanged(nameof(AllKeywords));
            OnPropertyChanged(nameof(FilteredKeywordsView));
            OnPropertyChanged(nameof(SelectedKeywordsView));
            OnPropertyChanged(nameof(ContainerSelection));
            OnPropertyChanged(nameof(FilteredContainers));
            OnPropertyChanged(nameof(SearchText));
            OnPropertyChanged(nameof(ShowAllKeywords));
        }

        // --------------------
        // Base fields (Armor + Weapon)
        // --------------------
        public bool WeightEnabled
        {
            get => _config.Weight.Enabled;
            set { if (_config.Weight.Enabled == value) return; _config.Weight.Enabled = value; OnPropertyChanged(); NotifyChanged(); }
        }
        public double Weight
        {
            get => _config.Weight.Value;
            set { if (_config.Weight.Value == value) return; _config.Weight.Value = value; OnPropertyChanged(); NotifyChanged(); }
        }

        public bool ValueEnabled
        {
            get => _config.Value.Enabled;
            set { if (_config.Value.Enabled == value) return; _config.Value.Enabled = value; OnPropertyChanged(); NotifyChanged(); }
        }
        public int Value
        {
            get => _config.Value.Value;
            set { if (_config.Value.Value == value) return; _config.Value.Value = value; OnPropertyChanged(); NotifyChanged(); }
        }

        // --------------------
        // Armor-specific fields
        // --------------------
        public bool ArmorRatingEnabled
        {
            get => _config.ArmorRating.Enabled;
            set { if (_config.ArmorRating.Enabled == value) return; _config.ArmorRating.Enabled = value; OnPropertyChanged(); NotifyChanged(); }
        }
        public double ArmorRating
        {
            get => _config.ArmorRating.Value;
            set { if (_config.ArmorRating.Value == value) return; _config.ArmorRating.Value = value; OnPropertyChanged(); NotifyChanged(); }
        }

        // --------------------
        // Weapon-specific fields
        // --------------------
        public bool DamageEnabled
        {
            get => _config.Damage.Enabled;
            set { if (_config.Damage.Enabled == value) return; _config.Damage.Enabled = value; OnPropertyChanged(); NotifyChanged(); }
        }
        public int Damage
        {
            get => _config.Damage.Value;
            set { if (_config.Damage.Value == value) return; _config.Damage.Value = value; OnPropertyChanged(); NotifyChanged(); }
        }

        public bool SpeedEnabled
        {
            get => _config.Speed.Enabled;
            set { if (_config.Speed.Enabled == value) return; _config.Speed.Enabled = value; OnPropertyChanged(); NotifyChanged(); }
        }
        public double Speed
        {
            get => _config.Speed.Value;
            set { if (_config.Speed.Value == value) return; _config.Speed.Value = value; OnPropertyChanged(); NotifyChanged(); }
        }

        public bool ReachEnabled
        {
            get => _config.Reach.Enabled;
            set { if (_config.Reach.Enabled == value) return; _config.Reach.Enabled = value; OnPropertyChanged(); NotifyChanged(); }
        }
        public double Reach
        {
            get => _config.Reach.Value;
            set { if (_config.Reach.Value == value) return; _config.Reach.Value = value; OnPropertyChanged(); NotifyChanged(); }
        }

        public bool StaggerEnabled
        {
            get => _config.Stagger.Enabled;
            set { if (_config.Stagger.Enabled == value) return; _config.Stagger.Enabled = value; OnPropertyChanged(); NotifyChanged(); }
        }
        public double Stagger
        {
            get => _config.Stagger.Value;
            set { if (_config.Stagger.Value == value) return; _config.Stagger.Value = value; OnPropertyChanged(); NotifyChanged(); }
        }

        // --------------------
        // Recipes (lazy)
        // --------------------
        private PresetRecipeVM _craftRecipe;
        private PresetRecipeVM _temperRecipe;

        public PresetRecipeVM CraftRecipe { get { EnsureLoaded(); return _craftRecipe; } }
        public PresetRecipeVM TemperRecipe { get { EnsureLoaded(); return _temperRecipe; } }

        // --------------------
        // Keywords
        // --------------------
        public bool KeywordsEnabled
        {
            get => _config.Keywords.Enabled;
            set { if (_config.Keywords.Enabled == value) return; _config.Keywords.Enabled = value; OnPropertyChanged(); NotifyChanged(); }
        }

        private ObservableCollection<KeywordSelectionVM> _allKeywordVMs;
        public ObservableCollection<KeywordSelectionVM> AllKeywords { get { EnsureLoaded(); return _allKeywordVMs; } }

        private CollectionViewSource _keywordViewSource;
        private CollectionViewSource _selectedKeywordViewSource;

        public ICollectionView FilteredKeywordsView { get { EnsureLoaded(); return _keywordViewSource.View; } }
        public ICollectionView SelectedKeywordsView { get { EnsureLoaded(); return _selectedKeywordViewSource.View; } }

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

            _config.Keywords.Value = _allKeywordVMs.Where(k => k.IsSelected).Select(k => k.Key).ToList();
            _keywordViewSource.View.Refresh();
            _selectedKeywordViewSource.View.Refresh();
            NotifyChanged();
        }

        // Same relevance/search filter ItemNodeVM.KeywordFilter uses, keyed off IsArmor instead of
        // a real item's record type. Business rules (Light/Heavy/Clothing exclusivity etc.) are
        // enforced live here too via KeywordRuleEngine, the same engine ItemNodeVM.ApplyKeywordRules
        // uses — so a preset's Armor slot behaves exactly like editing a real armor item's keywords.
        private void KeywordFilter(object sender, FilterEventArgs e)
        {
            if (e.Item is not KeywordSelectionVM kw) { e.Accepted = false; return; }

            if (ShowAllKeywords)
            {
                e.Accepted = string.IsNullOrWhiteSpace(SearchText)
                    || kw.Name.Contains(SearchText, StringComparison.OrdinalIgnoreCase);
                return;
            }

            var relevantPrefixes = IsArmor
                ? new[] { "Armor", "Clothing", "Jewelry", "VendorItemArmor", "Vendor", "Material" }
                : new[] { "Weap", "Weapon", "VendorItemWeapon", "Vendor", "Material", "DamageType" };

            bool isRelevant = kw.IsSelected || relevantPrefixes.Any(p => kw.Name.StartsWith(p, StringComparison.OrdinalIgnoreCase));
            if (!isRelevant) { e.Accepted = false; return; }

            if (!string.IsNullOrWhiteSpace(SearchText) && !kw.Name.Contains(SearchText, StringComparison.OrdinalIgnoreCase))
            { e.Accepted = false; return; }

            e.Accepted = true;
        }

        // --------------------
        // Container
        // --------------------
        public bool ContainerEnabled
        {
            get => _config.Container.Enabled;
            set { if (_config.Container.Enabled == value) return; _config.Container.Enabled = value; OnPropertyChanged(); NotifyChanged(); }
        }

        // Reuses the same ContainerSelectionVM/ContainerString format as real Armor/Weapon items
        // (ItemNodeVM.ContainerSelection) — SelectedContainers here holds this slot/type's own picks.
        private ContainerSelectionVM _containerSelection;
        public ContainerSelectionVM ContainerSelection { get { EnsureLoaded(); return _containerSelection; } }

        // Catalog list for the left-hand browse/toggle panel — separate ContainerEntryVM instances
        // from ContainerSelection.SelectedContainers, purely for display + IsSelected highlighting.
        public ObservableCollection<ContainerEntryVM> CatalogContainers { get; } = new();

        private bool _showExpertContainers;
        public bool ShowExpertContainers
        {
            get => _showExpertContainers;
            set { if (SetProperty(ref _showExpertContainers, value)) OnPropertyChanged(nameof(FilteredContainers)); }
        }

        private string _containerSearchText = "";
        public string ContainerSearchText
        {
            get => _containerSearchText;
            set { if (SetProperty(ref _containerSearchText, value)) OnPropertyChanged(nameof(FilteredContainers)); }
        }

        // Standard view limits to merchant containers (mirrors MainContentVM.LimitedContainerVMs);
        // Expert view shows every container in the modlist.
        public IEnumerable<ContainerEntryVM> FilteredContainers
        {
            get
            {
                EnsureLoaded();
                return (ShowExpertContainers ? CatalogContainers : CatalogContainers.Where(c => c.Name.Contains("Merchant", StringComparison.OrdinalIgnoreCase)))
                    .Where(c => string.IsNullOrWhiteSpace(ContainerSearchText) || c.Name.Contains(ContainerSearchText, StringComparison.OrdinalIgnoreCase));
            }
        }

        public ICommand ToggleExpertContainersCommand => new RelayCommand(() => ShowExpertContainers = !ShowExpertContainers);

        public ICommand ToggleContainerCommand => new RelayCommand<string>(key =>
        {
            EnsureLoaded();
            _containerSelection.ToggleContainer(key); // fires SelectedContainers.CollectionChanged -> sync
            var catalogEntry = CatalogContainers.FirstOrDefault(c => c.ContainerKey == key);
            if (catalogEntry != null)
                catalogEntry.IsSelected = _containerSelection.SelectedContainers.Any(sc => sc.ContainerKey == key);
        });

        public ICommand ClearContainerSelectionCommand => new RelayCommand(() =>
        {
            EnsureLoaded();
            _containerSelection.Clear(); // fires SelectedContainers.CollectionChanged -> sync
            foreach (var c in CatalogContainers)
                c.IsSelected = false;
        });

        private void SubscribeContainerEvents()
        {
            _containerSelection.SelectedContainers.CollectionChanged += OnSelectedContainersChanged;
            foreach (var c in _containerSelection.SelectedContainers)
                SubscribeContainerEntry(c);
        }

        private void SubscribeContainerEntry(ContainerEntryVM entry)
        {
            foreach (var lvli in entry.LVLiEntries)
                lvli.PropertyChanged += OnLVLiPropertyChanged;
        }

        private void UnsubscribeContainerEntry(ContainerEntryVM entry)
        {
            foreach (var lvli in entry.LVLiEntries)
                lvli.PropertyChanged -= OnLVLiPropertyChanged;
        }

        private void OnSelectedContainersChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.OldItems != null)
                foreach (ContainerEntryVM c in e.OldItems)
                    UnsubscribeContainerEntry(c);

            if (e.NewItems != null)
                foreach (ContainerEntryVM c in e.NewItems)
                    SubscribeContainerEntry(c);

            SyncContainerAndNotify();
        }

        private void OnLVLiPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(LVLiEntryVM.Level))
                SyncContainerAndNotify();
        }

        private void SyncContainerAndNotify()
        {
            _config.Container.Value = _containerSelection.BuildString();
            NotifyChanged();
        }

        private void NotifyChanged() => _onChanged();

        // --------------------
        // Bulk apply (multi-select)
        // --------------------

        // Overwrites the requested field groups on this slot's config from a template config, then
        // fires _onChanged (attach-to-file + save) exactly like a normal edit. Armor-only fields
        // (ArmorRating) are skipped on Weapon-Type nodes and vice versa, so a mixed selection stays
        // sane. Only fields whose template checkbox is Enabled get copied - a disabled template field
        // leaves this slot's field untouched. This is only ever called for slots that are NOT the
        // currently open single-slot editor (multi-select sets PresetsConfigVM.SelectedNode = null,
        // which unloads any open slot); the _loaded guard below is belt-and-braces so a later
        // EnsureLoaded rebuilds cleanly from the mutated config.
        internal void ApplyBulkTemplate(PresetSlotConfig template, PresetBulkFields fields)
        {
            if (fields.HasFlag(PresetBulkFields.Values))
            {
                if (template.Weight.Enabled) { _config.Weight.Enabled = true; _config.Weight.Value = template.Weight.Value; }
                if (template.Value.Enabled) { _config.Value.Enabled = true; _config.Value.Value = template.Value.Value; }

                if (IsArmor)
                {
                    if (template.ArmorRating.Enabled) { _config.ArmorRating.Enabled = true; _config.ArmorRating.Value = template.ArmorRating.Value; }
                }
                else
                {
                    if (template.Damage.Enabled) { _config.Damage.Enabled = true; _config.Damage.Value = template.Damage.Value; }
                    if (template.Speed.Enabled) { _config.Speed.Enabled = true; _config.Speed.Value = template.Speed.Value; }
                    if (template.Reach.Enabled) { _config.Reach.Enabled = true; _config.Reach.Value = template.Reach.Value; }
                    if (template.Stagger.Enabled) { _config.Stagger.Enabled = true; _config.Stagger.Value = template.Stagger.Value; }
                }
            }

            if (fields.HasFlag(PresetBulkFields.Keywords))
            {
                _config.Keywords.Enabled = template.Keywords.Enabled;
                _config.Keywords.Value = new List<string>(template.Keywords.Value ?? new List<string>());
            }

            if (fields.HasFlag(PresetBulkFields.CraftRecipe))
                CopyRecipeInto(template.CraftRecipe, _config.CraftRecipe);

            if (fields.HasFlag(PresetBulkFields.TemperRecipe))
                CopyRecipeInto(template.TemperRecipe, _config.TemperRecipe);

            if (_loaded) Unload();
            _onChanged();
        }

        private static void CopyRecipeInto(RecipeConfig src, RecipeConfig dst)
        {
            dst.WorkbenchKey.Enabled = src.WorkbenchKey.Enabled;
            dst.WorkbenchKey.Value = src.WorkbenchKey.Value;

            dst.Ingredients.Enabled = src.Ingredients.Enabled;
            dst.Ingredients.Value = (src.Ingredients.Value ?? new List<IngredientEntry>())
                .Select(i => new IngredientEntry { Key = i.Key, Count = i.Count })
                .ToList();

            dst.Conditions.Enabled = src.Conditions.Enabled;
            dst.Conditions.Value = (src.Conditions.Value ?? new List<ConditionEntry>())
                .Select(c => new ConditionEntry { ConditionType = c.ConditionType, Target = c.Target, Value = c.Value, RunOn = c.RunOn })
                .ToList();
        }

        public override string ToString() => DisplayName;
    }
}
