# Steam Stopper

<p align="center">
  <img src="docs/logo.png" width="96" alt="Steam Stopper">
</p>

<p align="center">
  <strong>Windows desktop app for your Steam client</strong><br>
  Firewall kill switch · download watcher · DNS jumper · cleaner · app installer
</p>

<p align="center">
  <a href="https://github.com/V3rkku1/steam-stopper/releases/latest"><img src="https://img.shields.io/github/v/release/V3rkku1/steam-stopper?label=latest" alt="Latest release"></a>
  <a href="https://github.com/V3rkku1/steam-stopper/releases/latest"><img src="https://img.shields.io/github/downloads/V3rkku1/steam-stopper/total?label=downloads" alt="Downloads"></a>
  <img src="https://img.shields.io/badge/platform-Windows%2010%20%2F%2011-0078D6" alt="Windows">
  <img src="https://img.shields.io/badge/.NET-8-512BD4" alt=".NET 8">
  <img src="https://img.shields.io/badge/license-MIT-green" alt="MIT">
</p>

<p align="center">
  <a href="https://github.com/V3rkku1/steam-stopper/releases/latest/download/SteamStopper.msi"><strong>Download installer (.msi)</strong></a>
  &nbsp;·&nbsp;
  <a href="https://github.com/V3rkku1/steam-stopper/releases/latest/download/SteamStopper-portable.zip"><strong>Download portable (.zip)</strong></a>
</p>

Steam Stopper is a local Windows utility for managing your Steam client: cut or restore internet access, watch downloads and act when they finish, switch DNS, clean junk, and install common tools.

It is **not** a Steam emulator, crack, or DRM bypass. It only reads Steam’s own files and uses Windows Firewall, processes, DNS, and power settings on this PC.

## Get started

### Installer (recommended)

1. Download [`SteamStopper.msi`](https://github.com/V3rkku1/steam-stopper/releases/latest/download/SteamStopper.msi)
2. Run the installer
3. Open **Steam Stopper** from the Start menu or Desktop

Installs to Program Files, creates shortcuts, and installs the .NET 8 Desktop Runtime if needed. Installed copies check for updates on launch.

Windows will ask for **Administrator** — firewall and DNS changes require it.

### Portable

1. Download [`SteamStopper-portable.zip`](https://github.com/V3rkku1/steam-stopper/releases/latest/download/SteamStopper-portable.zip)
2. Unzip anywhere
3. Run `SteamStopper.exe`

No install and no auto-update. Includes the .NET runtime, so it runs on a clean Windows PC.

## Features

| | |
|---|---|
| **Control** | Block or restore Steam internet with Windows Firewall. Quit, launch, or restart Steam. |
| **Downloads** | Watch download speed, then shut down, sleep, lock, or exit Steam when it finishes. |
| **Library** | Browse installed games and sizes, back up config, clear Steam caches. |
| **DNS** | Switch providers, ping for the fastest, restore automatic DNS. |
| **Clean** | Remove temp files, caches, Recycle Bin, and leftover Steam junk. |
| **Apps** | Install common tools (Discord, 7-Zip, AnyDesk, and more) from one place. |

Settings live in `%APPDATA%\SteamStopper\settings.json`.

## Requirements

- Windows 10 or 11 (64-bit)
- Administrator for firewall and DNS
- .NET 8 Desktop Runtime for the MSI install (installed automatically if missing)

## Build from source

Requires the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

```bat
run.bat
```

Or:

```bat
dotnet run --project src/SteamStopper -c Release
```

Rebuild packages:

```bat
pack.bat
```

Output in `dist\`:

| File | Purpose |
|---|---|
| `SteamStopper.msi` | Installer |
| `SteamStopper-portable.zip` | Portable app |
| `SteamStopper.zip` | Auto-update package for installed copies |

## License

[MIT](LICENSE)
