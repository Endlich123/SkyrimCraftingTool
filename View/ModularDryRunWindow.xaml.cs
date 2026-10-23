using System;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using SkyrimCraftingTool.Services;
using SkyrimCraftingTool.ViewModel;

namespace SkyrimCraftingTool.View
{
    // The modular preset dry-run. A window, like the other review steps: it reports on the tree as
    // it is filtered now and changes nothing, so it should not replace what is on screen.
    public partial class ModularDryRunWindow : Window
    {
        private ModularDryRunWindow(ModularDryRunVM vm)
        {
            InitializeComponent();
            DataContext = vm;
            SourceInitialized += ApplyCaption;
        }

        public static void Show(Window? owner, MainContentVM main)
        {
            var window = new ModularDryRunWindow(new ModularDryRunVM(main));
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
