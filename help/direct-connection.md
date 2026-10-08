---
title: Direct connection
description: "Connect straight to the host's address on TCP port 25001. Works on every copy of the game, including Xbox App, Microsoft Store and Game Pass."
---

# Direct connection

Players connect straight to the host's address and TCP port. This works on any copy of the
game, including Xbox App, Microsoft Store and Game Pass, but the host's port has to be
reachable.

The default port is TCP `25001`. The host, every joining player, the firewall rule and the
router rule all have to use the same port.

## Hosting

1. Open Multiplayer and click Host Game.
![](assets/img/ui-menu-multiplayer.webp)
![](assets/img/ui-menu-choice-host.webp)
2. Set the connection type to Direct Connection.
![](assets/img/ui-host-world.webp)
3. Load or create your world as usual. The session starts once the city has loaded.
4. Share your IP address and port with the other players.

Which address you share depends on where the other players are:

| Players | Address to share                                                                               |
| --- |------------------------------------------------------------------------------------------------|
| Same network (LAN) | Your local IP address, shown in the log when hosting starts or via a Terminal with the command `ipconfig` |
| Over the internet | Your public IPv4, from [api.ipify.org](https://api.ipify.org/)                                 |
| Over the internet, using IPv6 | Your IPv6 address, shown in the log when hosting starts. See [IPv6](#ipv6) |

Session settings live in the multiplayer panel while you play, and in the mod options
before you start: port, password, player limit, LAN Only and player approval.

![](assets/img/ui-session-panel-stopped.webp)

## Joining

1. Open Multiplayer and click Join Game.
![](assets/img/ui-menu-choice-join.webp)
2. Set the connection type to Direct Connection.
3. Enter the host's address and port, your player name, and the password if there is one.
![](assets/img/ui-join-direct.webp)
4. Click Join and wait while the host's city downloads.

!!! warning "A password is required"

    A host reachable from the internet accepts anyone who finds the port, and everyone who
    joins downloads a copy of the city. Hosting over the internet therefore needs a server
    password of at least 8 characters. Without one, use [Steam Relay](steam-relay.md) or
    switch LAN Only on to accept local players only.

## Playing on the same network

Nothing has to be forwarded. Joining players use the host's local IP address and the same
port. The host can switch LAN Only on to refuse everything that is not from the local
network.

## IPv6

Direct connections work over IPv6 as well as IPv4. A host accepts both at the same time,
so there is nothing to switch on.

IPv6 helps when your internet provider shares one public IPv4 address between many
customers (often called CGNAT, common on fibre and mobile connections). Port forwarding is
impossible there, but an IPv6 address reaches your PC directly.

### Hosting over IPv6

1. Start hosting as usual. The log lists your IPv6 addresses in the line
   `Players with IPv6 join via: ...`. You can also run `ipconfig` in a Terminal and use the
   entry `IPv6 Address`. Do not use `Temporary IPv6 Address`, which changes regularly, or
   `Link-local IPv6 Address`, which starts with `fe80` and only works inside your network.
2. IPv6 needs no port forwarding, but most routers block incoming IPv6 connections by
   default. In your router's IPv6 firewall settings (sometimes called IPv6 port sharing or
   a pinhole), allow incoming TCP on your port to your PC. The game also has to be allowed
   through the Windows Firewall.
3. Share your IPv6 address and port. A server password of at least 8 characters is still
   required, as for any direct host reachable from the internet.

With LAN Only switched on, players from your own network can join over IPv6 too.

### Joining over IPv6

Your own internet connection needs IPv6 as well. You can check it at
[test-ipv6.com](https://test-ipv6.com/). Enter the host's IPv6 address in Host Address, in one
of two ways:

- The address alone, for example `2001:db8::1`, with the port in the Port field.
- The address in square brackets, followed by the port, for example `[2001:db8::1]:25001`.
  A port written this way replaces the Port field.
