using SkyrimCraftingTool.Model;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows.Data;

namespace SkyrimCraftingTool.ViewModel
{
    public class IngredientEntryVM : ViewModelBase
    {
        private readonly ItemNodeVM? _parentItem;
        private readonly bool _isTemper;

        // Set instead of the isTemper routing when this row does not belong to the item's crafting
        // or temper ingredient list at all. The Breakdown section reuses this very row as its
        // OUTPUT line - same material picker, same amount box, same dead-reference handling - and
        // routes its changes to the one recipe it belongs to, because an item can have several
        // breakdown recipes and "which one changed" cannot be expressed as a field name.
        //
        // A callback rather than a third enum value on purpose: crafting and temper keep working
        // exactly as before, and nothing about their two-way routing had to be touched.
        private readonly System.Action? _onChanged;

        public IngredientEntryVM(ItemNodeVM? parentItem, bool isTemper = false, System.Action? onChanged = null)
        {
            _parentItem = parentItem;
            _isTemper = isTemper;
            _onChanged = onChanged;
        }

        private string? _key;
        private string? _materialName;
        // Default 1 so a freshly added ingredient row always starts at amount 1 (matches
        // PresetIngredientEntryVM). Load paths (COBJNodeVM) set Count explicitly from the record,
        // so this only ever applies to brand-new rows added via the "+" button.
        private int _count = 1;
        private FormIDRecord? _selectedMaterial;
        private string _searchText = "";
        private ICollectionView? _localMaterialsView;

        public string? Key
        {
            get => _key;
            set
            {
                if (SetProperty(ref _key, value))
                {
                    NotifyParent();
                    OnPropertyChanged(nameof(IsDeadReference));
                    RefreshSiblingMaterialFilters();
                }
            }
        }

        // The other ingredient rows of the SAME recipe (Crafting vs Temper, per _isTemper). Empty for
        // a template row with no owning item. Used to keep a material from being picked twice.
        private IEnumerable<IngredientEntryVM> Siblings =>
            _parentItem == null
                ? Enumerable.Empty<IngredientEntryVM>()
                : (_isTemper ? _parentItem.TemperIngredients : _parentItem.CraftingIngredients);

        private bool IsUsedBySibling(string? key) =>
            !string.IsNullOrEmpty(key)
            && Siblings.Any(s => !ReferenceEquals(s, this) && s.Key == key);

        // Re-entrancy guard: an ICollectionView.Refresh() on an editable ComboBox's ItemsSource can
        // synchronously churn its SelectedItem / editable Text, which writes back through the binding
        // and lands in a Key or SearchText setter -> another Refresh -> ping-pong flood/hang.
        // EVERY Refresh of a material view goes through here. UI thread only, so a static flag is enough.
        private static bool _refreshingFilters;

        public void RefreshMaterialFilter()
        {
            if (_refreshingFilters || _localMaterialsView == null) return;
            _refreshingFilters = true;
            try { _localMaterialsView.Refresh(); }
            finally { _refreshingFilters = false; }
        }

        private void RefreshSiblingMaterialFilters()
        {
            if (_refreshingFilters) return;

            // Only coordinate when this row is actually attached to a recipe's live ingredient
            // collection. During a COBJNodeVM rebuild (Reset / load) the rows get their Key set via
            // an object initializer while still detached - "Siblings" would then be the OLD, tearing-
            // down collection, and refreshing those views floods binding errors.
            if (_parentItem == null || !Siblings.Contains(this)) return;

            _refreshingFilters = true;
            try
            {
                foreach (var s in Siblings)
                    if (!ReferenceEquals(s, this))
                        s._localMaterialsView?.Refresh();
            }
            finally { _refreshingFilters = false; }
        }

        // True when this row has a material key that no longer resolves against the current scan.
        // Only meaningful for a row owned by a real item (a multi-select template row has no
        // resolver context, so it never flags).
        public bool IsDeadReference =>
            !string.IsNullOrEmpty(_key)
            && _parentItem?.Main?.References is { } refs
            && !refs.IsActive(_key);

        // Dead-reference case: no matching material was found, so SelectedMaterial stays null and the
        // editable box would be blank. Show the raw key so the (red-bordered) row isn't empty.
        public void ShowUnresolvedKey()
        {
            _searchText = _key ?? "";
            OnPropertyChanged(nameof(SearchText));
            OnPropertyChanged(nameof(IsDeadReference));
        }

        public string? MaterialName
        {
            get => _materialName;
            set
            {
                if (SetProperty(ref _materialName, value))
                    NotifyParent();
            }
        }

        public int Count
        {
            get => _count;
            // A recipe ingredient always needs at least 1 - clamp here so bad values from import /
            // preset-apply / a malformed "key*count" string can't slip through (the UI box also
            // enforces Min=1 on LostFocus).
            set
            {
                if (SetProperty(ref _count, System.Math.Max(1, value)))
                    NotifyParent();
            }
        }

