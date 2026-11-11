## Faster Fetch

- **No more crawl at 99%**: near the end, idle connections split the last pieces between them down to 128 KB, instead of leaving the final megabytes on one slow connection.
- **Auto connections ramp up faster**: start with 6 and add 4 every 2 seconds while the download keeps getting faster (up to 32).

## Downloads

- **SteamStopper.msi**: installer, recommended. Auto-updates.
- **SteamStopper-portable.zip**: no install, includes .NET.
- **SteamStopper.zip**: auto-update package used by installed copies.
- **\*.sha256**: checksums for the files above.
