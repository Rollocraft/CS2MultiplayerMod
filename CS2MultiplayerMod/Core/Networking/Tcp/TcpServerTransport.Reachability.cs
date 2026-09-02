using System;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using CS2MultiplayerMod.Core.Diagnostics;

namespace CS2MultiplayerMod.Core.Networking.Tcp
{
    public sealed partial class TcpServerTransport
    {
        /// <summary>
        /// Spell out where this host can actually be reached, so "my friend cannot
        /// connect" is debuggable from the log alone instead of by guessing IPs.
        /// </summary>
        private void LogReachability(int port, bool lanOnly)
        {
            try
            {
                var locals = new List<string>();
                foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.OperationalStatus != OperationalStatus.Up) continue;
                    if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    foreach (UnicastIPAddressInformation addr in nic.GetIPProperties().UnicastAddresses)
                    {
                        if (addr.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                        locals.Add(addr.Address + ":" + port + " (" + nic.Name + ")");
                    }
                }

                _log.Event(LogTopic.Transport,
                    locals.Count > 0 ? "Players on your network join via: " +
                    string.Join(", ", locals.ToArray()) : "Could not find any local network address - is this machine connected to a network?");

                if (!lanOnly)
                    _log.Event(LogTopic.Transport,
                        "Players on the internet need your PUBLIC IP and TCP port " + port +
                        " reaching this machine through the Windows Firewall. The router is being asked " +
                        "to forward it automatically - see the [upnp] lines below for whether it agreed. " +
                        "If it did not, forward the port by hand or host over the Steam relay instead.");
            }
            catch (Exception ex)
            {
                _log.Warn(LogTopic.Transport, "Could not enumerate local addresses: " + ex.Message);
            }
        }

        /// <summary>RFC1918/4193 + loopback + link-local - "on my own network".</summary>
        public static bool IsPrivateAddress(IPAddress address)
        {
            if (IPAddress.IsLoopback(address)) return true;

            if (address.AddressFamily == AddressFamily.InterNetworkV6)
            {
                if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
                else return address.IsIPv6LinkLocal || address.IsIPv6SiteLocal ||
                            (address.GetAddressBytes()[0] & 0xFE) == 0xFC; // fc00::/7 unique-local
            }

            byte[] b = address.GetAddressBytes();
            if (b.Length != 4) return false;
            if (b[0] == 10) return true;                       // 10.0.0.0/8
            if (b[0] == 172 && b[1] >= 16 && b[1] <= 31) return true; // 172.16.0.0/12
            if (b[0] == 192 && b[1] == 168) return true;       // 192.168.0.0/16
            if (b[0] == 169 && b[1] == 254) return true;       // link-local
            // 100.64.0.0/10 (CGNAT): Tailscale-style VPNs put peers here, and a friend
            // on your tailnet is exactly the "trusted local network" LAN-only means.
            // Unsolicited internet traffic cannot arrive from this range anyway.
            if (b[0] == 100 && b[1] >= 64 && b[1] <= 127) return true;
            return false;
        }

    }
}