        public string SearchText
        {
            get => _searchText;
            set
            {
                if (SetProperty(ref _searchText, value))
                    RefreshMaterialFilter();
            }
        }

        // Built on first read - that is, when a ComboBox actually shows it. A filtered view over every
        // material of the load order, per ingredient row, was 8.8 s of a 74 s preset patch over
        // 18,867 items whose rows nobody looks at (profile 2026-10-22).
        private List<FormIDRecord>? _allMaterials;

        public ICollectionView? LocalMaterialsView
        {
            get
            {
                if (_localMaterialsView == null && _allMaterials != null)
                    _localMaterialsView = BuildMaterialsView(_allMaterials);
                return _localMaterialsView;
            }
        }


        public FormIDRecord? SelectedMaterial
        {
            get => _selectedMaterial;
            set
            {
                if (SetProperty(ref _selectedMaterial, value) && value != null)
                {
                    Key = value.Key;
                    MaterialName = value.Name;

                    _searchText = value.Name;
                    OnPropertyChanged(nameof(SearchText));

                    NotifyParent();
                }
                OnPropertyChanged(nameof(IsDeadReference));
            }
        }

        private void NotifyParent()
        {
            // BEFORE the _parentItem guard, not after. A breakdown output row has no owning item -
            // it belongs to one of several recipes, not to the item's own ingredient lists - so the
            // null check below would swallow its changes entirely.
            if (_onChanged != null)
            {
                _onChanged();
                return;
            }

            if (_parentItem == null || _parentItem.IsLoading)
                return;

            if (_isTemper)
                _parentItem.NotifyFieldChanged(nameof(ItemNodeVM.TemperIngredients));
            else
                _parentItem.NotifyFieldChanged(nameof(ItemNodeVM.CraftingIngredients));
        }

        public void SetSelectedMaterialSilent(FormIDRecord? value)
        {
            _selectedMaterial = value;
            _key = value?.Key;
            _materialName = value?.Name;
            _searchText = value?.Name ?? "";

            OnPropertyChanged(nameof(SelectedMaterial));
            OnPropertyChanged(nameof(Key));
            OnPropertyChanged(nameof(MaterialName));
            OnPropertyChanged(nameof(SearchText));
            OnPropertyChanged(nameof(IsDeadReference));
            // No sibling refresh here: this is the silent load path. Every row's filter reads
            // Siblings live, so all dropdowns are already correct once the rows first render.
        }

        public void InitializeMaterials(List<FormIDRecord> allMaterials)
        {
            if (allMaterials == null) return;
            _allMaterials = allMaterials;
            _localMaterialsView = null;
            _materialFilter?.EnsureSource(_allMaterials);
            OnPropertyChanged(nameof(LocalMaterialsView));
        }

        // The kind chips of this row's material dropdown. Per row, like the view: each row filters on
        // its own. Built lazily - the chips are counted when a dropdown first shows them, not for the
        // thousands of rows a preset patch creates and nobody opens (see LocalMaterialsView).
        private PickerFilter? _materialFilter;
        public PickerFilter MaterialFilter
        {
            get
            {
                if (_materialFilter != null) return _materialFilter;
                // Armor and weapon start excluded - see CacheManager.GearKinds.
                _materialFilter = new PickerFilter(() => _allMaterials, excluded: Services.CacheManager.GearKinds);
                _materialFilter.Changed += RefreshMaterialFilter;
                return _materialFilter;
            }
        }

        private ICollectionView BuildMaterialsView(List<FormIDRecord> allMaterials)
        {
            var view = new ListCollectionView(allMaterials);

            view.Filter = obj =>
            {
                if (obj is not FormIDRecord mat) return false;

                // Always keep this row's own current pick visible (otherwise the editable ComboBox
                // nulls SelectedItem the moment it's filtered out and wipes the text being typed).
                if (ReferenceEquals(mat, SelectedMaterial))
                    return true;

                // Hide materials another ingredient row of the same recipe already uses - no dupes.
                if (IsUsedBySibling(mat.Key))
                    return false;

                // The kind chips in the dropdown (Misc, Food, Soul gem ...). After the own-pick check
                // above on purpose: a chip must never hide what the row holds. The property, not the
                // field: the filter starts with armor and weapons excluded, and must apply even before
                // the chip bar has asked for it. Creating it is cheap - its chips are counted lazily.
                if (!MaterialFilter.Passes(mat))
                    return false;

                // Not actively searching (box just shows the current pick, or is empty): show all.
                if (string.IsNullOrWhiteSpace(SearchText) ||
                    (SelectedMaterial != null && SearchText == SelectedMaterial.Name))
                    return true;

                // EditorID, game name and kind ("food", "soul gem") - see FormIDRecord.Matches.
                return mat.Matches(SearchText);
            };
            return view;
        }
    }
}
