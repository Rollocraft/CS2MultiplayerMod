using System;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace CS2MultiplayerMod.Core.Networking.Stun
{
    /// <summary>
    /// RFC 5389 compliant lightweight STUN client for public IP and NAT mapping discovery.
    /// </summary>
    public static class StunClient
    {
        private const ushort StunBindingRequest = 0x0001;
        private const uint MagicCookie = 0x2112A442;
        public const string DefaultStunServer = "stun.l.google.com";
        public const int DefaultStunPort = 19302;

        public static async Task<IPEndPoint> QueryExternalEndpointAsync(string host = DefaultStunServer, int port = DefaultStunPort)
        {
            return await Task.Run(() =>
            {
                try
                {
                    using (var udp = new UdpClient())
                    {
                        udp.Client.ReceiveTimeout = 3000;
                        udp.Client.SendTimeout = 3000;

                        // Build STUN Binding Request Header (20 bytes)
                        byte[] request = new byte[20];
                        request[0] = (byte)(StunBindingRequest >> 8);
                        request[1] = (byte)(StunBindingRequest & 0xFF);
                        request[2] = 0; // Message Length (0 attributes)
                        request[3] = 0;

                        // Magic Cookie (0x2112A442)
                        request[4] = 0x21;
                        request[5] = 0x12;
                        request[6] = 0xA4;
                        request[7] = 0x42;

                        // Transaction ID (96-bit random)
                        var rng = new Random();
                        byte[] txId = new byte[12];
                        rng.NextBytes(txId);
                        Array.Copy(txId, 0, request, 8, 12);

                        IPAddress[] addresses = Dns.GetHostAddresses(host);
                        if (addresses == null || addresses.Length == 0) return null;

                        var endpoint = new IPEndPoint(addresses[0], port);
                        udp.Send(request, request.Length, endpoint);

                        var remoteEp = new IPEndPoint(IPAddress.Any, 0);
                        byte[] response = udp.Receive(ref remoteEp);

                        if (response == null || response.Length < 20) return null;

                        // Parse attributes looking for XOR-MAPPED-ADDRESS (0x0020) or MAPPED-ADDRESS (0x0001)
                        int offset = 20;
                        while (offset + 4 <= response.Length)
                        {
                            ushort attrType = (ushort)((response[offset] << 8) | response[offset + 1]);
                            ushort attrLen = (ushort)((response[offset + 2] << 8) | response[offset + 3]);
                            offset += 4;

                            if (attrType == 0x0020 && attrLen >= 8 && offset + attrLen <= response.Length) // XOR-MAPPED-ADDRESS
                            {
                                byte family = response[offset + 1];
                                if (family == 0x01) // IPv4
                                {
                                    ushort xorPort = (ushort)((response[offset + 2] << 8) | response[offset + 3]);
                                    int realPort = xorPort ^ (int)(MagicCookie >> 16);

                                    byte[] ip = new byte[4];
                                    ip[0] = (byte)(response[offset + 4] ^ 0x21);
                                    ip[1] = (byte)(response[offset + 5] ^ 0x12);
                                    ip[2] = (byte)(response[offset + 6] ^ 0xA4);
                                    ip[3] = (byte)(response[offset + 7] ^ 0x42);

                                    return new IPEndPoint(new IPAddress(ip), realPort);
                                }
                            }
                            offset += attrLen;
                        }
                    }
                }
                catch
                {
                    // Fallback on timeout / unreachable STUN server
                }
                return null;
            });
        }
    }
}
