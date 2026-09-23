using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using CS2MultiplayerMod.Core.Diagnostics;

namespace CS2MultiplayerMod.Core.Networking
{
    /// <summary>
    /// Automatic UPnP port forwarding. The only real test is to map and read the mapping back
    /// (<see cref="Verify"/>); a refusal is reported as one. Runs on its own thread; the listener is
    /// already accepting, a mapping only adds outside reach.
    /// </summary>
    public sealed class PortForward : IDisposable
    {
        private const string SsdpAddress = "239.255.255.250";
        private const int SsdpPort = 1900;
        private const string GatewayDeviceType = "urn:schemas-upnp-org:device:InternetGatewayDevice:1";
        private const string MappingDescription = "Cities Skylines II Multiplayer";

        /// <summary>Total budget for listening to SSDP replies, in milliseconds.</summary>
        private const int DiscoveryBudgetMs = 3000;

        /// <summary>Per-socket receive slice, so one silent interface cannot eat the budget.</summary>
        private const int DiscoveryReceiveMs = 800;

        private const int HttpTimeoutMs = 4000;

        /// <summary>Most routers take 0 as "until deleted", some want a finite lease; try both.</summary>
        private static readonly int[] LeaseSeconds = { 0, 604800 };

        private readonly IModLogger _log;
        private readonly int _port;
        private readonly Thread _worker;

        private volatile string _controlUrl;
        private volatile string _serviceType;
        private volatile string _localAddress;
        private volatile string _externalAddress;
        private volatile bool _disposed;
        private int _mapped;

        private PortForward(IModLogger log, int port)
        {
            _log = log ?? NullModLogger.Instance;
            _port = port;
            _worker = new Thread(Run) { IsBackground = true, Name = "mp-upnp" };
        }

        /// <summary>Start asking, and return immediately.</summary>
        public static PortForward Begin(IModLogger log, int port)
        {
            var forward = new PortForward(log, port);
            forward._worker.Start();
            return forward;
        }

        private void Run()
        {
            try
            {
                _localAddress = RoutableLocalAddress();
                if (_localAddress == null)
                {
                    _log.Warn(LogTopic.Transport,
                        "UPnP: No local network address to forward to; skipping automatic port forwarding.");
                    return;
                }

                if (!Discover())
                {
                    _log.Event(LogTopic.Transport,
                        "UPnP: No router answered. If players outside your network cannot " +
                        "connect, forward TCP port " + _port + " to " + _localAddress +
                        " by hand.");
                    return;
                }

                _externalAddress = ExternalIp();

                string failure = null;
                foreach (int lease in LeaseSeconds)
                {
                    failure = AddMapping(lease);
                    if (failure == null) break;

                    // Usually our own mapping from an earlier session; safe to clear, it points here.
                    if (failure.Contains("718"))
                    {
                        DeleteMapping(announce: false);
                        failure = AddMapping(lease);
                        if (failure == null) break;
                    }
                }

                if (failure != null)
                {
                    _log.Warn(LogTopic.Transport, "UPnP: The router refused to open TCP port " +
                        _port + " (" + failure + "). Forward it to " + _localAddress +
                        " by hand, or host over the Steam relay.");
                    return;
                }

                if (!Verify())
                {
                    _log.Warn(LogTopic.Transport,
                        "UPnP: The router accepted the request for TCP port " + _port +
                        " but does not report the mapping back. Treating it as not forwarded.");
                    return;
                }

                Interlocked.Exchange(ref _mapped, 1);
                _log.Event(LogTopic.Transport, "UPnP: TCP port " + _port + " forwarded to " +
                    _localAddress + " automatically." +
                    (_externalAddress != null ? " Players outside your network connect to " + _externalAddress + ":" + _port + "." : ""));

                // Disposed while negotiating: nothing else knows about this mapping.
                if (_disposed) DeleteMapping(announce: true);
            }
            catch (Exception ex)
            {
                _log.Warn(LogTopic.Transport, "UPnP: Automatic port forwarding failed (" +
                    ex.Message + "). Forward TCP port " + _port +
                    " by hand if players cannot reach you.");
            }
        }

