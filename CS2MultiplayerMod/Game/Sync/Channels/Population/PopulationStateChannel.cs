using Unity.Entities;
using Game.City;
using CS2MultiplayerMod.Core.Protocol;

using CS2MultiplayerMod.Game.Sync.Infrastructure;
namespace CS2MultiplayerMod.Game.Sync.Channels
{
    /// <summary>
    /// Replicates <see cref="Game.City.Population"/> (population, move-ins, average
    /// happiness and health) - a singleton on the City entity. Population is an output
    /// of the host's simulation, so clients simply mirror it for a consistent HUD.
    /// </summary>
    public sealed class PopulationStateChannel : IStateChannel
    {
        public const byte Id = 2;
        public byte ChannelId => Id;

        private EntityQuery _query;
        private bool _ready;

        private void Ensure(EntityManager em)
        {
            if (_ready) return;
            _query = em.CreateEntityQuery(ComponentType.ReadWrite<Population>());
            _ready = true;
        }

        public bool Capture(EntityManager em, NetworkWriter writer)
        {
            Ensure(em);
            if (_query.CalculateEntityCount() == 0) return false;

            Population pop = em.GetComponentData<Population>(_query.GetSingletonEntity());
            writer.WriteInt(pop.m_Population);
            writer.WriteInt(pop.m_PopulationWithMoveIn);
            writer.WriteInt(pop.m_AverageHappiness);
            writer.WriteInt(pop.m_AverageHealth);
            return true;
        }

        public void Apply(EntityManager em, NetworkReader reader)
        {
            Ensure(em);
            int population = reader.ReadInt();
            int withMoveIn = reader.ReadInt();
            int happiness = reader.ReadInt();
            int health = reader.ReadInt();
            if (_query.CalculateEntityCount() == 0) return;

            Entity entity = _query.GetSingletonEntity();
            Population pop = em.GetComponentData<Population>(entity);
            pop.m_Population = population;
            pop.m_PopulationWithMoveIn = withMoveIn;
            pop.m_AverageHappiness = happiness;
            pop.m_AverageHealth = health;
            em.SetComponentData(entity, pop);
        }
    }
}
