using System;
using System.Collections.Generic;
using System.Linq;
using SkyrimCraftingTool.Model;

namespace SkyrimCraftingTool.ViewModel
{
    // One "which record does this NPC point at" field: class, race, voice, outfit, death item, skin
    // (Prio 8 / N-P4, docs/NPC-Plan.md).
    //
    // Seven fields with the same behaviour, so one type rather than seven copies. Each one is built
    // once with its catalogue and a pair of accessors, and follows whichever NPC is selected.
    //
    // The catalogues run to hundreds of entries (1.458 factions, 687 outfits, 335 races), so the box
    // filters as you type - the same editable-ComboBox arrangement as the enchantment picker, with
    // the same trap: a filtered list that no longer contains the selection makes WPF report
    // SelectedItem = null, and taking that at face value would clear the field mid-keystroke.
    public class NpcLinkPickerVM : ViewModelBase
    {
        private readonly Func<IReadOnlyList<FormIDRecord>> _catalogue;
        private readonly Func<NpcNodeVM?, string> _read;
        private readonly Action<NpcNodeVM, string> _write;
        private readonly Func<NpcNodeVM?, bool> _isChanged;

        private NpcNodeVM? _npc;

        public NpcLinkPickerVM(
            string label,
            Func<IReadOnlyList<FormIDRecord>> catalogue,
            Func<NpcNodeVM?, string> read,
            Action<NpcNodeVM, string> write,
            Func<NpcNodeVM?, bool> isChanged,
            bool clearable = false)
        {
            Label = label;
            _catalogue = catalogue;
            _read = read;
            _write = write;
            _isChanged = isChanged;
            Clearable = clearable;
        }

        public string Label { get; }

        // Whether "none" is a value this field can actually be set to. SkyPatcher documents null for
        // deathItem and skin and for nothing else, so the other five have no way to be emptied and
        // must not offer one - an entry that cannot be patched is worse than no entry.
        public bool Clearable { get; }

        public void Follow(NpcNodeVM? npc)
        {
            _npc = npc;
            _searchText = CurrentName;

            OnPropertyChanged(nameof(Selected));
            OnPropertyChanged(nameof(SearchText));
            OnPropertyChanged(nameof(IsChanged));
            OnPropertyChanged(nameof(Options));
        }

        private IReadOnlyList<FormIDRecord> Catalogue => _catalogue() ?? Array.Empty<FormIDRecord>();

        public IEnumerable<FormIDRecord> Options =>
            Catalogue.Where(o => string.IsNullOrWhiteSpace(SearchText)
                              || (o.Name ?? "").Contains(SearchText, StringComparison.OrdinalIgnoreCase));

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

        private string CurrentKey => _read(_npc) ?? "";

        private string CurrentName =>
            Catalogue.FirstOrDefault(o => string.Equals(o.Key, CurrentKey, StringComparison.OrdinalIgnoreCase))?.Name
            ?? CurrentKey;

        public FormIDRecord? Selected
        {
            get => Catalogue.FirstOrDefault(o =>
                string.Equals(o.Key, CurrentKey, StringComparison.OrdinalIgnoreCase));
            set
            {
                // null arrives when the filter hides the selected row. Taking it at face value would
                // clear the field while someone is typing - the entry that says "none" is how a
                // clearable field gets emptied.
                if (value == null || _npc == null) return;

                _write(_npc, value.Key ?? "");
                _searchText = value.Name ?? "";

                OnPropertyChanged();
                OnPropertyChanged(nameof(SearchText));
                OnPropertyChanged(nameof(IsChanged));
            }
        }

        public bool IsChanged => _isChanged(_npc);
    }
}
