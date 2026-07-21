using SkyrimCraftingTool.ViewModel;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SkyrimCraftingTool.Services.SavePipline
{
    public sealed class ArmorSaveHandler : ISaveHandler
    {
        private readonly IItemService _itemService;
        private readonly ICacheManager _cache;

        public ArmorSaveHandler(IItemService itemService, ICacheManager cache)
        {
            _itemService = itemService;
            _cache = cache;
        }

        // CanHandle: only handle armor-specific fields for armor items.
        // Field-specific handlers (Crafting/Temper) rely on FieldName checks and must be allowed to run for recipe fields.
        public bool CanHandle(SaveRequest r) =>
            r.Item != null && r.Item.IsArmor && (
                r.FieldName is nameof(ItemNodeVM.Name)
                or nameof(ItemNodeVM.Value)
                or nameof(ItemNodeVM.Weight)
                or nameof(ItemNodeVM.ArmorRating)
                or nameof(ItemNodeVM.BodySlotMask)
                or nameof(ItemNodeVM.SelectedKeywords)
                or nameof(ItemNodeVM.ContainerString)
            );

        public Task HandleAsync(SaveRequest r)
        {
            var item = r.Item;

            switch (r.FieldName)
            {
                case nameof(ItemNodeVM.Name):
                    _itemService.UpdateArmorName(item.Key, item.Name);
                    _cache.UpdateArmorName(item.Key, item.Name);
                    break;

                case nameof(ItemNodeVM.Value):
                    _itemService.UpdateArmorValue(item.Key, item.Value);
                    _cache.UpdateArmorValue(item.Key, item.Value);
                    break;

                case nameof(ItemNodeVM.Weight):
                    _itemService.UpdateArmorWeight(item.Key, item.Weight);
                    _cache.UpdateArmorWeight(item.Key, item.Weight);
                    break;

                case nameof(ItemNodeVM.ArmorRating):
                    _itemService.UpdateArmorRating(item.Key, item.ArmorRating);
                    _cache.UpdateArmorRating(item.Key, item.ArmorRating);
                    break;

                case nameof(ItemNodeVM.BodySlotMask):
                    _itemService.UpdateArmorBodySlotMask(item.Key, item.BodySlotMask);
                    _cache.UpdateArmorBodySlotMask(item.Key, item.BodySlotMask);
                    break;

                case nameof(ItemNodeVM.SelectedKeywords):
                    var keys = item.SelectedKeywords.Select(k => k.Key).ToList();
                    _itemService.UpdateArmorKeywords(item.Key, keys);
                    _cache.UpdateArmorKeywords(item.Key, keys);
                    break;

                case nameof(ItemNodeVM.ContainerString):
                    _itemService.UpdateArmorContainerString(item.Key, item.ContainerString);
                    _cache.UpdateArmorContainerString(item.Key, item.ContainerString);
                    break;
            }

            return Task.CompletedTask;
        }
    }
}
