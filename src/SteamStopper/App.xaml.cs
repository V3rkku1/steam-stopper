using System.Windows;
using SteamStopper.Core;

namespace SteamStopper;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        if (e.Args is [Updater.InstallArg, var oldPid, var dest] && int.TryParse(oldPid, out var updatingPid))
        {
            // Running as the new version from the download folder: swap the files, then start the result.
            var error = Updater.Install(updatingPid, AppContext.BaseDirectory, dest);
            if (error is not null)
                MessageBox.Show($"The update could not be installed, so the previous version was kept.\n\n{error}",
                    "Steam Stopper update", MessageBoxButton.OK, MessageBoxImage.Warning);
            Shutdown();
            return;
        }
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
        // Created here rather than through StartupUri: the helper modes above must exit without ever building
        // the window, or its close prompt runs during their shutdown and crashes the process.
        new MainWindow().Show();
    }
}
