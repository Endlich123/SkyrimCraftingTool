using SkyrimCraftingTool.Model;
using SkyrimCraftingTool.ViewModel;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SkyrimCraftingTool.Services.SavePipline
{
    public sealed class SaveRequest
    {
        // itemView
        public ItemNodeVM Item { get; }
        public string FieldName { get; }

        //EnchantmentView
        public EnchantmentRecord Enchantment { get; set; }
        public List<EnchantmentEffectViewModel> Effects { get; set; }
        public List<string> SelectedWornRestrictionKeywords { get; set; }

        public SaveRequest(ItemNodeVM item, string fieldName)
        {
            Item = item;
            FieldName = fieldName;
        }
    }
}
