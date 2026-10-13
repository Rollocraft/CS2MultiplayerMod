using Game.Simulation;
using Unity.Entities;
using CS2MultiplayerMod.Core.Protocol;

using CS2MultiplayerMod.Game.Sync.Infrastructure;
namespace CS2MultiplayerMod.Game.Sync.Channels
{
    /// <summary>
    /// Simulation speed (0 = paused), so one player pausing pauses everyone. Editable; the host
    /// arbitrates.
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
