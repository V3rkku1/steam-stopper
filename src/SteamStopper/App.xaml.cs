using System.Windows;
using SteamStopper.Core;

namespace SteamStopper;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        if (!SteamClient.IsAdmin())
        {
            try { SteamClient.RelaunchAsAdmin(); }
            catch { }
            Shutdown();
            return;
        }
        base.OnStartup(e);
    }
}
