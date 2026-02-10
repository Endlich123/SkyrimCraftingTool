using System.Windows;


namespace SkyrimCraftingTool;

public partial class App : System.Windows.Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        System.Windows.Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;

        var window = new FolderChoiceWindow();
        bool? result = window.ShowDialog();

        if (result == true)
        {
            Program.Handler(); // start program
        }

            if (result != true)
        {
            Shutdown();
            return;
        }

        new MainWindow().Show();

        // change modus
        System.Windows.Application.Current.ShutdownMode = ShutdownMode.OnMainWindowClose;
    }

}
