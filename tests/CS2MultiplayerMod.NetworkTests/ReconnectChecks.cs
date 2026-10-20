using System;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;
using CS2MultiplayerMod.Core.Networking;
using CS2MultiplayerMod.Core.Session;

internal static class ReconnectChecks
{
    public static void Run()
    {
        var schedule = new ReconnectBackoff();
        schedule.Begin(0);
        Check(!schedule.TryStartAttempt(999), "initial backoff");
        Check(schedule.TryStartAttempt(1000), "first attempt");
        Check(!schedule.TryStartAttempt(1001), "no overlapping attempts");
        Check(!schedule.AttemptTimedOut(15999) && schedule.AttemptTimedOut(16000), "silent handshake deadline");
        schedule.Failed(16000);
        Check(!schedule.TryStartAttempt(17999) && schedule.TryStartAttempt(18000), "second backoff");
        Check(schedule.Expired(120000), "overall deadline includes in-flight attempts");
        schedule.Begin(0);
        long now = 0;
        foreach (long delay in new long[] { 1000, 2000, 4000, 8000, 16000, 30000 })
        {
            now += delay;
            Check(schedule.TryStartAttempt(now), "bounded attempt starts");
            schedule.Failed(now);
        }
        Check(schedule.Attempts == 6 && schedule.Expired(now), "attempt cap");
        schedule.Reset();
        Check(!schedule.Active && !schedule.TryStartAttempt(long.MaxValue), "cancel clears schedule");

        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        var host = new MultiplayerSession(null);
        var client = new MultiplayerSession(null);
        var clock = Stopwatch.StartNew();
        MultiplayerConfig Config(string name, string password = "") =>
            new MultiplayerConfig(name, "127.0.0.1", port, password, useEncryption: false, autoReconnect: true);
        void Pump(Func<bool> done)
        {
            long deadline = clock.ElapsedMilliseconds + 8000;
            do
            {
                host.Update(clock.ElapsedMilliseconds);
                client.Update(clock.ElapsedMilliseconds);
                if (done()) return;
                Thread.Sleep(5);
            } while (clock.ElapsedMilliseconds < deadline);
            throw new Exception("Reconnect integration timeout: " + client.Status);
        }
        void Join()
        {
            client.Join(Config("Client"));
            Pump(() => client.Status == SessionStatus.Connected);
        }
        try
        {
            host.StartHost(Config("Host"));
            Join();
            int oldId = client.LocalPlayerId;
            host.DisconnectPeer(host.Peers.Single().Connection);
            Pump(() => client.Status == SessionStatus.Reconnecting);
            Check(client.Role == SessionRole.Client && client.IsReconnecting, "keep client intent during retry");
            Pump(() => client.Status == SessionStatus.Connected);
            Check(client.LocalPlayerId != oldId && !client.IsReconnecting, "fresh handshake after unexpected loss");

            host.DisconnectPeer(host.Peers.Single().Connection);
            Pump(() => client.IsReconnecting);
            client.Stop();
            long settle = clock.ElapsedMilliseconds + 1200;
            Pump(() => clock.ElapsedMilliseconds >= settle);
            Check(client.Status == SessionStatus.Offline && !client.IsReconnecting && !host.Peers.Any(), "cancel prevents redial");

            Join();
            host.KickPlayer(client.LocalPlayerId);
            Pump(() => client.Status == SessionStatus.Offline);
            Check(!client.IsReconnecting, "kick is terminal");
            Pump(() => !host.Peers.Any());

            Join();
            host.StopWithNotice("Host closed the session.");
            Pump(() => client.Status == SessionStatus.Offline);
            Check(!client.IsReconnecting, "intentional host close is terminal");

            host.StartHost(Config("Host"));
            Join();
            var transport = (ITransport)typeof(MultiplayerSession)
                .GetField("_transport", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(host);
            transport.Send(host.Peers.Single().Connection, new byte[] { 255 });
            Pump(() => client.Status == SessionStatus.Offline);
            Check(!client.IsReconnecting, "malformed host data is terminal");
            Pump(() => !host.Peers.Any());

            Join();
            host.Stop();
            Pump(() => client.IsReconnecting);
            host.StartHost(Config("Host", "changed-password"));
            Pump(() => client.Status == SessionStatus.Offline);
            Check(!client.IsReconnecting, "rejected reconnect does not loop");

            client.Join(Config("Client"));
            Pump(() => client.Status == SessionStatus.Offline);
            Check(!client.IsReconnecting, "initial rejected join does not retry");
        }
        finally { client.Stop(); host.Stop(); }
        Console.WriteLine("Reconnect scheduling, rejoin, cancellation, kick, shutdown, rejection and protocol checks passed.");
    }
    private static void Check(bool value, string why) { if (!value) throw new Exception(why); }
}
