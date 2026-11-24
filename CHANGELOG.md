# Changelog

Older release notes. The newest is in the [README](README.md#whats-new-in-2521) and on the [releases page](https://github.com/V3rkku1/steam-stopper/releases).

## 2.5.20

- **Bond every app** (optional): a virtual network adapter from net-merge bonds every app, TCP and UDP, not only Steam and browsers. The Bond page lists its downsides before you turn it on.
- **Faster bonding**: Steam's download connections are kept open and reused instead of reconnecting for every chunk.
- **Steadier bonding**: a slow or refusing server no longer knocks out a link, speed estimates don't jump on one burst, and connections stay on their adapter even with Mobile Hotspot on.
- **Fetch** asks each link's own DNS, so every link downloads from a server near its provider.

## 2.5.19

- **Routes for Steam**: pin Steam's downloads and store to one connection; games stay on the main one.
- **Updates and DLC** get a badge and sort under their game; Play starts the game they belong to.
- **Saves import** from files, folders or zips.
- **Bonding survives a link dropping**, and stops cleanly when every link is offline.

## 2.5.18

- **PS4 updates**: Play on an update .pkg installs it beside its game for shadPS4.

## 2.5.17

- **PS4 .pkg extraction** now uses shadPS4Plus's PKG extractor, and packages cut into pieces (part 1, part 2…) are joined first.

## 2.5.16

- **PS4 .pkg games** are extracted with Orbis when you press Play, then run in shadPS4; shadPS4 opens again from Emulators.
- **Switch**: split .nsp/.xci for FAT32 SD cards, and convert .nsz/.xcz back to .nsp/.xci with nsz.

## 2.5.15

- **Fetch retries failed downloads by itself**, up to 10 times in a row with growing waits.

## 2.5.14

- **Console mode**: a fullscreen game row for keyboard or controller (F11).
- **Steam, Epic and Heroic games** appear in the Games library and start in their launcher.

## 2.5.13

- **PS5 unpacking**: choose the unpack folder, see speed and time left, stop an unpack, and pick up one still running after the app closed.

## 2.5.12

- **PS5 images**: .ffpfs, .ffpfsc and .ffpkg games appear in Games, and Play unpacks them for KytyPS5 with PS5 Dump Forge.

## 2.5.11

- **New look**: near-black theme with a blue accent, flat cards, a simpler sidebar and plain text tabs.
- **Switch 2 covers** from Nintendo's eShop, and a lighter download watcher.

## 2.5.10

- **Covers you can pick**: Change cover searches every box art source and shows what it found; Find missing covers goes through every game without one. PS3 covers now come from GameTDB too.
- **Games tab**: rename games, `.pkg` games get their real name, games installed in RPCS3, shadPS4 and Vita3K appear by themselves, RetroArch can start games, and PS2/Dreamcast `.chd` files sort by folder.

## 2.5.9

- **More game files in Games**: PS3, PS4 and Vita `.pkg`, Vita `.vpk`, PS3 and PS4 game folders (titled and with their own icon), plus `.nro`, `.cxi`, `.wad`, `.gcm` and `.fds`. RPCS3, shadPS4 and Vita3K can now start games.

## 2.5.8

- **Faster Fetch**: no more crawl at 99% (idle connections help finish the last pieces), and Auto connections reach full speed sooner.

## 2.5.7

- **Faster bonding**: links share connections by their measured speed, a dead link drops out within seconds, each link uses its own DNS to reach its nearest server, and games and voice chat stay on the quickest link.
- **Fetch Auto connections**: adds connections while the download keeps getting faster.
- **Download doctor** checks Windows network tuning and Wi-Fi power saving, with one-click fixes.
- **Games** appear by themselves when you drop them into your games folder; every pop-up now matches your theme.

## 2.5.6

- **Tidier menu**: six groups (Home, Steam, Games, Fetch, Network, Tools); a group's pages are tabs at the top of the window.
- **Fetch**: continue a broken or expired download from a new link without losing progress; fixed resume after pause and connection speeds that blinked out.
- **Achievement banners**: new RetroAchievements unlocks pop up in the corner while you play.

## 2.5.5

Steam Stopper is now **Steam Multitool**, with a new icon, a splash screen and a faster start.

- **Home**: your games, downloads, Steam and updates at a glance, with a Continue playing shelf.
- **Settings** (gear in the corner): themes (Steam Multitool, Steam, Liquid Glass, From my game, or your own CSS file), menu on the left or on top, drag menu items to reorder, start page, close button.
- **Network**: live download and upload per app, with Block.
- **Download doctor**: finds out whether your line, Steam or distance makes Steam downloads slow.
- **Fetch**: shut down or sleep when all downloads finish, speed limit, right-click menu, time left.
- **Games**: RetroAchievements on each game's page, save backups for emulators and PC ports, game boost.
- **PC ports**: Zelda64Recomp, Ship of Harkinian, 2Ship2Harkinian and Starship join Hookshot and Dusklight.
- Smooth page transitions, a start-up animation and dark tooltips and menus everywhere.

## 2.5.4

- **Switch and Wii U box art**: covers now download automatically for Switch and Wii U games too, matched by title.
- **PC ports**: a new PC ports tab on the Emulators page installs Hookshot (Ocarina of Time) and Dusklight (Twilight Princess). Both build the game from your own copy.
- **Check for updates on the Emulators tab** checks your emulators and ports right away.

## 2.5.3

- **Stop button**: while a game runs, Play turns into a blue Stop button like Steam's. It closes the emulator so it can save, and force-closes it if it hangs.
- **Automatic box art for more games**: covers are matched by game title, so files don't need the exact No-Intro name. Xbox 360, NES, Game Boy and Game Boy Color games get art too.
- **Check for updates** in the sidebar and tray checks the app and your emulators right away; **What's new** shows the release notes in the app.

## 2.5.2

- **Emulator installs fixed**: Lichii, Eglantine, Ryujinx and Azahar download the right Windows build again; every emulator's download is now checked against its real release page on each build.
- **Install from a zip**: if a download can't be fetched, pick the .zip you downloaded yourself. Emulators unzipped by hand into `%LOCALAPPDATA%\SteamStopper\Emulators\<name>` are picked up too.

## 2.5.1

- **PS5**: KytyPS5 on the Emulators tab, and PS5 game folders and `.zar` dumps in the Games tab.
- **Switch 2**: Eglantine joins Lichii, and `.nup` game archives open in either.
- **Emulators update themselves** when a new build comes out (while closed), with a **PGO builds** switch for faster builds.
- **Add to Steam**: one button puts your games in Steam as non-Steam games, with box art.

## 2.5

- **Games**: a Steam-style library for your own game files. Pick a game on the left, press the big green Play button on the right, and it opens in the right emulator. Box art, last played and play time included.
- **Emulators**: pick a console from the tiles and install its emulator in one click. Downloads go through Fetch, so bonding speeds them up, and installed emulators get a Play button.
- **Better updater**: asks before updating, checks the download, and puts the old version back if anything goes wrong. Portable copies update too.
- **Safer bonding**: if Steam Multitool crashes or is closed from Task Manager while bonding or routes are on, your internet is put back automatically.
- **Fetch**: turn bonding on and pick connections right on the page; downloads from GitHub no longer fail when their link expires.

## 2.4

- **Fetch**: a download manager. Paste links and each file downloads in pieces over all your bonded connections at once, with pause, queue and resume after a restart.
- **Bond**: when every link failed recently, bonding now retries the one that failed longest ago instead of getting stuck on a dead one.

## 2.3

- **System tray** (2.3.1): the X button can minimize to the tray, and the tray icon's right-click menu has Steam blocking, bonding and quit.

- **Bond**: download Steam games over two internet connections at once, for example Ethernet plus your phone.
- **Routes**: choose which connection each app uses, like Valorant on Ethernet and Discord on your phone.
- New app icon.
