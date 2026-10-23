using System;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using SkyrimCraftingTool.Services;
using SkyrimCraftingTool.ViewModel;

namespace SkyrimCraftingTool.View
{
    // The item's OTHER recipes of one kind: which one the editor shows, and the two decisions that
    // can be made about the rest.
    //
    // A window rather than a section, for the same reason ChangedListsWindow is one: it is a
    // review-and-decide step, not a place to work in, and on most items it has nothing to say -
    // measured, 305 of ~8,700 items have more than one crafting recipe. A permanent panel for that
    // would be empty almost always.
    public partial class OtherRecipesWindow : Window
    {
        private OtherRecipesWindow(OtherRecipesVM vm)
        {
            InitializeComponent();
            DataContext = vm;
            SourceInitialized += ApplyCaption;
        }

        public static void Show(Window? owner, ItemNodeVM item, RecipeKind kind)
        {
            if (item == null) return;

            var window = new OtherRecipesWindow(new OtherRecipesVM(item, kind));
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
