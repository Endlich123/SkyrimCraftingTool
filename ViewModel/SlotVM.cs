using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SkyrimCraftingTool.ViewModel
{
    using SkyrimCraftingTool.ViewModel;
    using System.Windows.Input;

    public class SlotVM : ViewModelBase
    {
        public string Name { get; }
        public int Bit { get; }
        public uint Flag => 1u << Bit;

        private bool _isSelected;
        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (SetProperty(ref _isSelected, value))
                    SelectionChanged?.Invoke(this, EventArgs.Empty);
            }
        }
        public string DisplayName => $"{Name} (Slot {Bit + 30})";
        public event EventHandler SelectionChanged;

        public ICommand ToggleSelectedCommand { get; }

        public SlotVM(string name, int bit)
        {
            Name = name;
            Bit = bit;

            ToggleSelectedCommand = new RelayCommand(() => IsSelected = !IsSelected);
        }
    }
}
