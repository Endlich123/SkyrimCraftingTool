using SkyrimCraftingTool.Model;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows.Data;

namespace SkyrimCraftingTool.ViewModel
{
    public class IngredientEntryVM : ViewModelBase
    {
        // Nullable: a free-form template row (see MultiSelectDetailVM's Crafting/Temper Recipe
        // template, built directly by the user instead of copied from an item) isn't owned by any
        // ItemNodeVM yet - NotifyParent() below is simply a no-op for those until the row gets
        // cloned onto a real target item.
        private readonly ItemNodeVM? _parentItem;
        private readonly bool _isTemper;

        public IngredientEntryVM(ItemNodeVM? parentItem, bool isTemper = false)
        {
            _parentItem = parentItem;
            _isTemper = isTemper;
        }

        private string _key;
        private string _materialName;
        // Default 1 so a freshly added ingredient row always starts at amount 1 (matches
        // PresetIngredientEntryVM). Load paths (COBJNodeVM) set Count explicitly from the record,
        // so this only ever applies to brand-new rows added via the "+" button.
        private int _count = 1;
        private FormIDRecord _selectedMaterial;
        private string _searchText;
        private ICollectionView _localMaterialsView;

        public string Key
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

        public string MaterialName
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

        public ICollectionView LocalMaterialsView
        {
            get => _localMaterialsView;
            private set
            {
                _localMaterialsView = value;
                OnPropertyChanged(nameof(LocalMaterialsView));
            }
        }


        public FormIDRecord SelectedMaterial
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
            if (_parentItem == null || _parentItem.IsLoading)
                return;

            if (_isTemper)
                _parentItem.NotifyFieldChanged(nameof(ItemNodeVM.TemperIngredients));
            else
                _parentItem.NotifyFieldChanged(nameof(ItemNodeVM.CraftingIngredients));
        }

        public void SetSelectedMaterialSilent(FormIDRecord value)
        {
            _selectedMaterial = value;
            _key = value?.Key;
            _materialName = value?.Name;
            // Keep the editable ComboBox's text box in sync - without this it shows blank for a
            // silently-set material until the user interacts with it.
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

            LocalMaterialsView = new ListCollectionView(allMaterials);

            LocalMaterialsView.Filter = obj =>
            {
                if (obj is not FormIDRecord mat) return false;

                // Always keep this row's own current pick visible (otherwise the editable ComboBox
                // nulls SelectedItem the moment it's filtered out and wipes the text being typed).
                if (ReferenceEquals(mat, SelectedMaterial))
                    return true;

                // Hide materials another ingredient row of the same recipe already uses - no dupes.
                if (IsUsedBySibling(mat.Key))
                    return false;

                // Not actively searching (box just shows the current pick, or is empty): show all.
                if (string.IsNullOrWhiteSpace(SearchText) ||
                    (SelectedMaterial != null && SearchText == SelectedMaterial.Name))
                    return true;

                return mat.Name.Contains(SearchText, System.StringComparison.OrdinalIgnoreCase);
            };
        }
    }
}
