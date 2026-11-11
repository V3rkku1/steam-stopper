## New

- **Faster bonding** (Network > Bond):
  - Connections are shared by each link's measured speed, not only the weight you set (switch: Balance by measured speed).
  - A link that stops answering drops out within about 4 seconds and is used again once it's back; the list shows "offline".
  - Each link asks its own DNS, so it downloads from the server nearest to its provider.
  - Games, emulators and Discord stay on the link with the lowest ping instead of being spread (switch: Keep games and voice chat on the quickest link).
- **Fetch Auto connections** (new default): starts with 4 and adds more while the download keeps getting faster, up to 32.
- **Download doctor** checks Windows TCP auto-tuning and Wi-Fi power saving when plugged in, with a Fix button for each.
- **Folder watch**: games dropped or copied into your games folders appear by themselves, with covers.
- **Themed pop-ups**: every question and message now matches your theme instead of Windows' white box.

## Downloads

- **SteamStopper.msi**: installer, recommended. Auto-updates.
- **SteamStopper-portable.zip**: no install, includes .NET.
- **SteamStopper.zip**: auto-update package used by installed copies.
- **\*.sha256**: checksums for the files above.
