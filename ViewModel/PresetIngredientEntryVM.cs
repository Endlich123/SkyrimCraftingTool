using SkyrimCraftingTool.Model;
using SkyrimCraftingTool.Services;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows.Data;

namespace SkyrimCraftingTool.ViewModel
{
    // Decoupled counterpart to IngredientEntryVM: same shape and behavior, but notifies its owner
    // through a plain callback instead of a concrete ItemNodeVM parent, so it can be reused for
    // Preset ingredient rows (which have no backing item/COBJ).
    public class PresetIngredientEntryVM : ViewModelBase
    {
        private readonly Action _onChanged;
        private readonly Func<bool> _isLoading;
        private readonly Func<IEnumerable<PresetIngredientEntryVM>>? _siblings;
        private readonly IReferenceResolver? _references;

        public PresetIngredientEntryVM(Action onChanged, Func<bool> isLoading,
            Func<IEnumerable<PresetIngredientEntryVM>>? siblings = null,
            IReferenceResolver? references = null)
        {
            _onChanged = onChanged;
            _isLoading = isLoading;
            _siblings = siblings;
            _references = references;
        }

        // Key set but doesn't resolve against the current scan (see IngredientEntryVM.IsDeadReference).
        public bool IsDeadReference =>
            !string.IsNullOrEmpty(_key)
            && _references is { } r && !r.IsActive(_key);

        private IEnumerable<PresetIngredientEntryVM> Siblings =>
            _siblings?.Invoke() ?? Enumerable.Empty<PresetIngredientEntryVM>();

        private bool IsUsedBySibling(string? key) =>
            !string.IsNullOrEmpty(key)
            && Siblings.Any(s => !ReferenceEquals(s, this) && s.Key == key);

        // See IngredientEntryVM._refreshingFilters - every material-view Refresh goes through here,
        // guarding against a Refresh -> SelectedItem/Text churn -> Key/SearchText setter -> Refresh
        // ping-pong flood/hang. UI thread only.
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
            // Only coordinate when attached to the live collection (see IngredientEntryVM).
            if (!Siblings.Contains(this)) return;

            _refreshingFilters = true;
            try
            {
                foreach (var s in Siblings)
                    if (!ReferenceEquals(s, this))
                        s._localMaterialsView?.Refresh();
            }
            finally { _refreshingFilters = false; }
        }

        private string _key = "";
        private string _materialName = "";
        private int _count = 1;
        private FormIDRecord _selectedMaterial;
        private string _searchText = "";
        private ICollectionView _localMaterialsView;

        public string Key
        {
            get => _key;
            set
            {
                if (SetProperty(ref _key, value))
                {
                    NotifyChanged();
                    OnPropertyChanged(nameof(IsDeadReference));
                    RefreshSiblingMaterialFilters();
                }
            }
        }

        public string MaterialName
        {
            get => _materialName;
            set { if (SetProperty(ref _materialName, value)) NotifyChanged(); }
        }

        public int Count
        {
            get => _count;
            // A recipe ingredient always needs at least 1 (see IngredientEntryVM.Count).
            set { if (SetProperty(ref _count, System.Math.Max(1, value))) NotifyChanged(); }
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

                    NotifyChanged();
                }
            }
        }

        public void SetSelectedMaterialSilent(FormIDRecord value)
        {
            _selectedMaterial = value;
            _key = value?.Key ?? "";
            _materialName = value?.Name ?? "";
            // Keep the editable ComboBox's text box in sync (see IngredientEntryVM).
            _searchText = value?.Name ?? "";

            OnPropertyChanged(nameof(SelectedMaterial));
            OnPropertyChanged(nameof(Key));
            OnPropertyChanged(nameof(MaterialName));
            OnPropertyChanged(nameof(SearchText));
            OnPropertyChanged(nameof(IsDeadReference));
            // No sibling refresh: silent load path (see IngredientEntryVM).
        }

        public void InitializeMaterials(List<FormIDRecord> allMaterials)
        {
            if (allMaterials == null) return;

            LocalMaterialsView = new ListCollectionView(allMaterials);

            LocalMaterialsView.Filter = obj =>
            {
                if (obj is not FormIDRecord mat) return false;

                // Always keep this row's own current pick visible.
                if (ReferenceEquals(mat, SelectedMaterial))
                    return true;

                // Hide materials another ingredient row of the same recipe already uses - no dupes.
                if (IsUsedBySibling(mat.Key))
                    return false;

                // Not actively searching (box just shows the current pick, or is empty): show all.
                if (string.IsNullOrWhiteSpace(SearchText) ||
                    (SelectedMaterial != null && SearchText == SelectedMaterial.Name))
                    return true;

                return mat.Name.Contains(SearchText, StringComparison.OrdinalIgnoreCase);
            };
        }

        private void NotifyChanged()
        {
            if (_isLoading()) return;
            _onChanged();
        }
    }
}
