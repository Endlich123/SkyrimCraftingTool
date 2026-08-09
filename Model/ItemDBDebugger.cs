using System.Diagnostics;

namespace SkyrimCraftingTool.Model
{
    public class ItemDBDebugger
    {
        private readonly ItemDBHandler _db;

        public ItemDBDebugger(ItemDBHandler db)
        {
            _db = db;
        }

        public void RunAllTests()
        {
            Debug.WriteLine("=== ItemDB Debug Tests ===");

            Test_ArmorByKey();
            Test_WeaponByKey();
            Test_COBJByKey();

            Test_SearchArmorByName();
            Test_SearchWeaponsByName();
            Test_SearchCOBJByName();

            Test_SearchArmorByKeyword();
            Test_SearchWeaponsByKeyword();

            Test_SearchCOBJByWorkbench();
            Test_SearchCOBJByIngredient();

            Test_COBJByCreatedItem();
            Test_COBJ_ResolveCreatedItem();

            Test_COBJ_ForSpecificItem("[Predator] Dominatrix 2025.esp|000002");
            Test_FullItemInfo("[Predator] Dominatrix 2025.esp|000002");



            Debug.WriteLine("=== Debug Tests Finished ===");
        }

        // ============================
        //        KEY LOOKUPS
        // ============================

        private void Test_ArmorByKey()
        {
            Debug.WriteLine("\n--- Test: GetArmor (Key) ---");

            var first = _db.SearchArmorByName("").FirstOrDefault();
            if (first == null)
            {
                Debug.WriteLine("Keine Armor‑Einträge gefunden.");
                return;
            }

            var rec = _db.GetArmor(first.Key);

            Debug.WriteLine($"Input Key: {first.Key}");
            Debug.WriteLine($"Result: {rec?.Name} | {rec?.Key}");
        }

        private void Test_WeaponByKey()
        {
            Debug.WriteLine("\n--- Test: GetWeapon (Key) ---");

            var first = _db.SearchWeaponsByName("").FirstOrDefault();
            if (first == null)
            {
                Debug.WriteLine("Keine Weapon‑Einträge gefunden.");
                return;
            }

            var rec = _db.GetWeapon(first.Key);

            Debug.WriteLine($"Input Key: {first.Key}");
            Debug.WriteLine($"Result: {rec?.Name} | {rec?.Key}");
        }

        private void Test_COBJByKey()
        {
            Debug.WriteLine("\n--- Test: GetCOBJ (Key) ---");

            var first = _db.SearchCOBJByName("").FirstOrDefault();
            if (first == null)
            {
                Debug.WriteLine("Keine COBJ‑Einträge gefunden.");
                return;
            }

            var rec = _db.GetCOBJ(first.Key);

            Debug.WriteLine($"Input Key: {first.Key}");
            Debug.WriteLine($"Result: {rec?.Name} | {rec?.Key}");
        }

        // ============================
        //        NAME SEARCH
        // ============================

        private void Test_SearchArmorByName()
        {
            Debug.WriteLine("\n--- Test: SearchArmorByName ---");

            var list = _db.SearchArmorByName("Iron");

            foreach (var r in list.Take(5))
                Debug.WriteLine($"{r.Name} | {r.Key}");
        }

        private void Test_SearchWeaponsByName()
        {
            Debug.WriteLine("\n--- Test: SearchWeaponsByName ---");

            var list = _db.SearchWeaponsByName("Iron");

            foreach (var r in list.Take(5))
                Debug.WriteLine($"{r.Name} | {r.Key}");
        }

        private void Test_SearchCOBJByName()
        {
            Debug.WriteLine("\n--- Test: SearchCOBJByName ---");

            var list = _db.SearchCOBJByName("Temper");

            foreach (var r in list.Take(5))
                Debug.WriteLine($"{r.Name} | {r.Key}");
        }

        // ============================
        //        KEYWORD SEARCH
        // ============================

        private void Test_SearchArmorByKeyword()
        {
            Debug.WriteLine("\n--- Test: SearchArmorByKeyword ---");

            var first = _db.SearchArmorByName("").FirstOrDefault();
            if (first == null || first.Keywords.Count == 0)
            {
                Debug.WriteLine("Keine Armor‑Keywords gefunden.");
                return;
            }

            string keyword = first.Keywords.First();

            var list = _db.SearchArmorByKeyword(keyword);

            Debug.WriteLine($"Keyword: {keyword}");
            foreach (var r in list.Take(5))
                Debug.WriteLine($"{r.Name} | {r.Key}");
        }

