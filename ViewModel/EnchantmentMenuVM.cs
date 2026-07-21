using SkyrimCraftingTool.Model;
using SkyrimCraftingTool.Services;
using System.Collections.ObjectModel;
using System.Linq;

namespace SkyrimCraftingTool.ViewModel
{
    public class EnchantmentMenuVM : ViewModelBase
    {
        private readonly ItemDBHandler _handler;
        private readonly IKeywordService _keywordService;
        public ObservableCollection<EnchantmentEffectViewModel> EffectVMs { get; } = new();

        public ObservableCollection<EnchantmentTreeNode> TreeItems { get; } = new();
        public ObservableCollection<EnchantmentRecord> Enchantments { get; } = new();

        private EnchantmentRecord _selectedEnchantment;

        // MagicEffects loaded once
        public List<MagicEffectsRecords> AllMagicEffects { get; private set; } = new();

        public EnchantmentRecord SelectedEnchantment
        {
            get => _selectedEnchantment;
            set
            {
                if (SetProperty(ref _selectedEnchantment, value))
                {
                    UpdateKeywordSelection();
                    OnPropertyChanged(nameof(KeywordItems));

                    if (_selectedEnchantment != null)
                    {
                        // Inject MagicEffects into each effect
                        EffectVMs.Clear();

                        foreach (var eff in _selectedEnchantment.Effects)
                        {
                            var category = EnchantmentCategoryHelper.Classify(_selectedEnchantment);

                            EffectVMs.Add(new EnchantmentEffectViewModel(
                                eff,
                                AllMagicEffects,
                                category
                            ));
                        }
                    }
                }
            }
        }

        // Constructor
        public EnchantmentMenuVM(ItemDBHandler handler, IKeywordService keywordService)
        {
            _handler = handler;
            _keywordService = keywordService;

            // Load MagicEffects ONCE
            AllMagicEffects = _handler.SearchByType("MagicEffect")
                .Cast<MagicEffectsRecords>()
                .OrderBy(m => m.Name)
                .ToList();

            BuildEnchantmentTree();
        }



        // --- Keyword UI state ---
        private bool _showAllKeywords;
        public bool ShowAllKeywords
        {
            get => _showAllKeywords;
            set
            {
                if (SetProperty(ref _showAllKeywords, value))
                    OnPropertyChanged(nameof(KeywordItems));
            }
        }

        private string _currentSearch = string.Empty;
        public string CurrentSearch
        {
            get => _currentSearch;
            set
            {
                if (SetProperty(ref _currentSearch, value))
                    OnPropertyChanged(nameof(KeywordItems));
            }
        }

        public IEnumerable<KeywordSelectionVM> KeywordItems
        {
            get
            {
                if (SelectedEnchantment == null)
                    return Enumerable.Empty<KeywordSelectionVM>();

                var category = EnchantmentCategoryHelper.Classify(SelectedEnchantment);

                IEnumerable<KeywordSelectionVM> baseList;

                if (ShowAllKeywords)
                    baseList = _keywordService.GlobalKeywords;
                else
                    baseList = _keywordService.FilterByEnchantmentCategory(category);

                if (!string.IsNullOrWhiteSpace(CurrentSearch))
                    baseList = baseList.Where(k =>
                        k.Name.Contains(CurrentSearch, StringComparison.OrdinalIgnoreCase));

                return baseList;
            }
        }


        private void UpdateKeywordSelection()
        {
            var selectedKeys = SelectedEnchantment?.WornRestrictionKeywords?.ToHashSet()
                               ?? new HashSet<string>();

            foreach (var kw in _keywordService.GlobalKeywords)
                kw.IsSelected = selectedKeys.Contains(kw.Key);
        }

        // --- Build Tree ---
        public void BuildEnchantmentTree()
        {
            TreeItems.Clear();

            var enchantments = _handler.GetAllEnchantments();

            var grouped = enchantments
                .GroupBy(e => e.Plugin)
                .OrderBy(g => g.Key);

            foreach (var pluginGroup in grouped)
            {
                var pluginNode = new EnchantmentTreeNode
                {
                    DisplayName = pluginGroup.Key
                };

                var weaponNode = new EnchantmentTreeNode { DisplayName = "Weapon Enchantments" };
                var armorNode = new EnchantmentTreeNode { DisplayName = "Armor Enchantments" };
                var otherNode = new EnchantmentTreeNode { DisplayName = "Other" };

                foreach (var ench in pluginGroup.OrderBy(e => e.Name))
                {
                    var node = new EnchantmentTreeNode
                    {
                        DisplayName = ench.EditorID,
                        Enchantment = ench
                    };

                    switch (EnchantmentCategoryHelper.Classify(ench))
                    {
                        case EnchantmentCategory.Weapon:
                            weaponNode.Children.Add(node);
                            break;

                        case EnchantmentCategory.Armor:
                            armorNode.Children.Add(node);
                            break;

                        default:
                            otherNode.Children.Add(node);
                            break;
                    }
                }

                if (weaponNode.Children.Any()) pluginNode.Children.Add(weaponNode);
                if (armorNode.Children.Any()) pluginNode.Children.Add(armorNode);
                if (otherNode.Children.Any()) pluginNode.Children.Add(otherNode);

                TreeItems.Add(pluginNode);
            }
        }
    }
}
