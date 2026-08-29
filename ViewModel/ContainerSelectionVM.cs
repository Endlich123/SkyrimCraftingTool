using Mutagen.Bethesda.Skyrim;
using SkyrimCraftingTool.Model;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
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

        public ContainerSelectionVM(List<ContainerRecord> allContainers)
        {
            _allContainers = allContainers ?? new List<ContainerRecord>();
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
