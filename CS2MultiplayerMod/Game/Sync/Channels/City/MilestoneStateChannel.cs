using Unity.Entities;
using Game.City;
using CS2MultiplayerMod.Core.Protocol;

using CS2MultiplayerMod.Game.Sync.Infrastructure;
namespace CS2MultiplayerMod.Game.Sync.Channels
{
    /// <summary>Replicates the achieved milestone level (<see cref="Game.City.MilestoneLevel"/>).</summary>
    public sealed class MilestoneStateChannel : IStateChannel
    {
        public const byte Id = 4;
        public byte ChannelId => Id;

        private EntityQuery _query;
        private bool _ready;

        private void Ensure(EntityManager em)
        {
            if (_ready) return;
            _query = em.CreateEntityQuery(ComponentType.ReadWrite<MilestoneLevel>());
            _ready = true;
        }

        public bool Capture(EntityManager em, NetworkWriter writer)
        {
            Ensure(em);
            if (_query.CalculateEntityCount() == 0) return false;
            writer.WriteInt(em.GetComponentData<MilestoneLevel>(_query.GetSingletonEntity()).m_AchievedMilestone);
            return true;
        }

        public void Apply(EntityManager em, NetworkReader reader)
        {
            Ensure(em);
            int level = reader.ReadInt();
            if (_query.CalculateEntityCount() == 0) return;
            Entity e = _query.GetSingletonEntity();
            MilestoneLevel m = em.GetComponentData<MilestoneLevel>(e);
            m.m_AchievedMilestone = level;
            em.SetComponentData(e, m);
        }
    }
}
