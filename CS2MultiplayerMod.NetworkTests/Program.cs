using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using CS2MultiplayerMod.Core.Diagnostics;
using CS2MultiplayerMod.Core.Networking;
using CS2MultiplayerMod.Core.Networking.Tcp;
using CS2MultiplayerMod.Core.Session;

// What a player may type as the host address.
CheckSplit("127.0.0.1", "127.0.0.1", 0);
CheckSplit(" 192.168.1.5:25002 ", "192.168.1.5", 25002);
CheckSplit("example.com:25001", "example.com", 25001);
CheckSplit("2001:db8::1", "2001:db8::1", 0);
CheckSplit("2001:db8::1:25001", "2001:db8::1:25001", 0); // unbracketed IPv6 never carries a port
CheckSplit("[2001:db8::1]", "2001:db8::1", 0);
CheckSplit("[2001:db8::1]:25001", "2001:db8::1", 25001);
CheckSplit("[fe80::1%12]:25001", "fe80::1%12", 25001);
foreach (string bad in new[] { "", "[]:25001", "[2001:db8::1]:", "[2001:db8::1]:99999", "[2001:db8::1]x", "host:abc", ":1" })
    Check(!NetAddress.TrySplitHostPort(bad, out _, out _), "'" + bad + "' must not parse as a host");

Check(NetAddress.FormatEndpoint("2001:db8::1", 25001) == "[2001:db8::1]:25001", "IPv6 endpoints are bracketed");
Check(NetAddress.FormatEndpoint("1.2.3.4", 25001) == "1.2.3.4:25001", "IPv4 endpoints are not");

// Bans: one key per IPv4 address, per IPv6 /64, and per relay identity.
Check(NetAddress.BanKey("::ffff:1.2.3.4") == "1.2.3.4", "a dual-stack IPv4 peer is keyed as plain IPv4");
Check(NetAddress.BanKey("2001:db8:1:2:aaaa::1") == "2001:db8:1:2::/64", "IPv6 is keyed by its /64");
Check(NetAddress.BanKey("2001:db8:1:3::1") != NetAddress.BanKey("2001:db8:1:2::1"), "a neighbouring /64 is another key");
Check(NetAddress.BanKey("::1") == "::1" && NetAddress.BanKey("fe80::1") == "fe80::1", "loopback and link-local stay whole");
Check(NetAddress.BanKey("steam:76561198000000000") == "steam:76561198000000000", "relay identities pass through");

var tracker = new FailedAuthTracker();
for (int i = 1; i < FailedAuthTracker.MaxFailures; i++) tracker.RecordFailure("2001:db8:1:2::" + i, 0);
Check(tracker.RecordFailure("2001:db8:1:2::ffff", 0), "failures from fresh addresses in one /64 add up to a ban");
Check(tracker.IsBanned("2001:db8:1:2:abcd::9", 1), "the ban covers the whole /64");
Check(!tracker.IsBanned("2001:db8:1:3::9", 1), "the ban stops at the /64");

// The real transports over loopback: IPv4 always, IPv6 where this machine has it.
var probe = new TcpListener(IPAddress.Loopback, 0);
probe.Start();
int port = ((IPEndPoint)probe.LocalEndpoint).Port; // a free port for the host
probe.Stop();
var host = new TcpServerTransport(NullModLogger.Instance);
host.Start(port, lanOnly: true);
CheckConnects(host, "127.0.0.1", port, "127.0.0.1");
CheckConnects(host, "localhost", port, null);
if (Socket.OSSupportsIPv6) CheckConnects(host, "::1", port, "::1");
else Console.WriteLine("IPv6 unavailable on this machine; skipped the IPv6 loopback check.");
host.Shutdown();

Console.WriteLine("Network regression checks passed.");

static void CheckSplit(string text, string host, int port)
{
    Check(NetAddress.TrySplitHostPort(text, out string h, out int p) && h == host && p == port,
        "'" + text + "' should split into '" + host + "' and " + port + ", got '" + h + "' and " + p);
}

static void CheckConnects(TcpServerTransport host, string address, int port, string expectedRemote)
{
    var client = new TcpClientTransport(NullModLogger.Instance);
    client.Connect(address, port, useTls: false);
    var events = new List<TransportEvent>();
    string remote = null;
    bool joined = false;
    var deadline = Stopwatch.StartNew();
    while ((remote == null || !joined) && deadline.ElapsedMilliseconds < 5000)
    {
        events.Clear();
        host.Poll(events);
        foreach (TransportEvent evt in events)
            if (evt.Type == TransportEventType.Connected) remote = host.GetRemoteAddress(evt.Connection);
        events.Clear();
        client.Poll(events);
        foreach (TransportEvent evt in events)
        {
            Check(evt.Type != TransportEventType.Disconnected, "joining " + address + " failed: " + evt.Detail);
            joined |= evt.Type == TransportEventType.Connected;
        }
        Thread.Sleep(5);
    }
    client.Shutdown();
    Check(joined && remote != null, "joining " + address + " timed out");
    Check(expectedRemote == null || remote == expectedRemote,
        "the host should see " + expectedRemote + " when joined via " + address + ", saw " + remote);
}

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}
