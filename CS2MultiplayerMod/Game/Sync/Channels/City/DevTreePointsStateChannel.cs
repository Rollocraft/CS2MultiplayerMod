using Unity.Entities;
using Game.City;
using CS2MultiplayerMod.Core.Protocol;

using CS2MultiplayerMod.Game.Sync.Infrastructure;
namespace CS2MultiplayerMod.Game.Sync.Channels
{
    /// <summary>Replicates development-tree points (<see cref="Game.City.DevTreePoints"/>) - the "tuning" points.</summary>
    public sealed class DevTreePointsStateChannel : IStateChannel
    {
        public const byte Id = 5;
        public byte ChannelId => Id;

        private EntityQuery _query;
        private bool _ready;

        private void Ensure(EntityManager em)
        {
            if (_ready) return;
            _query = em.CreateEntityQuery(ComponentType.ReadWrite<DevTreePoints>());
            _ready = true;
        }

        public bool Capture(EntityManager em, NetworkWriter writer)
        {
            Ensure(em);
            if (_query.CalculateEntityCount() == 0) return false;
            writer.WriteInt(em.GetComponentData<DevTreePoints>(_query.GetSingletonEntity()).m_Points);
            return true;
        }

        public void Apply(EntityManager em, NetworkReader reader)
        {
            Ensure(em);
            int points = reader.ReadInt();
            if (_query.CalculateEntityCount() == 0) return;
            Entity e = _query.GetSingletonEntity();
            DevTreePoints d = em.GetComponentData<DevTreePoints>(e);
            d.m_Points = points;
            em.SetComponentData(e, d);
        }
    }
}
