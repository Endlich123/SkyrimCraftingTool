using SkyrimCraftingTool.Model;
using SkyrimCraftingTool.ViewModel;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace SkyrimCraftingTool.Services
{
    public class KeywordService : IKeywordService
    {
        public ObservableCollection<KeywordSelectionVM> GlobalKeywords { get; } = new();

        public void InitializeFrom(List<FormIDRecord> keywords)
        {
            GlobalKeywords.Clear();
            if (keywords == null) return;

            foreach (var kw in keywords.OrderBy(k => k.Name))
            {
                GlobalKeywords.Add(new KeywordSelectionVM(
                    key: kw.Key,
                    name: kw.Name,
                    isSelected: false,
                    onSelectedChanged: HandleKeywordSelectionChanged
                ));
            }
        }

        public void ApplySelectionFromItem(ItemNodeVM item)
        {
            if (item == null) return;

            var set = item.SelectedKeywords
                .Select(k => k.Key)
                .ToHashSet();

            foreach (var kw in GlobalKeywords)
                kw.IsSelected = set.Contains(kw.Key);
        }

        public IEnumerable<KeywordSelectionVM> FilterByItemType(ItemNodeVM item)
        {
            if (item == null)
                return GlobalKeywords;

            if (item.IsArmor)
                return GlobalKeywords.Where(k =>
                    k.Name.StartsWith("Armor", StringComparison.OrdinalIgnoreCase) ||
                    k.Name.StartsWith("Clothing", StringComparison.OrdinalIgnoreCase) ||
                    k.Name.StartsWith("Jewelry", StringComparison.OrdinalIgnoreCase) ||
                    k.Name.StartsWith("VendorItemArmor", StringComparison.OrdinalIgnoreCase) ||
                    k.Name.StartsWith("Material", StringComparison.OrdinalIgnoreCase));

            if (item.IsWeapon)
                return GlobalKeywords.Where(k =>
                    k.Name.StartsWith("Weap", StringComparison.OrdinalIgnoreCase) ||
                    k.Name.StartsWith("Weapon", StringComparison.OrdinalIgnoreCase) ||
                    k.Name.StartsWith("VendorItemWeapon", StringComparison.OrdinalIgnoreCase) ||
                    k.Name.StartsWith("Material", StringComparison.OrdinalIgnoreCase) ||
                    k.Name.StartsWith("DamageType", StringComparison.OrdinalIgnoreCase));

            return GlobalKeywords;
        }

        public IEnumerable<KeywordSelectionVM> FilterByEnchantmentCategory(EnchantmentCategory category)
        {
            if (category == EnchantmentCategory.Armor)
                return GlobalKeywords.Where(k =>
                    k.Name.StartsWith("Armor", StringComparison.OrdinalIgnoreCase) ||
                    k.Name.StartsWith("Clothing", StringComparison.OrdinalIgnoreCase) ||
                    k.Name.StartsWith("Jewelry", StringComparison.OrdinalIgnoreCase) ||
                    k.Name.StartsWith("VendorItemArmor", StringComparison.OrdinalIgnoreCase) ||
                    k.Name.StartsWith("Material", StringComparison.OrdinalIgnoreCase));

            if (category == EnchantmentCategory.Weapon)
                return GlobalKeywords.Where(k =>
                    k.Name.StartsWith("Weap", StringComparison.OrdinalIgnoreCase) ||
                    k.Name.StartsWith("Weapon", StringComparison.OrdinalIgnoreCase) ||
                    k.Name.StartsWith("VendorItemWeapon", StringComparison.OrdinalIgnoreCase) ||
                    k.Name.StartsWith("Material", StringComparison.OrdinalIgnoreCase) ||
                    k.Name.StartsWith("DamageType", StringComparison.OrdinalIgnoreCase));

            // Other → show all
            return GlobalKeywords.Where(k =>
                    k.Name.StartsWith("Armor", StringComparison.OrdinalIgnoreCase) ||
                    k.Name.StartsWith("Clothing", StringComparison.OrdinalIgnoreCase) ||
                    k.Name.StartsWith("Jewelry", StringComparison.OrdinalIgnoreCase) ||
                    k.Name.StartsWith("VendorItemArmor", StringComparison.OrdinalIgnoreCase) ||
                    k.Name.StartsWith("Material", StringComparison.OrdinalIgnoreCase) ||
                    k.Name.StartsWith("Weap", StringComparison.OrdinalIgnoreCase) ||
                    k.Name.StartsWith("Weapon", StringComparison.OrdinalIgnoreCase) ||
                    k.Name.StartsWith("VendorItemWeapon", StringComparison.OrdinalIgnoreCase) ||
                    k.Name.StartsWith("Material", StringComparison.OrdinalIgnoreCase) ||
                    k.Name.StartsWith("DamageType", StringComparison.OrdinalIgnoreCase));
        
        }


        public IEnumerable<KeywordSelectionVM> FilterBySearch(string search)
        {
            if (string.IsNullOrWhiteSpace(search))
                return GlobalKeywords;

            search = search.ToLowerInvariant();
            return GlobalKeywords.Where(k => k.Name.ToLowerInvariant().Contains(search));
        }

        // rules
        public void HandleKeywordSelectionChanged(KeywordSelectionVM changedKeyword)
        {
            if (changedKeyword.IsSelected && IsWeaponType(changedKeyword))
            {
                foreach (var kw in GlobalKeywords)
                {
                    // Alle ANDEREN Waffentypen abwählen
                    if (kw != changedKeyword && IsWeaponType(kw))
                    {
                        kw.IsSelected = false;
                    }
                }
            }
        }

        private bool IsWeaponType(KeywordSelectionVM kw)
        {
            return kw.Name.StartsWith("WeapType", StringComparison.OrdinalIgnoreCase);
        }

    }
}
