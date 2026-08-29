using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using CS2MultiplayerMod.Core.Diagnostics;

namespace CS2MultiplayerMod.Core.Networking.Discovery
{
    /// <summary>
    /// Information about a multiplayer session discovered on the local area network.
    /// </summary>
    public sealed class DiscoveredLanServer
    {
        public string ServerName { get; set; }
        public string CityName { get; set; }
        public int Population { get; set; }
        public int PlayerCount { get; set; }
        public int MaxPlayers { get; set; }
        public string Address { get; set; }
        public int Port { get; set; }
        public bool RequiresPassword { get; set; }
        public long LastSeenMs { get; set; }
    }

    /// <summary>
    /// Automatic LAN Server Discovery broadcaster (host) and listener (client).
    /// Uses lightweight UDP broadcast beacons on port 25002.
    /// </summary>
    public sealed class LanDiscovery : IDisposable
    {
        public const int DiscoveryPort = 25002;
        private const string BeaconMagic = "CS2MP_LAN_BEACON:";

        private readonly IModLogger _log;
        private readonly ConcurrentDictionary<string, DiscoveredLanServer> _servers =
            new ConcurrentDictionary<string, DiscoveredLanServer>();

        private UdpClient _udpListener;
        private Thread _listenerThread;
        private volatile bool _listening;

        private Timer _beaconTimer;
        private Func<DiscoveredLanServer> _beaconDataProvider;

        public LanDiscovery(IModLogger log = null)
        {
            _log = log ?? NullModLogger.Instance;
        }

        public ICollection<DiscoveredLanServer> DiscoveredServers => _servers.Values;

        /// <summary>
        /// Start broadcasting LAN discovery beacons on the host every 2 seconds.
        /// </summary>
        public void StartBroadcaster(Func<DiscoveredLanServer> dataProvider)
        {
            StopBroadcaster();
            _beaconDataProvider = dataProvider;
            _beaconTimer = new Timer(SendBeacon, null, 0, 2000);
            _log.Info("[MP] LAN Discovery beacon broadcaster started.");
        }

        public void StopBroadcaster()
        {
            if (_beaconTimer != null)
            {
                _beaconTimer.Dispose();
                _beaconTimer = null;
                _beaconDataProvider = null;
                _log.Info("[MP] LAN Discovery beacon broadcaster stopped.");
            }
        }

        private void SendBeacon(object state)
        {
            if (_beaconDataProvider == null) return;
            try
            {
                DiscoveredLanServer info = _beaconDataProvider();
                if (info == null) return;

                string payload = BeaconMagic +
                                 (info.ServerName ?? "Host") + "|" +
                                 (info.CityName ?? "City") + "|" +
                                 info.Population + "|" +
                                 info.PlayerCount + "|" +
                                 info.MaxPlayers + "|" +
                                 info.Port + "|" +
                                 (info.RequiresPassword ? "1" : "0");

                byte[] bytes = Encoding.UTF8.GetBytes(payload);
                using (var udp = new UdpClient())
                {
                    udp.EnableBroadcast = true;
                    // Standard global broadcast
                    udp.Send(bytes, bytes.Length, new IPEndPoint(IPAddress.Broadcast, DiscoveryPort));

                    // Multi-interface broadcast across all up network adapters
                    try
                    {
                        foreach (var nic in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
                        {
                            if (nic.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                            if (nic.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback) continue;

                            foreach (var u in nic.GetIPProperties().UnicastAddresses)
                            {
                                if (u.Address.AddressFamily == AddressFamily.InterNetwork && u.IPv4Mask != null)
                                {
                                    byte[] ipBytes = u.Address.GetAddressBytes();
                                    byte[] maskBytes = u.IPv4Mask.GetAddressBytes();
                                    byte[] bcastBytes = new byte[4];
                                    for (int i = 0; i < 4; i++) bcastBytes[i] = (byte)(ipBytes[i] | ~maskBytes[i]);
                                    var bcastIp = new IPAddress(bcastBytes);
                                    udp.Send(bytes, bytes.Length, new IPEndPoint(bcastIp, DiscoveryPort));
                                }
                            }
                        }
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                _log.Debug("[MP] LAN beacon broadcast error: " + ex.Message);
            }
        }

        /// <summary>
        /// Start listening for LAN discovery beacons in the background.
        /// </summary>
        public void StartListener()
        {
            if (_listening) return;
            _listening = true;

            try
            {
                _udpListener = new UdpClient();
                _udpListener.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                _udpListener.Client.Bind(new IPEndPoint(IPAddress.Any, DiscoveryPort));

                _listenerThread = new Thread(ListenLoop)
                {
                    IsBackground = true,
                    Name = "mp-lan-discovery"
                };
                _listenerThread.Start();
                _log.Info("[MP] LAN Discovery listener started on port " + DiscoveryPort + ".");
            }
            catch (Exception ex)
            {
                _log.Warn("[MP] Could not start LAN discovery listener: " + ex.Message);
                _listening = false;
            }
        }

        public void StopListener()
        {
            _listening = false;
            try
            {
                _udpListener?.Close();
            }
            catch { }
            _udpListener = null;
            _servers.Clear();
            _log.Info("[MP] LAN Discovery listener stopped.");
        }

        private void ListenLoop()
        {
            var remoteEp = new IPEndPoint(IPAddress.Any, 0);
            while (_listening)
            {
                try
                {
                    if (_udpListener == null) break;
                    byte[] bytes = _udpListener.Receive(ref remoteEp);
                    if (bytes == null || bytes.Length == 0) continue;

                    string text = Encoding.UTF8.GetString(bytes);
                    if (!text.StartsWith(BeaconMagic, StringComparison.Ordinal)) continue;

                    string[] parts = text.Substring(BeaconMagic.Length).Split('|');
                    if (parts.Length < 7) continue;

                    string serverName = parts[0];
                    string cityName = parts[1];
                    int.TryParse(parts[2], out int pop);
                    int.TryParse(parts[3], out int players);
                    int.TryParse(parts[4], out int maxPlayers);
                    int.TryParse(parts[5], out int port);
                    bool reqPassword = parts[6] == "1";

                    string key = remoteEp.Address.ToString() + ":" + port;
                    long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

                    var server = new DiscoveredLanServer
                    {
                        ServerName = serverName,
                        CityName = cityName,
                        Population = pop,
                        PlayerCount = players,
                        MaxPlayers = maxPlayers,
                        Address = remoteEp.Address.ToString(),
                        Port = port > 0 ? port : 25001,
                        RequiresPassword = reqPassword,
                        LastSeenMs = now
                    };

                    _servers[key] = server;

                    // Prune servers not seen in > 6 seconds
                    foreach (var pair in _servers)
                    {
                        if (now - pair.Value.LastSeenMs > 6000)
                        {
                            DiscoveredLanServer removed;
                            _servers.TryRemove(pair.Key, out removed);
                        }
                    }
                }
                catch (SocketException)
                {
                    // Closed during stop
                    break;
                }
                catch (Exception ex)
                {
                    _log.Debug("[MP] LAN discovery listen loop warning: " + ex.Message);
                }
            }
        }

        public void Dispose()
        {
            StopBroadcaster();
            StopListener();
        }
    }
}
