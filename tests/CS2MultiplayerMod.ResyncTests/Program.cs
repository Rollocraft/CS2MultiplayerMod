using System;
using System.Collections.Generic;
using CS2MultiplayerMod.Core.Diagnostics;
using CS2MultiplayerMod.Core.Protocol;
using CS2MultiplayerMod.Game.Diagnostics;
using CS2MultiplayerMod.Game.Sync.Commands;
using CS2MultiplayerMod.Game.Sync.Infrastructure;

internal static class Program
{
    private static int _checks;
    private static long _now;
    private static int _submissions;

    private static void Check(bool condition, string message)
    {
        _checks++;
        if (!condition) throw new Exception(message);
    }

    private static ResyncReport Missing(string subject, string subsystem = "object") =>
        ResyncReport.Create("target missing", subsystem, ResyncEvidence.MissingTarget).About(subject);

    private static void Main()
    {
        SyncInbox.Arbitrate = report =>
        {
            _submissions++;
            return ResyncArbiter.Submit(report, _now, false);
        };

        var retry = new ResyncRetry();
        _now = 10000;
        Check(retry.KeepTrying(Missing("overpass")), "first timeout retains the operation");
        for (_now = 10200; _now < 22000; _now += 200)
            Check(retry.KeepTrying(Missing("overpass")), "retries continue throughout the hold");
        Check(_submissions == 1, "ordinary retries are not corroboration and do not reset the hold");
        Check(!ResyncArbiter.NetMutationFrozen(21999), "object hold lets dependency producers progress");
        retry.Resolve(21999, "late road arrived");
        Check(!retry.HasReport && ResyncArbiter.TakeMatured(22000) == null,
            "late success cancels the scheduled reload");

        _now = 30000;
        Check(retry.KeepTrying(Missing("gas station")), "building integration enters hold");
        Check(ResyncArbiter.TakeMatured(41999) == null, "no early reload");
        var matured = ResyncArbiter.TakeMatured(42000);
        Check(matured != null && matured.Count == 1, "persistent failure still requests repair");
        Check(!retry.IsHeld && !retry.KeepTrying(Missing("gas station")),
            "matured repair does not start an endless new hold");
        retry.Reset();

        _now = 50000;
        retry.KeepTrying(Missing("old building"));
        retry.Resolve(51000, "host removed the building");
        Check(ResyncArbiter.TakeMatured(62000) == null, "superseded buildings cannot trigger stale reloads");

        _now = 70000;
        retry.KeepTrying(Missing("railway", "net"));
        Check(ResyncArbiter.NetMutationFrozen(70001), "net reports still freeze destructive feeders");
        ResyncArbiter.Reset();
        Check(!retry.IsHeld, "snapshot reset invalidates retained reports");
        retry.Reset();
        SyncInbox.Arbitrate = report => ResyncVerdict.AlreadyRecovering;
        Check(!retry.KeepTrying(Missing("old epoch")), "incoming snapshot supersedes retries");
        retry.Reset();
        SyncInbox.Arbitrate = null;
        Check(!retry.KeepTrying(Missing("no arbiter")), "standalone caller queues recovery");
        Check(SyncInbox.TryTakeResyncRequest(out _), "standalone recovery request is preserved");

        foreach (byte op in new[] { GrowableLifecycleCommand.OpSpawn,
                     GrowableLifecycleCommand.OpLevel, GrowableLifecycleCommand.OpState })
        foreach (bool constructing in new[] { false, true })
        foreach (bool connected in new[] { false, true })
        {
            var command = new GrowableLifecycleCommand
            {
                Op = op, PrefabName = "House", RotW = 1,
                Flags = (byte)((constructing ? GrowableLifecycleCommand.FlagUnderConstruction : 0) |
                               (connected ? GrowableLifecycleCommand.FlagRoadConnected : 0)),
                ConstructionProgress = 42, ConstructionSpeed = 7,
            };
            var decoded = GrowableLifecycleCommand.Decode(command.Encode());
            Check(decoded.RoadConnected == connected && decoded.Flags == command.Flags,
                "host road expectation survives every lifecycle codec");
            Check(decoded.ConstructionProgress == 42 && decoded.ConstructionSpeed == 7,
                "road state preserves construction clock");
        }
        var bad = new GrowableLifecycleCommand
        { Op = GrowableLifecycleCommand.OpSpawn, PrefabName = "House", RotW = 1, Flags = 4 };
        bool rejected = false;
        try { GrowableLifecycleCommand.Decode(bad.Encode()); }
        catch (ProtocolException) { rejected = true; }
        Check(rejected, "unknown flags remain rejected");

        var inbox = new GrowableCommandInbox();
        for (uint seq = 1; seq <= 3; seq++)
            Check(inbox.TryEnqueue(new GrowableLifecycleCommand
            {
                Op = GrowableLifecycleCommand.OpState, PrefabName = "House", Sequence = seq,
                Flags = seq == 2 ? GrowableLifecycleCommand.FlagRoadConnected : (byte)0,
            }), "road transition accepted");
        Check(inbox.Count == 3, "road connection transitions cannot be coalesced away");
        for (uint seq = 1; seq <= 3; seq++)
        {
            Check(inbox.TryTake(true, true, out var command) && command.Sequence == seq,
                "road transitions stay ordered");
        }

        Console.WriteLine("PASS: " + _checks + " Case1 resync assertions.");
    }
}

namespace CS2MultiplayerMod.Game.Diagnostics
{
    // The game logger is replaced; arbitration, retry state and codecs above are production code.
    internal static class SyncLog
    {
        public static void Event(LogTopic topic, string message, IList<string> lines = null) { }
    }
}
