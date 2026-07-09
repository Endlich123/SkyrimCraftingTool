using System.Diagnostics;

namespace SkyrimCraftingTool.Model
{
    public class FormIDDBDebugger
    {
        private readonly FormIDDBHandler _db;

        public FormIDDBDebugger(FormIDDBHandler db)
        {
            _db = db;
        }

        public void RunAllTests()
        {
            Debug.WriteLine("=== FormIDDB Debug Tests ===");

            Test_GetByKey();
            Test_SearchByName();
            Test_SearchByPrefix();
            Test_SearchByPlugin();
            Test_SearchByType_Keyword();
            Test_SearchByType_Material();
            Test_SearchByType_Enchantment();
            Test_CombinedSearch();
            Test_SearchByCombinedKey();

            Debug.WriteLine("=== Debug Tests Finished ===");
        }

        // ---------------------------------------------------------
        //  Test: GetByKey (Plugin|FormID)
        // ---------------------------------------------------------
        private void Test_GetByKey()
        {
            Debug.WriteLine("\n--- Test: GetByKey ---");

            var first = _db.SearchByType("Keyword").FirstOrDefault();
            if (first == null)
            {
                Debug.WriteLine("Keine Keywords gefunden.");
                return;
            }

            var rec = _db.GetByKey(first.Key);

            Debug.WriteLine($"Input Key: {first.Key}");
            Debug.WriteLine($"Result: {rec?.Type} | {rec?.Name} | {rec?.Plugin} | {rec?.FormID}");
        }

        private void Test_SearchByName()
        {
            Debug.WriteLine("\n--- Test: SearchByName ---");

            var first = _db.SearchByType("Material").FirstOrDefault();
            if (first == null)
            {
                Debug.WriteLine("Keine Materials gefunden.");
                return;
            }

            var list = _db.SearchByName(first.Name);

            Debug.WriteLine($"Name: {first.Name}");
            foreach (var r in list.Take(5))
                Debug.WriteLine($"{r.Type} | {r.Name} | {r.Plugin} | {r.FormID} | {r.Key}");
        }

        private void Test_SearchByPrefix()
        {
            Debug.WriteLine("\n--- Test: SearchByPrefix ---");

            var prefix = "Armor";
            var list = _db.SearchByPrefix(prefix);

            Debug.WriteLine($"Prefix: {prefix}");
            foreach (var r in list.Take(5))
                Debug.WriteLine($"{r.Type} | {r.Name} | {r.Plugin} | {r.FormID} | {r.Key}");
        }

        private void Test_SearchByPlugin()
        {
            Debug.WriteLine("\n--- Test: SearchByPlugin ---");

            var plugin = "Skyrim.esm";
            var list = _db.SearchByPlugin(plugin);

            Debug.WriteLine($"Plugin: {plugin}");
            foreach (var r in list.Take(5))
                Debug.WriteLine($"{r.Type} | {r.Name} | {r.FormID} | {r.Key}");
        }

        private void Test_SearchByType_Keyword()
        {
            Debug.WriteLine("\n--- Test: SearchByType (Keyword) ---");

            var list = _db.SearchByType("Keyword");

            foreach (var r in list.Take(5))
                Debug.WriteLine($"{r.Name} | {r.Plugin} | {r.FormID} | {r.Key}");
        }

        private void Test_SearchByType_Material()
        {
            Debug.WriteLine("\n--- Test: SearchByType (Material) ---");

            var list = _db.SearchByType("Material");

            foreach (var r in list.Take(5))
                Debug.WriteLine($"{r.Name} | {r.Plugin} | {r.FormID} | {r.Key}");
        }

        private void Test_SearchByType_Enchantment()
        {
            Debug.WriteLine("\n--- Test: SearchByType (Enchantment) ---");

            var list = _db.SearchByType("Enchantment");

            foreach (var r in list.Take(5))
                Debug.WriteLine($"{r.Name} | {r.Plugin} | {r.FormID} | {r.Key}");
        }

        private void Test_CombinedSearch()
        {
            Debug.WriteLine("\n--- Test: Combined Search ---");

            var list = _db.Search(prefix: "Armor", type: "Keyword");

            Debug.WriteLine("Combined: prefix='Armor', type='Keyword'");
            foreach (var r in list.Take(5))
                Debug.WriteLine($"{r.Name} | {r.Plugin} | {r.FormID} | {r.Key}");
        }

        private void Test_SearchByCombinedKey()
        {
            Debug.WriteLine("\n--- Test: Search by Combined Key ---");

            var first = _db.SearchByType("Enchantment").FirstOrDefault();
            if (first == null)
            {
                Debug.WriteLine("Keine Enchantments gefunden.");
                return;
            }

            var rec = _db.Search(key: first.Key).FirstOrDefault();

            Debug.WriteLine($"Input Key: {first.Key}");
            Debug.WriteLine($"Result: {rec?.Type} | {rec?.Name} | {rec?.Plugin} | {rec?.FormID}");
        }
    }
}
