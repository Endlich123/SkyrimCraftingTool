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
                    }
        }

        private void OnLVLiPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(LVLiEntryVM.Level))
                LevelChanged?.Invoke();
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
                vm.ApplyLevels(entry.Levels);
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
