using CS2MultiplayerMod.Core.Protocol.Messages;
using CS2MultiplayerMod.Core.Diagnostics;
using CS2MultiplayerMod.Core.Session;
using CS2MultiplayerMod.Game.Diagnostics;
using CS2MultiplayerMod.Game.Sync.Commands;
using CS2MultiplayerMod.Game.Sync.Infrastructure;

namespace CS2MultiplayerMod.Game.Sync.Systems
{
    public partial class GrowableSyncSystem
    {
        private sealed class GrowableObserver : SessionObserver
        {
            private readonly GrowableCommandInbox _sink;
            private readonly MultiplayerSession _session;
            private readonly object _authorityGate = new object();
            private long _foreignCommands;
            private int _lastForeignWarningTick;

            public GrowableObserver(GrowableCommandInbox sink, MultiplayerSession session)
            {
                _sink = sink;
                _session = session;
            }

            public override void OnCommandReceived(SimulationCommandMessage message)
            {
                if (message.CommandId != GrowableLifecycleCommand.Id ||
                    message.OriginPlayerId == _session.LocalPlayerId) return;
                if (_session.Role == SessionRole.Host)
                {
                    // Count foreign authors without queuing them or warning per packet.
                    long foreignCount;
                    int now = System.Environment.TickCount;
                    lock (_authorityGate)
                    {
                        foreignCount = ++_foreignCommands;
                        if (foreignCount != 1 && unchecked(now - _lastForeignWarningTick) < 5000) return;
                        _lastForeignWarningTick = now;
                    }
                    SyncLog.Warn(LogTopic.Buildings, "GrowableSync: host discarded foreign zoned-building " +
                        "commands (total=" + foreignCount + ", latest player=" + message.OriginPlayerId +
                        "); only the host may author them.");
                    return;
                }
                GrowableLifecycleCommand command;
                try { command = GrowableLifecycleCommand.Decode(message.Body); }
                catch (System.Exception ex)
                {
                    SyncInbox.RequestResync(ResyncReport.Create("malformed growable command",
                        "growable", ResyncEvidence.StreamLoss).About("growable inbox")
                        .Fact("decoder said", ex.Message));
                    return;
                }
                if (_sink.TryEnqueue(command, out uint? resetFrom)) return;
                if (resetFrom.HasValue)
                {
                    SyncLog.Warn(LogTopic.Buildings, "GrowableSync: sequence moved backwards from " +
                        resetFrom.Value + " to " + command.Sequence +
                        "; cleared the old inbox and sequence gate; requesting world repair.");
                    SyncInbox.RequestResync(ResyncReport.Create("growable sequence moved backwards",
                        "growable", ResyncEvidence.StreamLoss).About("growable sequence gate")
                        .Tried("cleared the old queued suffix and reset the sequence baseline")
                        .Fact("previous sequence", resetFrom.Value)
                        .Fact("received sequence", command.Sequence));
                    return;
                }
                int count = _sink.Count;
                _sink.Clear();
                SyncInbox.RequestResync(ResyncReport.Create("growable inbox overflow",
                    "growable", ResyncEvidence.StreamLoss).About("growable ordered inbox")
                    .Tried("coalesced superseded condition/progress samples at ingress")
                    .Fact("queued entries", count).Fact("incoming operation", command.Op)
                    .Fact("state cap", GrowableCommandInbox.StateCapacity)
                    .Fact("lifecycle cap", GrowableCommandInbox.LifecycleCapacity));
            }
        }
    }
}
