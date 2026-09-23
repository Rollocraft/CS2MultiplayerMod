using Unity.Entities;
using CS2MultiplayerMod.Core.Protocol;

namespace CS2MultiplayerMod.Game.Sync.Infrastructure
{
    /// <summary>
    /// One slice of replicated city state: the host captures a payload, clients apply it. Routed by
    /// <see cref="ChannelId"/>; runs on the simulation thread.
    /// </summary>
    public interface IStateChannel
    {
        byte ChannelId { get; }

        /// <summary>Host: write the current state. Return false to skip sending this tick.</summary>
        bool Capture(EntityManager entityManager, NetworkWriter writer);

        /// <summary>Client: apply a received snapshot to the world.</summary>
        void Apply(EntityManager entityManager, NetworkReader reader);
    }

    /// <summary>Apply stores the payload; <see cref="Pump"/> runs every frame until it is consumed.</summary>
    public interface IPumpedStateChannel
    {
        void Pump(EntityManager entityManager);

        /// <summary>Drop the standing payload: it describes a world that is no longer loaded.</summary>
        void ResetPending();
    }

    /// <summary>Deltas that must arrive in order; ordinary channels are absolute and may coalesce.</summary>
    public interface IOrderedStateChannel
    {
    }
}
