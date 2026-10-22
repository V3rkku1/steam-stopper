## 2.3.5: bonding back to 2.3.1

2.3.2–2.3.4 changed how bonding hands out connections and made downloads slower. 2.3.5 puts that back exactly as it was in 2.3.1 and keeps only these fixes:

- **Correct speeds**: the Bond page shows download speed, smoothed, and the Downloads page shows the combined speed of all links.
- **Links follow IP changes**: when a phone tether reconnects with a new IP, bonding switches to it. No Rescan needed.
- **Survives the main internet going down**: the other links keep working with the last known server addresses.
- **Easier to see what's wrong**: a link that can't reach the internet shows *can't connect*, and the Bond page says when Steam isn't using bonding yet (restart Steam).

## Downloads

- **SteamStopper.msi**: installer, recommended. Auto-updates.
- **SteamStopper-portable.zip**: no install, includes .NET.
- **SteamStopper.zip**: auto-update package used by installed copies.
