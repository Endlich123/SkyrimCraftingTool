using System.Collections.Generic;
using System.Linq;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using SkyrimCraftingTool.Model;
using SkyrimCraftingTool.Services;

namespace SkyrimCraftingTool.ViewModel
{
    // GetItemCount: "has at least N of this item". Editable since C3 (docs/Conditions-Plan.md) - it is
    // the most common condition after HasPerk (3,880 on the real modlist, 84 plugins), mostly a gate
    // like "carries the Dwarven Smithing book" or "has the quest item".
    //
    // The target list is ConditionItemCatalog (~54,000 records). The picker shows only the first
    // matches of what is typed, so SearchText drives Matches instead of a ComboBox over everything.
    public class ItemCountConditionViewModel : BaseConditionViewModel
    {
        public ItemCountConditionViewModel()
        {
            Type = CustomConditionType.GetItemCount;
        }

        private FormKey _itemFormKey;
        public FormKey ItemFormKey
        {
            get => _itemFormKey;
            set
            {
                if (SetProperty(ref _itemFormKey, value))
                    OnPropertyChanged(nameof(IsDeadReference));
            }
        }

        // Kept even when the catalogue does not know it, so an unresolved target still saves back.
        public string ItemKey => ItemFormKey.IsNull ? "" : KeyFactory.BuildMasterKey(ItemFormKey);

        private FormIDRecord? _selectedItem;
        public FormIDRecord? SelectedItem
        {
            get => _selectedItem;
            set
            {
                // The editable ComboBox writes null back as soon as the typed text matches no entry.
                // That is typing, not "remove the target" - the condition keeps what it points at.
                if (value == null) return;
                if (SetProperty(ref _selectedItem, value))
                {
                    ItemFormKey = KeyFactory.ParseFormKey(value.Key);
                    OnPropertyChanged(nameof(IsDeadReference));
                }
            }
        }

        // A target is set, but the catalogue has no such record (its plugin is disabled or gone).
        public bool IsDeadReference => !ItemFormKey.IsNull && SelectedItem == null;

        private string _searchText = "";
        public string SearchText
        {
            get => _searchText;
            set
            {
                if (SetProperty(ref _searchText, value ?? ""))
                    OnPropertyChanged(nameof(Matches));
            }
        }

        // The selected item always stays in the list - otherwise the editable ComboBox drops its
        // selection the moment the list is refiltered.
        public IReadOnlyList<FormIDRecord> Matches
        {
            get
            {
                var text = SelectedItem != null && SearchText == SelectedItem.Name ? "" : SearchText;
                var found = ConditionItemCatalog.Search(text);
                if (SelectedItem != null && !found.Contains(SelectedItem)) found.Insert(0, SelectedItem);
                return found;
            }
        }

        private float _count = 1f;
        public float Count
        {
            get => _count;
            set => SetProperty(ref _count, value);
        }

        public override float ComparisonValue
        {
            get => Count;
            set => Count = value;
        }

        public override ConditionFloat ToMutagenCondition()
        {
            var data = new GetItemCountConditionData();
            data.ItemOrList.Link.SetTo(ItemFormKey);
            return new ConditionFloat
            {
                CompareOperator = CompareOperator.GreaterThanOrEqualTo,
                ComparisonValue = Count,
                Data = data,
            };
        }
    }
}
