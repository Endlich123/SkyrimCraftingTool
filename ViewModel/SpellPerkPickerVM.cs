using System;
using System.Collections.Generic;
using System.Linq;
using SkyrimCraftingTool.Model;

namespace SkyrimCraftingTool.ViewModel
{
    public sealed class SpellPerkPickerVM : ViewModelBase
    {
        private readonly Func<IReadOnlyList<PerkChoice>> _catalogue;
        private readonly Func<bool> _isChanged;
        private SpellRecord? _spell;

        public SpellPerkPickerVM(Func<IReadOnlyList<PerkChoice>> catalogue, Func<bool> isChanged)
        {
            _catalogue = catalogue;
            _isChanged = isChanged;
        }

        private IEnumerable<PerkChoice> All =>
            new[] { PerkChoice.None }.Concat(_catalogue() ?? Array.Empty<PerkChoice>());

        public IEnumerable<PerkChoice> Options
        {
            get
            {
                var current = Current;
                return All.Where(p => p.Key.Length == 0
                                      || ReferenceEquals(p, current)
                                      || ((IsUntouchedText || p.Matches(SearchText)) && Filter.Passes(p)));
            }
        }

        // The skill-tree chips, counted over the whole perk catalogue.
        private PickerFilter? _filter;
        public PickerFilter Filter
        {
            get
            {
                if (_filter != null) return _filter;
                _filter = new PickerFilter(() => _catalogue());
                _filter.Changed += () => OnPropertyChanged(nameof(Options));
                return _filter;
            }
        }

        private bool IsUntouchedText =>
            string.IsNullOrWhiteSpace(SearchText)
            || string.Equals(SearchText, CurrentDisplay, StringComparison.OrdinalIgnoreCase);

        public void Follow(SpellRecord? spell)
        {
            _spell = spell;
            _searchText = CurrentDisplay;

            OnPropertyChanged(nameof(Selected));
            OnPropertyChanged(nameof(SearchText));
            OnPropertyChanged(nameof(IsChanged));
            OnPropertyChanged(nameof(Options));
            OnPropertyChanged(nameof(IsUnknownKey));
        }

        private string CurrentKey => _spell?.HalfCostPerkKey ?? "";

        private PerkChoice? Current =>
            All.FirstOrDefault(p => string.Equals(p.Key, CurrentKey, StringComparison.OrdinalIgnoreCase));

        private string CurrentDisplay => Current?.Display ?? CurrentKey;

        // The stored key names no perk this load order has.
        public bool IsUnknownKey => _spell != null && CurrentKey.Length > 0 && Current == null;

        private string _searchText = "";
        public string SearchText
        {
            get => _searchText;
            set
            {
                if (SetProperty(ref _searchText, value ?? ""))
                    OnPropertyChanged(nameof(Options));
            }
        }

        public PerkChoice? Selected
        {
            get => Current;
            set
            {
                if (value == null || _spell == null) return;

                // EditorID first, so the school-mismatch note reads the new perk when the key change
                // makes it re-evaluate.
                _spell.HalfCostPerkEditorId = value.EditorId;
                _spell.HalfCostPerkKey = value.Key;
                _searchText = value.Display;

                OnPropertyChanged();
                OnPropertyChanged(nameof(SearchText));
                OnPropertyChanged(nameof(IsChanged));
                OnPropertyChanged(nameof(IsUnknownKey));
                OnPropertyChanged(nameof(Options));
            }
        }

        public bool IsChanged => _isChanged();
    }
}
