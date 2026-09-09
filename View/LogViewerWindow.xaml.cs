using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using SkyrimCraftingTool.Model;
using SkyrimCraftingTool.Services;

namespace SkyrimCraftingTool.View
{
    // Log viewer and bug-report generator.
    //
    // Deliberately offline: it builds the report locally and hands it to the user via clipboard or
    // a file. The tool makes no network calls, and a modding tool that phoned home would rightly
    // be treated as suspect.
    public partial class LogViewerWindow : Window
    {
        public LogViewerWindow()
        {
            InitializeComponent();
            LoadReport();
            SourceInitialized += LogViewerWindow_SourceInitialized;
        }

        // Every other window recolors its native title bar to match the dark theme; this one was
        // the only one left out, so it opened with a light Windows caption next to a dark app.
        private void LogViewerWindow_SourceInitialized(object? sender, EventArgs e)
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            var captionColor = ((System.Windows.Media.SolidColorBrush)FindResource("ColorBackgroundBase")).Color;
            var textColor = ((System.Windows.Media.SolidColorBrush)FindResource("ColorTextPrimary")).Color;
            DwmTitleBarService.ApplyAccentCaption(hwnd, captionColor, textColor);
        }

        private void LoadReport()
        {
            ReportBox.Text = DiagnosticsReport.Build();
            ReportBox.ScrollToEnd();   // the newest log entries are what people came for
            StatusText.Text = $"Log file: {AppLogger.LogFilePath}";
        }

        private void Refresh_Click(object sender, RoutedEventArgs e) => LoadReport();

        private void Copy_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                System.Windows.Clipboard.SetText(ReportBox.Text);
                StatusText.Text = "Report copied to the clipboard.";
            }
            catch (Exception ex)
            {
                // The clipboard can genuinely be locked by another process; say so rather than
                // leaving the user wondering whether the copy worked.
                AppLogger.LogError("LogViewerWindow.Copy", ex);
                StatusText.Text = $"Could not copy: {ex.Message} — use \"Save report…\" instead.";
            }
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Save bug report",
                FileName = $"SkyrimCraftingTool-report-{DateTime.Now:yyyyMMdd-HHmmss}.txt",
                Filter = "Text file (*.txt)|*.txt|All files (*.*)|*.*",
                DefaultExt = ".txt",
            };

            if (dialog.ShowDialog(this) != true) return;

            try
            {
                File.WriteAllText(dialog.FileName, ReportBox.Text);
                StatusText.Text = $"Saved to {dialog.FileName}";
            }
            catch (Exception ex)
            {
                AppLogger.LogError("LogViewerWindow.Save", ex);
                System.Windows.MessageBox.Show(this,
                    $"Could not save the report:{Environment.NewLine}{ex.Message}",
                    "Save failed", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void OpenFolder_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Directory.CreateDirectory(AppLogger.LogFolderPath);
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{AppLogger.LogFolderPath}\"")
                {
                    UseShellExecute = true,
                });
            }
            catch (Exception ex)
            {
                AppLogger.LogError("LogViewerWindow.OpenFolder", ex);
                StatusText.Text = $"Could not open the folder: {ex.Message}";
            }
        }

        private void Clear_Click(object sender, RoutedEventArgs e)
        {
            var answer = System.Windows.MessageBox.Show(this,
                "Delete the log file?" + Environment.NewLine + Environment.NewLine +
                "Useful right before reproducing a bug, so the report contains only the relevant run.",
                "Clear log", MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (answer != MessageBoxResult.Yes) return;

            AppLogger.Clear();
            LoadReport();
            StatusText.Text = "Log cleared.";
        }
    }
}
