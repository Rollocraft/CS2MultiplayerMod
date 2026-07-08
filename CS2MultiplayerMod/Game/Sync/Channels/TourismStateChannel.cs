using Game.City;
using Unity.Entities;
using Unity.Mathematics;
using CS2MultiplayerMod.Core.Protocol;

using CS2MultiplayerMod.Game.Sync.Infrastructure;
namespace CS2MultiplayerMod.Game.Sync.Channels
{
    /// <summary>
    /// Replicates the tourism aggregates (<see cref="Tourism"/> singleton on the city
    /// entity) host -> clients, so the tourism dashboard matches. Like population, this is
    /// a sim *output* - the host's numbers are authoritative, clients display them.
    /// </summary>
    public sealed class TourismStateChannel : IStateChannel
    {
        public const byte Id = 9;
        public byte ChannelId => Id;

        private EntityQuery _query;
        private bool _ready;

        private void Ensure(EntityManager em)
        {
            if (_ready) return;
            _query = em.CreateEntityQuery(ComponentType.ReadWrite<Tourism>());
            _ready = true;
        }

        public bool Capture(EntityManager em, NetworkWriter writer)
        {
            Ensure(em);
            if (_query.CalculateEntityCount() == 0) return false;
            Tourism tourism = em.GetComponentData<Tourism>(_query.GetSingletonEntity());
            writer.WriteInt(tourism.m_AverageTourists);
            writer.WriteInt(tourism.m_CurrentTourists);
            writer.WriteInt(tourism.m_Lodging.x);
            writer.WriteInt(tourism.m_Lodging.y);
            return true;
        }

        public void Apply(EntityManager em, NetworkReader reader)
        {
            Ensure(em);
            int average = reader.ReadInt();
            int current = reader.ReadInt();
            var lodging = new int2(reader.ReadInt(), reader.ReadInt());
            if (_query.CalculateEntityCount() == 0) return;

            Entity e = _query.GetSingletonEntity();
            Tourism tourism = em.GetComponentData<Tourism>(e);
            tourism.m_AverageTourists = average;
            tourism.m_CurrentTourists = current;
            tourism.m_Lodging = lodging;
            em.SetComponentData(e, tourism);
        }
    }
}
