using CS2MultiplayerMod.Core.Diagnostics;
using CS2MultiplayerMod.Core.Protocol;
using CS2MultiplayerMod.Game.Diagnostics;
using CS2MultiplayerMod.Game.Sync.Commands;
using CS2MultiplayerMod.Game.Sync.Infrastructure;
using CS2MultiplayerMod.Game.Sync.Systems;
using Unity.Entities;

namespace CS2MultiplayerMod.Game.Sync.Channels
{
    /// <summary>
    /// Bounded rolling rent pages from host to clients. This remains an ordinary coalescible state
    /// channel: every entry is absolute and a missed page is repaired by a later sweep, so malformed
    /// data or backpressure is dropped locally and never requests a full-world resync.
    /// </summary>
    internal sealed class PropertyRentStateChannel : IStateChannel, IPumpedStateChannel
    {
        public const byte Id = 20;
        private readonly PropertyRentSyncSystem _runtime;
        private bool _captureWarned;

        public PropertyRentStateChannel(PropertyRentSyncSystem runtime)
        {
            _runtime = runtime;
        }

        public byte ChannelId => Id;

        public bool Capture(EntityManager entityManager, NetworkWriter writer)
        {
            try
            {
                return _runtime != null && _runtime.Capture(writer);
            }
            catch (System.Exception ex)
            {
                // CityState capture has no per-channel exception boundary. Fail this optional page
                // closed so a bad local property cannot suppress money, clock, demand, and every
                // other state channel on all following ticks.
                if (!_captureWarned)
                {
                    _captureWarned = true;
                    SyncLog.Warn(LogTopic.Residential,
                        "PropertyRent: host capture failed; rent page skipped " +
                        "(logged once until world reset): " + ex.Message);
                }
                return false;
            }
        }

        public void Apply(EntityManager entityManager, NetworkReader reader)
        {
            _runtime.Enqueue(PropertyRentSnapshot.Read(reader));
        }

        // Application must happen in GameSimulation between RentAdjustSystem and
        // PropertyRenterSystem, not from CityStateSyncSystem's UIUpdate pump.
        public void Pump(EntityManager entityManager)
        {
            if (_runtime != null) _runtime.PumpIncoming();
        }

        public void ResetPending()
        {
            _captureWarned = false;
            if (_runtime != null) _runtime.DrainForWorldChange();
        }
    }
}
