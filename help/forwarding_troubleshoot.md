# Diagnose issues with port forwarding

[Set up port forwarding.](/help/forwarding.md)

If you still experience issues, even though you have port forwarding enabled, check that your firewall/anti-virus allows opening ports on your local machine. 

Also check that you have forwarded the port that is set in the mod (default: `25001`); and that you have set the protocol to `TCP`, if it asks for that. The port forward needs to point to the computer you are using with the mod.

Every player should use the same port to connect - the one you have set to forward from your PC running the mod.

If you still cannot connect, check that you are not under Carrier-Grade NAT (CGNAT). Open your router settings, check the displayed public IP address (WAN IP) and compare to the IP shown [here](https://api.ipify.org/). If they are different, you are likely behind CGNAT. You might have luck by letting someone else host. Some internet providers might also let you have a public dynamic IP address after contacting support.

## Alternatives to port forwarding

Some people have had success in using a local VPN to get their friends onto their local network. **This is not recommended.** People have been using e.g. Radmin. Many routers also support hosting a VPN into your local network.

---

**[Back to troubleshooting.](/help/troubleshooting.md)**
