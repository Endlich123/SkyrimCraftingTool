using Mutagen.Bethesda.Plugins;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SkyrimCraftingTool.Services
{
    public static class KeyFactory
    {
        // Synthetic pseudo-plugin name user-created COBJ recipes (Original=0, see
        // ItemDBHandler.CreateNewCOBJRecordForItem) are keyed under. It never corresponds to a real
        // file on disk, so it must never be passed to the actual plugin-parsing pipeline
        // (ItemDBHandler/FormIDDBHandler.PutIntoDataBank) — only added to the plugin list used for
        // cache/tree building, so user recipes stay visible. Kept as one shared constant (rather than
        // duplicated as a literal) so every call site that needs to add it to a plugin list agrees.
        public const string UserPluginName = "SkyrimCraftingTool.esp";

        // masterplugin|formid
        public static string BuildMasterKey(FormKey formKey)
        {
            string masterName = formKey.ModKey.FileName;
            string id = formKey.ID.ToString("X6");

            return $"{masterName}|{id}";
        }

        public static (string plugin, string master, string formID) SplitItemKey(string key)
        {
            var parts = key.Split('|');
            return (parts[0], parts[1], parts[2]);
        }

        public static (string master, string formID) SplitMasterKey(string key)
        {
            var parts = key.Split('|');
            return (parts[0], parts[1]);
        }

        // "master|formID" -> Mutagen FormKey
        public static FormKey ParseFormKey(string masterKey)
        {
            if (string.IsNullOrWhiteSpace(masterKey))
                return default;

            var (master, formID) = SplitMasterKey(masterKey);
            return FormKey.Factory($"{formID}:{master}");
        }
    }

}
