## Bonding

- **Bond every app (optional)**: turn on "Bond every app" on the Bond page and every app is bonded, TCP and UDP, through a virtual network adapter from [net-merge](https://github.com/net-merge/net-merge) (MIT), not only Steam and browsers. net-merge is downloaded from GitHub the first time.
  - **Downsides**: games and voice chat are spread over the links too and can lag; sites and game servers may see two addresses and log you out or ask for a captcha; links no longer use their own DNS; it adds a virtual adapter and routes, which some VPNs, anti-cheat or firewall software may not like. The Bond page shows these and asks before turning it on.
- **Faster**: Steam's download connections to its servers are kept open and reused, so each chunk no longer waits on a new connection and a slow start.
- **Steadier**: only real link failures take a link out of use, not a slow or refusing server. Link speeds rise within seconds and fall slowly, so one burst doesn't flip the balance. Connections stay on their adapter even with Mobile Hotspot or connection sharing on.

## Fetch

- Each link asks its own DNS, so a CDN hands every link a server near its provider.

## Downloads

- **SteamStopper.msi**: installer, recommended. Auto-updates.
- **SteamStopper-portable.zip**: no install, includes .NET.
- **SteamStopper.zip**: auto-update package used by installed copies.
- **\*.sha256**: checksums for the files above.
