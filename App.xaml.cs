using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using SkyrimCraftingTool.Model;

namespace SkyrimCraftingTool;

public partial class App : System.Windows.Application
{
    // Global single-instance guard. The tool is the only writer of its SQLite DB and of the
    // COBJ FormID counter - a second instance racing on the same files would corrupt both.
    // Deliberately global (not keyed on the DB/output path), so two separate tool copies pointing
    // at different setups would also block each other; acceptable for a personal tool.
    private const string SingleInstanceMutexName = @"Global\SkyrimCraftingTool.SingleInstance";
    private Mutex? _instanceMutex;

    public App()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        System.Windows.Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;

        if (!TryAcquireSingleInstanceLock())
        {
            System.Windows.MessageBox.Show(
                "Skyrim Crafting Tool is already running.",
                "Already Running", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        var window = new View.FolderChoiceWindow(); // Startup window for folder choice
        bool? result = window.ShowDialog();

        if (result != true)
        {
            Shutdown();
            return;
        }

        try
        {
            Program.Handler(); // Initialisierung
        }
        catch (Exception ex)
        {
            AppLogger.LogError("Startup initialization failed (Program.Handler)", ex);
            System.Windows.MessageBox.Show(
                $"Initialization failed:{Environment.NewLine}{ex.Message}{Environment.NewLine}{Environment.NewLine}Details were saved to Logs\\error.log.",
                "Startup Error", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
            return;
        }

        var main = new View.MainWindow();
        main.Show();

        System.Windows.Application.Current.ShutdownMode = ShutdownMode.OnMainWindowClose;
    }

    private bool TryAcquireSingleInstanceLock()
    {
        try
        {
            _instanceMutex = new Mutex(initiallyOwned: false, SingleInstanceMutexName);
            try
            {
                return _instanceMutex.WaitOne(TimeSpan.Zero, exitContext: false);
            }
            catch (AbandonedMutexException)
            {
                // Previous instance was killed without releasing - we still own it now.
                return true;
            }
        }
        catch (Exception ex)
        {
            // Don't let a mutex problem (e.g. a locked-down environment) block startup entirely.
            AppLogger.LogError("Single-instance mutex acquisition failed; continuing without the guard", ex);
            return true;
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _instanceMutex?.ReleaseMutex();
        }
        catch { /* not owned / already released */ }
        finally
        {
            _instanceMutex?.Dispose();
            _instanceMutex = null;
        }

        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object? sender, DispatcherUnhandledExceptionEventArgs e)
    {
        AppLogger.LogError("Unhandled UI-thread exception", e.Exception);
        System.Windows.MessageBox.Show(
            $"An unexpected error occurred:{Environment.NewLine}{e.Exception.Message}{Environment.NewLine}{Environment.NewLine}Details were saved to Logs\\error.log. The program will continue, but should be restarted if the error recurs.",
            "Unexpected Error", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }

    private void OnAppDomainUnhandledException(object? sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
            AppLogger.LogError("Unhandled non-UI-thread exception (fatal)", ex);
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        AppLogger.LogError("Unobserved task exception", e.Exception);
        e.SetObserved();
    }
}
