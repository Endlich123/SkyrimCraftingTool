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

        // How the effect is written in the "add effect" picker: "EditorID | Name", whichever half
        // exists, and the key when neither does. Same shape as MainContentVM.EnchantmentLabel, and
        // for the same reason - names repeat across records, EditorIDs tell the variants apart.
        //
        // The fallback is not theoretical: of 3,875 scanned effects, 9 carry no name and exactly one
        // (MysticismMagic.esp|3A3E12) carries neither, which showed up in the picker as an empty
        // row. Reported by the user.
        public string Label
        {
            get
            {
                var editorId = (EditorID ?? "").Trim();
                var name = (Name ?? "").Trim();

                if (editorId.Length > 0 && name.Length > 0) return $"{editorId} | {name}";
                if (editorId.Length > 0) return editorId;
                if (name.Length > 0) return name;

                return Key;
            }
        }
    }
}
