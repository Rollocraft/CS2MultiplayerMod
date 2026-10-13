using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace CS2MultiplayerMod.Core.Networking
{
    /// <summary>
    /// IPv4/IPv6 text and identity rules shared by the direct transport and the session: what a player may
    /// type as the host, how an endpoint is printed, and which key a ban is counted under.
    /// </summary>
    public static class NetAddress
    {
        /// <summary>
        /// A dual-stack listener sees IPv4 peers as ::ffff:a.b.c.d; this returns the plain IPv4 form, so
        /// logs, bans and the LAN check see one spelling per peer.
        /// </summary>
        public static IPAddress Normalize(IPAddress address) =>
            address != null && address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

        /// <summary>"host:port", with an IPv6 address bracketed ("[2001:db8::1]:25001") so the port is unambiguous.</summary>
        public static string FormatEndpoint(string host, int port)
        {
            string shown = host != null && host.IndexOf(':') >= 0 ? "[" + host + "]" : host;
            return shown + ":" + port.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Splits what a player typed as the host: a name, an IPv4 or IPv6 address, "host:port", "[ipv6]" or
        /// "[ipv6]:port". <paramref name="port"/> is 0 when the text names none. An unbracketed IPv6 address
        /// never carries a port: "2001:db8::1:25001" is a single address.
        /// </summary>
        public static bool TrySplitHostPort(string text, out string host, out int port)
        {
            host = "";
            port = 0;
            string s = text == null ? "" : text.Trim();
            if (s.Length == 0) return false;

            if (s[0] == '[')
            {
                int close = s.IndexOf(']');
                if (close < 2) return false;
                host = s.Substring(1, close - 1);
                if (close == s.Length - 1) return true;
                return s[close + 1] == ':' && TryParsePort(s.Substring(close + 2), out port);
            }

            int colon = s.IndexOf(':');
            if (colon >= 0 && colon == s.LastIndexOf(':'))
            {
                host = s.Substring(0, colon);
                return host.Length > 0 && TryParsePort(s.Substring(colon + 1), out port);
            }

            host = s;
            return true;
        }

        /// <summary>
        /// The key bans and auth-failure limits count under. One IPv6 subscriber normally owns a whole /64 and
        /// can take a fresh address in it at will, so a global IPv6 address counts as its /64; IPv4, loopback,
        /// link-local and non-IP identities (such as "steam:...") count as themselves.
        /// </summary>
        public static string BanKey(string address)
        {
            if (string.IsNullOrEmpty(address) || !IPAddress.TryParse(address, out IPAddress parsed))
                return address;
            parsed = Normalize(parsed);
            return Prefix64(parsed) ?? parsed.ToString();
        }

        /// <summary>"2001:db8:1:2::/64" for a routable IPv6 address; null for IPv4, loopback and link-local.</summary>
        public static string Prefix64(IPAddress address)
        {
            if (address == null || address.AddressFamily != AddressFamily.InterNetworkV6) return null;
            if (IPAddress.IsLoopback(address) || address.IsIPv6LinkLocal || address.IsIPv4MappedToIPv6) return null;
            byte[] bytes = address.GetAddressBytes();
            for (int i = 8; i < bytes.Length; i++) bytes[i] = 0;
            return new IPAddress(bytes) + "/64";
        }

        private static bool TryParsePort(string text, out int port)
        {
            if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out port) &&
                port > 0 && port <= 65535) return true;
            port = 0;
            return false;
        }
    }
}
