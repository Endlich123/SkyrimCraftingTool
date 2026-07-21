using SkyrimCraftingTool.ViewModel;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SkyrimCraftingTool.Services.SavePipline
{
    public sealed class CraftingSaveHandler : ISaveHandler
    {
        private readonly IItemService _itemService;
        private readonly ICacheManager _cache;

        public CraftingSaveHandler(IItemService itemService, ICacheManager cache)
        {
            _itemService = itemService;
            _cache = cache;
        }

        public bool CanHandle(SaveRequest r) =>
            r.FieldName is nameof(ItemNodeVM.CraftingIngredients)
            or nameof(ItemNodeVM.CraftingWorkbenchKey)
            or nameof(ItemNodeVM.CraftingPerkKey);

        public Task HandleAsync(SaveRequest r)
        {
            var item = r.Item;

            if (!item.HasCraftingRecipe)
                item.CreateCraftingRecipe();

            var rec = item.CraftingRecipe.Record;

            switch (r.FieldName)
            {
                case nameof(ItemNodeVM.CraftingIngredients):
                    rec.IngredientKeys = item.CraftingIngredients
                        .Select(i => $"{i.Key}*{i.Count}")
                        .ToList();
                    break;

                case nameof(ItemNodeVM.CraftingWorkbenchKey):
                    rec.WorkbenchKeywordKey = item.CraftingWorkbenchKey;
                    break;

                case nameof(ItemNodeVM.CraftingPerkKey):
                    rec.PerkKey = item.CraftingPerkKey;
                    break;
            }

            _itemService.SaveCOBJ(rec);
            _cache.UpdateRecipe(rec);

            return Task.CompletedTask;
        }
    }
}