        // ---- discovery ------------------------------------------------------------

        /// <summary>
        /// Searches every live interface and keeps the first WAN gateway. One socket per address: with a
        /// VPN adapter, multicast on Any often leaves through the wrong interface.
        /// </summary>
        private bool Discover()
        {
            byte[] search = Encoding.ASCII.GetBytes(
                "M-SEARCH * HTTP/1.1\r\n" +
                "HOST: " + SsdpAddress + ":" + SsdpPort + "\r\n" +
                "MAN: \"ssdp:discover\"\r\n" +
                "MX: 2\r\n" +
                "ST: " + GatewayDeviceType + "\r\n\r\n");

            var target = new IPEndPoint(IPAddress.Parse(SsdpAddress), SsdpPort);
            var deadline = System.Diagnostics.Stopwatch.StartNew();
            var seen = new HashSet<string>();

            foreach (IPAddress local in SearchOrder())
            {
                if (_disposed || deadline.ElapsedMilliseconds >= DiscoveryBudgetMs) break;

                using (var socket = new UdpClient(new IPEndPoint(local, 0)))
                {
                    socket.Client.ReceiveTimeout = DiscoveryReceiveMs;
                    try
                    {
                        // Twice: the first datagram of a burst is the one routers drop.
                        socket.Send(search, search.Length, target);
                        socket.Send(search, search.Length, target);
                    }
                    catch (Exception)
                    {
                        continue; // interface cannot multicast; try the next
                    }

                    while (!_disposed && deadline.ElapsedMilliseconds < DiscoveryBudgetMs)
                    {
                        string reply;
                        try
                        {
                            IPEndPoint from = null;
                            reply = Encoding.ASCII.GetString(socket.Receive(ref from));
                        }
                        catch (Exception)
                        {
                            break; // receive timed out on this interface
                        }

                        string location = Header(reply, "LOCATION");
                        if (location == null || !seen.Add(location)) continue;
                        if (ReadServices(location, local.ToString())) return true;
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// Keeps the WAN control endpoint (IP or PPP flavour) and its exact service type, the namespace for
        /// every later request.
        /// </summary>
        private bool ReadServices(string location, string viaLocal)
        {
            string description = HttpGet(location);
            if (description == null) return false;

            foreach (string service in Blocks(description, "service"))
            {
                string type = Tag(service, "serviceType");
                string control = Tag(service, "controlURL");
                if (type == null || control == null) continue;
                if (type.IndexOf("WANIPConnection", StringComparison.OrdinalIgnoreCase) < 0 &&
                    type.IndexOf("WANPPPConnection", StringComparison.OrdinalIgnoreCase) < 0) continue;

                if (!Uri.TryCreate(new Uri(location), control, out Uri controlUri)) continue;

                _controlUrl = controlUri.ToString();
                _serviceType = type;
                _localAddress = viaLocal;
                _log.Detail(LogTopic.Transport, "UPnP: Router found at " + controlUri.Host + " (" +
                    type + ").");
                return true;
            }

            return false;
        }

        // ---- the four things we ask the router ------------------------------------

        /// <summary>Returns null on success, or a short description of the refusal.</summary>
        private string AddMapping(int leaseSeconds)
        {
            Soap("AddPortMapping",
                 "<NewRemoteHost></NewRemoteHost>" +
                 "<NewExternalPort>" + _port + "</NewExternalPort>" +
                 "<NewProtocol>TCP</NewProtocol>" +
                 "<NewInternalPort>" + _port + "</NewInternalPort>" +
                 "<NewInternalClient>" + _localAddress + "</NewInternalClient>" +
                 "<NewEnabled>1</NewEnabled>" +
                 "<NewPortMappingDescription>" + MappingDescription + "</NewPortMappingDescription>" +
                 "<NewLeaseDuration>" + leaseSeconds + "</NewLeaseDuration>",
                 out string error);
            return error;
        }

        /// <summary>Reads the mapping back: some routers accept AddPortMapping and forward nothing.</summary>
        private bool Verify()
        {
            string response = Soap("GetSpecificPortMappingEntry",
                                   "<NewRemoteHost></NewRemoteHost>" +
                                   "<NewExternalPort>" + _port + "</NewExternalPort>" +
                                   "<NewProtocol>TCP</NewProtocol>",
                                   out string error);
            if (error != null) return false;

            string client = Tag(response, "NewInternalClient");
            return client != null && client.Trim() == _localAddress;
        }

        private string ExternalIp()
        {
            string response = Soap("GetExternalIPAddress", "", out string error);
            if (error != null) return null;

            string address = Tag(response, "NewExternalIPAddress");
            if (string.IsNullOrEmpty(address)) return null;

            return IPAddress.TryParse(address.Trim(), out IPAddress parsed) ? parsed.ToString() : null;
        }

        /// <summary>
        /// Removes the mapping. <paramref name="announce"/> is false when clearing a stale entry before a
        /// retry.
        /// </summary>
        private void DeleteMapping(bool announce)
        {
            if (_controlUrl == null) return;
            Soap("DeletePortMapping",
                 "<NewRemoteHost></NewRemoteHost>" +
                 "<NewExternalPort>" + _port + "</NewExternalPort>" +
                 "<NewProtocol>TCP</NewProtocol>",
                 out string error);
            if (!announce) return;

            _log.Detail(LogTopic.Transport,
                error == null ? "UPnP: Released the automatic forward of TCP port " + _port +
                "." : "UPnP: Could not release TCP port " + _port + " (" + error +
                "); it will expire when the router restarts.");
        }

        // ---- transport ------------------------------------------------------------

        private string Soap(string action, string arguments, out string error)
        {
            error = null;
            string control = _controlUrl, service = _serviceType;
            if (control == null || service == null)
            {
                error = "no router";
                return null;
            }

            string body =
                "<?xml version=\"1.0\"?>" +
                "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" " +
                "s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\">" +
                "<s:Body><u:" + action + " xmlns:u=\"" + service + "\">" + arguments +
                "</u:" + action + "></s:Body></s:Envelope>";

            try
            {
                var request = (HttpWebRequest)WebRequest.Create(control);
                request.Method = "POST";
                request.ContentType = "text/xml; charset=\"utf-8\"";
                request.Headers.Add("SOAPACTION", "\"" + service + "#" + action + "\"");
                request.Timeout = HttpTimeoutMs;
                request.ReadWriteTimeout = HttpTimeoutMs;
                // Routers routinely answer a 100-continue handshake with nothing at all.
                request.ServicePoint.Expect100Continue = false;

                byte[] payload = Encoding.UTF8.GetBytes(body);
                request.ContentLength = payload.Length;
                using (Stream stream = request.GetRequestStream()) stream.Write(payload, 0, payload.Length);

                using (var response = (HttpWebResponse)request.GetResponse())
                using (var reader = new StreamReader(response.GetResponseStream()))
                    return reader.ReadToEnd();
            }
            catch (WebException ex)
            {
                // A refusal is HTTP 500 carrying a UPnP error code.
                error = ex.Message;
                try
                {
                    if (ex.Response == null) return null;
                    using (var reader = new StreamReader(ex.Response.GetResponseStream()))
                    {
                        string fault = reader.ReadToEnd();
                        string code = Tag(fault, "errorCode");
                        string text = Tag(fault, "errorDescription");
                        if (code != null) error = "error " + code.Trim() + (text != null ? " " + text.Trim() : "");
                    }
                }
                catch (Exception) { /* keep the outer message */ }
                return null;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return null;
            }
        }

        private string HttpGet(string url)
        {
            try
            {
                var request = (HttpWebRequest)WebRequest.Create(url);
                request.Timeout = HttpTimeoutMs;
                request.ReadWriteTimeout = HttpTimeoutMs;
                using (var response = (HttpWebResponse)request.GetResponse())
                using (var reader = new StreamReader(response.GetResponseStream()))
                    return reader.ReadToEnd();
            }
            catch (Exception)
            {
                return null;
            }
        }

        // ---- addresses and parsing ------------------------------------------------

        /// <summary>
        /// Default-route interface first: virtual adapters (Hyper-V, WSL, Docker) enumerate ahead, and the
        /// answering interface decides where the mapping points.
        /// </summary>
        private IEnumerable<IPAddress> SearchOrder()
        {
            var ordered = new List<IPAddress>();
            if (_localAddress != null && IPAddress.TryParse(_localAddress, out IPAddress preferred))
                ordered.Add(preferred);

            foreach (IPAddress address in LocalAddresses())
                if (!ordered.Contains(address)) ordered.Add(address);
            return ordered;
        }

        private static IEnumerable<IPAddress> LocalAddresses()
        {
            var found = new List<IPAddress>();
            foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (UnicastIPAddressInformation addr in nic.GetIPProperties().UnicastAddresses)
                {
                    if (addr.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    if (IPAddress.IsLoopback(addr.Address)) continue;
                    found.Add(addr.Address);
                }
            }
            return found;
        }

        /// <summary>The interface a UDP socket "connected" to a public address would use; sends nothing.</summary>
        private static string RoutableLocalAddress()
        {
            try
            {
                using (var probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
                {
                    probe.Connect(new IPEndPoint(IPAddress.Parse("203.0.113.1"), 9));
                    if (probe.LocalEndPoint is IPEndPoint local &&
                        !IPAddress.IsLoopback(local.Address)) return local.Address.ToString();
                }
            }
            catch (Exception) { /* fall through to the first usable address */ }

            foreach (IPAddress address in LocalAddresses()) return address.ToString();
            return null;
        }

        private static string Header(string response, string name)
        {
            foreach (string line in response.Split('\n'))
            {
                int colon = line.IndexOf(':');
                if (colon <= 0) continue;
                if (!string.Equals(line.Substring(0, colon).Trim(), name, StringComparison.OrdinalIgnoreCase)) continue;
                string value = line.Substring(colon + 1).Trim();
                if (value.Length > 0) return value;
            }
            return null;
        }

        /// <summary>First element with this local name; namespace prefixes vary by router.</summary>
        private static string Tag(string xml, string name)
        {
            if (string.IsNullOrEmpty(xml)) return null;

            int scan = 0;
            while (true)
            {
                int open = xml.IndexOf('<', scan);
                if (open < 0) return null;
                int end = xml.IndexOf('>', open);
                if (end < 0) return null;

                string tag = xml.Substring(open + 1, end - open - 1);
                scan = end + 1;
                if (tag.StartsWith("/", StringComparison.Ordinal) ||
                    tag.StartsWith("?", StringComparison.Ordinal) ||
                    tag.EndsWith("/", StringComparison.Ordinal)) continue;

                int space = tag.IndexOf(' ');
                if (space > 0) tag = tag.Substring(0, space);
                int prefix = tag.IndexOf(':');
                if (prefix >= 0) tag = tag.Substring(prefix + 1);
                if (!string.Equals(tag, name, StringComparison.OrdinalIgnoreCase)) continue;

                int close = xml.IndexOf('<', scan);
                return close < 0 ? null : xml.Substring(scan, close - scan);
            }
        }

        /// <summary>Every &lt;name&gt;...&lt;/name&gt; region, prefix-tolerant like <see cref="Tag"/>.</summary>
        private static IEnumerable<string> Blocks(string xml, string name)
        {
            var blocks = new List<string>();
            if (string.IsNullOrEmpty(xml)) return blocks;

            int scan = 0;
            while (true)
            {
                int open = xml.IndexOf("<" + name + ">", scan, StringComparison.OrdinalIgnoreCase);
                if (open < 0) break;
                int start = open + name.Length + 2;
                int close = xml.IndexOf("</" + name + ">", start, StringComparison.OrdinalIgnoreCase);
                if (close < 0) break;
                blocks.Add(xml.Substring(start, close - start));
                scan = close + name.Length + 3;
            }
            return blocks;
        }

        /// <summary>On its own thread, so an unresponsive router cannot hitch the game thread.</summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            if (Thread.VolatileRead(ref _mapped) == 0) return;
            var closer = new Thread(() => DeleteMapping(announce: true))
            {
                IsBackground = true,
                Name = "mp-upnp-close",
            };
            closer.Start();
        }
    }
}
