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

            foreach (var lvli in def.LVLIEntries)
            {
                LVLiEntries.Add(new LVLiEntryVM(lvli));
            }
        }

        public void ApplyLevels(Dictionary<string, int> levels)
        {
            foreach (var lvli in LVLiEntries)
            {
                if (levels.TryGetValue(lvli.Key, out int lvl))
                    lvli.Level = lvl;
                else
                    lvli.Level = 0;
            }
        }
    }


}
