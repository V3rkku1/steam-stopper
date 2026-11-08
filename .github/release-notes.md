## Fixed

- **Emulator installs**: Lichii, Eglantine and Ryujinx could fail to download and only opened a web page; Azahar picked the RetroArch core instead of the emulator. All 17 emulators now resolve to the right Windows build, checked against their real release pages.
- When an automatic download still fails, Install now says why instead of silently opening the download page.

## New

- **Install from a zip you downloaded**: Install offers to use a .zip or .7z you already have. Your file is not deleted.
- **Manual installs are picked up**: an emulator unzipped into `%LOCALAPPDATA%\SteamStopper\Emulators\<name>` (for example `...\Emulators\Lichii`) gets Play, Update and Remove like a downloaded one.

## Downloads

- **SteamStopper.msi**: installer, recommended. Auto-updates.
- **SteamStopper-portable.zip**: no install, includes .NET.
- **SteamStopper.zip**: auto-update package used by installed copies.
- **\*.sha256**: checksums for the files above.
