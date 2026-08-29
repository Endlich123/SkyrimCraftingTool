using Mutagen.Bethesda.Skyrim;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Policy;
using System.Text;
using System.Threading.Tasks;

namespace SkyrimCraftingTool.Model
{
    public class MagicEffectsRecords
    {
        public string Key { get; set; } = "";
        public string Name { get; set; } = "";
        public string EditorID { get; set; } = "";
        public bool HasMagnitude { get; set; }
        public bool HasDuration { get; set; }
        public bool HasArea { get; set; }

        //Type
        public string CastType { get; set; }
        public string TargetType { get; set; }

    }
}
