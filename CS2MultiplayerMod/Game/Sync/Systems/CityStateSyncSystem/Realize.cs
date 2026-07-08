using CS2MultiplayerMod.Core.Protocol;
using CS2MultiplayerMod.Core.Protocol.Messages;
using CS2MultiplayerMod.Game.Sync.Infrastructure;

namespace CS2MultiplayerMod.Game.Sync.Systems
{
    public partial class CityStateSyncSystem
    {
        /// <summary>Apply edits clients submitted; the snapshot that follows confirms them.</summary>
        private void ApplyIncomingEdits()
        {
            bool any = false;
            StateEditMessage edit;
            while (_incomingEdits.TryDequeue(out edit))
            {
                IStateChannel channel;
                if (!_channels.TryGetValue(edit.ChannelId, out channel) || !_editable.Contains(edit.ChannelId))
                {
                    Mod.log.Warn("[MP] CityState: ignoring edit on non-editable channel " + edit.ChannelId +
                                 " from player " + edit.OriginPlayerId + ".");
                    continue;
                }

                try
                {
                    channel.Apply(EntityManager, new NetworkReader(edit.Data));
                    any = true;
                    Mod.Verbose("[MP] CityState: player " + edit.OriginPlayerId + " edited channel " +
                                 edit.ChannelId + "; applied and broadcasting.");
                }
                catch (System.Exception ex)
                {
                    // Wire data must never take the host down — malformed or hostile
                    // edits are dropped, not crashed on.
                    Mod.log.Warn("[MP] CityState: dropping bad edit on channel " + edit.ChannelId + ": " + ex.Message);
                }
            }

            // Confirm edits to everyone right away instead of waiting out the interval.
            if (any) _lastSnapshotMs = 0;
        }

        private void ApplyIncoming()
        {
            StateSnapshotMessage snapshot;
            while (_incoming.TryDequeue(out snapshot))
            {
                IStateChannel channel;
                if (!_channels.TryGetValue(snapshot.ChannelId, out channel)) continue;

                if (_editable.Contains(snapshot.ChannelId) && !ShouldApplyEditable(snapshot)) continue;

                try
                {
                    channel.Apply(EntityManager, new NetworkReader(snapshot.Data));
                    _applied++;
                }
                catch (System.Exception ex)
                {
                    Mod.log.Warn("[MP] CityState: dropping bad state on channel " + snapshot.ChannelId + ": " + ex.Message);
                }
            }

            long now = _clock.ElapsedMilliseconds;
            if (_applied > 0 && now - _lastLogMs >= 30000)
            {
                _lastLogMs = now;
                Mod.Verbose("[MP] CityState: applied " + _applied + " state snapshot(s) from host in last 30s.");
                _applied = 0;
            }
        }

        /// <summary>
        /// Editable channels honor in-flight edits: matching snapshot confirms it;
        /// different snapshot held off until edit window expires, then wins.
        /// </summary>
        private bool ShouldApplyEditable(StateSnapshotMessage snapshot)
        {
            PendingEdit pending;
            if (_pendingEdits.TryGetValue(snapshot.ChannelId, out pending))
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
