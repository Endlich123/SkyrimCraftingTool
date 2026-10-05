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

        // BreakdownSection: WHICH recipe changed.
        //
        // Item + FieldName is enough for every other edit, because each one names exactly one
        // thing on the item. An item can have SEVERAL breakdown recipes (measured: 15 armours have
        // one at both benches, 143 have more than one consuming recipe), so the field name alone
        // cannot say which row the user just edited.
        public COBJRecord Recipe { get; set; }

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
