using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;

namespace SkyrimCraftingTool.Model;

// Timings for the performance work (docs/Performance-Plan.md, P-P0): what took how long, on the
// user's own machine and load order, written to Logs\perf.log.
//
// WHY A FILE OF ITS OWN: the Stopwatch timings that existed went to Debug.WriteLine, which a release
// build never shows anyone - so not one number ever came back from the large modlists where the lags
// are. error.log is for errors; mixing hundreds of timing lines into it would bury them.
//
// ONLY WHAT IS SLOW. Anything under the threshold is not written at all, so on a small modlist the
// file stays practically empty and on a large one it lists exactly the places worth looking at.
//
// Logging never throws: a timing that cannot be written is a lost number, not a crash.
public static class PerfLog
{
    public const double DefaultThresholdMs = 150;

    private static readonly object _lock = new();
    private static bool _sessionHeaderWritten;

    // internal so the tests can point it at a temp file and lower the bar.
    internal static string FilePath = Path.Combine(AppLogger.LogFolderPath, "perf.log");
    internal static double ThresholdMs = DefaultThresholdMs;

    public static string PerfLogPath => FilePath;

    // A block of synchronous work: using (PerfLog.Measure("NPC tab: read database")) { ... }
    //
    // Written on Dispose, which a using block runs even when the work throws - a slow step that then
    // failed is still a slow step, and the error itself goes to error.log as always.
    public static PerfScope Measure(string what) => new(what);

    // From now until WPF has laid out and RENDERED what the current work produces.
    //
    // Call it BEFORE the work: the clock starts here, and a dispatcher callback at ContextIdle stops
    // it - that priority only runs once the work on the UI thread is done and the frame is drawn. A
    // tab switch measured only to the end of the method misses the part the user actually waits for:
    // building and laying out the view.
    //
    // Without a dispatcher (tests, or before the app is up) there is no rendering to wait for, and
    // the time is written straight away.
    public static void UntilRendered(string what)
    {
        var clock = Stopwatch.StartNew();
        var dispatcher = Application.Current?.Dispatcher;

        if (dispatcher == null)
        {
            Write(what, clock.Elapsed, "");
            return;
        }

        dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
            Write(what + " (to render)", clock.Elapsed, "")));
    }

    // A time measured elsewhere, by a Stopwatch that already existed - the scan and startup steps, whose
    // numbers went only to the debugger until now.
    public static void Record(string what, TimeSpan elapsed, string detail = "")
        => Write(what, elapsed, detail);

    // The same from a PerfClock, which also knows how much of that time .NET spent collecting garbage.
    public static void Record(string what, PerfClock clock, string detail = "")
        => Write(what, clock.Elapsed, detail, clock.Gc());

    // The newest part of the file, for the bug report - bounded like AppLogger.ReadTail, for the same
    // reason: a report needs the recent runs, not the whole history.
    public static string ReadTail(int maxChars = 50_000)
    {
        try
        {
            lock (_lock)
            {
                if (!File.Exists(FilePath)) return "(no timings yet - nothing took longer than " +
                                                   $"{ThresholdMs:0} ms)";

                var text = File.ReadAllText(FilePath);
                return text.Length <= maxChars
                    ? text
                    : "... (truncated, older timings are in perf.log)" + Environment.NewLine + text[^maxChars..];
            }
        }
        catch (Exception ex)
        {
            return $"(could not read perf.log: {ex.Message})";
        }
    }

    // One line per timing: when, how long, what. The detail is optional and is where a caller puts the
    // size of what it worked on ("4982 lists") - a time means little without the amount behind it.
    internal static void Write(string what, TimeSpan elapsed, string detail, string gc = "")
    {
        if (elapsed.TotalMilliseconds < ThresholdMs) return;

        try
        {
            lock (_lock)
            {
                var dir = Path.GetDirectoryName(FilePath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                // Once per run, so two runs in one file - before and after a change - can be told apart.
                if (!_sessionHeaderWritten)
                {
                    File.AppendAllText(FilePath,
                        $"--- {DateTime.Now:yyyy-MM-dd HH:mm:ss}  session start, tool version {ToolVersion()}, " +
                        $"{GcMode()}  (only steps of {ThresholdMs:0} ms or more are listed) ---{Environment.NewLine}");
                    _sessionHeaderWritten = true;
                }

                var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {elapsed.TotalMilliseconds,7:0} ms  {what}";
                if (!string.IsNullOrWhiteSpace(detail)) line += $"  [{detail}]";
                if (!string.IsNullOrWhiteSpace(gc)) line += $"  ({gc})";

                File.AppendAllText(FilePath, line + Environment.NewLine);
            }
        }
        catch
        {
            // A timing that cannot be written is a lost number, never a crash.
        }
    }

    // For the tests: forget that the header was written, so a fresh file gets one again.
    internal static void ResetSessionForTests()
    {
        lock (_lock) _sessionHeaderWritten = false;
    }

    // The GC mode in effect, as the runtime reports it - not as the project asks for it: an environment
    // variable (DOTNET_gcServer) can change it, and two runs are only comparable in the same mode.
    // "DATAS" is .NET 8's dynamic heap count for server GC (GarbageCollectionAdaptationMode=1).
    internal static string GcMode()
    {
        if (!System.Runtime.GCSettings.IsServerGC) return "GC workstation";

        var adaptation = AppContext.GetData("System.GC.DynamicAdaptationMode");
        bool datas = adaptation != null && Convert.ToString(adaptation) == "1";
        return datas ? "GC server + DATAS" : "GC server";
    }

    private static string ToolVersion()
        => Assembly.GetEntryAssembly()?
               .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
           ?? "unknown";
}

