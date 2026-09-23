using CS2MultiplayerMod.Core.Diagnostics;
using CS2MultiplayerMod.Core.Protocol;
using CS2MultiplayerMod.Core.Protocol.Messages;
using CS2MultiplayerMod.Game.Diagnostics;
using CS2MultiplayerMod.Game.Sync.Infrastructure;

namespace CS2MultiplayerMod.Game.Sync.Systems
{
    public partial class CityStateSyncSystem
    {
        /// <summary>Apply edits clients submitted; the snapshot that follows confirms them.</summary>
        private void ApplyIncomingEdits()
        {
            bool any = false;
            while (_incomingEdits.TryDequeue(out StateEditMessage edit))
            {
                if (!_channels.TryGetValue(edit.ChannelId, out IStateChannel channel) ||
                    !_editable.Contains(edit.ChannelId))
                {
                    SyncLog.Warn(LogTopic.City, "CityState: ignoring edit on non-editable channel " +
                        edit.ChannelId + " from player " + edit.OriginPlayerId + ".");
                    continue;
                }

                try
                {
                    channel.Apply(EntityManager, new NetworkReader(edit.Data));
                    any = true;
                    SyncLog.Detail(LogTopic.City, "CityState: player " + edit.OriginPlayerId +
                        " edited channel " + edit.ChannelId + "; applied and broadcasting.");
                }
                catch (System.Exception ex)
                {
                    SyncLog.Warn(LogTopic.City, "CityState: dropping bad edit on channel " +
                        edit.ChannelId + ": " + ex.Message);
                }
            }

            // Confirm edits to everyone right away instead of waiting out the interval.
            if (any) _lastSnapshotMs = 0;
        }

        private void ApplyIncoming()
        {
            // Only the newest snapshot per channel matters; applying every queued one turned a hitch into a stall.
            _newestSnapshot.Clear();
            _newestOrder.Clear();
            int queued = 0;
            int orderedAttempts = 0;
            int orderedDequeuedFromIncoming = 0;
            while (!_orderedInvalidated && orderedAttempts < OrderedApplyPerFrame &&
                   _orderedDeferred.TryDequeue(out StateSnapshotMessage deferred))
                ApplyOrdered(deferred, ref orderedAttempts);
            while (_incoming.TryDequeue(out StateSnapshotMessage snapshot))
            {
                queued++;
                if (!_channels.ContainsKey(snapshot.ChannelId)) continue;

                if (_ordered.Contains(snapshot.ChannelId))
                {
                    orderedDequeuedFromIncoming++;
                    if (_orderedInvalidated) continue;
                    if (orderedAttempts < OrderedApplyPerFrame && _orderedDeferred.Count == 0)
                    {
                        ApplyOrdered(snapshot, ref orderedAttempts);
                        continue;
                    }
                    if (_orderedDeferred.Count >= OrderedDeferredCap)
                    {
                        SyncLog.Warn(LogTopic.City,
                            "CityState: ordered-state deferred queue overflowed; " +
                            "requesting a fresh world sync.");
                        PoisonOrderedStream("ordered state deferred overflow");
                        continue;
                    }
                    _orderedDeferred.Enqueue(snapshot);
                    continue;
                }

                // Every editable snapshot is inspected: one may be the echo that retires our edit.
                if (_editable.Contains(snapshot.ChannelId) && !ShouldApplyEditable(snapshot)) continue;

                if (!_newestSnapshot.ContainsKey(snapshot.ChannelId))
                    _newestOrder.Add(snapshot.ChannelId);
                _newestSnapshot[snapshot.ChannelId] = snapshot;
            }

            for (int i = 0; i < _newestOrder.Count; i++)
            {
                StateSnapshotMessage newest = _newestSnapshot[_newestOrder[i]];
                try
                {
                    _channels[newest.ChannelId].Apply(EntityManager, new NetworkReader(newest.Data));
                    _applied++;
                }
                catch (System.Exception ex)
                {
                    SyncLog.Warn(LogTopic.City, "CityState: dropping bad state on channel " +
                        newest.ChannelId + ": " + ex.Message);
                }
            }

            _superseded += queued - _newestOrder.Count - orderedDequeuedFromIncoming;

            long now = _clock.ElapsedMilliseconds;
            if (_applied > 0 && now - _lastLogMs >= 30000)
            {
                _lastLogMs = now;
                SyncLog.Detail(LogTopic.City, "CityState: applied " + _applied +
                    " state snapshot(s) from host in last 30s" +
                    (_superseded > 0 ? ", " + _superseded + " superseded before apply." : "."));
                _applied = 0;
                _superseded = 0;
            }
        }

        private void ApplyOrdered(StateSnapshotMessage snapshot, ref int orderedAttempts)
        {
            orderedAttempts++;
            try
            {
                _channels[snapshot.ChannelId].Apply(
                    EntityManager, new NetworkReader(snapshot.Data));
                _applied++;
            }
            catch (System.Exception ex)
            {
                SyncLog.Warn(LogTopic.City, "CityState: dropping bad ordered state on channel " +
                    snapshot.ChannelId + ": " + ex.Message);
                PoisonOrderedStream("malformed ordered household state");
            }
        }

        /// <summary>Advance the channels that spread one snapshot over several frames.</summary>
        private readonly PropertySpatialPass _propertySpatialPass = new PropertySpatialPass();

        private void PumpChannels()
        {
            using (_propertySpatialPass.Begin(EntityManager))
            {
                for (int i = 0; i < _pumped.Count; i++)
                {
                    // Only read-only property channels share the pass; others drop the cached snapshots.
                    bool propertyChannel = _pumped[i] is IPropertyStateChannel;
                    if (!propertyChannel) _propertySpatialPass.Invalidate();
                    try { _pumped[i].Pump(EntityManager); }
                    catch (System.Exception ex)
                    {
                        SyncLog.Warn(LogTopic.City,
                            "CityState: channel pump failed: " + ex.Message);
                    }
                    finally { if (!propertyChannel) _propertySpatialPass.Invalidate(); }
                }
            }
        }

        /// <summary>
        /// A matching snapshot confirms an in-flight edit; a different one waits until the edit window
        /// expires, then wins.
        /// </summary>
        private bool ShouldApplyEditable(StateSnapshotMessage snapshot)
        {
            if (_pendingEdits.TryGetValue(snapshot.ChannelId, out PendingEdit pending))
            {
                if (BytesEqual(snapshot.Data, pending.Payload))
                {
                    // Host confirmed our edit; our world already looks like this.
                    _pendingEdits.Remove(snapshot.ChannelId);
                    _lastHostPayload[snapshot.ChannelId] = snapshot.Data;
                    return false;
                }

                if (_clock.ElapsedMilliseconds - pending.SentMs < EditPendingTimeoutMs)
                    return false; // stale snapshot racing our edit — hold

                _pendingEdits.Remove(snapshot.ChannelId); // edit lost (host took another writer) — accept
            }

            _lastHostPayload[snapshot.ChannelId] = snapshot.Data;
            return true;
        }

        private static bool BytesEqual(byte[] a, byte[] b)
        {
            if (ReferenceEquals(a, b)) return true;
            if (a == null || b == null || a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
                if (a[i] != b[i]) return false;
            return true;
        }
    }
}
