## PS4

- **Play extracts PS4 .pkg games by itself** with Orbis (install it from Emulators → PS4), then starts the game folder in shadPS4. shadPS4 can't install .pkg files any more. A stopped extract carries on where it stopped.
- **shadPS4 opens again** from Emulators: newer builds are command line only, so it starts in its own game picker (-b). Games installed in shadPS4 are found from its new config.json.

## Switch

- **Split for FAT32** on a game page cuts an .nsp or .xci over 4 GB into parts Switch installers read as one file (an _split.nsp folder, or .xc0, .xc1…). The original stays.
- **Convert to NSP / XCI** turns .nsz and .xcz games back into normal ones with nsz (install it from Emulators → Switch). It uses your own prod.keys from Eden, Ryujinx or %USERPROFILE%\.switch.
- .nsz and .xcz files now show up as Switch games.

## Other

- PS4 and PS5 games unpack into the Unpack folder from Settings; Play asks for the emulator before a long unpack.

## Downloads

- **SteamStopper.msi**: installer, recommended. Auto-updates.
- **SteamStopper-portable.zip**: no install, includes .NET.
- **SteamStopper.zip**: auto-update package used by installed copies.
- **\*.sha256**: checksums for the files above.
