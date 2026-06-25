using System.Windows;

namespace CodexUpdater.App;

public partial class App : System.Windows.Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var window = new MainWindow(AppCommandLine.Parse(e.Args));
        MainWindow = window;
        window.Show();
    }
}
