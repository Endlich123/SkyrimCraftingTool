using Noggog;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace SkyrimCraftingTool
{
    public static class JsonTranslator
    {
        public static List<PluginInfo> Load(string path)
        {
            if (!File.Exists(path))
                return new List<PluginInfo>();

            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<List<PluginInfo>>(json)
                   ?? new List<PluginInfo>();
        }

        public static void Save(string path, List<PluginInfo> items)
        {
            var json = JsonSerializer.Serialize(items, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json);
        }
    }

}
