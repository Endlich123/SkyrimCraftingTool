using SkyrimCraftingTool.Model;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SkyrimCraftingTool.ViewModel
{
    public class ContainerEntryVM : ViewModelBase
    {
        private bool _isSelected;
        public bool IsSelected
        {
            get => _isSelected;
            set => SetProperty(ref _isSelected, value);
        }

        // "The scanned load order already has this item in this container" - a completely different
        // statement from IsSelected, which means "the patch will put it there".
        //
        // They are two states and never one: an item can already sit in a chest without the user
        // having selected it, and can be selected for a chest it has never been in. Merging them
        // would either claim the patch adds something it does not touch, or hide a placement that
        // already exists. Read-only, refreshed when the selected item changes or a rescan runs.
        private bool _alreadyContainsItem;
        public bool AlreadyContainsItem
        {
            get => _alreadyContainsItem;
            set => SetProperty(ref _alreadyContainsItem, value);
        }

        // How many of it the container already holds, for the badge's tooltip. 0 while
        // AlreadyContainsItem is false.
        private int _alreadyContainsCount;
        public int AlreadyContainsCount
        {
            get => _alreadyContainsCount;
            set
            {
                if (SetProperty(ref _alreadyContainsCount, value))
                    OnPropertyChanged(nameof(AlreadyContainsTooltip));
            }
        }

        public string AlreadyContainsTooltip => !AlreadyContainsItem
            ? ""
            : $"Already in your load order: this container holds {AlreadyContainsCount}x of this item. " +
              "That is what the game has now, not something your patch adds.";

        public System.Windows.Input.ICommand ToggleSelectedCommand { get; }
        public event Action<ContainerEntryVM>? ToggleSelectedRequested;

        public string ContainerKey => Definition.ContainerKey;
        public string Name => Definition.Name;

        // Which rows a container list shows - the ONE rule for every place that lists containers, so
        // the item editor, the bulk editor, the presets and the placement tabs cannot disagree. It
        // used to be four copies of the same Contains("Merchant").
        //
        //   Standard view: merchants only, to keep a list of hundreds short.
        //   Expert view:   everything.
        public static bool IsShown(ContainerEntryVM row, bool expert)
            => expert || row.Name.Contains("Merchant", StringComparison.OrdinalIgnoreCase);

        public ContainerRecord Definition { get; }

        public ObservableCollection<LVLiEntryVM> LVLiEntries { get; } = new();

        public ContainerEntryVM(ContainerRecord def)
        {
            Definition = def;

            ToggleSelectedCommand = new RelayCommand(() => ToggleSelectedRequested?.Invoke(this));

            // The set of edited lists is cached in the store, so this is a hash lookup per row rather
            // than a query - one container can carry dozens of lists, and the item editor builds
            // hundreds of these rows.
            var edited = Services.LeveledListEditStore.EditedKeys();

            foreach (var lvli in def.LVLIEntries)
            {
                LVLiEntries.Add(new LVLiEntryVM(lvli)
                {
                    IsListEdited = edited.Contains(lvli.LVLiKey),
                    OwnerContainer = this,
                });
            }
        }

        // What opens the detail window behind a row - installed by whoever owns this container row
        // (ContainerSelectionVM, or the multi-select's template rows). Kept here rather than set row
        // by row, because rows for nested lists arrive later, and one installed on the rows that
        // happened to exist would leave those links dead.
        private Action<LVLiEntryVM>? _rowOpenRequested;
        public Action<LVLiEntryVM>? RowOpenRequested
        {
            get => _rowOpenRequested;
            set
            {
                _rowOpenRequested = value;
                foreach (var lvli in LVLiEntries)
                    lvli.OpenRequested = value;
            }
        }

        // --- Placements into NESTED lists ---
        //
        // The container is the WAY to a list, not its owner. Its own rows are the lists it holds
        // directly; a list further down - LItemArmorBootsHeavyBlacksmith below LItemBlacksmithArmor75
        // - is reached by opening one of those, and placing there puts the item into the narrower
        // list instead of the broad one. Stored in the same ContainerString under this container
        // ("{Container: {NestedList,Level}}"): the rule builder has always grouped by LIST and only
        // uses the container for the comment, so the patch needs nothing new.
        //
        // A nested row exists only while it carries a placement, or while its window is open: an
        // empty one is a row nobody chose.
        public LVLiEntryVM? GetOrAddNested(string listKey, string listName)
        {
            if (string.IsNullOrWhiteSpace(listKey)) return null;

            // Reached both directly and through another list: the direct row is the same placement.
            var existing = LVLiEntries.FirstOrDefault(l =>
                string.Equals(l.Key, listKey, StringComparison.OrdinalIgnoreCase));
            if (existing != null) return existing;

            return AddNestedRow(listKey, listName);
        }

        public void RemoveNestedIfUnused(LVLiEntryVM row)
        {
            if (row.IsNested && row.Level == 0)
                LVLiEntries.Remove(row);
        }

        private LVLiEntryVM AddNestedRow(string listKey, string listName)
        {
            var row = new LVLiEntryVM(new ContainerLVLIRecord
            {
                ContainerKey = ContainerKey,
                LVLiKey = listKey,
                LVLiName = string.IsNullOrWhiteSpace(listName) ? listKey : listName,
            })
            {
                IsNested = true,
                IsListEdited = Services.LeveledListEditStore.EditedKeys().Contains(listKey),
                OwnerContainer = this,
                OpenRequested = _rowOpenRequested,
            };

            LVLiEntries.Add(row);
            return row;
        }

        // Both halves of the read-only state in one call, so the badge and its tooltip can never
        // disagree - a container marked as holding the item but reporting "0x" is worse than no
        // badge at all.
        public void SetExistingPlacement(int count)
        {
            AlreadyContainsCount = count;
            AlreadyContainsItem = count > 0;
            OnPropertyChanged(nameof(AlreadyContainsTooltip));
        }

        public void ApplyPlacements(Dictionary<string, Services.LvliPlacement> placements)
        {
            // Case-insensitive whatever the caller built: the bulk editors hand over a plain
            // ToDictionary, and Skyrim keys turn up in both casings (see key-case-variants).
            placements = new Dictionary<string, Services.LvliPlacement>(
                placements ?? new(), StringComparer.OrdinalIgnoreCase);

            // A nested row the string no longer names is gone, not zeroed - see GetOrAddNested.
            foreach (var stale in LVLiEntries.Where(l => l.IsNested && !placements.ContainsKey(l.Key)).ToList())
                LVLiEntries.Remove(stale);

            // A list this container does not hold directly is a nested placement. It needs a row of
            // its own, or the next BuildString would quietly drop it. One that no longer resolves -
            // left the load order, or an own list that was deleted - is dropped, as a vanished
            // direct list always was.
            foreach (var key in placements.Keys)
            {
                if (LVLiEntries.Any(l => string.Equals(l.Key, key, StringComparison.OrdinalIgnoreCase)))
                    continue;
                if (placements[key].Level <= 0) continue;

                var name = Services.PlacementLookup.LeveledListName(key);
                if (name == null) continue;

                AddNestedRow(key, name);
            }

            foreach (var lvli in LVLiEntries)
            {
                if (placements.TryGetValue(lvli.Key, out var placement))
                {
                    lvli.Level = placement.Level;
                    lvli.Count = placement.Count;
                }
                else
                {
                    // Not in the string means "no placement" - the count has to go back to its
                    // default too, or a stale one from a previously loaded item would be written
                    // back out the next time this entry is switched on.
                    lvli.Level = 0;
                    lvli.Count = Services.ContainerStringParser.DefaultCount;
                }
            }
        }
    }


}
