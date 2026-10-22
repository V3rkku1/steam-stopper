## Bonding fixes

- **2.3.3: bonding uses every link again.** 2.3.2 sent almost everything to whichever link got going first; requests are now shared out in turn (by weight).
- **2.3.3: HTTPS connections no longer cut off.** In 2.3.2 every HTTPS connection through the bonder was closed 45 seconds after it opened, even mid-download. Quiet ones (Steam login, Discord) now get 90 seconds.
- **2.3.3: survives the main internet going down.** Bonding remembers the last address for each server, so the other links keep downloading when the main link's DNS stops answering.
- **Faster, steadier downloads**: Steam's requests reuse open connections on each link instead of opening a new one per chunk.
- **No more slowdowns from dead connections**: a connection with no traffic for 45 seconds is closed, so Steam retries on a working link.
- **Links follow IP changes**: when a phone tether reconnects or gets a new IP, bonding switches to it automatically. No Rescan needed.
- **Correct speeds**: the Bond page shows download speed, smoothed, and the Downloads page shows the combined speed of all links.

## Downloads

- **SteamStopper.msi**: installer, recommended. Auto-updates.
- **SteamStopper-portable.zip**: no install, includes .NET.
- **SteamStopper.zip**: auto-update package used by installed copies.
