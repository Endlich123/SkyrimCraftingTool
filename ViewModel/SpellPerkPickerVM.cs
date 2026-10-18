using System;
using System.Collections.Generic;
using System.Linq;
using SkyrimCraftingTool.Model;

namespace SkyrimCraftingTool.ViewModel
{
    // The half-cost perk of the selected spell, as a pick from the scanned perks (user, 2026-10-18).
    //
    // It was a free text box, and that is how "Skyrim.esm|0C44C02323" - ten hex digits, no record at
    // all - reached the patch as halfCostPerk=Skyrim.esm|C44C02323. halfCostPerk= takes a perk and
    // nothing else, so the field offers perks and nothing else. "(none)" is first: a spell without a
    // half-cost perk is the ordinary case.
    //
    // Filters as you type, like NpcLinkPickerVM - the catalogue runs to hundreds of perks - and with
    // the same guard: a filter that hides the selected row makes WPF report SelectedItem = null,
    // which must not clear the field mid-keystroke.
    //
    // A key already stored that is not in the catalogue (typed before this, or from a mod since
    // removed) is shown as it is, so the user can see what is there and pick a real perk instead.
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

        // Filtered only by what the user TYPED. The box shows the current perk's name, and filtering
        // by that would open the list on one row - on a spell without a perk, on "(none)" alone.
        public IEnumerable<PerkChoice> Options =>
            IsUntouchedText
                ? All
                : All.Where(p => p.Key.Length == 0
                                 || p.Display.Contains(SearchText, StringComparison.OrdinalIgnoreCase));

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
