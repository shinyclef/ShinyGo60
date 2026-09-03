using System.Windows;
using ShinyGo60.Builder.Core.Workspaces;

namespace ShinyGo60.Builder;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        try
        {
            string installationRoot = BuilderInstallationLocator.FindRoot(AppContext.BaseDirectory);
            MainWindow window = new(installationRoot, e.Args);
            this.MainWindow = window;
            window.Show();
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                exception.Message,
                "ShinyGo60 Builder could not start",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            this.Shutdown(1);
        }
    }
}
