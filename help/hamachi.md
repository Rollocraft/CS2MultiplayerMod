---
title: Hamachi and other VPNs
seo_title: "Hamachi and VPN setup | CS2 Multiplayer Mod"
description: "Use Hamachi or another VPN for Cities: Skylines II multiplayer when direct internet access is unavailable. Set up the network and troubleshoot joining."
---

# Hamachi and other VPNs

A VPN program such as Hamachi puts every player into one virtual network. A
[direct connection](direct-connection.md) then works without any port forwarding, even when
the host's router refuses to open a port or the internet provider shares one public address
between many customers (CGNAT).

!!! tip "Everyone owns the game on Steam?"

    [Steam Relay](steam-relay.md) needs no extra program at all. A VPN is the way to go for
    Xbox App, Microsoft Store and Game Pass copies, or when the relay does not work for you.

## What you need

- [LogMeIn Hamachi](https://vpn.net/) installed and signed in on every computer. The free plan
  allows up to 5 computers in one network, so up to 5 players including the host.
- Everything a normal session needs: the same mod version, game version and DLC. See
  [Requirements](getting-started.md#requirements).

## Set up the Hamachi network

1. The host switches Hamachi on with the power button, chooses **Network ▸ Create a new
   network** and picks a network ID and a password.
2. Every other player chooses **Network ▸ Join an existing network** and enters that ID and
   password.
3. Wait until every player has a **green** dot in the network list. Yellow means Hamachi is
   still connecting. Blue means the connection runs through Hamachi's own relay - it works,
   but it is slower.

Your Hamachi address is shown at the top of the Hamachi window, next to the power button. It
starts with `25.`.

## Hosting { #hosting }

1. Open Multiplayer, click Host Game and set the connection to **Direct Connection**.
2. Switch **LAN Only off**. The mod counts Hamachi addresses (`25.x.x.x`) as internet
   addresses, so LAN Only would turn your friends away.
3. Set a **server password of at least 8 characters**. The mod requires one as soon as LAN
   Only is off.
4. Note the port (default `25001`).
5. Load or create your world as usual. The session starts once the city has loaded.
6. Give your friends your Hamachi address, the port and the password. The mod's log lists the
   address too, in the line `Players on your network join via: 25.x.x.x:25001 (Hamachi)`.

!!! note "Messages about your router"

    With LAN Only off, the mod also asks your real router to open the port, and the log may
    say `UPnP: The router refused ...` or `UPnP: No router answered`. Hamachi players do not
    need that, so you can ignore it. If the router does open the port, players outside
    Hamachi could reach it as well - that is what the password is for.

## Joining { #joining }

1. Open Multiplayer, click Join Game and set the connection to **Direct Connection**.
2. Enter the host's Hamachi address (`25.x.x.x`) as Host Address, the host's port, your player
   name and the password.
3. Click Join and wait while the host's city downloads.

## Windows Firewall { #firewall }

Windows usually treats the Hamachi network as a **public** network. On the host's computer,
Cities: Skylines II has to be allowed through the firewall on public networks:

1. Open **Windows Security ▸ Firewall & network protection ▸ Allow an app through firewall**.
2. Click **Change settings**, find Cities: Skylines II (`Cities2`) and tick both **Private**
   and **Public**.

If Windows asked when you first hosted and you only allowed private networks, this is the
setting to fix.

## Troubleshooting { #troubleshooting }

| Problem | What to do |
| --- | --- |
| "The host did not answer in time" | Both players need a green dot in Hamachi. Check that the address is the host's `25.` address and that the port matches. Allow the game through the host's [Windows Firewall](#firewall) on public networks. |
| The host's log says `Refused connection from 25.x.x.x ... session is LAN-only` | Switch LAN Only off on the host. |
| "Hosting over the internet needs a server password" | Set a password of at least 8 characters. It is required whenever LAN Only is off. |
| The world download is very slow or players lag | Hamachi shows a blue dot: the connection runs through Hamachi's relay. Restart Hamachi on both computers, or try [Steam Relay](steam-relay.md). |
| Someone cannot join the Hamachi network | The free plan is full at 5 computers. |

## Other VPN programs { #other-vpns }

The same steps work with other VPN programs. Whether LAN Only can stay on depends on the
addresses the program hands out:

| Program | Addresses | LAN Only |
| --- | --- | --- |
| Radmin VPN | `26.x.x.x` | Off, with a password of at least 8 characters - same as Hamachi |
| ZeroTier | Usually a private range such as `10.x.x.x`, `172.16-31.x.x` or `192.168.x.x` | Can stay on |
| Tailscale | `100.64.x.x` to `100.127.x.x` | Can stay on |

With LAN Only on, no password is required and the mod does not ask your router to open
anything. The firewall step above applies to every VPN program.