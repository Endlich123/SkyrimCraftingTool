using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace SkyrimCraftingTool
{
        public static class SettingsStorage
        {
            private static readonly string FilePath =
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                             "SkyrimCraftingTool",
                             "slotsettings.json");

            public static AllSettings Load()
            {
                if (!File.Exists(FilePath))
                    return new AllSettings();

                var json = File.ReadAllText(FilePath);
                return JsonSerializer.Deserialize<AllSettings>(json)
                       ?? new AllSettings();
            }

            public static void Save(AllSettings data)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);

                var json = JsonSerializer.Serialize(
                    data,
                    new JsonSerializerOptions { WriteIndented = true }
                );

                File.WriteAllText(FilePath, json);
            }
        }
}
