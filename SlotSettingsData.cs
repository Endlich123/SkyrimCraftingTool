using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SkyrimCraftingTool
{
    public class SlotSettingsData
    {
        public bool IsWeapon { get; set; }
        public string SlotName { get; set; }
        public float Cost { get; set; }
        public float Weight { get; set; }
        public float Damage { get; set; }
        public float ArmorRating { get; set; }
        public string Workbench { get; set; }
        public List<string> Vendors { get; set; } = new();
        public List<MaterialEntryData> Materials { get; set; } = new();

        public class MaterialEntryData
        {
            public string Material { get; set; }
            public int Amount { get; set; }
        }
    }

    public class CategorySettings
    {
        public string CategoryName { get; set; }
        public List<SlotSettingsData> Slots { get; set; } = new();
    }
}