        private void Test_SearchWeaponsByKeyword()
        {
            Debug.WriteLine("\n--- Test: SearchWeaponsByKeyword ---");

            var first = _db.SearchWeaponsByName("").FirstOrDefault();
            if (first == null || first.Keywords.Count == 0)
            {
                Debug.WriteLine("Keine Weapon‑Keywords gefunden.");
                return;
            }

            string keyword = first.Keywords.First();

            var list = _db.SearchWeaponsByKeyword(keyword);

            Debug.WriteLine($"Keyword: {keyword}");
            foreach (var r in list.Take(5))
                Debug.WriteLine($"{r.Name} | {r.Key}");
        }

        // ============================
        //        WORKBENCH SEARCH
        // ============================

        private void Test_SearchCOBJByWorkbench()
        {
            Debug.WriteLine("\n--- Test: SearchCOBJByWorkbench ---");

            var first = _db.SearchCOBJByName("").FirstOrDefault();
            if (first == null || string.IsNullOrWhiteSpace(first.WorkbenchKeywordKey))
            {
                Debug.WriteLine("Keine Workbench‑Keywords gefunden.");
                return;
            }

            string workbench = first.WorkbenchKeywordKey;

            var list = _db.SearchCOBJByWorkbench(workbench);

            Debug.WriteLine($"Workbench: {workbench}");
            foreach (var r in list.Take(5))
                Debug.WriteLine($"{r.Name} | {r.Key}");
        }

        // ============================
        //        INGREDIENT SEARCH
        // ============================

        private void Test_SearchCOBJByIngredient()
        {
            Debug.WriteLine("\n--- Test: SearchCOBJByIngredient ---");

            var first = _db.SearchCOBJByName("").FirstOrDefault();
            if (first == null || first.IngredientKeys.Count == 0)
            {
                Debug.WriteLine("Keine Ingredients gefunden.");
                return;
            }

            // IngredientKeys = "Plugin|FormID*Count"
            string ingredientKey = first.IngredientKeys.First().Split('*')[0];

            var list = _db.SearchCOBJByIngredient(ingredientKey);

            Debug.WriteLine($"Ingredient: {ingredientKey}");
            foreach (var r in list.Take(5))
                Debug.WriteLine($"{r.Name} | {r.Key}");
        }

        // ============================
        //        CREATED ITEM LOOKUP
        // ============================

        private void Test_COBJByCreatedItem()
        {
            Debug.WriteLine("\n--- Test: GetCOBJByCreatedItem ---");

            var first = _db.SearchCOBJByName("").FirstOrDefault();
            if (first == null)
            {
                Debug.WriteLine("Keine COBJ‑Einträge gefunden.");
                return;
            }

            var list = _db.GetCOBJByCreatedItem(first.CreatedItemKey);

            Debug.WriteLine($"CreatedItem: {first.CreatedItemKey}");
            foreach (var r in list.Take(5))
                Debug.WriteLine($"{r.Name} | {r.Key}");
        }

        // ============================
        //        COBJ from which ITEM LOOKUP
        // ============================

        private void Test_COBJ_ResolveCreatedItem()
        {
            Debug.WriteLine("\n--- Test: Resolve COBJ → Created Item ---");

            var first = _db.SearchCOBJByName("").FirstOrDefault();
            if (first == null)
            {
                Debug.WriteLine("Keine COBJ‑Einträge gefunden.");
                return;
            }

            Debug.WriteLine($"COBJ: {first.Name} | {first.Key}");
            Debug.WriteLine($"CreatedItemKey: {first.CreatedItemKey}");

            // Armor?
            var armor = _db.GetArmor(first.CreatedItemKey);
            if (armor != null)
            {
                Debug.WriteLine($"→ Created Item ist ARMOR: {armor.Name} | {armor.Key}");
                return;
            }

            // Weapon?
            var weapon = _db.GetWeapon(first.CreatedItemKey);
            if (weapon != null)
            {
                Debug.WriteLine($"→ Created Item ist WEAPON: {weapon.Name} | {weapon.Key}");
                return;
            }

            Debug.WriteLine("→ Created Item konnte nicht als Armor oder Weapon gefunden werden.");
        }

