using System;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using SkyrimCraftingTool.Services;
using SkyrimCraftingTool.ViewModel;

namespace SkyrimCraftingTool.View
{
    // The overview of every leveled list whose own properties the user changed.
    //
    // A window rather than a view: it is a review-and-undo step, not a place to work in, and it has
    // to be reachable from wherever the container section is without replacing what is on screen.
    public partial class ChangedListsWindow : Window
    {
        private ChangedListsWindow(ChangedListsVM vm)
        {
            InitializeComponent();
            DataContext = vm;
            SourceInitialized += ApplyCaption;
        }

        public static void Show(Window? owner)
        {
            var window = new ChangedListsWindow(new ChangedListsVM());
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
    }
}
