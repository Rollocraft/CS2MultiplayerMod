---
title: Port forwarding problems
description: "Port forwarded and still nobody can join? Check the firewall, rule out CGNAT and confirm the port is really open."
---

# Diagnose issues with port forwarding

[Set up port forwarding.](forwarding.md)

If you still experience issues, even though you have port forwarding enabled, check that your firewall/anti-virus allows opening ports on your local machine. 

If you still cannot connect, check that you are not under Carrier-Grade NAT (CGNAT). Open your router settings, check the displayed public IP address (WAN IP) and compare to the IP shown [here](https://api.ipify.org/). If they are different, you are likely behind CGNAT. You might have luck by letting someone else host.

If the game reports that the hosting port is already being used, close any other running host/game instance. You can instead choose another TCP port, but the host, every joining player, the firewall rule, and the router forwarding rule must all use that same new port.

## Alternatives to port forwarding

Some people have had success in using a local VPN to get their friends onto their local network. This is not recommended. People have been using Radmin. Many routers also support hosting a VPN into your local network.

---

[Back to troubleshooting.](troubleshooting.md)
