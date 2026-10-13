using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using CS2MultiplayerMod.Core.Networking;
using CS2MultiplayerMod.Core.Protocol;
using CS2MultiplayerMod.Core.Protocol.Messages;
using CS2MultiplayerMod.Core.Session;
using CS2MultiplayerMod.Game.Diagnostics;
using CS2MultiplayerMod.Game.Sync.Channels;

namespace CS2MultiplayerMod.Game.Sync.Systems
{
    public partial class CityStateSyncSystem
    {
        // Only absolute, synchronous, host-owned state. Structural, editable and paged state needs a world cut.
        private static readonly byte[] RepairChannels = {
            MoneyStateChannel.Id, XpStateChannel.Id, DevTreePointsStateChannel.Id,
            TourismStateChannel.Id, WeatherStateChannel.Id,
        };
        private sealed class RepairRequest
        {
            public ConnectionId Connection;
            public long Id;
            public StateDigest[] Digests;
        }
        private readonly ConcurrentQueue<RepairRequest> _repairRequests = new ConcurrentQueue<RepairRequest>();
        private readonly ConcurrentQueue<StateRepairMessage> _repairReplies = new ConcurrentQueue<StateRepairMessage>();
        private RepairObserver _repairObserver;

        public void RequestSmartSync()
        {
            var service = Mod.Service;
            if (service == null || !service.GameplaySyncReady || !service.Session.CanRequestStateRepair) return;
            try
            {
                var digests = new List<StateDigest>();
                foreach (byte id in RepairChannels)
                    digests.Add(new StateDigest { ChannelId = id, Hash = StateDigest.Compute(CaptureRepairState(id)) });
                service.Session.RequestStateRepair(digests.ToArray());
            }
            catch (Exception ex)
            {
                SyncLog.Warn(Core.Diagnostics.LogTopic.Resync, "Cannot capture state for smart sync: " + ex.Message);
                service.RequestWorldSync();
            }
        }

        private byte[] CaptureRepairState(byte id)
        {
            if (Array.IndexOf(RepairChannels, id) < 0) throw new ProtocolException("Unsupported repair channel.");
            var writer = new NetworkWriter(64);
            if (!_channels[id].Capture(EntityManager, writer)) throw new InvalidOperationException("State is unavailable.");
            byte[] data = writer.ToArray();
            if (data.Length == 0 || data.Length > StateRepairMessage.MaxChannelBytes)
                throw new ProtocolException("Repair state is too large.");
            return data;
        }

        private void PumpRepairs(MultiplayerSession session)
        {
            while (_repairRequests.TryDequeue(out var request))
            {
                if (session.Role != SessionRole.Host) continue;
                try
                {
                    var changed = new List<StateSnapshotMessage>();
                    foreach (var digest in request.Digests)
                    {
                        byte[] data = CaptureRepairState(digest.ChannelId);
                        if (!StateDigest.Equal(digest.Hash, StateDigest.Compute(data)))
                            changed.Add(new StateSnapshotMessage(digest.ChannelId, data));
                    }
                    session.SendStateRepair(request.Connection, request.Id, changed.ToArray());
                }
                catch (Exception ex)
                {
                    SyncLog.Warn(Core.Diagnostics.LogTopic.Resync, "State refresh needs a world snapshot: " + ex.Message);
                    session.FallBackStateRepair(request.Connection, request.Id);
                }
            }
            while (_repairReplies.TryDequeue(out var reply))
            {
                if (session.Role != SessionRole.Client || !session.IsStateRepairCurrent(reply.RequestId)) continue;
                bool applied = false;
                try
                {
                    foreach (var snapshot in reply.Snapshots)
                    {
                        if (Array.IndexOf(RepairChannels, snapshot.ChannelId) < 0)
                            throw new ProtocolException("Unexpected repair channel.");
                        var reader = new NetworkReader(snapshot.Data);
                        _channels[snapshot.ChannelId].Apply(EntityManager, reader);
                        if (reader.Remaining != 0 || !StateDigest.Equal(snapshot.Data, CaptureRepairState(snapshot.ChannelId)))
                            throw new InvalidOperationException("State repair did not settle.");
                    }
                    applied = true;
                }
                catch (Exception ex)
                {
                    SyncLog.Warn(Core.Diagnostics.LogTopic.Resync, "State refresh could not be verified: " + ex.Message);
                }
                session.CompleteStateRepair(reply.RequestId, applied);
            }
        }

        private void ClearRepairQueues()
        {
            while (_repairRequests.TryDequeue(out _)) { }
            while (_repairReplies.TryDequeue(out _)) { }
        }

        private sealed class RepairObserver : SessionObserver
        {
            private readonly CityStateSyncSystem _owner;
            public RepairObserver(CityStateSyncSystem owner) { _owner = owner; }
            public override void OnStateResyncRequested(ConnectionId connection, long requestId, StateDigest[] digests)
            {
                if (_owner._repairRequests.Count < 32)
                    _owner._repairRequests.Enqueue(new RepairRequest { Connection = connection, Id = requestId, Digests = digests });
            }
            public override void OnStateRepairReceived(StateRepairMessage message)
            {
                if (_owner._repairReplies.Count < 2) _owner._repairReplies.Enqueue(message);
            }
        }
    }
}
