---
title: Set up port forwarding
description: "How to open a TCP port on your router so friends can join over the internet."
---

# Port Forwarding

This guide shows how to open a TCP port on your router so friends can join over the
internet.

!!! tip "You may not need this at all"

    Port forwarding is only for [direct connections](direct-connection.md) over the
    internet.

    - [Steam Relay](steam-relay.md) needs no port and no router setup at all.
    - On the same network nothing has to be forwarded either.
    - Over [IPv6](direct-connection.md#ipv6) nothing is forwarded, but your router's IPv6
      firewall has to let the port in.
    - Over [Hamachi or another VPN](hamachi.md) nothing is forwarded.
    - When hosting starts, the mod asks your router to open the port itself. Check first
      whether that worked - see [Let the mod open the port](#upnp).

    The default port is TCP `25001`. The host, every joining player, the firewall rule and
    the router rule all have to use the same port.

## Let the mod open the port { #upnp }

When you host with Direct Connection and LAN Only off, the mod asks your router to open the
port automatically (UPnP). The router's answer is written to the mod's log,
`CS2MultiplayerMod.log` in
`%USERPROFILE%\AppData\LocalLow\Colossal Order\Cities Skylines II\Logs\`. Look for the line
containing `UPnP:`:

| Log line | Meaning |
| --- | --- |
| `UPnP: TCP port <port> forwarded to <address> automatically.` | Done. The line also names the public address your friends connect to. |
| `UPnP: The router refused to open TCP port <port> (...)` | The router has UPnP, but does not let this PC open ports. Allow it (below) or forward the port by hand. |
| `UPnP: No router answered.` | UPnP is switched off on the router, or the router does not support it. Switch it on or forward the port by hand. |
| `UPnP: The router accepted the request ... but does not report the mapping back.` | The router cannot be trusted to forward. Forward the port by hand. |
| `UPnP: Automatic port forwarding failed (...)` or `UPnP: No local network address ...` | Something went wrong while asking. Forward the port by hand. |

On a **FRITZ!Box**, UPnP has to be allowed for each device: open **Internet ▸ Permit Access ▸
Port Sharing** (German: Internet ▸ Freigaben ▸ Portfreigaben), edit your PC's entry and tick
**Permit independent port sharing for this device** (Selbstständige Portfreigaben für dieses
Gerät erlauben). Other routers usually have a single UPnP switch in their network settings.

## What is port forwarding?

[https://www.youtube.com/watch?v=WOZQppVNGvA](https://www.youtube.com/watch?v=WOZQppVNGvA)

## Setting up port forwarding

Forward **TCP** on your port to your PC's local IP address. The mod lists that address in its
log when hosting starts: `Players on your network join via: <address>:<port>`.

Set up port forwarding on these popular routers (third-party sites):

- [FritzBox](https://fritz.com/en/apps/knowledge-base/FRITZ-Box-7490/34_Setting-up-port-sharing-in-the-FRITZ-Box).
- [TP-Link](https://www.tp-link.com/us/support/faq/1379/).
- [NETGEAR](https://kb.netgear.com/24290/How-do-I-add-a-custom-port-forwarding-service-on-my-NETGEAR-router)
- [ASUS](https://www.asus.com/support/faq/1037906/)
- [Linksys](https://support.linksys.com/kb/article/318-en/)
- [Eero](https://eero.com/support/articles/207908443-How-do-I-set-up-port-forwarding)
- [Google Nest](https://support.google.com/googlehome/answer/6274503?hl=en)
- [Ubiquiti](https://help.ui.com/hc/en-us/articles/235723207-UniFi-Gateway-Port-Forwarding)
- [Huawei](https://consumer.huawei.com/en/support/content/en-us15806329/)

Your router not among them? Search for your router model plus "port forwarding" and you will
find a tutorial.

## Which address to give your friends

Your **public IPv4 address**, from [api.ipify.org](https://api.ipify.org/) or your router's
overview page, together with the port. Not the local address that starts with `192.168.` or
`10.` - that one only works inside your own network.

Many internet providers change your public address from time to time, for example once a day.
Look it up again before every session instead of reusing an old one.

---

[Issues? Troubleshooting](forwarding_troubleshoot.md)
