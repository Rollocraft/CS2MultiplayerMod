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
using CS2MultiplayerMod.Core.Sync;

internal static class SmartResyncChecks
{
    private static readonly MessageCodec Codec = MessageCodec.CreateDefault();
    public static void Run()
    {
        var a = new ConnectionId(10);
        var b = new ConnectionId(11);
        var scope = new SnapshotRecoveryScope();
        scope.Request(a);
        Check(scope.Take(new[] { a, b }).SequenceEqual(new[] { a }), "client fault only reloads its world");
        scope.Request(a); scope.Request(b);
        Check(scope.Take(new[] { a, b }).Count == 2, "concurrent requests combine");
        scope.Request(ConnectionId.None);
        Check(scope.Take(new[] { a, b }).Count == 2, "host evidence reloads everyone");
        scope.Request(a); scope.Remove(a);
        Check(scope.Take(new[] { b }).Count == 0, "departed target never broadens recovery");
        scope.Request(a); scope.Take(new[] { a, b }); scope.Request(b);
        Check(scope.HasRequests && scope.Take(new[] { a, b }).SequenceEqual(new[] { b }), "late target remains for next epoch");
        var limiter = new PeerRateLimiter();
        limiter.Account(0, 64, -1, false, true);
        Check(limiter.TryAcceptRepairFallback(0), "fallback can use remaining recovery budget");
        Check(!limiter.TryAcceptRepairFallback(0), "partial fallback cannot bypass full recovery budget");

        var request = new ResyncRequestMessage(9, "repair") { StateRequestId = 1, Digests = Digests() };
        var requestCopy = (ResyncRequestMessage)Codec.Decode(Codec.Encode(request));
        Check(requestCopy.StateRequestId == 1 && requestCopy.Digests.Length == 2, "request round-trip");
        Check(StateDigest.Equal(requestCopy.Digests[0].Hash, StateDigest.Compute(new byte[] { 1 })), "checksum round-trip");
        Check(!StateDigest.Equal(requestCopy.Digests[0].Hash, StateDigest.Compute(new byte[] { 2 })), "mismatch detected");
        var reply = new StateRepairMessage { RequestId = 1, Snapshots = new[] { new StateSnapshotMessage(1, new byte[] { 2 }) } };
        foreach (INetMessage message in new INetMessage[] { request, reply })
        {
            var encoded = Codec.Encode(message);
            for (int i = 0; i < encoded.Length; i++) Reject(() => Codec.Decode(encoded.Take(i).ToArray()));
        }
        Reject(() => Codec.Encode(new StateRepairMessage { RequestId = 1, Stage = (StateRepairStage)255 }));
        Reject(() => Codec.Encode(new StateRepairMessage { RequestId = 1,
            Snapshots = new[] { new StateSnapshotMessage(1, new byte[4097]) } }));
        Reject(() => Codec.Encode(new StateRepairMessage { RequestId = 1, Stage = StateRepairStage.Applied, Snapshots = reply.Snapshots }));
        Reject(() => Codec.Encode(new ResyncRequestMessage { StateRequestId = 1, Digests = new[] { Digests()[0], Digests()[0] } }));
        Reject(() => Codec.Encode(new ResyncRequestMessage { StateRequestId = 1, Digests = new[] { new StateDigest { Hash = new byte[31] } } }));
        byte[] invalidLength = Codec.Encode(reply);
        Array.Copy(BitConverter.GetBytes(int.MaxValue), 0, invalidLength, 12, 4);
        Reject(() => Codec.Decode(invalidLength));

        SessionCase(ClientResyncPolicy.Allow, (host, client, other, h, c, o, pump) =>
        {
            Check(client.RequestStateRepair(Digests()), "request started");
            Check(!client.RequestStateRepair(Digests()), "only one request in flight");
            pump(() => c.Replies.Count == 1);
            Check(c.Replies[0].Snapshots.Length == 1 && c.Replies[0].Snapshots[0].ChannelId == 1, "only mismatching channel transferred");
            Check(o.Replies.Count == 0, "other clients receive no repair payload");
            client.CompleteStateRepair(c.Replies[0].RequestId, true);
            client.SendChat("fence");
            pump(() => h.Fences == 1);
            Check(!client.StateRepairPending && h.WorldRequests.Count == 0, "verified repair does not reload");
        });
        SessionCase(ClientResyncPolicy.Allow, (host, client, other, h, c, o, pump) =>
        {
            client.RequestStateRepair(Digests());
            pump(() => c.Replies.Count == 1);
            long id = c.Replies[0].RequestId;
            RawSend(client, ConnectionId.Server, new StateRepairMessage { RequestId = id + 1, Stage = StateRepairStage.Failed });
            client.SendChat("fence"); pump(() => h.Fences == 1);
            Check(h.WorldRequests.Count == 0, "stale failure cannot authorize reload");
            client.CompleteStateRepair(id, false);
            pump(() => h.WorldRequests.Count == 1);
            Check(h.WorldRequests[0] == host.Peers.Single(p => p.PlayerId == client.LocalPlayerId).Connection, "fallback targets affected client");
            RawSend(client, ConnectionId.Server, new StateRepairMessage { RequestId = id, Stage = StateRepairStage.Failed });
            client.SendChat("fence"); pump(() => h.Fences == 2);
            Check(h.WorldRequests.Count == 1, "duplicate failure cannot loop reloads");
        });
        SessionCase(ClientResyncPolicy.Allow, (host, client, other, h, c, o, pump) =>
        {
            client.RequestStateRepair(Digests()); pump(() => c.Replies.Count == 1);
            typeof(MultiplayerSession).GetMethod("PumpStateRepairs", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(host, new object[] { long.MaxValue });
            Check(h.WorldRequests.Count == 1, "missing apply acknowledgement falls back");
        });
        SessionCase(ClientResyncPolicy.RequireApproval, (host, client, other, h, c, o, pump) =>
        {
            client.RequestStateRepair(Digests()); pump(() => host.PendingResyncRequests.Any());
            Check(c.Replies.Count == 0 && h.RepairRequests == 0, "approval gate holds partial repair");
            host.ApproveResyncRequest(client.LocalPlayerId, 10000);
            pump(() => c.Replies.Count == 1);
            client.CompleteStateRepair(c.Replies[0].RequestId, true);
        });
        SessionCase(ClientResyncPolicy.RequireApproval, (host, client, other, h, c, o, pump) =>
        {
            client.RequestStateRepair(Digests()); pump(() => host.PendingResyncRequests.Any());
            host.DeclineResyncRequest(client.LocalPlayerId); pump(() => !client.StateRepairPending);
            Check(h.RepairRequests == 0 && h.WorldRequests.Count == 0, "declined repair does not mutate or reload");
        });
        SessionCase(ClientResyncPolicy.HostOnly, (host, client, other, h, c, o, pump) =>
        {
            client.RequestStateRepair(Digests()); pump(() => !client.StateRepairPending);
            Check(h.RepairRequests == 0, "host-only policy also blocks partial repairs");
        });
        SessionCase(ClientResyncPolicy.RequireApproval, (host, client, other, h, c, o, pump) =>
        {
            client.RequestStateRepair(Digests()); pump(() => host.PendingResyncRequests.Any());
            host.BeginWorldSync(1, 1, host.Peers.Select(p => p.Connection).ToList());
            Check(!host.PendingResyncRequests.Any(), "world replacement cancels stale partial approvals");
            pump(() => !client.StateRepairPending);
        });
        SessionCase(ClientResyncPolicy.Allow, (host, client, other, h, c, o, pump) =>
        {
            int id = client.LocalPlayerId;
            RawSend(client, ConnectionId.Server, new StateRepairMessage { RequestId = 1 });
            pump(() => !host.Peers.Any(p => p.PlayerId == id));
            Check(h.WorldRequests.Count == 0, "client cannot send authoritative repair snapshots");
        });
        SessionCase(ClientResyncPolicy.Allow, (host, client, other, h, c, o, pump) =>
        {
            h.SendSnapshots = false;
            client.RequestStateRepair(Digests()); pump(() => h.RepairRequests == 1);
            var participants = host.Peers.Select(p => p.Connection).ToList();
            var affected = host.Peers.Single(p => p.PlayerId == client.LocalPlayerId).Connection;
            host.BeginWorldSync(1, 1, participants, new[] { affected });
            pump(() => client.WorldSyncSuspended && other.WorldSyncSuspended);
            Check(!client.StateRepairPending, "world replacement supersedes pending partial repair");
            Check(c.Stages.Contains(WorldSyncStage.Begin) && o.Stages.Contains(WorldSyncStage.BeginBarrierOnly), "healthy peer holds barrier without snapshot");
            host.ResumeWorldSync(1, 1, participants);
            pump(() => !client.WorldSyncSuspended);
            RawSend(host, affected, new StateRepairMessage { RequestId = 1 });
            host.SendChat("fence"); pump(() => c.Fences == 1);
            Check(c.Replies.Count == 0, "old reply cannot cross a world epoch");
        });
        Console.WriteLine("Smart resync codec, scope, checksum, approval, acknowledgement, fallback, and epoch checks passed.");
    }

    private static StateDigest[] Digests() => new[] {
        new StateDigest { ChannelId = 1, Hash = StateDigest.Compute(new byte[] { 1 }) },
        new StateDigest { ChannelId = 3, Hash = StateDigest.Compute(new byte[] { 3 }) },
    };
    private delegate void Scenario(MultiplayerSession host, MultiplayerSession client, MultiplayerSession other,
        Observer h, Observer c, Observer o, Action<Func<bool>> pump);
    private static void SessionCase(ClientResyncPolicy policy, Scenario scenario)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start(); int port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        var host = new MultiplayerSession(null);
        var client = new MultiplayerSession(null);
        var other = new MultiplayerSession(null);
        var h = new Observer(host); var c = new Observer(client); var o = new Observer(other);
        host.AddObserver(h); client.AddObserver(c); other.AddObserver(o);
        var clock = Stopwatch.StartNew();
        void Pump(Func<bool> done)
        {
            long until = clock.ElapsedMilliseconds + 5000;
            do
            {
                host.Update(clock.ElapsedMilliseconds); client.Update(clock.ElapsedMilliseconds); other.Update(clock.ElapsedMilliseconds);
                if (done()) return;
                Thread.Sleep(3);
            } while (clock.ElapsedMilliseconds < until);
            throw new Exception("Smart resync integration timed out.");
        }
        try
        {
            host.StartHost(new MultiplayerConfig("Host", "127.0.0.1", port, useEncryption: false, clientResyncPolicy: policy));
            client.Join(new MultiplayerConfig("Client", "127.0.0.1", port, useEncryption: false));
            other.Join(new MultiplayerConfig("Other", "127.0.0.1", port, useEncryption: false));
            Pump(() => client.Status == SessionStatus.Connected && other.Status == SessionStatus.Connected);
            scenario(host, client, other, h, c, o, Pump);
        }
        finally { client.Stop(); other.Stop(); host.Stop(); }
    }
    private sealed class Observer : SessionObserver
    {
        private readonly MultiplayerSession _session;
        public Observer(MultiplayerSession session) { _session = session; }
        public bool SendSnapshots = true;
        public int RepairRequests, Fences;
        public readonly List<StateRepairMessage> Replies = new List<StateRepairMessage>();
        public readonly List<ConnectionId> WorldRequests = new List<ConnectionId>();
        public readonly List<WorldSyncStage> Stages = new List<WorldSyncStage>();
        public override void OnStateResyncRequested(ConnectionId connection, long requestId, StateDigest[] digests)
        {
            RepairRequests++;
            if (!SendSnapshots) return;
            var changed = new List<StateSnapshotMessage>();
            foreach (var digest in digests)
            {
                byte[] authoritative = { digest.ChannelId == 1 ? (byte)2 : digest.ChannelId };
                if (!StateDigest.Equal(digest.Hash, StateDigest.Compute(authoritative)))
                    changed.Add(new StateSnapshotMessage(digest.ChannelId, authoritative));
            }
            _session.SendStateRepair(connection, requestId, changed.ToArray());
        }
        public override void OnStateRepairReceived(StateRepairMessage message) => Replies.Add(message);
        public override void OnResyncRequested(int playerId, ConnectionId connection) => WorldRequests.Add(connection);
        public override void OnChatReceived(string name, string text) { if (text == "fence") Fences++; }
        public override void OnWorldSyncControl(WorldSyncStage stage, long epoch, float speed, ConnectionId connection) => Stages.Add(stage);
    }
    private static void RawSend(MultiplayerSession session, ConnectionId target, INetMessage message) =>
        typeof(MultiplayerSession).GetMethod("SendTo", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(session, new object[] { target, message });
    private static void Check(bool condition, string why) { if (!condition) throw new Exception(why); }
    private static void Reject(Action action)
    {
        try { action(); } catch (ProtocolException) { return; }
        throw new Exception("Accepted invalid state repair payload.");
    }
}
