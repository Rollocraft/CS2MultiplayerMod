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
        /// The IPv6 /64s this machine has an address in. Home IPv6 networks hand out public addresses, so a peer
        /// from one of these subnets is on this network even though its address is not private.
        /// </summary>
        private HashSet<string> _localIpv6Subnets = new HashSet<string>();

        /// <summary>Addresses on interfaces that are up, with the interface name; empty if they cannot be read.</summary>
        private List<KeyValuePair<IPAddress, string>> LocalAddresses()
        {
            var found = new List<KeyValuePair<IPAddress, string>>();
            try
            {
                foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.OperationalStatus != OperationalStatus.Up) continue;
                    if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    foreach (UnicastIPAddressInformation addr in nic.GetIPProperties().UnicastAddresses)
                        found.Add(new KeyValuePair<IPAddress, string>(addr.Address, nic.Name));
                }
            }
            catch (Exception ex)
            {
                _log.Warn(LogTopic.Transport, "Could not enumerate local addresses: " + ex.Message);
            }
            return found;
        }

        private static HashSet<string> Ipv6Subnets(List<KeyValuePair<IPAddress, string>> locals)
        {
            var subnets = new HashSet<string>();
            foreach (KeyValuePair<IPAddress, string> pair in locals)
            {
                if (pair.Key.IsIPv6Teredo) continue; // a tunnel endpoint, not a network
                string subnet = NetAddress.Prefix64(pair.Key);
                if (subnet != null) subnets.Add(subnet);
            }
            return subnets;
        }

        /// <summary>Logs where this host is reachable, so connection problems are debuggable from the log.</summary>
        private void LogReachability(int port, bool lanOnly, List<KeyValuePair<IPAddress, string>> addresses)
        {
            var locals = new List<string>();
            var ipv6 = new List<string>();
            foreach (KeyValuePair<IPAddress, string> pair in addresses)
            {
                IPAddress address = pair.Key;
                if (address.AddressFamily == AddressFamily.InterNetwork)
                {
                    locals.Add(NetAddress.FormatEndpoint(address.ToString(), port) + " (" + pair.Value + ")");
                    continue;
                }

                // Link-local needs a per-machine scope id and Teredo rarely accepts inbound; neither is worth sharing.
                if (!_dualStack || address.AddressFamily != AddressFamily.InterNetworkV6 ||
                    address.IsIPv6LinkLocal || address.IsIPv6Teredo) continue;
                string shown = NetAddress.FormatEndpoint(new IPAddress(address.GetAddressBytes()).ToString(), port) +
                               " (" + pair.Value + ")";
                if (IsPrivateAddress(address)) locals.Add(shown);
                else ipv6.Add(shown);
            }

            _log.Event(LogTopic.Transport,
                locals.Count > 0 ? "Players on your network join via: " +
                string.Join(", ", locals.ToArray()) : "Could not find any local network address - is this machine connected to a network?");

            if (ipv6.Count > 0)
                _log.Event(LogTopic.Transport, lanOnly
                    ? "Players on your network can also join over IPv6 via: " + string.Join(", ", ipv6.ToArray())
                    : "Players with IPv6 join via: " + string.Join(", ", ipv6.ToArray()) + ". IPv6 needs no " +
                      "port forwarding, but your router's IPv6 firewall and the Windows Firewall must let TCP port " +
                      port + " in to this machine - many routers block incoming IPv6 by default.");

            if (!lanOnly)
                _log.Event(LogTopic.Transport,
                    "Players on the internet need your PUBLIC IP and TCP port " + port +
                    " reaching this machine through the Windows Firewall. The router is being asked " +
                    "to forward it automatically - see the [upnp] lines below for whether it agreed. " +
                    "If it did not, forward the port by hand or host over the Steam relay instead.");
        }

        /// <summary>"On my own network": a private address, or IPv6 from a subnet this machine is on.</summary>
        private bool IsLanAddress(IPAddress address)
        {
            if (IsPrivateAddress(address)) return true;
            string subnet = NetAddress.Prefix64(address);
            return subnet != null && !address.IsIPv6Teredo && _localIpv6Subnets.Contains(subnet);
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
            // 100.64.0.0/10 (CGNAT): Tailscale-style VPN peers count as LAN; internet traffic cannot come from it.
            if (b[0] == 100 && b[1] >= 64 && b[1] <= 127) return true;
            return false;
        }
    }
}
