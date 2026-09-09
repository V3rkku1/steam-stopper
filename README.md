# Steam Stopper

Windows WPF app for your own Steam client: firewall kill switch, download watcher,
DNS jumper, junk cleaner, and a small app installer.

This is not a Steam emulator or DRM bypass. It only reads Steam’s own files and
drives Windows Firewall, processes, DNS, and power management.

## Run

Double-click `run.bat`, or:

```
dotnet run --project src/SteamStopper -c Release
```

Requires the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).
The app starts as Administrator.

## Send to a friend

Run `pack.bat`, then send **Steam Stopper Setup.zip** from the Desktop.
They unzip it and run `Install.bat`.

Updates come from [github.com/finyt56/steam-stopper](https://github.com/finyt56/steam-stopper) releases.

Settings are stored in `%APPDATA%\SteamStopper\settings.json`.
