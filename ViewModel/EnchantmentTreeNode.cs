using SkyrimCraftingTool.Model;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SkyrimCraftingTool.ViewModel
{
    public class EnchantmentTreeNode
    {
        public string DisplayName { get; set; }
        public ObservableCollection<EnchantmentTreeNode> Children { get; set; }
            = new();

        public EnchantmentRecord Enchantment { get; set; } // null = Ordner
    }

}
