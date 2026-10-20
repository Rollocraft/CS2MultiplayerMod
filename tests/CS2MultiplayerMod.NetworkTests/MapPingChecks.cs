using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;
using CS2MultiplayerMod.Core.Networking;
using CS2MultiplayerMod.Core.Protocol;
using CS2MultiplayerMod.Core.Protocol.Messages;
using CS2MultiplayerMod.Core.Session;

internal static class MapPingChecks
{
    public static void Run()
    {
        var codec = MessageCodec.CreateDefault();
        var message = new MapPingMessage { PlayerId = 7, PlayerName = "Tester", X = 12, Y = 5, Z = -20 };
        byte[] bytes = codec.Encode(message);
        var copy = (MapPingMessage)codec.Decode(bytes);
        Check(copy.PlayerId == 7 && copy.PlayerName == "Tester" && copy.Z == -20, "ping round-trip");
        foreach (float invalid in new[] { float.NaN, float.PositiveInfinity, WireGuard.MaxCoordinate + 1 })
        {
            byte[] malformed = (byte[])bytes.Clone();
            Array.Copy(BitConverter.GetBytes(invalid), 0, malformed, malformed.Length - 4, 4);
            Reject(() => codec.Decode(malformed));
        }
        for (int i = 0; i < bytes.Length; i++) Reject(() => codec.Decode(bytes.Take(i).ToArray()));

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var host = new MultiplayerSession(null);
        var alice = new MultiplayerSession(null);
        var bob = new MultiplayerSession(null);
        var hostEvents = new Observer();
        var aliceEvents = new Observer();
        var bobEvents = new Observer();
        host.AddObserver(hostEvents);
        alice.AddObserver(aliceEvents);
        bob.AddObserver(bobEvents);
        var clock = Stopwatch.StartNew();
        void PumpUntil(Func<bool> done)
        {
            long deadline = clock.ElapsedMilliseconds + 5000;
            do
            {
                host.Update(clock.ElapsedMilliseconds);
                alice.Update(clock.ElapsedMilliseconds);
                bob.Update(clock.ElapsedMilliseconds);
                if (done()) return;
                Thread.Sleep(5);
            } while (clock.ElapsedMilliseconds < deadline);
            throw new Exception("Ping integration check timed out.");
        }
        void RawPing(MapPingMessage ping) => typeof(MultiplayerSession)
            .GetMethod("SendTo", BindingFlags.Instance | BindingFlags.NonPublic)
            .Invoke(alice, new object[] { ConnectionId.Server, ping });
        try
        {
            host.StartHost(new MultiplayerConfig("Host", "127.0.0.1", port, useEncryption: false));
            alice.Join(new MultiplayerConfig("Alice", "127.0.0.1", port, useEncryption: false));
            bob.Join(new MultiplayerConfig("Bob", "127.0.0.1", port, useEncryption: false));
            PumpUntil(() => alice.Status == SessionStatus.Connected && bob.Status == SessionStatus.Connected);
            RawPing(new MapPingMessage { PlayerId = 999, PlayerName = "Impostor", X = 3 });
            PumpUntil(() => hostEvents.Pings.Count == 1 && aliceEvents.Pings.Count == 1 && bobEvents.Pings.Count == 1);
            Check(bobEvents.Pings[0].PlayerId == alice.LocalPlayerId && bobEvents.Pings[0].PlayerName == "Alice", "host stamps ping identity");
            RawPing(message);
            // A subsequent chat provides an ordering fence for the rejected second ping.
            alice.SendChat("fence");
            PumpUntil(() => bobEvents.ChatCount == 1);
            Check(hostEvents.Pings.Count == 1 && bobEvents.Pings.Count == 1, "host drops rapid pings");
            Check(host.SendMapPing(5, 6, 7), "host can ping");
            Check(!host.SendMapPing(5, 6, 7), "local cooldown");
            PumpUntil(() => aliceEvents.Pings.Count == 2 && bobEvents.Pings.Count == 2);
            var targets = host.Peers.Select(p => p.Connection).ToList();
            Check(host.BeginWorldSync(1, 1, targets), "start sync");
            PumpUntil(() => alice.WorldSyncSuspended && bob.WorldSyncSuspended);
            Check(!alice.SendMapPing(1, 2, 3), "no pings during world sync");
            host.Stop();
            Check(!host.SendMapPing(1, 2, 3), "no offline pings");
        }
        finally { alice.Stop(); bob.Stop(); host.Stop(); }
        Console.WriteLine("Map ping codec, identity, relay, cooldown, and sync checks passed.");
    }

    private static void Check(bool value, string why) { if (!value) throw new Exception(why); }
    private static void Reject(Action action)
    {
        try { action(); }
        catch (ProtocolException) { return; }
        throw new Exception("Malformed ping was accepted.");
    }
    private sealed class Observer : SessionObserver
    {
        public readonly List<MapPingMessage> Pings = new List<MapPingMessage>();
        public int ChatCount;
        public override void OnMapPingReceived(MapPingMessage ping) => Pings.Add(ping);
        public override void OnChatReceived(string senderName, string text) { if (text == "fence") ChatCount++; }
    }
}
