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
        // plugin|masterplugin|formid
        public static string BuildItemKey(ModKey plugin, FormKey formKey)
        {
            string pluginName = plugin.FileName;
            string masterName = formKey.ModKey.FileName;
            string id = formKey.ID.ToString("X6");

            return $"{pluginName}|{masterName}|{id}";
        }

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

        public static string ToMasterKey(string itemKey)
        {
            var parts = itemKey.Split('|');
            string master = parts[1];
            string id = parts[2];
            return $"{master}|{id}";
        }

        public static string GetPluginFromItemKey(string itemKey) => itemKey.Split('|')[0];


        public static bool IsOverride(string plugin, string master)
        {
            return !plugin.Equals(master, StringComparison.OrdinalIgnoreCase);
        }
    }

}
