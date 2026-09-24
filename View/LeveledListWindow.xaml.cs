using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using SkyrimCraftingTool.Model;
using SkyrimCraftingTool.Services;
using SkyrimCraftingTool.ViewModel;

namespace SkyrimCraftingTool.View
{
    // The detail window behind a leveled list in an item's Container section.
    //
    // A window rather than a navigable view because it only makes sense for a particular item: the
    // editable half is "what does THIS item get in THIS list". A view would have no such context and
    // would replace the item editor the user came from.
    public partial class LeveledListWindow : Window
    {
        private readonly LeveledListEditorVM _vm;

        private LeveledListWindow(LeveledListEditorVM vm)
        {
            InitializeComponent();
            _vm = vm;
            DataContext = vm;
            Title = $"Leveled List - {vm.ListName}";
            SourceInitialized += ApplyCaption;
        }

        public static void Show(Window? owner, LVLiEntryVM entry, string itemKey, string itemName)
            => Show(owner, entry, new[] { new PlacementSubject(itemKey, itemName) });

        // Open a list on its own, with no item to place - for callers that arrived at the list
        // directly rather than through an item, such as the lost-entry report. Same read-only shape
        // a nested list gets when it is opened from another list's contents.
        public static void ShowList(Window? owner, string listKey, string listName)
        {
            var window = new LeveledListWindow(new LeveledListEditorVM(listKey, listName));
            if (owner != null) window.Owner = owner;
            window.ShowDialog();
        }

        // The multi-select opens the same window for a whole selection: one placement row, many
        // items receiving it. Nothing about the list changes with the number of items - and neither
        // does the arithmetic, which is why this is one window and not two.
        public static void Show(Window? owner, LVLiEntryVM entry, IReadOnlyList<PlacementSubject> subjects)
        {
            var window = new LeveledListWindow(new LeveledListEditorVM(entry, subjects));
            if (owner != null) window.Owner = owner;
            window.ShowDialog();
        }

        private void ApplyCaption(object? sender, EventArgs e)
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;

            var caption = ((SolidColorBrush)FindResource("ColorBackgroundBase")).Color;
            var text = ((SolidColorBrush)FindResource("ColorTextPrimary")).Color;
            DwmTitleBarService.ApplyAccentCaption(hwnd, caption, text);
        }

        // Nested lists are shown as links, not expanded - a third of all entries point at another
        // list, up to nine levels deep. Opening one in its own window keeps the chain explicit and
        // keeps the row count of any single window bounded.
        //
        // The nested window has no placement to edit: the entry being edited belongs to the list the
        // user came from, and pretending otherwise would let them set a level on a list they never
        // selected. It opens read-only, with the same entry passed through only so the window has
        // something to show for "your placement" - guarded below.
        private void Contents_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (sender is not ListBox list || list.SelectedItem is not LeveledListEntryInfo entry) return;
            if (!entry.IsList) return;

            try
            {
                if (PlacementLookup.Read(entry.Reference) == null)
                {
                    MessageBox.Show(this,
                        $"'{entry.Name}' is not in the scanned data - rescan if the plugin was added recently.",
                        "Leveled List", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                var vm = new LeveledListEditorVM(entry.Reference, entry.Name);
                var window = new LeveledListWindow(vm) { Owner = this };
                window.ShowDialog();
            }
            catch (Exception ex)
            {
                AppLogger.LogError($"Opening nested leveled list {entry.Reference}", ex);
            }
        }
    }
}
