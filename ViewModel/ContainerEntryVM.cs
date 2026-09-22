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
                LVLiEntries.Add(new LVLiEntryVM(lvli) { IsListEdited = edited.Contains(lvli.LVLiKey) });
            }
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
