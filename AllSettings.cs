using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using static SkyrimCraftingTool.GlobalState;

namespace SkyrimCraftingTool
{
    public class AllSettings
    {
        public Dictionary<string, SlotSettingsData> Settings { get; set; }
            = new();
    }

}
