using SkyrimCraftingTool.Model;
using System.Threading.Tasks;

namespace SkyrimCraftingTool.Services.SavePipline
{
    // Persists one breakdown recipe.
    //
    // Unlike the crafting and temper handlers this one does NOT read the fields off the item: it
    // takes the COBJRecord straight out of the request. An item can have several breakdown recipes,
    // so "which one" has to travel with the request - see SaveRequest.Recipe.
    //
    // It also does not create a record on demand the way CraftingSaveHandler does
    // ("if (!item.HasCraftingRecipe) item.CreateCraftingRecipe()"). A breakdown recipe is created
    // explicitly by the Add button and written out right there, so by the time an edit arrives the
    // row already exists. That deliberately avoids the phantom-recipe problem: a recipe that lives
    // only in the ViewModel until some field happens to be saved first.
    public sealed class BreakdownSaveHandler : ISaveHandler
    {
        public const string FieldName = "BreakdownRecipe";

        private readonly IItemService _itemService;
        private readonly ICacheManager _cache;

        public BreakdownSaveHandler(IItemService itemService, ICacheManager cache)
        {
            _itemService = itemService;
            _cache = cache;
        }

        public bool CanHandle(SaveRequest r) =>
            r.FieldName == FieldName && r.Recipe != null;

        public Task HandleAsync(SaveRequest r)
        {
            var rec = r.Recipe;

            // Zero objects out is not a recipe, and a 0 reaching the ESP would turn a working
            // recipe into one that silently gives nothing. Clamped here as well as in the DB layer
            // because this is the last place the value is still a plain field.
            if (rec.CreatedObjectCount < 1)
                rec.CreatedObjectCount = 1;

            _itemService.SaveCOBJ(rec);
            _cache.UpdateRecipe(rec);

            return Task.CompletedTask;
        }
    }
}
