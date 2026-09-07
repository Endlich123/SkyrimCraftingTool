using System;
using System.IO;

namespace SkyrimCraftingTool.Model;

public static class AppLogger
{
    private static readonly string LogFolder = Path.Combine(AppContext.BaseDirectory, "Logs");
    private static readonly string LogFile = Path.Combine(LogFolder, "error.log");
    private static readonly object _lock = new();

    // Exposed so the log viewer can show the file and the "Open folder" button can reveal it.
    public static string LogFilePath => LogFile;
    public static string LogFolderPath => LogFolder;

    // Tail of the log, newest content last. Bounded because the file grows unbounded across
    // sessions and a bug report only needs the recent history - reading a 40 MB log into a
    // TextBox would hang the UI for no benefit.
    public static string ReadTail(int maxChars = 200_000)
    {
        try
        {
            lock (_lock)
            {
                if (!File.Exists(LogFile)) return "(no log file yet - nothing has been logged)";

                var text = File.ReadAllText(LogFile);
                if (text.Length <= maxChars) return text;

                return "... (truncated, older entries are in the log file)"
                       + Environment.NewLine + Environment.NewLine
                       + text[^maxChars..];
            }
        }
        catch (Exception ex)
        {
            return $"(could not read the log file: {ex.Message})";
        }
    }

    public static void Clear()
    {
        try
        {
            lock (_lock)
            {
                if (File.Exists(LogFile)) File.Delete(LogFile);
            }
        }
        catch
        {
            // Same rule as above: never crash over logging.
        }
    }

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
