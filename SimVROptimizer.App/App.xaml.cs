using System.Windows;

namespace SimVROptimizer.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(args.Exception.Message, "VR Auto-Optimizer", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        var continueSession = e.Args.Any(argument => argument.Equals("--continue-session", StringComparison.OrdinalIgnoreCase));
        MainWindow = new MainWindow(continueSession);
        MainWindow.Show();
    }
}
