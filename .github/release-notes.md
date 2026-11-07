## New

- **Games**: a Steam-style library for your own game files. The left side lists your games by console; the right shows box art, a big green **Play** button, last played and play time. Add a folder (or drop files), and each game opens in the matching emulator.
- **Emulators**: a page of console tiles (PlayStation, Switch, Switch 2, 3DS, Wii U, Wii, Xbox and more). Click one to see its emulators and install with one click. Downloads go through Fetch, so bonding speeds them up, and installed emulators get **Play** and **Remove**. Games, BIOS, keys and firmware are not included.
- **Better updater**: Steam Stopper now asks before updating (What's new, Skip, Later, Update now), checks the download against a checksum, and restores the old version if anything fails. Click the version number to check any time. Portable copies get updates too.
- **Fetch**: a "Use all my connections" switch with your connections listed right on the page.

## Fixed

- If Steam Stopper crashed or was ended from Task Manager while bonding or routes were on, browsers and Discord lost the internet until it was reopened. A watchdog now puts everything back right away.
- Downloads from GitHub and similar sites failed with 403 when their temporary link expired mid-download; they now pick up a fresh link and continue.
- Helper processes (watchdog, admin restart) crashed on exit.
- The Fetch file list hid the file name column, and opening Fetch could show "Request not supported."

## Note

Updating from 2.4 happens the old way, once: it restarts right away. From 2.5 on, you'll be asked first.

## Downloads

- **SteamStopper.msi**: installer, recommended. Auto-updates.
- **SteamStopper-portable.zip**: no install, includes .NET.
- **SteamStopper.zip**: auto-update package used by installed copies.
- **\*.sha256**: checksums for the files above.
