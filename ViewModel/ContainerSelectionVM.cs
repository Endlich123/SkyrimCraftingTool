using Mutagen.Bethesda.Skyrim;
using SkyrimCraftingTool.Model;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using SkyrimCraftingTool.Services;

namespace SkyrimCraftingTool.ViewModel
{
    public class ContainerSelectionVM : ViewModelBase
    {
        public ObservableCollection<ContainerEntryVM> SelectedContainers { get; }
            = new();

        private readonly List<ContainerRecord> _allContainers;

        // Raised whenever an LVLi slider of a selected container changes its level. The sliders bind
        // straight to LVLiEntryVM.Level, so without this nobody would know to rebuild the owner's
        // ContainerString - and a level that never reaches the string is a level that never gets
        // saved or patched. PresetSlotNodeVM already did exactly this subscribing by hand; it is
        // here now so the item editor gets it too, instead of the view's code-behind guessing at
        // which mouse gesture counts as "the user changed something".
        public event Action? LevelChanged;

        public ContainerSelectionVM(List<ContainerRecord> allContainers)
        {
            _allContainers = allContainers ?? new List<ContainerRecord>();
            SelectedContainers.CollectionChanged += OnSelectedContainersChanged;
        }

        private void OnSelectedContainersChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.OldItems != null)
                foreach (ContainerEntryVM c in e.OldItems)
                    foreach (var lvli in c.LVLiEntries)
                        lvli.PropertyChanged -= OnLVLiPropertyChanged;

            if (e.NewItems != null)
                foreach (ContainerEntryVM c in e.NewItems)
                    foreach (var lvli in c.LVLiEntries)
                    {
                        lvli.PropertyChanged -= OnLVLiPropertyChanged;
                        lvli.PropertyChanged += OnLVLiPropertyChanged;
                        lvli.OpenRequested = OpenLeveledList;
                        lvli.AlreadyContainsItem = _existingListKeys.Contains(lvli.Key);
                    }
        }

        // The leveled lists that already hold the owner's item, from the scanned load order.
        //
        // Applied to the rows rather than looked up by them: the lookup is one query per item, and a
        // container can carry dozens of list rows. Kept here so a container opened later gets the
        // mark too - the set is known before the row exists.
        // Copied into a case-insensitive set no matter what the caller hands over: Skyrim ignores
        // case, so the same list turns up as "Skyrim.esm|00AAAA" and "skyrim.esm|00aaaa" between a
        // mod and Creation Club content, and an ordinal comparison would mark one and miss its twin.
        private HashSet<string> _existingListKeys = new(StringComparer.OrdinalIgnoreCase);

        public void SetExistingListKeys(IEnumerable<string>? listKeys)
        {
            _existingListKeys = listKeys == null
                ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(listKeys, StringComparer.OrdinalIgnoreCase);

            foreach (var c in SelectedContainers)
                foreach (var lvli in c.LVLiEntries)
                    lvli.AlreadyContainsItem = _existingListKeys.Contains(lvli.Key);
        }

        // Who this selection belongs to - an item, or a preset slot. Supplied as a function rather
        // than as two strings because the name can change while the editor is open, and the window
        // should show what it is called now.
        public Func<(string Key, string Name)>? OwnerInfo { get; set; }

        // One place opens the detail window, for every view that renders these rows. The alternative
        // was the same command on four unrelated view models, kept in step by hand.
        private void OpenLeveledList(LVLiEntryVM entry)
        {
            var owner = OwnerInfo?.Invoke() ?? ("", "");

            View.LeveledListWindow.Show(
                System.Windows.Application.Current?.MainWindow,
                entry,
                owner.Key,
                string.IsNullOrWhiteSpace(owner.Name) ? owner.Key : owner.Name);

            // The window is the only place a list's own properties change, so this is the only place
            // the mark on the row can go stale.
            entry.IsListEdited = LeveledListEditStore.IsEdited(entry.Key);
        }

        // Count belongs here as much as Level does: both end up in the ContainerString, and an
        // amount that never triggers a rebuild is an amount that is never saved and never patched -
        // the exact failure the Level binding already had once.
        private void OnLVLiPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(LVLiEntryVM.Level) ||
                e.PropertyName == nameof(LVLiEntryVM.Count))
            {
                LevelChanged?.Invoke();
            }
        }

        public void LoadFromString(string containerString)
        {
            SelectedContainers.Clear();

            var parsed = ContainerStringParser.Parse(containerString);

            foreach (var entry in parsed)
            {
                var def = _allContainers.FirstOrDefault(c => c.ContainerKey == entry.ContainerKey);
                if (def == null) continue;

                var vm = new ContainerEntryVM(def);
                vm.ApplyPlacements(entry.Placements);
                vm.ToggleSelectedRequested += v => SelectedContainers.Remove(v);
                SelectedContainers.Add(vm);
            }
        }

        public string BuildString()
        {
            return ContainerStringBuilder.Build(SelectedContainers);
        }

        public void ToggleContainer(string containerKey)
        {
            var existing = SelectedContainers.FirstOrDefault(c => c.ContainerKey == containerKey);

            if (existing != null)
            {
                SelectedContainers.Remove(existing);
                return;
            }

            var def = _allContainers.FirstOrDefault(c => c.ContainerKey == containerKey);
            if (def == null) return;
            var vm = new ContainerEntryVM(def);
            vm.ToggleSelectedRequested += v => SelectedContainers.Remove(v);
            SelectedContainers.Add(vm);
        }

        public void Clear()
        {
            SelectedContainers.Clear();
        }
    }


}
