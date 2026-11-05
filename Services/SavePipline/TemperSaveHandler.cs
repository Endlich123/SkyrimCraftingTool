using SkyrimCraftingTool.Model;
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
            or nameof(ItemNodeVM.TemperPerkKey)
            or nameof(ItemNodeVM.TemperConditions);

        public Task HandleAsync(SaveRequest r)
        {
            var item = r.Item;

            bool created = false;
            if (!item.HasTemperRecipe)
            {
                item.CreateTemperRecipe();
                created = true;
            }

            var rec = item.TemperRecipe.Record;

            // A new recipe arrives with the Arcane Blacksmith gate (ItemNodeVM.ArcaneBlacksmithGate).
            // Conditions only reach the DB when the conditions field is what is being saved, so when
            // the recipe came into being through an ingredient or the workbench, write them here -
            // otherwise the gate shows in the editor and is gone after a restart.
            if (created && r.FieldName != nameof(ItemNodeVM.TemperConditions) && rec.Conditions.Count > 0)
            {
                _itemService.SaveCOBJConditions(rec.Key, rec.Conditions);
                _cache.UpdateRecipeConditions(rec.Key, rec.Conditions);
            }

            switch (r.FieldName)
            {
                case nameof(ItemNodeVM.TemperIngredients):
                    // See CraftingSaveHandler: skip empty rows, merge duplicate keys.
                    rec.IngredientKeys = item.TemperIngredients
                        .Where(i => !string.IsNullOrEmpty(i.Key))
                        .GroupBy(i => i.Key)
                        .Select(g => $"{g.Key}*{g.Sum(i => i.Count)}")
                        .ToList();
                    break;

                case nameof(ItemNodeVM.TemperWorkbenchKey):
                    rec.WorkbenchKeywordKey = item.TemperWorkbenchKey;
                    break;

                case nameof(ItemNodeVM.TemperPerkKey):
                    rec.PerkKey = item.TemperPerkKey;
                    break;

                case nameof(ItemNodeVM.TemperConditions):
                    // See CraftingSaveHandler: skip half-built conditions.
                    var conditions = item.TemperConditions
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
