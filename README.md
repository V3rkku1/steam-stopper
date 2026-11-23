# Steam Multitool

<p align="center">
  <img src="docs/logo.png" width="96" alt="Steam Multitool">
</p>

<p align="center">
  <strong>Windows desktop app for your Steam client</strong> (formerly Steam Stopper)<br>
  Firewall kill switch · download watcher · DNS jumper · link bonding · download manager · emulators · per-app routes · cleaner · Downloads organizer · app installer
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

Steam Multitool is a local Windows utility for managing your Steam client: cut or restore internet access, watch downloads and act when they finish, combine two internet connections for faster downloads, choose which connection each app uses, switch DNS, clean junk, and install common tools.

It is **not** a Steam emulator, crack, or DRM bypass. It only reads Steam’s own files and uses Windows Firewall, processes, DNS, routing, proxy, and power settings on this PC.

**Contents:** [Get started](#get-started) · [Features](#features) · [Link bonding](#link-bonding) · [Fetch](#fetch-download-manager) · [Routes](#per-app-routes) · [Games and emulators](#games-and-emulators) · [Requirements](#requirements) · [Build](#build-from-source)

## What's new in 2.5.20

- **Bond every app** (optional): a virtual network adapter from net-merge bonds every app, TCP and UDP, not only Steam and browsers. The Bond page lists its downsides before you turn it on.
- **Faster bonding**: Steam's download connections are kept open and reused instead of reconnecting for every chunk.
- **Steadier bonding**: a slow or refusing server no longer knocks out a link, speed estimates don't jump on one burst, and connections stay on their adapter even with Mobile Hotspot on.
- **Fetch** asks each link's own DNS, so every link downloads from a server near its provider.

Older versions: [changelog](CHANGELOG.md) · [all releases](https://github.com/V3rkku1/steam-stopper/releases)

## Get started

### Installer (recommended)

1. Download [`SteamStopper.msi`](https://github.com/V3rkku1/steam-stopper/releases/latest/download/SteamStopper.msi)
2. Run the installer
3. Open **Steam Multitool** from the Start menu or Desktop

Installs to Program Files, creates shortcuts, and installs the .NET 8 Desktop Runtime if needed. Checks for updates at launch and every few hours, and asks before installing. **Check for updates** in the sidebar (or the tray menu) checks the app and your emulators right away and shows when it last checked; **What's new** shows the release notes in the app.

Windows will ask for **Administrator** — firewall and DNS changes require it.

### Portable

1. Download [`SteamStopper-portable.zip`](https://github.com/V3rkku1/steam-stopper/releases/latest/download/SteamStopper-portable.zip)
2. Unzip anywhere
3. Run `SteamStopper.exe`

No install. Includes the .NET runtime, so it runs on a clean Windows PC. It offers updates too and replaces only its own exe.

## Features

| | |
|---|---|
| **Control** | Block or restore Steam internet with Windows Firewall. Quit, launch, or restart Steam. |
| **Downloads** | Watch download speed, then shut down, sleep, lock, or exit Steam when it finishes. |
| **Library** | Browse installed games and sizes, back up config, clear Steam caches. |
| **DNS** | Switch providers, ping for the fastest, restore automatic DNS. |
| **Routes** | Pick which internet each app uses: keep games like Valorant on Ethernet and send Discord, browsers or Spotify over your phone. |
| **Bond** | Combine two internet connections (for example Ethernet plus your phone) so Steam downloads use both at once. Optionally bond every app. |
| **Fetch** | Download manager: paste links and each file downloads in pieces over all your bonded connections at once. Pause, resume after a restart, queue several files. |
| **Clean** | Remove temp files, caches, Recycle Bin, and leftover Steam junk. |
| **Organize** | Sort a messy Downloads folder into Installers, Archives, Documents, Images and more. One-click undo. |
| **Apps** | Install or remove 56 common apps (Discord, Brave, OBS, 7-Zip, and more), then clear the folders they leave behind in AppData and Program Files. |
| **Games** | A Steam-style library of your own game files, grouped by console, with box art and a **Play** button that opens the right emulator. [More](#games-and-emulators) |
| **Emulators** | Install console emulators from PlayStation 1 to 5, Switch and Switch 2, Xbox, Nintendo handhelds and retro consoles, kept up to date. [More](#games-and-emulators) |

**System tray**: pressing **X** asks whether to minimize to the tray or quit (tick *Remember my choice* to stop asking). Right-click the tray icon to block or allow Steam's internet, turn bonding on or off, or quit.

Settings live in `%APPDATA%\SteamStopper\settings.json`.

## Link bonding

The **Bond** page lets Steam download over two or more internet connections at once, for example your Ethernet plus your phone.

1. Connect the second internet: plug in your phone with **USB tethering** on, or join its **hotspot** over Wi-Fi while Ethernet stays plugged in.
2. Open **Bond** and press **Rescan**. Every connection that has its own gateway is listed.
3. Tick the connections to combine. Give a faster one a higher **weight** so it gets more of the traffic.
4. Press **Start bonding**. Steam restarts and its downloads are spread across the links. The per-link speed columns show it working.

**How it works:** Steam Multitool runs a small proxy on `127.0.0.1` and points the Windows proxy setting at it. Steam downloads a game over many parallel connections, and each Steam request goes out the link with the most room, so the speeds add up. Connections to Steam's servers are kept open per link and reused. Each link asks its own DNS, so it downloads from a server near its provider. Games and voice chat stay on the quickest link. A link that stops answering is skipped within seconds.

One single connection still runs over one link, so one big HTTPS file in a browser won't get faster; use [Fetch](#fetch-download-manager) for that.

Stopping bonding or closing the app puts your old proxy setting back. If the app crashes, it is restored the next time Steam Multitool starts. Mobile data counts against your phone plan, so watch the totals.

### Bond every app (optional)

The proxy only reaches Steam and browsers. Switch on **Bond every app** to bond everything, TCP and UDP. This mode uses [net-merge](https://github.com/net-merge/net-merge) (MIT), which adds a Wintun virtual network adapter. It is downloaded from GitHub the first time, and stopped when bonding stops or the app closes.

Downsides, also shown on the Bond page before it turns on:

- Games and voice chat are spread over the links too, so they can lag or drop when a link is slow.
- Sites and game servers may see you coming from two addresses at once; some log you out or ask for a captcha.
- Links no longer ask their own DNS.
- It adds a virtual adapter and routes. Some VPNs, anti-cheat and firewall software may not like that.

## Fetch (download manager)

The **Fetch** page downloads any file, not just Steam games, over all your internet connections at once. Turn on **Use all my connections** and tick the ones to combine right on the page (the same list as the **Bond** page). Bonding itself does not need to be on.

1. Paste one or more `http://` or `https://` links (one per line). A link on the clipboard is filled in for you.
2. Pick the folder, the **connections per file** (8 is a good start) and how many **files at once**.
3. Press **Download**. Each row shows which links are carrying it and how fast.

How it works: the file is split into pieces that download in parallel, and each piece's connection is pinned to one link, so a single big file uses all of them. When a link finishes its piece, it takes over half of the biggest piece still running, so faster links end up doing more. A link that fails is skipped for 20 seconds and its piece moves to another one.

Progress is kept in `name.part.json` next to the unfinished `name.part`, so **Pause**, closing the app or losing the internet doesn't lose anything. If the server doesn't allow resuming, the file downloads over one connection and starts over after a failure. Servers that limit connections per user may refuse many connections; lower the count if a download keeps failing.

## Per-app routes

The **Routes** page chooses which internet connection each app uses, for example Valorant on Ethernet and Discord on your phone.

1. **Main connection**: pick your steadiest link (usually Ethernet) and press **Make main**. Everything without its own route uses it, including games. **Restore automatic** undoes it.
2. **Apps**: pick a connection next to Steam, Discord, Chrome, Edge, Brave, Opera GX or Spotify and press **Apply**. The app restarts on that link.

How it works: Steam Multitool never touches the game, so anti-cheat such as Vanguard has nothing to see. A routed app is restarted with `--proxy-server` pointing at a local proxy pinned to its link. Discord voice runs over UDP, which a proxy can't carry, so Discord also gets a Windows route that sends Discord's voice servers (`66.22.192.0/18`) out the same link. That route disappears on reboot.

Steam has no proxy switch, so routing it points the Windows proxy setting at its link instead; other apps that follow that setting go the same way. Games are left alone: Steam's downloads and store use the link, game traffic stays on the main connection. Bonding and a Steam route both use the Windows proxy setting, so only one can be on at a time.

Closing Steam Multitool or pressing **Reset all** restarts routed apps on the main connection. If the routed link drops, that app goes offline until you switch it back.

## Games and emulators

**Games** is a library of your own game files. The list on the left is grouped by console; the right shows box art, a big **Play** button, last played and play time. Add folders (or drop files), and each game opens in the matching emulator from the Emulators tab. Updates and DLC get a badge and sort under their game. Switch 2 `.nup` archives open in Lichii or Eglantine, and PS5 game folders and `.zar` dumps in KytyPS5. Box art comes from libretro-thumbnails when the file is named like the official release; you can set your own.

**Emulators** installs console emulators by system: PlayStation 1 to 4, PSP and Vita, Switch, DS and 3DS, Wii U, Wii and GameCube, Xbox and Xbox 360, retro consoles, and all-in-one apps like RetroArch. Switch 2 has the experimental Lichii and Eglantine; PS5 has KytyPS5, an experimental emulator in active development.

- Most emulators install as portable builds, downloaded through Fetch (so bonding speeds them up) into `%LOCALAPPDATA%\SteamStopper\Emulators`; the rest use winget.
- New builds install by themselves while the emulator is closed. Switch Auto-update off to press **Update** yourself.
- **PGO builds** (on by default) takes an emulator's profile-guided build when it publishes one.
- If a download can't be fetched, Install offers to use a .zip you downloaded yourself. An emulator unzipped by hand into `%LOCALAPPDATA%\SteamStopper\Emulators\<name>` is picked up too.

Games, BIOS, keys and firmware are not included.

## Requirements

- Windows 10 or 11 (64-bit)
- Administrator (firewall, DNS and routes need it)
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
| `*.sha256` | Checksums; the updater refuses a download that doesn't match |

## Making a release

Releases are built by GitHub Actions ([`.github/workflows/release.yml`](.github/workflows/release.yml)) on a Windows runner.

1. Bump the version in `src/SteamStopper/SteamStopper.csproj`, `installer/SteamStopper.wixproj`, `pack.bat` and the fallback in `src/SteamStopper/Core/Updater.cs`.
2. Write [`.github/release-notes.md`](.github/release-notes.md): a one-line summary first (the app's update banner shows it), then sections. The app's **What's new** shows everything above `## Downloads`, as plain text.
3. Add a short "What's new" to this README and move the previous one to [`CHANGELOG.md`](CHANGELOG.md).
4. Commit, then push a tag (`git tag v2.5.20` and `git push origin v2.5.20`), or start **Release** from the **Actions** tab with **Run workflow**.

The workflow builds the MSI, the portable zip and the auto-update zip, and publishes them as a GitHub release. Running copies offer it within a few hours, or right away on their next launch.

## License

[MIT](LICENSE)
