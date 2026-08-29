using System;
using System.IO;

namespace SkyrimCraftingTool.Model;

public static class AppLogger
{
    private static readonly string LogFolder = Path.Combine(AppContext.BaseDirectory, "Logs");
    private static readonly string LogFile = Path.Combine(LogFolder, "error.log");
    private static readonly object _lock = new();

    public static void LogError(string context, Exception ex)
    {
        try
        {
            lock (_lock)
            {
                Directory.CreateDirectory(LogFolder);
                File.AppendAllText(LogFile,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {context}{Environment.NewLine}{ex}{Environment.NewLine}{new string('-', 80)}{Environment.NewLine}");
            }
        }
        catch
        {
            // Logging must never itself crash the app.
        }
    }

    // Non-fatal notices worth recording (e.g. a preset file from a newer build) - no exception,
    // no user dialog.
    public static void LogWarning(string message)
    {
        try
        {
            lock (_lock)
            {
                Directory.CreateDirectory(LogFolder);
                File.AppendAllText(LogFile,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] WARN: {message}{Environment.NewLine}{new string('-', 80)}{Environment.NewLine}");
            }
        }
        catch
        {
            // Logging must never itself crash the app.
        }
    }
}
