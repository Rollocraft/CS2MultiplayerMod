using Unity.Entities;
using CS2MultiplayerMod.Core.Protocol;

namespace CS2MultiplayerMod.Game.Sync.Infrastructure
{
    /// <summary>
    /// One slice of replicated city state (money, population, ...). The host
    /// <see cref="Capture"/>s the current value into a payload; clients
    /// <see cref="Apply"/> a received payload back onto the world. Each channel owns a
    /// stable <see cref="ChannelId"/> used to route snapshots, so new state can be
    /// synced by adding a channel and registering it - nothing else changes.
    /// Both methods run on the simulation thread with a valid <see cref="EntityManager"/>.
    /// </summary>
    public interface IStateChannel
    {
        byte ChannelId { get; }

        /// <summary>Host: write the current state. Return false to skip sending this tick.</summary>
        bool Capture(EntityManager entityManager, NetworkWriter writer);

        /// <summary>Client: apply a received snapshot to the world.</summary>
        void Apply(EntityManager entityManager, NetworkReader reader);
    }

    /// <summary>
    /// A channel whose snapshot costs too much to resolve in the frame it lands in.
    /// <see cref="IStateChannel.Apply"/> takes the payload and returns; the client then calls
    /// <see cref="Pump"/> every frame until that payload is consumed.
    /// </summary>
    public interface IPumpedStateChannel
    {
        void Pump(EntityManager entityManager);

        /// <summary>Drop the standing payload: it describes a world that is no longer loaded.</summary>
        void ResetPending();
    }

    /// <summary>
    /// Marks an event-shaped state channel whose snapshots must be delivered in transport order.
    /// Ordinary channels are absolute values and may be coalesced to the newest payload; ordered
    /// channels carry bounded deltas, so dropping an older payload would drop simulation state.
    /// </summary>
    public interface IOrderedStateChannel
    {
    }
}
