using SkyrimCraftingTool.Model;
using System.Collections.ObjectModel;
using System.Data;

namespace SkyrimCraftingTool.ViewModel
{
    public class EnchantmentMenuVM : ViewModelBase
    {
        private readonly ItemDBHandler _handler;

        public ObservableCollection<EnchantmentRecord> Enchantments { get; } = new();
        private EnchantmentRecord _selectedEnchantment;

        public ObservableCollection<EnchantmentTreeNode> TreeItems { get; } = new();


        public EnchantmentRecord SelectedEnchantment
        {
            get => _selectedEnchantment;
            set => SetProperty(ref _selectedEnchantment, value);
        }

        public EnchantmentMenuVM(ItemDBHandler handler)
        {
            _handler = handler;

            BuildEnchantmentTree();
        }

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
                        DisplayName = ench.Name,
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
