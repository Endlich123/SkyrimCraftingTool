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
        public ItemNodeVM Item { get; }
        public string FieldName { get; }

        public SaveRequest(ItemNodeVM item, string fieldName)
        {
            Item = item;
            FieldName = fieldName;
        }
    }
}
