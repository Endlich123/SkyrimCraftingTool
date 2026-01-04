using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;

namespace SkyrimCraftingTool
{
    public class PluginInfo : INotifyPropertyChanged
    {
        public string EspName { get; set; }
        public string EspPath { get; set; }
        public string ItemName { get; set; }
        public string FormKey { get; set; }
        public int ItemValue { get; set; }
        public float ItemWeight { get; set; }
        public List<string> Vendors { get; set; } = new();
        public string Workbench { get; set; }
        public Dictionary<string, int> Materials { get; set; } = new();
        public int ArmorRating { get; set; }
        public string ArmorSlot { get; set; }
        public int Damage { get; set; }
        public string WeaponType { get; set; }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

}
