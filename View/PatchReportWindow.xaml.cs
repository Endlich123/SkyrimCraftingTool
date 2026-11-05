using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using SkyrimCraftingTool.Model;
using SkyrimCraftingTool.Services;

namespace SkyrimCraftingTool.View
{
    // The result of Generate Patch and Preview Patch. Replaces the MessageBox that used to carry the
    // whole report: on a large edit set it was taller than the screen (2026-10-26).
    public partial class PatchReportWindow : Window
    {
        private readonly string? _reportFile;

        private PatchReportWindow(string title, string headline, string report, string? outputFolder, string? reportFile)
        {
            InitializeComponent();
            Title = title;
            HeadlineText.Text = headline;
            ReportBox.Text = report;
            _reportFile = reportFile;

            OutputFolderButton.FolderPath = outputFolder ?? "";
            OutputFolderButton.ToolTip = outputFolder;
            OutputFolderButton.Visibility = string.IsNullOrWhiteSpace(outputFolder) ? Visibility.Collapsed : Visibility.Visible;
            OpenFileButton.Visibility = reportFile != null && File.Exists(reportFile) ? Visibility.Visible : Visibility.Collapsed;
            SourceInitialized += ApplyCaption;
        }

        // outputFolder: offered as "Open output folder" (the real run). reportFile: the copy in the
        // Logs folder, when there is one.
        public static void Show(string title, string headline, string report, string? outputFolder = null, string? reportFile = null)
        {
            var window = new PatchReportWindow(title, headline, report, outputFolder, reportFile);
            var owner = Application.Current?.MainWindow;
            if (owner != null && owner.IsVisible) window.Owner = owner;
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

        private void OpenFile_Click(object sender, RoutedEventArgs e) => Open(_reportFile, "report file");

        private void Open(string? path, string what)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            try
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                AppLogger.LogError($"Opening the patch {what} failed", ex);
                StatusText.Text = $"Could not open the {what}: {ex.Message}";
            }
        }

        private void Copy_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Clipboard.SetText(ReportBox.Text);
                StatusText.Text = "Report copied to the clipboard.";
            }
            catch (Exception ex)
            {
                // The clipboard can be locked by another process; say so instead of failing silently.
                AppLogger.LogError("PatchReportWindow.Copy", ex);
                StatusText.Text = $"Could not copy: {ex.Message} - use \"Open report file\" instead.";
            }
        }
    }
}
