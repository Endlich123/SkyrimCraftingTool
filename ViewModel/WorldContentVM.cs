using System;
using System.Collections.Generic;
using System.Linq;
using SkyrimCraftingTool.Model;
using SkyrimCraftingTool.Services;

namespace SkyrimCraftingTool.ViewModel
{
    // The Container/LeveledList tab.
    //
    // WHY IT IS ITS OWN TAB AND NOT A PANEL IN THE ITEM VIEW: this is where the tool stops being
    // purely additive. Every other screen adds something of the user's on top of the load order;
    // here they delete entries other mods put there. When those operations were first looked at and
    // turned down, the condition written down for ever offering them was "a clearly separated area,
    // marked as changing things for every mod, not next to the level slider". This tab is that area.
    //
    // The two sub-tabs ask the same question of different things, so they are two instances of one
    // view model rather than two view models (see OwnerContentsVM).
    public sealed class WorldContentVM : ViewModelBase
    {
        // MainContentVM supplies the armor and weapon rows AND the container catalogue, because both
        // already live there and a second copy of either would be a second truth. The placement
        // sub-tabs show its ItemNodeVM instances, not new ones - see PlacementListVM.
        public WorldContentVM(MainContentVM content, IItemService itemService, string dbPath = null)
        {
            // The contents panes can take a placement back out, and a placement lives in the ITEM
            // that made it. Where that item has a live view model, the undo has to go through it -
            // an ARMO/WEAP is the item tree's own ItemNodeVM, with its dirty state and save pipeline
            // - so the two panes are handed a way to find one. See OwnerContentsVM.UnPlace.
            IPlaceable? FindPlaceable(string itemKey)
            {
                if (string.IsNullOrWhiteSpace(itemKey)) return null;

                bool Match(IPlaceable p) => string.Equals(p.Key, itemKey, StringComparison.OrdinalIgnoreCase);

                // The placement sub-tabs first: a world item has no other home, and whatever the
                // user was just working in is loaded.
                foreach (var tab in Placements ?? new List<PlacementListVM>())
                    foreach (var row in tab.Rows)
                        if (Match(row)) return row;

                // ... then the item tree itself, which holds armor and weapons whether or not their
                // sub-tab has ever been opened.
                return (content?.PlaceableItems(isArmor: true) ?? new List<IPlaceable>()).FirstOrDefault(Match)
                       ?? (content?.PlaceableItems(isArmor: false) ?? new List<IPlaceable>()).FirstOrDefault(Match);
            }

            // THE PLUGIN ROWS IN LOAD ORDER, same as the item tree. Every one of these ten trees
            // groups by plugin, and a tool that shows the same plugins in two orders on two screens
            // makes the reader translate between them - the order a load order is read in is itself
            // the answer to "which mod wins".
            //
            // ActivePlugins is already load-ordered (FileServiceAdapter maps GetActivePlugins to
            // GetActivePluginsInLoadOrder). Read through a function because a scan REPLACES the list
            // - see PluginTree.
            IReadOnlyList<string> LoadOrder()
                => (content?.ActivePlugins ?? new List<PluginInfo>()).Select(p => p.FileName).ToList();

            Containers = new OwnerContentsVM(RemovalScope.Container, dbPath, FindPlaceable, LoadOrder);
            LeveledLists = new OwnerContentsVM(RemovalScope.LeveledList, dbPath, FindPlaceable, LoadOrder);

            List<ContainerRecord> Catalogue() => content?.AllContainers ?? new List<ContainerRecord>();

            Placements = new List<PlacementListVM>
            {
                new("Armor", () => content.PlaceableItems(isArmor: true), Catalogue, LoadOrder),
                new("Weapons", () => content.PlaceableItems(isArmor: false), Catalogue, LoadOrder),
            };

            // One sub-tab per kind, from the same table. The order is the one someone reaches for:
            // books first, because that is what the request came from.
            foreach (var kind in new[] { "Book", "Scroll", "Misc", "SoulGem", "Ammo", "Food", "Ingredient", "Key" })
            {
                var captured = kind;
                Placements.Add(new PlacementListVM(
                    Plural(captured),
                    () => LoadWorldItems(itemService, content, captured),
                    Catalogue,
                    LoadOrder));
            }

            Tabs = new List<object> { Containers, LeveledLists };
            Tabs.AddRange(Placements);
        }

        private static string Plural(string kind) => kind switch
        {
            "Misc" => "Misc",
            "Ammo" => "Ammo",
            "SoulGem" => "Soul gems",
            "Food" => "Food",
            _ => kind + "s",
        };

        // Built fresh per sub-tab rather than once for all eight: a load order has thousands of misc
        // items, and nobody opens this tab to look at every kind at once.
        private static IReadOnlyList<IPlaceable> LoadWorldItems(IItemService itemService, MainContentVM content, string kind)
        {
            var containers = content?.AllContainers ?? new List<ContainerRecord>();

            return (itemService?.LoadWorldItems() ?? new List<WorldItemRecord>())
                .Where(r => string.Equals(r.Kind, kind, StringComparison.OrdinalIgnoreCase))
                .OrderBy(r => r.Display, StringComparer.CurrentCultureIgnoreCase)
                .Select(r => (IPlaceable)new WorldItemNodeVM(r, containers, itemService))
                .ToList();
        }

        public OwnerContentsVM Containers { get; }
        public OwnerContentsVM LeveledLists { get; }

        public List<PlacementListVM> Placements { get; }

        // One list for the TabControl, two content shapes inside it. The view picks the shape from
        // each entry's TYPE through an implicit DataTemplate, which is what lets the eight placement
        // sub-tabs be generated instead of written out.
        public List<object> Tabs { get; }

        private int _selectedTabIndex;
        public int SelectedTabIndex
        {
            get => _selectedTabIndex;
            set
            {
                if (SetProperty(ref _selectedTabIndex, value)) EnsureLoaded();
            }
        }

        // Only the sub-tab in front of the user is read. Both together are tens of thousands of rows
        // on a real load order, and nobody opens this tab to look at both at once.
        public void EnsureLoaded()
        {
            // Only the sub-tab in front of the user. Together these are tens of thousands of rows.
            switch (Tabs.ElementAtOrDefault(SelectedTabIndex))
            {
                case OwnerContentsVM owner: owner.EnsureLoaded(); break;
                case PlacementListVM placement: placement.EnsureLoaded(); break;
            }
        }

        // A scan rewrites both sets of rows, so whatever is held here is stale. Dropped rather than
        // re-read, like the NPC tab: the user is usually somewhere else when a scan finishes.
        public void Invalidate()
        {
            Containers.Invalidate();
            LeveledLists.Invalidate();
            foreach (var p in Placements) p.Invalidate();
        }
    }
}
