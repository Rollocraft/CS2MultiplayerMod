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
    /// Bounded rolling pages of the host's residential occupancy. Deliberately an ordinary
    /// coalescible state channel: every page is an absolute statement about the properties it
    /// names, so a dropped page is repaired by the next sweep and malformed data is dropped
    /// locally rather than escalated into a full-world resync.
    /// </summary>
    internal sealed class ResidentialOccupancyChannel : IStateChannel, IPumpedStateChannel
    {
        public const byte Id = 21;
        private readonly ResidentialOccupancySyncSystem _runtime;
        private bool _captureWarned;

        public ResidentialOccupancyChannel(ResidentialOccupancySyncSystem runtime)
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
                // closed so one broken local household cannot suppress money, clock, demand and
                // every other state channel on all following ticks.
                if (!_captureWarned)
                {
                    _captureWarned = true;
                    SyncLog.Warn(LogTopic.Residential,
                        "Occupancy: host capture failed; page skipped " +
                        "(logged once until world reset): " + ex.Message);
                }
                return false;
            }
        }

        public void Apply(EntityManager entityManager, NetworkReader reader)
        {
            _runtime.Enqueue(ResidentialOccupancySnapshot.Read(reader));
        }

        // Decoding and resolving a page is read-only work and cheap to spread over frames; the
        // structural writes stay in the runtime's own GameSimulation update. Authority is kept
        // engaged from here too, because this pump also runs while the game is paused.
        public void Pump(EntityManager entityManager)
        {
            if (_runtime == null) return;
            _runtime.MaintainAuthority();
            _runtime.PumpIncoming();
        }

        public void ResetPending()
        {
            _captureWarned = false;
            if (_runtime != null) _runtime.ResetPending();
        }
    }
}
