using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using SkyrimCraftingTool.Services;
using SkyrimCraftingTool.ViewModel;

namespace SkyrimCraftingTool.View
{
    // The editor for the modular preset building blocks. A window like the dry run, so both can be
    // reached from the Presets tab and from each other without replacing what is on screen.
    public partial class ModularCatalogWindow : Window
    {
        private readonly ModularCatalogEditorVM _vm;

        private ModularCatalogWindow(ModularCatalogEditorVM vm)
        {
            InitializeComponent();
            _vm = vm;
            DataContext = vm;
            SourceInitialized += ApplyCaption;
            Closing += AskBeforeLosingChanges;
        }

        // The Presets tab's own editor, not a second one: two editors over the same files would
        // overwrite each other's saves, and the tab would show stale blocks after this window.
        public static void Show(Window? owner, MainContentVM main, bool weapons = false)
        {
            var presets = (Application.Current?.MainWindow?.DataContext as MainWindowVM)?.PresetsVM;
            var editor = (weapons ? presets?.WeaponEditor : presets?.Editor)
                         ?? new ModularCatalogEditorVM(main, weapons);
            var window = new ModularCatalogWindow(editor);
            if (owner != null) window.Owner = owner;
            window.ShowDialog();
        }

        // Nothing autosaves here - the files are what a dry run reads, and a half-edited rule
        // should not take effect on its own.
        private void AskBeforeLosingChanges(object? sender, CancelEventArgs e)
        {
            if (!_vm.IsDirty) return;

            var answer = MessageBox.Show(this,
                "Save the changes to the building blocks?",
                "Building blocks", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);

            if (answer == MessageBoxResult.Cancel) { e.Cancel = true; return; }
            if (answer == MessageBoxResult.Yes && !_vm.Save())
            {
                // Validation failed - the status line says why; stay open so it can be fixed.
                e.Cancel = true;
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
