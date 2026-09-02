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
![](assets/img/ui-menu-multiplayer.png)
![](assets/img/ui-menu-choice-host.png)
2. Set the connection type to Direct Connection.
![](assets/img/ui-host-world.png)
3. Load or create your world as usual. The session starts once the city has loaded.
4. Share your IP address and port with the other players.

Which address you share depends on where the other players are:

| Players | Address to share                                                                               |
| --- |------------------------------------------------------------------------------------------------|
| Same network (LAN) | Your local IP address, shown in the log when hosting starts or via a Terminal with the command `ipconfig` |
| Over the internet | Your public IPV4, from [api.ipify.org](https://api.ipify.org/)                                 |

Session settings live in the multiplayer panel while you play, and in the mod options
before you start: port, password, player limit, LAN Only, player approval and the world
re-sync interval.

![](assets/img/ui-session-panel-stopped.png)

## Joining

1. Open Multiplayer and click Join Game.
![](assets/img/ui-menu-choice-join.png)
2. Set the connection type to Direct Connection.
3. Enter the host's address and port, your player name, and the password if there is one.
![](assets/img/ui-join-direct.png)
4. Click Join and wait while the host's city downloads.

!!! warning "Set a password"

    A host reachable from the internet accepts anyone who finds the port, and everyone who
    joins downloads a copy of the city. Set a server password, or use
    [Steam Relay](steam-relay.md), or switch LAN Only on to accept local players only.

## Playing on the same network

Nothing has to be forwarded. Joining players use the host's local IP address and the same
port. The host can switch LAN Only on to refuse everything that is not from the local
network.
