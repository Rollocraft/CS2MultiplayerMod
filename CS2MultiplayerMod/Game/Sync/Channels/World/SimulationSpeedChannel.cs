using Game.Simulation;
using Unity.Entities;
using CS2MultiplayerMod.Core.Protocol;

using CS2MultiplayerMod.Game.Sync.Infrastructure;
namespace CS2MultiplayerMod.Game.Sync.Channels
{
    /// <summary>
    /// Replicates the simulation speed (<see cref="SimulationSystem.selectedSpeed"/>,
    /// where 0 = paused). Without this the two simulations free-run independently: one
    /// player pausing to plan leaves the other's city racing ahead until the next world
    /// resync. Player-editable - any player may pause or change speed and everyone
    /// follows; the host arbitrates concurrent changes.
    /// </summary>
    public sealed class SimulationSpeedStateChannel : IStateChannel
    {
        public const byte Id = 11;
        public byte ChannelId => Id;

        private SimulationSystem _simulation;

        private SimulationSystem Resolve(EntityManager em) =>
            _simulation ?? (_simulation = em.World.GetOrCreateSystemManaged<SimulationSystem>());

        public bool Capture(EntityManager em, NetworkWriter writer)
        {
            writer.WriteFloat(Resolve(em).selectedSpeed);
            return true;
        }

        public void Apply(EntityManager em, NetworkReader reader)
        {
            float speed = reader.ReadFloat();
            SimulationSystem simulation = Resolve(em);
            if (!simulation.selectedSpeed.Equals(speed)) simulation.selectedSpeed = speed;
        }
    }
}
