using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using CS2MultiplayerMod.Core.Diagnostics;
using CS2MultiplayerMod.Core.Networking;
using CS2MultiplayerMod.Core.Protocol;
using CS2MultiplayerMod.Core.Protocol.Messages;
using CS2MultiplayerMod.Core.Session;

static class Program
{
    static readonly MessageCodec Codec = MessageCodec.CreateDefault();
    static readonly FieldInfo TransportField = typeof(MultiplayerSession).GetField("_transport", BindingFlags.Instance | BindingFlags.NonPublic);
    static int Main()
    {
        try
        {
            CodecChecks();
            SessionChecks(false);
            SessionChecks(true);
            Console.WriteLine("PASS: hover codecs, hostile payloads, TCP/TLS relay, identity stamping, clearing, backpressure and world-sync barriers.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    static PlayerHoverShape Shape(PlayerHoverKind kind = PlayerHoverKind.Box) => new()
    {
        Kind = kind, Placement = true, Key = 123,
        A = new(10, 20, 30), B = new(40, 20, 30), C = new(40, 20, 60), D = new(10, 20, 60),
        Width = 12, Height = 30
    };
    static PlayerStateMessage State(params PlayerHoverShape[] shapes) => new(99, 1, 2, 3, 4, 5, 6, 7, shapes);
    static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
    static void Reject(Action action, string name)
    {
        try { action(); }
        catch (ProtocolException) { return; }
        throw new Exception("Accepted invalid input: " + name);
    }

    static void CodecChecks()
    {
        var handshake = new HandshakeRequest(ProtocolConstants.ProtocolVersion, "test", "commit", "game",
            "Player", new byte[] { 1, 2 }, new[] { "DLC" }, new[] { "Traffic", "Move It" });
        var decodedHandshake = (HandshakeRequest)Codec.Decode(Codec.Encode(handshake));
        Assert(decodedHandshake.ModManifest.SequenceEqual(handshake.ModManifest),
            "handshake preserves the active mod manifest");
        Assert(MultiplayerSession.DescribeModMismatch(new[] { "Traffic" }, new[] { "traffic" }) == null,
            "mod manifest comparison is case-insensitive");
        Assert(MultiplayerSession.DescribeModMismatch(new[] { "Traffic" }, new[] { "Move It" }) != null,
            "mod manifest comparison names a differing playset");
        string buildMismatch = MultiplayerSession.DescribeModMismatch(
            new[] { "Traffic@1.2.0#abc" }, new[] { "Traffic@1.3.0#def" });
        Assert(buildMismatch != null && buildMismatch.Contains("different build") &&
               !buildMismatch.Contains("you are missing"),
            "mod manifest comparison separates build mismatch from missing mod");
        var receipt = new NetOperationReceiptMessage(3, 91, true, "committed and drained");
        var decodedReceipt = (NetOperationReceiptMessage)Codec.Decode(Codec.Encode(receipt));
        Assert(decodedReceipt.OriginPlayerId == 3 && decodedReceipt.OperationId == 91 &&
               decodedReceipt.Applied && decodedReceipt.Detail == "committed and drained",
            "net-operation receipt round trips");

        foreach (PlayerHoverKind kind in Enum.GetValues<PlayerHoverKind>())
        {
            var shape = Shape(kind);
            byte[] wire = Codec.Encode(State(shape));
            Assert(wire.Length == 34 + PlayerHoverShape.WireSize, "wire size");
            var decoded = (PlayerStateMessage)Codec.Decode(wire);
            Assert(decoded.PlayerId == 99 && decoded.Yaw == 7 && decoded.Hover.Length == 1, "presence round trip");
            Assert(Codec.Encode(decoded).SequenceEqual(wire), "lossless geometry round trip");
            for (int length = 0; length < wire.Length; length++)
                Reject(() => Codec.Decode(wire.Take(length).ToArray()), "truncated geometry");
        }
        Assert(((PlayerStateMessage)Codec.Decode(Codec.Encode(State()))).Hover.Length == 0, "explicit clear");
        Assert(Codec.Encode(State(Enumerable.Repeat(Shape(), 8).ToArray())).Length == 530, "bounded maximum packet");
        Reject(() => Codec.Encode(State(Enumerable.Repeat(Shape(), 9).ToArray())), "too many outbound shapes");
        var good = Codec.Encode(State(Shape()));
        void BadByte(int offset, byte value)
        {
            var bad = (byte[])good.Clone(); bad[offset] = value;
            Reject(() => Codec.Decode(bad), "byte at " + offset);
        }
        BadByte(33, 9); BadByte(34, 0); BadByte(34, 255); BadByte(35, 2);
        foreach (float invalid in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity })
            for (int offset = 40; offset <= 92; offset += 4)
            {
                var bad = (byte[])good.Clone();
                BitConverter.GetBytes(invalid).CopyTo(bad, offset);
                Reject(() => Codec.Decode(bad), "nonfinite geometry");
            }
        foreach (int offset in new[] { 88, 92 })
        {
            foreach (float invalid in new[] { -1f, 5001f })
            {
                var bad = (byte[])good.Clone();
                BitConverter.GetBytes(invalid).CopyTo(bad, offset);
                Reject(() => Codec.Decode(bad), "unbounded dimensions");
            }
        }
        var far = Shape(); far.D.X = 50000;
        Reject(() => Codec.Encode(State(far)), "oversized footprint");
        var invalidLocal = Shape(); invalidLocal.Width = float.NaN;
        Reject(() => Codec.Encode(State(invalidLocal)), "invalid local geometry");
        var rng = new Random(54321);
        for (int i = 0; i < 5000; i++)
        {
            var wire = (byte[])good.Clone();
            wire[rng.Next(33, wire.Length)] = (byte)rng.Next(256);
            try { Codec.Encode(Codec.Decode(wire)); }
            catch (ProtocolException) { }
        }
    }

    static void SessionChecks(bool encryption)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start(); int port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        var host = new MultiplayerSession(NullModLogger.Instance);
        var alice = new MultiplayerSession(NullModLogger.Instance);
        var bob = new MultiplayerSession(NullModLogger.Instance);
        var sessions = new[] { host, alice, bob };
        var observed = sessions.Select(_ => new Observer()).ToArray();
        for (int i = 0; i < sessions.Length; i++) sessions[i].AddObserver(observed[i]);
        var clock = Stopwatch.StartNew();
        void Pump(Func<bool> done)
        {
            long deadline = clock.ElapsedMilliseconds + 10000;
            while (clock.ElapsedMilliseconds < deadline)
            {
                foreach (var session in sessions) session.Update(clock.ElapsedMilliseconds);
                if (done()) return;
                Thread.Sleep(2);
            }
            throw new Exception("Session condition timed out (TLS=" + encryption + ").");
        }
        void Settle()
        {
            long end = clock.ElapsedMilliseconds + 150;
            Pump(() => clock.ElapsedMilliseconds >= end);
        }
        MultiplayerConfig Config(string name) => new(name, "127.0.0.1", port, "hover-test", true, encryption);
        void Send(MultiplayerSession session, params PlayerHoverShape[] shapes) =>
            session.SendPlayerState(1, 2, 3, 4, 5, 6, 7, shapes);
        try
        {
            host.StartHost(Config("Host")); alice.Join(Config("Alice")); bob.Join(Config("Bob"));
            Pump(() => alice.Status == SessionStatus.Connected && bob.Status == SessionStatus.Connected);
            Send(alice, Shape());
            Pump(() => observed[0].States.Count == 1 && observed[2].States.Count == 1);
            Assert(observed[1].States.Count == 0, "source must not receive its own echo");
            Assert(observed[2].States.Last().PlayerId == alice.LocalPlayerId, "relayed source identity");
            Assert(observed[2].States.Last().Hover[0].Height == 30, "relayed geometry");
            Send(host, Shape(PlayerHoverKind.Circle));
            Pump(() => observed[1].States.Count == 1 && observed[2].States.Count == 2);
            Assert(observed[1].States.Last().PlayerId == host.LocalPlayerId, "host presence");
            var transport = (ITransport)TransportField.GetValue(alice);
            transport.Send(ConnectionId.Server, Codec.Encode(State(Shape(PlayerHoverKind.Curve))));
            Pump(() => observed[0].States.Count == 2 && observed[2].States.Count == 3);
            Assert(observed[0].States.Last().PlayerId == alice.LocalPlayerId &&
                observed[2].States.Last().PlayerId == alice.LocalPlayerId, "host prevents impersonation");

            Send(alice);
            Pump(() => observed[0].States.Count == 3 && observed[2].States.Count == 4);
            Assert(observed[2].States.Last().Hover.Length == 0, "clear relays to peers");

            var blocked = new BackpressureTransport(transport);
            TransportField.SetValue(alice, blocked);
            Send(alice, Shape());
            Assert(blocked.Sends == 0, "sender drops presence under backpressure");
            TransportField.SetValue(alice, transport);
            var hostTransport = (ITransport)TransportField.GetValue(host);
            TransportField.SetValue(host, new BackpressureTransport(hostTransport));
            Send(alice, Shape());
            Pump(() => observed[0].States.Count == 4);
            Settle();
            Assert(observed[2].States.Count == 4, "host suppresses relay under backpressure");
            TransportField.SetValue(host, hostTransport);
            Send(alice, Shape(PlayerHoverKind.Circle));
            Pump(() => observed[2].States.Count == 5);
            Assert(observed[2].States.Last().Hover[0].Kind == PlayerHoverKind.Circle, "newest state recovers");

            var targets = host.Peers.Select(p => p.Connection).ToList();
            Assert(host.BeginWorldSync(123, 1, targets), "begin barrier");
            Pump(() => alice.WorldSyncSuspended && bob.WorldSyncSuspended);
            int count = observed.Sum(o => o.States.Count);
            Send(alice, Shape()); Send(host, Shape());
            Settle();
            Assert(observed.Sum(o => o.States.Count) == count, "barrier suppresses hover");
            long resumeAt = clock.ElapsedMilliseconds;
            Assert(host.ResumeWorldSync(123, 1, targets), "resume barrier");
            Pump(() => !alice.WorldSyncSuspended && !bob.WorldSyncSuspended);
            alice.SendCommand(1, 7, new byte[] { 1 });
            Settle();
            Assert(observed[0].Commands.Count == 0 && observed[2].Commands.Count == 0,
                "post-sync stale command is discarded");
            Pump(() => clock.ElapsedMilliseconds >= resumeAt + 350);
            alice.SendCommand(2, 7, new byte[] { 2 });
            Pump(() => observed[0].Commands.Count == 1);
            Send(alice);
            Pump(() => observed[2].States.Count == 6);
            Assert(observed[2].States.Last().Hover.Length == 0, "clear after reload");
        }
        finally { foreach (var session in sessions) session.Stop(); }
    }

    sealed class Observer : SessionObserver
    {
        public readonly List<PlayerStateMessage> States = new();
        public readonly List<SimulationCommandMessage> Commands = new();
        public override void OnPlayerStateReceived(PlayerStateMessage state) => States.Add(state);
        public override void OnCommandReceived(SimulationCommandMessage command) => Commands.Add(command);
    }

    sealed class BackpressureTransport(ITransport inner) : ITransport
    {
        public int Sends;
        public bool IsActive => inner.IsActive;
        public long PendingSendBytes => 16385;
        public void Send(ConnectionId target, byte[] payload) { Sends++; inner.Send(target, payload); }
        public void Disconnect(ConnectionId connection) => inner.Disconnect(connection);
        public void DisconnectAfterFlush(ConnectionId connection) => inner.DisconnectAfterFlush(connection);
        public int Poll(IList<TransportEvent> sink) => inner.Poll(sink);
        public string GetRemoteAddress(ConnectionId connection) => inner.GetRemoteAddress(connection);
        public byte[] GetChannelBinding(ConnectionId connection) => inner.GetChannelBinding(connection);
        public void Shutdown() => inner.Shutdown();
        public void ShutdownAfterFlush(int timeoutMs) => inner.ShutdownAfterFlush(timeoutMs);
        public void Dispose() => inner.Dispose();
    }
}
