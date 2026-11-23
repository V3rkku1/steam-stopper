Bond every app, faster Steam bonding, and steadier links.

## New

- **Bond every app** (optional, on the Bond page): every app is bonded, TCP and UDP, not only Steam and browsers. It uses a virtual network adapter from net-merge (github.com/net-merge/net-merge, MIT), downloaded the first time you turn it on. Read the downsides below first.

## Faster

- **Steam downloads over bonding**: connections to Steam's servers are kept open and reused, so each chunk no longer waits on a new connection.
- **Fetch**: each link asks its own DNS, so every link downloads from a server near its provider.

## Fixed

- A slow or refusing server no longer takes a good link out of use; only real link failures do.
- Link speeds don't jump on one burst, so the balance between links stays steady.
- Connections stay on their link even with Windows Mobile Hotspot or connection sharing on.

## Before you turn on "Bond every app"

- Games and voice chat are spread over the links too, so they can lag or drop when a link is slow.
- Sites and game servers may see you coming from two addresses at once; some log you out or ask for a captcha.
- Links no longer ask their own DNS.
- It adds a virtual adapter and routes. Some VPNs, anti-cheat and firewall software may not like that.

## Downloads

| File | For |
|---|---|
| **SteamStopper.msi** | Most people. Installs, adds shortcuts, updates itself. |
| **SteamStopper-portable.zip** | No install: unzip and run `SteamStopper.exe`. Includes .NET. |
| SteamStopper.zip | Used by installed copies to update. You don't need it. |
| \*.sha256 | Checksums for the files above. |

Windows 10 or 11, 64-bit. Asks for Administrator: firewall, DNS and routes need it.
