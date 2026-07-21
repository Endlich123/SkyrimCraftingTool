using SkyrimCraftingTool.ViewModel;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SkyrimCraftingTool.Services.SavePipline
{
    public sealed class TemperSaveHandler : ISaveHandler
    {
        private readonly IItemService _itemService;
        private readonly ICacheManager _cache;

        public TemperSaveHandler(IItemService itemService, ICacheManager cache)
        {
            _itemService = itemService;
            _cache = cache;
        }

        public bool CanHandle(SaveRequest r) =>
            r.FieldName is nameof(ItemNodeVM.TemperIngredients)
            or nameof(ItemNodeVM.TemperWorkbenchKey)
            or nameof(ItemNodeVM.TemperPerkKey);

        public Task HandleAsync(SaveRequest r)
        {
            var item = r.Item;

            if (!item.HasTemperRecipe)
                item.CreateTemperRecipe();

            var rec = item.TemperRecipe.Record;

            switch (r.FieldName)
            {
                case nameof(ItemNodeVM.TemperIngredients):
                    rec.IngredientKeys = item.TemperIngredients
                        .Select(i => $"{i.Key}*{i.Count}")
                        .ToList();
                    break;

                case nameof(ItemNodeVM.TemperWorkbenchKey):
                    rec.WorkbenchKeywordKey = item.TemperWorkbenchKey;
                    break;

                case nameof(ItemNodeVM.TemperPerkKey):
                    rec.PerkKey = item.TemperPerkKey;
                    break;
            }

            _itemService.SaveCOBJ(rec);
            _cache.UpdateRecipe(rec);

            return Task.CompletedTask;
        }
    }
}
