---
title: Port forwarding problems
seo_title: "Fix port forwarding problems | CS2 Multiplayer Mod"
description: "Troubleshoot unreachable Cities: Skylines II multiplayer hosts. Check TCP ports, router settings, Windows Firewall, public addresses and CGNAT."
---

# Diagnose issues with port forwarding

Your friend sees "The host did not answer in time" or a similar error? Go through these
checks in order. Most problems are solved by the first two.

## 1. Did the port really open? { #port-open }

Open the host's `CS2MultiplayerMod.log` and look for the line containing `UPnP:`. If it says
the router refused, or that no router answered, the port is closed. Allow UPnP on the router
or forward the port by hand - see [Set up port forwarding](forwarding.md#upnp).

## 2. Is it the right address? { #address }

- Over the internet, your friends need your **public** address from
  [api.ipify.org](https://api.ipify.org/), not the local one that starts with `192.168.` or
  `10.`.
- Many providers change the public address from time to time. Look it up again before each
  session.
- The port your friends enter has to be exactly the port you host on.

## 3. Is the firewall letting it in? { #firewall }

Windows Firewall, and any antivirus with its own firewall, has to allow Cities: Skylines II.
Open **Windows Security ▸ Firewall & network protection ▸ Allow an app through firewall**,
click **Change settings** and make sure `Cities2` is ticked for the network type your PC uses
(Private, Public, or both).

## 4. Are you testing from inside your own network? { #own-network }

Joining your own public address from a PC in the same network fails on many routers, even when
the forward works. Test with a friend outside your network, or from a phone hotspot. Players in
your own network use your local address instead.

## 5. Are you behind CGNAT? { #cgnat }

Open your router settings and compare the public address shown there (WAN IP) with the one
from [api.ipify.org](https://api.ipify.org/). If they differ, your provider shares one
public address between many customers and port forwarding cannot work. Use one of the
alternatives below, or let someone else host.

## 6. Is the port already in use? { #port-in-use }

If the game reports that the hosting port is already being used, close any other running
host or game instance. You can instead choose another TCP port, but the host, every joining
player, the firewall rule and the router forwarding rule must all use that same new port.

## Alternatives to port forwarding { #alternatives }

| Alternative | When it works |
| --- | --- |
| [Steam Relay](steam-relay.md) | Everyone has the Steam copy of the game. No port, no router setup. |
| [IPv6](direct-connection.md#ipv6) | Both sides have IPv6. Nothing to forward, but the router's IPv6 firewall has to let the port in. |
| [Hamachi or another VPN](hamachi.md) | Works on every copy of the game, with no router setup. Every player installs the VPN program. |

---

[Back to troubleshooting.](troubleshooting.md)