// The using-block half of PerfLog. Detail can be filled in once the amount is known - which is
// usually only after the work: Measure("tree"), build it, then Detail = $"{rows} rows".
public sealed class PerfScope : IDisposable
{
    private readonly string _what;
    private readonly PerfClock _clock = PerfClock.StartNew();
    private bool _disposed;

    internal PerfScope(string what) => _what = what;

    public string Detail { get; set; } = "";

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _clock.Stop();
        PerfLog.Write(_what, _clock.Elapsed, Detail, _clock.Gc());
    }
}

// A Stopwatch that also notes how much garbage collection happened while it ran - so a line in
// perf.log says how much of a slow step was the program's own work and how much was .NET pausing it
// to clean up memory. The question came from a run whose memory climbed to 2.3 GB with GCs in dense
// series; whether those cost real time was not visible from a graph.
//
// What it reads is all process-wide and cheap: GC.GetTotalPauseDuration (the time every thread
// stood still for the GC, .NET 7+), the collection counters per generation, and the managed heap size.
// A background gen2 collection runs alongside the program and pauses it only briefly, so a gen2 count
// with little pause time is not a problem; the pause time is the number that matters.
//
// Process-wide also means: two steps measured at the same time on different threads both see the
// same pauses. For the scan, where these clocks run one after another, that does not arise.
public sealed class PerfClock
{
    private readonly Stopwatch _watch = new();
    private TimeSpan _pauseAtStart;
    private readonly int[] _countsAtStart = new int[3];
    private string? _frozen;

    public static PerfClock StartNew()
    {
        var clock = new PerfClock();
        clock.Restart();
        return clock;
    }

    public TimeSpan Elapsed => _watch.Elapsed;
    public long ElapsedMilliseconds => _watch.ElapsedMilliseconds;

    public void Restart()
    {
        _frozen = null;
        _pauseAtStart = GC.GetTotalPauseDuration();
        for (int g = 0; g < 3; g++) _countsAtStart[g] = GC.CollectionCount(g);
        _watch.Restart();
    }

    // Stops the time AND the GC reading, so what happens between Stop and the log line is not counted.
    public void Stop()
    {
        _watch.Stop();
        _frozen = Describe();
    }

    // "GC 412 ms paused, 30/6/2 collections gen0/1/2, heap 1.9 GB, process 2.6 GB" - since the start.
    public string Gc() => _frozen ?? Describe();

    // heap    = what .NET's objects occupy right now (GC.GetTotalMemory).
    // process = the private memory of the whole process: managed heap plus what the GC keeps reserved
    //           around it, SQLite, WPF and native code. Added for server GC, which keeps one heap per
    //           core and can hold far more than the objects need - the heap alone would hide that.
    private string Describe()
    {
        var paused = GC.GetTotalPauseDuration() - _pauseAtStart;
        var heapGb = GC.GetTotalMemory(forceFullCollection: false) / (1024.0 * 1024 * 1024);
        return $"GC {paused.TotalMilliseconds:0} ms paused, " +
               $"{GC.CollectionCount(0) - _countsAtStart[0]}/{GC.CollectionCount(1) - _countsAtStart[1]}/" +
               $"{GC.CollectionCount(2) - _countsAtStart[2]} collections gen0/1/2, heap {heapGb:0.0} GB, " +
               $"process {ProcessGb():0.0} GB";
    }

    private static double ProcessGb()
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            return process.PrivateMemorySize64 / (1024.0 * 1024 * 1024);
        }
        catch
        {
            return double.NaN;   // a lost number, never a crash
        }
    }
}
