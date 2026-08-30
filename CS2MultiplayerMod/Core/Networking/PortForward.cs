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
    /// <summary>What the router had to say about opening the port.</summary>
    public enum PortForwardState
    {
        /// <summary>Still asking. The host is already listening either way.</summary>
        Working,

        /// <summary>The router opened the port and confirmed the mapping back to us.</summary>
        Open,

        /// <summary>Nothing on the network answered. Almost always UPnP switched off.</summary>
        NoRouter,

        /// <summary>A router answered and declined. The port has to be forwarded by hand.</summary>
        Refused,
    }

    /// <summary>
    /// Asks the router to forward the host's port automatically (UPnP IGD), so a player
    /// hosting on the internet does not have to configure anything.
    ///
    /// There is no way to ask a router whether it *would* allow this - the only honest test
    /// is to request the mapping and then read it back, which is what <see cref="Verify"/>
    /// is for. A router that answers and refuses is reported as such rather than as success,
    /// because a host who believes the port is open has no reason to look at their router
    /// when nobody can connect.
    ///
    /// Every step runs on its own thread: SSDP discovery alone waits seconds, and hosting
    /// must not be delayed by it. The listener is already accepting connections before this
    /// finishes; a successful mapping only adds reachability from outside the network.
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

        /// <summary>
        /// Routers disagree about lease lifetimes: most treat 0 as "until deleted", a few
        /// reject it and want a finite one. Asking for 0 first and falling back covers both
        /// without needing to know which kind is on the other end.
        /// </summary>
        private static readonly int[] LeaseSeconds = { 0, 604800 };

        private readonly IModLogger _log;
        private readonly int _port;
        private readonly Thread _worker;

        private volatile string _controlUrl;
        private volatile string _serviceType;
        private volatile string _localAddress;
        private volatile string _externalAddress;
        private volatile bool _disposed;
        private int _state = (int)PortForwardState.Working;

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

        public PortForwardState State
        {
            get { return (PortForwardState)Thread.VolatileRead(ref _state); }
        }

        /// <summary>The public address the router reports, or null until one is known.</summary>
        public string ExternalAddress
        {
            get { return _externalAddress; }
        }

        public int Port
        {
            get { return _port; }
        }

        private void Settle(PortForwardState state)
        {
            Interlocked.Exchange(ref _state, (int)state);
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
                    Settle(PortForwardState.NoRouter);
                    return;
                }

                if (!Discover())
                {
                    _log.Event(LogTopic.Transport,
                        "UPnP: No router answered. If players outside your network cannot " +
                        "connect, forward TCP port " + _port + " to " + _localAddress +
                        " by hand.");
                    Settle(PortForwardState.NoRouter);
                    return;
                }

                _externalAddress = ExternalIp();

                string failure = null;
                foreach (int lease in LeaseSeconds)
                {
                    failure = AddMapping(lease);
                    if (failure == null) break;

                    // A conflicting entry is usually this mod's own mapping from a previous
                    // session that outlived the process. Clearing it is safe precisely
                    // because it points at this machine and this port.
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
                    Settle(PortForwardState.Refused);
                    return;
                }

                if (!Verify())
                {
                    _log.Warn(LogTopic.Transport,
                        "UPnP: The router accepted the request for TCP port " + _port +
                        " but does not report the mapping back. Treating it as not forwarded.");
                    Settle(PortForwardState.Refused);
                    return;
                }

                Settle(PortForwardState.Open);
                _log.Event(LogTopic.Transport, "UPnP: TCP port " + _port + " forwarded to " +
                    _localAddress + " automatically." +
                    (_externalAddress != null ? " Players outside your network connect to " + _externalAddress + ":" + _port + "." : ""));

                // Disposed while we were still negotiating: the mapping exists now and has
                // to come back down, because nothing else knows about it.
                if (_disposed) DeleteMapping(announce: true);
            }
            catch (Exception ex)
            {
                _log.Warn(LogTopic.Transport, "UPnP: Automatic port forwarding failed (" +
                    ex.Message + "). Forward TCP port " + _port +
                    " by hand if players cannot reach you.");
                Settle(PortForwardState.Refused);
            }
        }

        // ---- discovery ------------------------------------------------------------

        /// <summary>
        /// Multicast a search on every live interface and keep the first gateway that
        /// exposes a WAN connection service. One socket per address rather than one on
        /// Any: a machine with a VPN adapter routes multicast out of whichever interface
        /// the stack prefers, which is regularly not the one the router is on.
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
        /// Fetch a device description and keep its WAN connection control endpoint. Both
        /// service flavours appear in the wild - IP for ethernet-style uplinks, PPP for
        /// DSL - and the exact service type has to be carried forward, because it is also
        /// the namespace every later request is addressed to.
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

                Uri controlUri;
                if (!Uri.TryCreate(new Uri(location), control, out controlUri)) continue;

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
            string error;
            Soap("AddPortMapping",
                 "<NewRemoteHost></NewRemoteHost>" +
                 "<NewExternalPort>" + _port + "</NewExternalPort>" +
                 "<NewProtocol>TCP</NewProtocol>" +
                 "<NewInternalPort>" + _port + "</NewInternalPort>" +
                 "<NewInternalClient>" + _localAddress + "</NewInternalClient>" +
                 "<NewEnabled>1</NewEnabled>" +
                 "<NewPortMappingDescription>" + MappingDescription + "</NewPortMappingDescription>" +
                 "<NewLeaseDuration>" + leaseSeconds + "</NewLeaseDuration>",
                 out error);
            return error;
        }

        /// <summary>
        /// Read the mapping back. This is the whole "is UPnP actually allowed" question:
        /// some routers answer AddPortMapping politely and forward nothing.
        /// </summary>
        private bool Verify()
        {
            string error;
            string response = Soap("GetSpecificPortMappingEntry",
                                   "<NewRemoteHost></NewRemoteHost>" +
                                   "<NewExternalPort>" + _port + "</NewExternalPort>" +
                                   "<NewProtocol>TCP</NewProtocol>",
                                   out error);
            if (error != null) return false;

            string client = Tag(response, "NewInternalClient");
            return client != null && client.Trim() == _localAddress;
        }

        private string ExternalIp()
        {
            string error;
            string response = Soap("GetExternalIPAddress", "", out error);
            if (error != null) return null;

            string address = Tag(response, "NewExternalIPAddress");
            if (string.IsNullOrEmpty(address)) return null;

            IPAddress parsed;
            return IPAddress.TryParse(address.Trim(), out parsed) ? parsed.ToString() : null;
        }

        /// <summary>
        /// Remove the mapping. Clearing a stale entry before retrying passes
        /// <paramref name="announce"/> false: nothing of ours has been released yet, and
        /// saying so would read as the session ending.
        /// </summary>
        private void DeleteMapping(bool announce)
        {
            if (_controlUrl == null) return;
            string error;
            Soap("DeletePortMapping",
                 "<NewRemoteHost></NewRemoteHost>" +
                 "<NewExternalPort>" + _port + "</NewExternalPort>" +
                 "<NewProtocol>TCP</NewProtocol>",
                 out error);
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
                // A refusal arrives as HTTP 500 carrying a UPnP error code, which is far
                // more use than "the remote server returned an error".
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
        /// Interfaces to search, the one carrying the default route first. Machines with
        /// Hyper-V, WSL or Docker have several virtual adapters that enumerate ahead of the
        /// real one, and whichever interface answers decides where the mapping will point -
        /// a router can only forward to an address on its own subnet.
        /// </summary>
        private IEnumerable<IPAddress> SearchOrder()
        {
            var ordered = new List<IPAddress>();
            IPAddress preferred;
            if (_localAddress != null && IPAddress.TryParse(_localAddress, out preferred))
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

        /// <summary>
        /// The address the router would forward to. Taken from a UDP socket "connected" to
        /// a public address - no packet is sent, but the stack picks the interface it would
        /// route through, which is the one the gateway is on.
        /// </summary>
        private static string RoutableLocalAddress()
        {
            try
            {
                using (var probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
                {
                    probe.Connect(new IPEndPoint(IPAddress.Parse("203.0.113.1"), 9));
                    var local = probe.LocalEndPoint as IPEndPoint;
                    if (local != null && !IPAddress.IsLoopback(local.Address)) return local.Address.ToString();
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

        /// <summary>
        /// Inner text of the first element with this local name. Deliberately not an XML
        /// parser: the values wanted here are flat leaves, and every one of them arrives
        /// under a namespace prefix that varies by router.
        /// </summary>
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

        /// <summary>
        /// Take the mapping back down. Runs on its own thread: this is called from the game
        /// thread when hosting stops, and a router that has stopped answering must not turn
        /// that into a frame hitch.
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            if (State != PortForwardState.Open) return;
            var closer = new Thread(() => DeleteMapping(announce: true))
            {
                IsBackground = true,
                Name = "mp-upnp-close",
            };
            closer.Start();
        }
    }
}
