using SkyrimCraftingTool.Model;
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
            or nameof(ItemNodeVM.CraftingPerkKey)
            or nameof(ItemNodeVM.CraftingConditions);

        public Task HandleAsync(SaveRequest r)
        {
            var item = r.Item;

            if (!item.HasCraftingRecipe)
                item.CreateCraftingRecipe();

            var rec = item.CraftingRecipe.Record;

            switch (r.FieldName)
            {
                case nameof(ItemNodeVM.CraftingIngredients):
                    // Skip not-yet-filled rows (empty Key) and merge duplicate keys (sum counts).
                    rec.IngredientKeys = item.CraftingIngredients
                        .Where(i => !string.IsNullOrEmpty(i.Key))
                        .GroupBy(i => i.Key)
                        .Select(g => $"{g.Key}*{g.Sum(i => i.Count)}")
                        .ToList();
                    break;

                case nameof(ItemNodeVM.CraftingWorkbenchKey):
                    rec.WorkbenchKeywordKey = item.CraftingWorkbenchKey;
                    break;

                case nameof(ItemNodeVM.CraftingPerkKey):
                    rec.PerkKey = item.CraftingPerkKey;
                    break;

                case nameof(ItemNodeVM.CraftingConditions):
                    // Skip half-built conditions (HasPerk with no perk, GetStageDone with no quest) -
                    // same idea as the empty-ingredient filter above.
                    var conditions = item.CraftingConditions
                        .Where(ConditionMapper.HasUsableTarget)
                        .Select(vm => ConditionMapper.ToRecord(vm, rec.Key))
                        .ToList();
                    rec.Conditions = conditions;
                    _itemService.SaveCOBJConditions(rec.Key, conditions);
                    _cache.UpdateRecipeConditions(rec.Key, conditions);
                    break;
            }

            _itemService.SaveCOBJ(rec);
            _cache.UpdateRecipe(rec);

            return Task.CompletedTask;
        }
    }
}
