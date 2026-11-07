using System.Windows;
using SteamStopper.Core;

namespace SteamStopper;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        if (e.Args is [Watchdog.Arg, var pid] && int.TryParse(pid, out var appPid))
        {
            Watchdog.Run(appPid);
            Shutdown();
            return;
        }
        if (!SteamClient.IsAdmin())
        {
            try { SteamClient.RelaunchAsAdmin(); }
            catch { }
            Shutdown();
            return;
        }
        // One failed action (missing steam.exe, locked file, netsh error) should not close the app.
        DispatcherUnhandledException += (_, args) =>
        {
            args.Handled = true;
            MessageBox.Show(args.Exception.Message, "Steam Stopper", MessageBoxButton.OK, MessageBoxImage.Warning);
        };
        base.OnStartup(e);
    }
}
