using SkyrimCraftingTool.Model;
using SkyrimCraftingTool.ViewModel;
using System.Linq;
using System.Threading.Tasks;

namespace SkyrimCraftingTool.Services.SavePipline
{
    public sealed class EnchantmentSaveHandler : ISaveHandler
    {
        private readonly IEnchantmentService _service;
        private readonly ICacheManager _cache;

        public EnchantmentSaveHandler(IEnchantmentService service, ICacheManager cache)
        {
            _service = service;
            _cache = cache;
        }

        public bool CanHandle(SaveRequest r) =>
            r.Enchantment != null &&
            (
                r.FieldName is nameof(EnchantmentRecord.Name)
                or nameof(EnchantmentRecord.EditorID)
                or nameof(EnchantmentRecord.EnchantType)
                or nameof(EnchantmentRecord.Flags)
                or nameof(EnchantmentRecord.ChargeTime)
                or nameof(EnchantmentRecord.EnchantmentAmount)
                or nameof(EnchantmentRecord.CastType)
                or nameof(EnchantmentRecord.TargetType)
                or nameof(EnchantmentRecord.EnchantmentCost)
                or nameof(EnchantmentRecord.WornRestrictionListKey)
                or "Effects"
                or "WornRestrictionKeywords"
            );


        public Task HandleAsync(SaveRequest r)
        {
            if (r.Enchantment is not { } ench) return Task.CompletedTask;

            switch (r.FieldName)
            {
                case nameof(EnchantmentRecord.Name):
                    _service.UpdateEnchantmentName(ench.Key, ench.Name);
                    _cache.UpdateEnchantmentName(ench.Key, ench.Name);
                    break;

                case nameof(EnchantmentRecord.EditorID):
                    _service.UpdateEnchantmentEditorId(ench.Key, ench.EditorID);
                    break;

                // No cache counterparts for these four: CacheManager's enchantment methods are all
                // empty stubs (see the EditorID note above).
                case nameof(EnchantmentRecord.EnchantType):
                    _service.UpdateEnchantmentEnchantType(ench.Key, ench.EnchantType);
                    break;

                case nameof(EnchantmentRecord.Flags):
                    _service.UpdateEnchantmentFlags(ench.Key, ench.Flags);
                    break;

                case nameof(EnchantmentRecord.ChargeTime):
                    _service.UpdateEnchantmentChargeTime(ench.Key, ench.ChargeTime);
                    break;

                case nameof(EnchantmentRecord.EnchantmentAmount):
                    _service.UpdateEnchantmentAmount(ench.Key, ench.EnchantmentAmount);
                    break;

                case nameof(EnchantmentRecord.CastType):
                    _service.UpdateEnchantmentCastType(ench.Key, ench.CastType);
                    _cache.UpdateEnchantmentCastType(ench.Key, ench.CastType);
                    break;

                case nameof(EnchantmentRecord.TargetType):
                    _service.UpdateEnchantmentTargetType(ench.Key, ench.TargetType);
                    _cache.UpdateEnchantmentTargetType(ench.Key, ench.TargetType);
                    break;

                case nameof(EnchantmentRecord.EnchantmentCost):
                    _service.UpdateEnchantmentCost(ench.Key, ench.EnchantmentCost);
                    _cache.UpdateEnchantmentCost(ench.Key, ench.EnchantmentCost);
                    break;

                case nameof(EnchantmentRecord.WornRestrictionListKey):
                    _service.UpdateEnchantmentWornRestrictionListKey(ench.Key, ench.WornRestrictionListKey);
                    _cache.UpdateEnchantmentWornRestrictionListKey(ench.Key, ench.WornRestrictionListKey);
                    break;

                case "Effects":
                    if (r.Effects == null)
                    {
                        AppLogger.LogWarning($"EnchantmentSaveHandler: Effects request for {ench.Key} without effects - not saved.");
                        break;
                    }
                    var effects = r.Effects.Select(vm => vm.Model).ToList();
                    _service.SaveEnchantmentEffects(ench.Key, effects);
                    _cache.SaveEnchantmentEffects(ench.Key, effects);
                    break;

                case "WornRestrictionKeywords":
                    if (r.SelectedWornRestrictionKeywords == null)
                    {
                        AppLogger.LogWarning($"EnchantmentSaveHandler: keyword request for {ench.Key} without keywords - not saved.");
                        break;
                    }
                    var keywords = r.SelectedWornRestrictionKeywords.ToList();
                    _service.SaveEnchantmentWornRestrictionKeywords(ench.WornRestrictionListKey, keywords);
                    _cache.SaveEnchantmentWornRestrictionKeywords(ench.WornRestrictionListKey, keywords);
                    break;
            }

            return Task.CompletedTask;
        }
    }
}
