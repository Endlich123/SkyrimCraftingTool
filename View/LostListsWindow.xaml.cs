using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using SkyrimCraftingTool.Model;
using SkyrimCraftingTool.Services;
using SkyrimCraftingTool.ViewModel;

namespace SkyrimCraftingTool.View
{
    // The overview of every leveled list that lost entries to an override.
    //
    // A window for the same reason as ChangedListsWindow: it is something you look through after a
    // scan and then leave, not a place to work in. It is also the only way this information is
    // reachable at all - a list is otherwise opened through an item that happens to sit in it.
    public partial class LostListsWindow : Window
    {
        private LostListsWindow(LostListsVM vm)
        {
            InitializeComponent();
            DataContext = vm;
            SourceInitialized += ApplyCaption;
        }

        public static void Show(Window? owner)
        {
            var window = new LostListsWindow(new LostListsVM());
            if (owner != null) window.Owner = owner;
            window.ShowDialog();
        }

        // Opens the list itself, with no item context - the same read-only shape a nested list gets
        // when it is opened from another list's contents. There is nothing to place here: the reader
        // came from a report, not from an item.
        private void Open_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button || button.DataContext is not LostListRowVM row) return;

            try
            {
                if (PlacementLookup.Read(row.ListKey) == null)
                {
                    MessageBox.Show(this,
                        $"'{row.Display}' is not in the scanned data - rescan if the plugin was removed recently.",
                        "Leveled List", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                LeveledListWindow.ShowList(this, row.ListKey, row.Display);
            }
            catch (Exception ex)
            {
                AppLogger.LogError($"Opening leveled list {row.ListKey} from the lost-entry report", ex);
            }
        }

        private void ApplyCaption(object? sender, EventArgs e)
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;

            var caption = ((SolidColorBrush)FindResource("ColorBackgroundBase")).Color;
            var text = ((SolidColorBrush)FindResource("ColorTextPrimary")).Color;
            DwmTitleBarService.ApplyAccentCaption(hwnd, caption, text);
        }
    }
}
