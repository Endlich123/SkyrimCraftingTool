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
        private static readonly string BasePath =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                         "SkyrimCraftingTool", "categories");

        public static CategorySettings LoadCategory(string category)
        {
            Directory.CreateDirectory(BasePath);
            var file = Path.Combine(BasePath, $"{category}.json");

            if (!File.Exists(file))
                return new CategorySettings { CategoryName = category };

            var json = File.ReadAllText(file);
            return JsonSerializer.Deserialize<CategorySettings>(json)
                   ?? new CategorySettings { CategoryName = category };
        }

        public static void SaveCategory(CategorySettings data)
        {
            Directory.CreateDirectory(BasePath);
            var file = Path.Combine(BasePath, $"{data.CategoryName}.json");

            var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(file, json);
        }
    }
}
