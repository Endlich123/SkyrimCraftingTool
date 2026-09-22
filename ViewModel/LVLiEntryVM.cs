using SkyrimCraftingTool.Model;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SkyrimCraftingTool.ViewModel
{
    public class LVLiEntryVM : ViewModelBase
    {
        public string Key => Definition.LVLiKey;
        public string LVLiName => Definition.LVLiName;

        public ContainerLVLIRecord Definition { get; }

        private int _level;
        public int Level
        {
            get => _level;
            set
            {
                if (_level != value)
                {
                    _level = value;
                    if (_level > 0) _lastActiveLevel = _level;

                    OnPropertyChanged();
                    OnPropertyChanged(nameof(LevelDouble));
                    OnPropertyChanged(nameof(HasPlacement));
                    OnPropertyChanged(nameof(IsSelected));
                    OnPropertyChanged(nameof(PlacementSummary));
                }
            }
        }

        public double LevelDouble
        {
            get => Level;
            set => Level = (int)value;
        }

        // Level 0 is "off", so there is no placement at all.
        public bool HasPlacement => Level > 0;

        // The container row's checkbox. Deliberately a view over Level rather than a flag of its
        // own: the patch decides everything from the level, and the rule "every list off means the
        // item goes into the container itself" reads Level == 0. A second, independent flag could
        // disagree with the level, and then the checkbox and the patch would tell different stories.
        //
        // Unchecking remembers the level so re-checking restores what was set in the detail window
        // instead of silently resetting to 1.
        private int _lastActiveLevel = DefaultLevel;
        public const int DefaultLevel = 1;

        public bool IsSelected
        {
            get => Level > 0;
            set
            {
                if (value == IsSelected) return;
                Level = value ? _lastActiveLevel : 0;
            }
        }

        // How many of the item the list hands out - the third field of addOnceToLLs
        // (<obj>~<level>~<count>), which used to be hardcoded to 1.
        //
        // Per ENTRY, so it belongs to this placement and changes nothing for other mods feeding the
        // same list. That is what separates it from chanceNone and the calc flags, which are
        // properties of the whole list.
        private int _count = Services.ContainerStringParser.DefaultCount;
        public int Count
        {
            get => _count;
            set
            {
                // A leveled-list entry with a count below 1 hands out nothing; clamped rather than
                // rejected so a cleared spinner cannot silently disable a placement.
                var clamped = value < 1 ? 1 : value;
                if (_count == clamped) return;
                _count = clamped;
                OnPropertyChanged();
                OnPropertyChanged(nameof(PlacementSummary));
            }
        }

        // What the container row shows next to the name, so the settings made in the detail window
        // are visible without opening it again.
        public string PlacementSummary => !HasPlacement
            ? ""
            : Count == Services.ContainerStringParser.DefaultCount
                ? $"from level {Level}"
                : $"from level {Level}, {Count}x";

        // "The scanned load order already has this item in this list" - the same read-only mark the
        // container rows carry, and the same reason for keeping it apart from IsSelected: checked
        // means the patch will add the item, marked means it is in there already. Both at once is a
        // normal state (the user re-adds an item at a different level), and one flag could not say
        // that.
        //
        // This is where the leveled-list half of "already in your load order" becomes visible. It
        // cannot be shown in the container list itself, because 86% of all leveled lists hang in no
        // container at all - they only appear here, under a container the user opened.
        private bool _alreadyContainsItem;
        public bool AlreadyContainsItem
        {
            get => _alreadyContainsItem;
            set => SetProperty(ref _alreadyContainsItem, value);
        }

        // "You changed this list's own properties" - chance or calculation, not a placement.
        //
        // A third state next to IsSelected and AlreadyContainsItem, and the last blind spot of the
        // three: a changed chance left no trace anywhere in the container tab, so the only way to
        // find your own edits again was the patch report or opening every list. Set when the row is
        // built and refreshed when the list window closes, which is the only place it can change.
        private bool _isListEdited;
        public bool IsListEdited
        {
            get => _isListEdited;
            set => SetProperty(ref _isListEdited, value);
        }

        // Opening the detail window hangs on the row itself, not on a command reached through
        // "RelativeSource AncestorType=UserControl". That route resolves to whichever view model the
        // surrounding view happens to have - and this row is rendered in four different views
        // (item, multi-select, and both preset editors), so an ancestor binding would need the same
        // command on four unrelated view models to keep working. ContainerSelectionVM installs this
        // once for every row it owns.
        public Action<LVLiEntryVM>? OpenRequested { get; set; }

        public System.Windows.Input.ICommand OpenCommand => new RelayCommand(() => OpenRequested?.Invoke(this));

        public LVLiEntryVM(ContainerLVLIRecord def)
        {
            Definition = def;
        }
    }

}
