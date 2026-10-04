using System.Collections.Generic;
using SkyrimCraftingTool.Model;
using SkyrimCraftingTool.Services;
using System.Threading.Tasks;

namespace SkyrimCraftingTool.ViewModel
{
    // A book, scroll, misc item, soul gem, ammo, food, ingredient or key in the placement list.
    //
    // DELIBERATELY NOT AN ItemNodeVM. That one carries stats, keywords, two recipes, change tracking
    // for a dozen fields and a save pipeline to match. This carries a key, a name and a placement -
    // because that is all the record has that the tool understands. Making these share a view model
    // would have meant turning IsArmor into a record-type enum and touching all 62 places that ask
    // it, for eight types that never reach any of them.
    //
    // WRITES STRAIGHT THROUGH, no debouncer. The item editor debounces because a keystroke in a text
    // field arrives dozens of times a second; a placement is a button press, and the save pipeline's
    // shared debouncer is a thing to stay out of rather than to join (see EnchantmentMenuVM's effect
    // blocks, which write directly for the same reason).
    public sealed class WorldItemNodeVM : ViewModelBase, IPlaceable
    {
        private readonly WorldItemRecord _record;
        private readonly IItemService _itemService;

        public WorldItemNodeVM(WorldItemRecord record, List<ContainerRecord> allContainers, IItemService itemService)
        {
            _record = record;
            _itemService = itemService;

            ContainerSelection = new ContainerSelectionVM(allContainers);
            ContainerSelection.OwnerInfo = () => (Key, Display);
            ContainerSelection.LoadFromString(record.ContainerString ?? "{}");

            // A slider is a change like any other here - without this the level would live in the
            // control and never reach the string, which is the exact scar ContainerSelectionVM's
            // LevelChanged event was added for.
            ContainerSelection.LevelChanged += CommitPlacement;
        }

        public string Key => _record.Key;
        public string EditorID => _record.EditorID;
        public string Name => _record.Name;
        public string Kind => _record.Kind;

        public string Display => _record.Display;

        public ContainerSelectionVM ContainerSelection { get; }

        public void CommitPlacement()
        {
            _record.ContainerString = ContainerSelection.BuildString();
            _itemService.UpdateWorldItemContainerString(Key, _record.ContainerString);
            OnPropertyChanged(nameof(HasPlacement));
        }

        // Nothing to wait for - see the note above about writing straight through. The bulk apply
        // asks for this anyway, because an item on the Armor/Weapons tabs very much does have
        // something to wait for.
        public Task CommitPlacementAsync()
        {
            CommitPlacement();
            return Task.CompletedTask;
        }

        public string PlacementString => _record.ContainerString ?? "{}";

        public Task SetPlacementAsync(string containerString)
        {
            _record.ContainerString = containerString ?? "{}";
            ContainerSelection.LoadFromString(_record.ContainerString);

            _itemService.UpdateWorldItemContainerString(Key, _record.ContainerString);
            OnPropertyChanged(nameof(HasPlacement));

            return Task.CompletedTask;
        }

        // Drives the marker in the list, so a record that has been placed is findable again without
        // clicking through everything.
        public bool HasPlacement =>
            !string.IsNullOrWhiteSpace(_record.ContainerString) && _record.ContainerString != "{}";
    }
}
