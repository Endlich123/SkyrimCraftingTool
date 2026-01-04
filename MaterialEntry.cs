using Mutagen.Bethesda.Plugins;
using System;
using System.Collections.Generic;
using System.ComponentModel;

namespace SkyrimCraftingTool
{
    public class MaterialEntry : INotifyPropertyChanged
    {
        private string _material;
        public string Material
        {
            get => _material;
            set
            {
                if (_material != value)
                {
                    Console.WriteLine($"MaterialEntry.Material SET: '{value}'");
                    _material = value;
                    OnPropertyChanged(nameof(Material));
                }
            }
        }

        private int _amount;
        public int Amount
        {
            get => _amount;
            set
            {
                if (_amount != value)
                {
                    _amount = value;
                    OnPropertyChanged(nameof(Amount));
                }
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged(string name)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

}
