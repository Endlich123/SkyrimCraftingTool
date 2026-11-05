using System;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using SkyrimCraftingTool.Model;
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

        // Under the loading overlay: the view model computes every card in its constructor, on this
        // thread - several seconds on a big load order, with nothing on screen (2026-10-30). The overlay
        // goes once the window has drawn its first frame.
        public static void Show(Window? owner, MainContentVM main)
        {
            var busy = BusyService.Show("Preset dry run: computing the cards…", blocksUiThread: true);
            ModularDryRunWindow window;
            try
            {
                PerfLog.UntilRendered("Preset dry run: open");
                window = new ModularDryRunWindow(new ModularDryRunVM(main));
            }
            catch
            {
                busy.Dispose();
                throw;
            }
            window.ContentRendered += (_, _) => busy.Dispose();
            if (owner != null) window.Owner = owner;
            window.ShowDialog();
            busy.Dispose();   // twice is harmless - covers a window closed before it drew
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
