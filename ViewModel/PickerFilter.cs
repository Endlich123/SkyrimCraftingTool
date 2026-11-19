using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using SkyrimCraftingTool.Model;

namespace SkyrimCraftingTool.ViewModel
{
    // THE FILTER CHIPS OF A PICKER.
    //
    // One instance per picker, owned by whoever owns the picker's list. It knows the groups of its
    // source with their counts (the chips), which ones are switched on, and whether an entry passes.
    // It does NOT hold the list: every picker already filters by typed text in its own way, and this
    // is one more condition in that same filter - the owner asks Passes() and re-filters on Changed.
    //
    // Several chips on = any of them (Smithing OR Enchanting). None on = no restriction.
    //
    // RIGHT-CLICK EXCLUDES (2026-11-09): a chip can also be "not these" - its entries leave the list,
    // whatever else is on. Some pickers start with chips excluded: the material box holds every
    // armor and weapon too (38,706 next to 6,332 materials, for the ~2,000 recipes that take one as
    // an ingredient), and shows them only when asked. Never remembered past the picker - what one
    // recipe needs is not what the next one does.
    //
    // THE OWNER MUST NEVER LET A CHIP HIDE THE CURRENT PICK: an editable ComboBox whose SelectedItem
    // leaves its ItemsSource writes null back, and for an ingredient or a condition that is a lost
    // value, not a cosmetic glitch. The material view and PickerOptionsConverter both keep the pick.
    public sealed class PickerFilter : ViewModelBase
    {
        // For entries with no group at all inside a source that has groups - a perk in no skill tree.
        public const string NoGroup = "(none)";

        private readonly Func<IEnumerable?>? _source;
        private readonly HashSet<string> _active = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _excluded = new(StringComparer.OrdinalIgnoreCase);
        private object? _sourceRef;
        private bool _built;

        // source: read lazily, the first time the chips are asked for - that is, when a dropdown
        // actually opens. An ingredient row has a material view nobody may ever look at, and building
        // chips for 6.000 materials per row up front is the kind of cost the preset patch measured.
        // order: chips listed here come first, in this order; all others follow alphabetically.
        // excluded: chips that start switched to "not these" (see above).
        private readonly IReadOnlyList<string>? _order;

        public PickerFilter(Func<IEnumerable?>? source = null, IReadOnlyList<string>? order = null,
                            IEnumerable<string>? excluded = null)
        {
            _source = source;
            _order = order;
            foreach (var label in excluded ?? Array.Empty<string>()) _excluded.Add(label);
            ClearCommand = new RelayCommand(Clear);
        }

        private int OrderOf(string label)
        {
            if (_order == null) return int.MaxValue;
            for (int i = 0; i < _order.Count; i++)
                if (string.Equals(_order[i], label, StringComparison.OrdinalIgnoreCase)) return i;
            return int.MaxValue;
        }

        public ObservableCollection<PickerChip> Chips { get; } = new();

        // One group is no choice: a single chip would only ever show everything or nothing.
        public bool HasChips
        {
            get { EnsureBuilt(); return Chips.Count > 1; }
        }

        // A chip switched ON ("only these"). RecordPickerVM lists a chip's entries on this alone, which
        // an exclusion must not trigger - hence two flags.
        public bool HasActive => _active.Count > 0;

        // Anything set at all, on or excluded - shows the reset button.
        public bool IsFiltering => _active.Count > 0 || _excluded.Count > 0;

        // Bumped on every change, for bindings that have to re-run (PickerOptionsConverter).
        private int _version;
        public int Version
        {
            get => _version;
            private set => SetProperty(ref _version, value);
        }

        public event Action? Changed;

        public ICommand ClearCommand { get; }

        public bool Passes(object? item)
        {
            if (_active.Count == 0 && _excluded.Count == 0) return true;
            if (item is not IPickerGrouped grouped) return true;

            bool any = false, included = false;
            foreach (var group in grouped.PickerGroups)
            {
                any = true;
                // Excluded wins: a perk in two trees, one of them excluded, is gone.
                if (_excluded.Contains(group)) return false;
                if (_active.Contains(group)) included = true;
            }

            if (!any)
            {
                if (_excluded.Contains(NoGroup)) return false;
                included = _active.Contains(NoGroup);
            }

            return _active.Count == 0 || included;
        }

        // For a source handed over from outside (the converter): rebuilt only when it is a different
        // list, so a binding that re-runs on every keystroke does not recount every time.
        public void EnsureSource(IEnumerable? items)
        {
            if (ReferenceEquals(items, _sourceRef) && _built) return;
            Build(items);
        }

