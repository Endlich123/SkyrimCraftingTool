using System;

namespace SkyrimCraftingTool.ViewModel
{
    // One flag switch.
    //
    // A FLAG IS A BIT IN A NUMBER AND A CHECKBOX IS NOT, so the two are joined here rather than by a
    // converter: the spell's Flags value is a raw dword that also carries bits nothing names, and a
    // binding that turned the whole number into checkboxes and back would quietly drop them.
    public sealed class SpellFlagVM : ViewModelBase
    {
        public SpellFlagVM(string label, int bit, bool isSet)
        {
            Label = label;
            Bit = bit;
            _isSet = isSet;
        }

        public string Label { get; }

        // The bit this switch owns. Only ever one that SkyPatcher can name: setFlags/removeFlags
        // work by name, so a switch for an unnameable bit would be a control that does nothing.
        public int Bit { get; }

        public event Action Changed;

        private bool _isSet;
        public bool IsSet
        {
            get => _isSet;
            set
            {
                if (_isSet == value) return;
                _isSet = value;
                OnPropertyChanged();
                Changed?.Invoke();
            }
        }
    }
}
