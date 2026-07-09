using System;
using System.Windows.Input;

namespace SkyrimCraftingTool.ViewModel
{
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

        // Silent setter: keine Events, kein SelectionChanged
        public void SetSelectedSilent(bool value)
        {
            _isSelected = value;
            OnPropertyChanged(nameof(IsSelected));
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