        public void Test_COBJ_ForSpecificItem(string itemKey)
        {
            Debug.WriteLine("\n--- Test: COBJ for specific item ---");
            Debug.WriteLine($"ItemKey: {itemKey}");

            var list = _db.GetCOBJByCreatedItem(itemKey);

            if (list.Count == 0)
            {
                Debug.WriteLine("→ Keine COBJ‑Rezepte gefunden.");
                return;
            }

            foreach (var cobj in list)
            {
                Debug.WriteLine($"COBJ: {cobj.Name} | {cobj.Key}");

                // Workbench
                Debug.WriteLine($"  Workbench: {cobj.WorkbenchKeywordKey}");

                // Ingredients
                Debug.WriteLine("  Ingredients:");
                foreach (var ing in cobj.IngredientKeys)
                {
                    // Format: Plugin|FormID*Count
                    var parts = ing.Split('*');
                    string ingKey = parts[0];
                    string count = parts.Length > 1 ? parts[1] : "1";

                    Debug.WriteLine($"    {ingKey} x{count}");
                    var ingo = ItemDBHandler.ParseIngredient(ing);
                }
            }
        }

        public void Test_FullItemInfo(string itemKey)
        {
            Debug.WriteLine("\n=== Full Item Info ===");
            Debug.WriteLine($"ItemKey: {itemKey}");

            // ============================
            // 1. Item selbst finden
            // ============================

            var armor = _db.GetArmor(itemKey);
            var weapon = _db.GetWeapon(itemKey);

            if (armor == null && weapon == null)
            {
                Debug.WriteLine("→ Item wurde nicht als Armor oder Weapon gefunden.");
            }
            else if (armor != null)
            {
                Debug.WriteLine("\n--- ITEM (ARMOR) ---");
                Debug.WriteLine($"Name: {armor.Name}");
                Debug.WriteLine($"Key: {armor.Key}");
                Debug.WriteLine($"Weight: {armor.Weight}");
                Debug.WriteLine($"Value: {armor.Value}");
                Debug.WriteLine($"ArmorRating: {armor.ArmorRating}");

                Debug.WriteLine("Keywords:");
                foreach (var kw in armor.Keywords)
                    Debug.WriteLine($"  {kw}");
            }
            else if (weapon != null)
            {
                Debug.WriteLine("\n--- ITEM (WEAPON) ---");
                Debug.WriteLine($"Name: {weapon.Name}");
                Debug.WriteLine($"Key: {weapon.Key}");
                Debug.WriteLine($"Weight: {weapon.Weight}");
                Debug.WriteLine($"Value: {weapon.Value}");
                Debug.WriteLine($"Damage: {weapon.Damage}");

                Debug.WriteLine("Keywords:");
                foreach (var kw in weapon.Keywords)
                    Debug.WriteLine($"  {kw}");
            }

            // ============================
            // 2. Rezepte finden
            // ============================

            Debug.WriteLine("\n--- COBJ Recipes for this Item ---");

            var recipes = _db.GetCOBJByCreatedItem(itemKey);

            if (recipes.Count == 0)
            {
                Debug.WriteLine("→ Keine Rezepte gefunden.");
                return;
            }

            foreach (var cobj in recipes)
            {
                Debug.WriteLine($"\nCOBJ: {cobj.Name} | {cobj.Key}");

                // Workbench
                Debug.WriteLine($"  Workbench: {cobj.WorkbenchKeywordKey}");

                // Ingredients
                Debug.WriteLine("  Ingredients:");
                foreach (var ingRaw in cobj.IngredientKeys)
                {
                    var ing = ItemDBHandler.ParseIngredient(ingRaw);

                    Debug.WriteLine($"    {ing.Plugin}|{ing.FormID} x{ing.Count}");

                    // Optional: Ingredient‑Name aus FormID‑DB holen
                    // var item = formIDDB.GetItem(ing.Plugin, ing.FormID);
                    // Debug.WriteLine($"      → {item?.Name ?? "Unbekannt"}");
                }
            }
        }


    }
}
