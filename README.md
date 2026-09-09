# Steam Stopper

Native **C# / WPF** Windows app for your own Steam client: internet kill switch, download watcher
with auto-shutdown, and maintenance tools.

This is not a Steam emulator or DRM bypass. It only reads Steam’s own files and drives
Windows Firewall, processes, and power management.

## Run

Double-click `run.bat`, or:

```
dotnet run --project src/SteamStopper -c Release
```

The built exe lives at `src/SteamStopper/bin/Release/net8.0-windows/SteamStopper.exe`.

Requires the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) (already used to build this copy).

Firewall changes need **Administrator**. Use **Relaunch as Administrator** in the sidebar.

## Features

- Block / restore Steam internet via `SteamStopper-*` Windows Firewall rules
- Watch live downloads from `appmanifest_*.acf` and shut down, sleep, lock, or exit Steam when they finish
- Kill / launch Steam, including offline mode
- Library browser with search and disk size
- Cache cleanup and config + userdata backup

Settings are stored in `%APPDATA%\SteamStopper\settings.json`.
