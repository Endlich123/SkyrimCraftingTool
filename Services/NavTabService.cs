using System;
using System.Collections.Generic;
using System.Linq;
using SkyrimCraftingTool.Model;

namespace SkyrimCraftingTool.Services
{
    // Which of the main tabs the nav row shows.
    //
    // ITEMS AND SETTINGS ARE NOT IN HERE, and that is the design rather than an omission: a tool you
    // cannot get back to its items from is a tool someone has shut themselves out of, and settings
    // is the only way back from any of this. They are not toggles forced to true - they are simply
    // not offered, so there is no state in which they can be off.
    public enum NavTab
    {
        Enchantments,
        Npcs,
        NpcGroups,
        World,
        Presets,
    }

    // Owns the preference and the key it is stored under, the way ThemeService owns the palette and
    // its own key. The two view models that care - MainWindowVM for the nav row, SettingsVM for the
    // checkboxes - present it; neither keeps a second copy.
    public static class NavTabService
    {
        // The label each tab carries in the nav row, so the settings list cannot drift from the
        // buttons it is about.
        public static IReadOnlyList<(NavTab Tab, string Label)> Hideable { get; } = new[]
        {
            (NavTab.Enchantments, "Enchantments"),
            (NavTab.Npcs, "NPCs"),
            (NavTab.NpcGroups, "NPC groups"),
            (NavTab.World, "Container/LeveledList"),
            (NavTab.Presets, "Presets"),
        };

        // Raised when any of them changes, for whoever is showing them.
        public static event Action? Changed;

        private static string KeyFor(NavTab tab) => $"nav.tab.{tab}.visible";

        // Shown unless the user said otherwise: a new tab appears for everyone who has not been
        // asked, rather than staying hidden until someone finds the setting.
        public static bool IsVisible(NavTab tab) => AppPrefs.GetBool(KeyFor(tab), fallback: true);

        public static void SetVisible(NavTab tab, bool visible)
        {
            if (IsVisible(tab) == visible) return;

            AppPrefs.SetBool(KeyFor(tab), visible);
            Changed?.Invoke();
        }

        // Everything still reachable from the nav row. Items and Settings are always among them -
        // they are not part of NavTab at all - so this can never answer "nothing".
        public static IReadOnlyList<NavTab> Visible()
            => Hideable.Select(h => h.Tab).Where(IsVisible).ToList();
    }
}
