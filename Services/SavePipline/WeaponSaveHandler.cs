using SkyrimCraftingTool.ViewModel;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SkyrimCraftingTool.Services.SavePipline
{
    public sealed class WeaponSaveHandler : ISaveHandler
    {
        private readonly IItemService _itemService;
        private readonly ICacheManager _cache;

        public WeaponSaveHandler(IItemService itemService, ICacheManager cache)
        {
            _itemService = itemService;
            _cache = cache;
        }

        // CanHandle: only handle weapon-specific fields for weapon items.
        // This prevents capturing recipe-related fields which are handled by Crafting/Temper handlers.
        public bool CanHandle(SaveRequest r) =>
            r.Item != null && r.Item.IsWeapon && (
                r.FieldName is nameof(ItemNodeVM.Name)
                or nameof(ItemNodeVM.Value)
                or nameof(ItemNodeVM.Weight)
                or nameof(ItemNodeVM.Damage)
                or nameof(ItemNodeVM.Speed)
                or nameof(ItemNodeVM.Reach)
                or nameof(ItemNodeVM.Stagger)
                or nameof(ItemNodeVM.SelectedKeywords)
                or nameof(ItemNodeVM.ContainerString)
            );

        public Task HandleAsync(SaveRequest r)
        {
            var item = r.Item;

            switch (r.FieldName)
            {
                case nameof(ItemNodeVM.Name):
                    _itemService.UpdateWeaponName(item.Key, item.Name);
                    _cache.UpdateWeaponName(item.Key, item.Name);
                    break;

                case nameof(ItemNodeVM.Value):
                    _itemService.UpdateWeaponValue(item.Key, item.Value);
                    _cache.UpdateWeaponValue(item.Key, item.Value);
                    break;

                case nameof(ItemNodeVM.Weight):
                    _itemService.UpdateWeaponWeight(item.Key, item.Weight);
                    _cache.UpdateWeaponWeight(item.Key, item.Weight);
                    break;

                case nameof(ItemNodeVM.Damage):
                    _itemService.UpdateWeaponDamage(item.Key, item.Damage);
                    _cache.UpdateWeaponDamage(item.Key, item.Damage);
                    break;

                case nameof(ItemNodeVM.Speed):
                    _itemService.UpdateWeaponSpeed(item.Key, item.Speed);
                    _cache.UpdateWeaponSpeed(item.Key, item.Speed);
                    break;

                case nameof(ItemNodeVM.Reach):
                    _itemService.UpdateWeaponReach(item.Key, item.Reach);
                    _cache.UpdateWeaponReach(item.Key, item.Reach);
                    break;

                case nameof(ItemNodeVM.Stagger):
                    _itemService.UpdateWeaponStagger(item.Key, item.Stagger);
                    _cache.UpdateWeaponStagger(item.Key, item.Stagger);
                    break;

                case nameof(ItemNodeVM.SelectedKeywords):
                    var keys = item.SelectedKeywords.Select(k => k.Key).ToList();
                    _itemService.UpdateWeaponKeywords(item.Key, keys);
                    _cache.UpdateWeaponKeywords(item.Key, keys);
                    break;

                case nameof(ItemNodeVM.ContainerString):
                    _itemService.UpdateWeaponContainerString(item.Key, item.ContainerString);
                    _cache.UpdateWeaponContainerString(item.Key, item.ContainerString);
                    break;
            }

            return Task.CompletedTask;
        }
    }
}
