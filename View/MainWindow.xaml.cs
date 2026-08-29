using SkyrimCraftingTool.Model;
using SkyrimCraftingTool.Services;
using SkyrimCraftingTool.ViewModel;
using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace SkyrimCraftingTool.View
{
    public partial class MainWindow : Window
    {
        private bool _pendingSavesFlushed;

        public MainWindow()
        {
            InitializeComponent();
            DataContext = new MainWindowVM();
            SourceInitialized += MainWindow_SourceInitialized;
            Closing += MainWindow_Closing;
        }

        private void MainWindow_SourceInitialized(object? sender, EventArgs e)
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            var captionColor = ((SolidColorBrush)FindResource("ColorBackgroundBase")).Color;
            var textColor = ((SolidColorBrush)FindResource("ColorTextPrimary")).Color;
            DwmTitleBarService.ApplyAccentCaption(hwnd, captionColor, textColor);
        }

        // Autosave is debounced (~350ms). Closing the window right after an edit would otherwise drop
        // that pending write. So: cancel this close, commit the field currently being edited, flush
        // the debounced save, then re-issue the close via the dispatcher - calling Close() straight
        // from inside a Closing handler throws "Cannot ... Close ... while a Window is closing".
        private void MainWindow_Closing(object? sender, CancelEventArgs e)
        {
            if (_pendingSavesFlushed || DataContext is not MainWindowVM vm)
                return;

            e.Cancel = true;
            CommitFocusedEdit();
            FlushThenClose(vm);
        }

        // A TextBox / editable ComboBox with the default LostFocus binding hasn't pushed its value to
        // the VM yet if the user closed the window without leaving the field. Force the commit.
        private void CommitFocusedEdit()
        {
            if (Keyboard.FocusedElement is not FrameworkElement fe)
                return;

            var expr = fe.GetBindingExpression(System.Windows.Controls.TextBox.TextProperty)
                    ?? fe.GetBindingExpression(System.Windows.Controls.ComboBox.TextProperty);

            try
            {
                expr?.UpdateSource();
            }
            catch (Exception ex)
            {
                AppLogger.LogError("Committing focused edit on close failed", ex);
            }
        }

        private async void FlushThenClose(MainWindowVM vm)
        {
            try
            {
                await vm.FlushAllPendingSavesAsync();
            }
            catch (Exception ex)
            {
                AppLogger.LogError("Flushing pending saves on window close failed", ex);
            }

            _pendingSavesFlushed = true;
            _ = Dispatcher.BeginInvoke(new Action(Close), DispatcherPriority.Background);
        }
    }
}