        private void EnsureBuilt()
        {
            if (_built || _source == null) return;
            Build(_source());
        }

        private void Build(IEnumerable? items)
        {
            _built = true;
            _sourceRef = items;

            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            int ungrouped = 0;

            foreach (var item in items ?? Array.Empty<object>())
            {
                if (item is not IPickerGrouped grouped) continue;

                bool any = false;
                foreach (var group in grouped.PickerGroups)
                {
                    if (string.IsNullOrWhiteSpace(group)) continue;
                    any = true;
                    counts[group] = counts.TryGetValue(group, out var n) ? n + 1 : 1;
                }

                if (!any) ungrouped++;
            }

            // "(none)" only beside real groups: in a list without any groups it would be the one chip.
            if (ungrouped > 0 && counts.Count > 0) counts[NoGroup] = ungrouped;

            Chips.Clear();
            foreach (var (label, count) in counts
                         .OrderBy(kv => kv.Key == NoGroup ? 1 : 0)
                         .ThenBy(kv => OrderOf(kv.Key))
                         .ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                         .Select(kv => (kv.Key, kv.Value)))
                Chips.Add(new PickerChip(this, label, count, _active.Contains(label), _excluded.Contains(label)));

            // A chip that was on (or excluded) and whose group left the source stays - and visible, so
            // it can be switched off again. Otherwise the list would be filtered by something not on
            // screen. An exclusion set up front for a group this source never had is not shown.
            foreach (var label in _active.Where(a => !counts.ContainsKey(a)).ToList())
                Chips.Add(new PickerChip(this, label, 0, true, false));

            OnPropertyChanged(nameof(HasChips));
        }

        internal void Set(PickerChip chip, bool on)
        {
            bool changed = on ? _active.Add(chip.Label) : _active.Remove(chip.Label);
            // "Only these" and "not these" exclude each other.
            if (on && _excluded.Remove(chip.Label)) { changed = true; chip.SetExcludedSilently(false); }
            if (!changed) return;

            Notify();
        }

        internal void SetExcluded(PickerChip chip, bool excluded)
        {
            bool changed = excluded ? _excluded.Add(chip.Label) : _excluded.Remove(chip.Label);
            if (excluded && _active.Remove(chip.Label)) { changed = true; chip.SetSilently(false); }
            if (!changed) return;

            Notify();
        }

        // Every chip back to off - exclusions a picker started with included: the button says
        // "show every group again", and that is what it does.
        public void Clear()
        {
            if (!IsFiltering) return;

            _active.Clear();
            _excluded.Clear();
            foreach (var chip in Chips) { chip.SetSilently(false); chip.SetExcludedSilently(false); }

            Notify();
        }

        private void Notify()
        {
            OnPropertyChanged(nameof(HasActive));
            OnPropertyChanged(nameof(IsFiltering));
            Version++;
            Changed?.Invoke();
        }
    }

    public sealed class PickerChip : ViewModelBase
    {
        private readonly PickerFilter _owner;

        internal PickerChip(PickerFilter owner, string label, int count, bool isActive, bool isExcluded = false)
        {
            _owner = owner;
            Label = label;
            Count = count;
            _isActive = isActive;
            _isExcluded = isExcluded;
            ToggleExcludedCommand = new RelayCommand(() => IsExcluded = !IsExcluded);
        }

        public string Label { get; }
        public int Count { get; }

        public string ToolTip
        {
            get
            {
                var count = Count == 1 ? $"{Label}: 1 entry" : $"{Label}: {Count} entries";
                return IsExcluded
                    ? $"{count} - hidden. Right-click to show them again."
                    : $"{count}. Click: only these. Right-click: hide them.";
            }
        }

        // Right-click on the chip (MouseBinding in the chip bar's template).
        public ICommand ToggleExcludedCommand { get; }

        private bool _isExcluded;
        public bool IsExcluded
        {
            get => _isExcluded;
            set
            {
                if (!SetProperty(ref _isExcluded, value)) return;
                OnPropertyChanged(nameof(ToolTip));
                _owner.SetExcluded(this, value);
            }
        }

        internal void SetExcludedSilently(bool value)
        {
            if (_isExcluded == value) return;
            _isExcluded = value;
            OnPropertyChanged(nameof(IsExcluded));
            OnPropertyChanged(nameof(ToolTip));
        }

        private bool _isActive;
        public bool IsActive
        {
            get => _isActive;
            set
            {
                if (!SetProperty(ref _isActive, value)) return;
                _owner.Set(this, value);
            }
        }

        internal void SetSilently(bool value)
        {
            if (_isActive == value) return;
            _isActive = value;
            OnPropertyChanged(nameof(IsActive));
        }
    }
}
