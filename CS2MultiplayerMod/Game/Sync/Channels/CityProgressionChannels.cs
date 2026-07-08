using Unity.Entities;
using Game.City;
using CS2MultiplayerMod.Core.Protocol;

using CS2MultiplayerMod.Game.Sync.Infrastructure;
namespace CS2MultiplayerMod.Game.Sync.Channels
{
    /// <summary>
    /// Replicates city XP (<see cref="Game.City.XP"/>) - progression toward the next
    /// milestone, with its recorded peak population/income.
    /// </summary>
    public sealed class XpStateChannel : IStateChannel
    {
        public const byte Id = 3;
        public byte ChannelId => Id;

        private EntityQuery _query;
        private bool _ready;

        private void Ensure(EntityManager em)
        {
            if (_ready) return;
            _query = em.CreateEntityQuery(ComponentType.ReadWrite<XP>());
            _ready = true;
        }

        public bool Capture(EntityManager em, NetworkWriter writer)
        {
            Ensure(em);
            if (_query.CalculateEntityCount() == 0) return false;
            XP xp = em.GetComponentData<XP>(_query.GetSingletonEntity());
            writer.WriteInt(xp.m_XP);
            writer.WriteInt(xp.m_MaximumPopulation);
            writer.WriteInt(xp.m_MaximumIncome);
            return true;
        }

        public void Apply(EntityManager em, NetworkReader reader)
        {
            Ensure(em);
            int xpValue = reader.ReadInt();
            int maxPop = reader.ReadInt();
            int maxIncome = reader.ReadInt();
            if (_query.CalculateEntityCount() == 0) return;

            Entity e = _query.GetSingletonEntity();
            XP xp = em.GetComponentData<XP>(e);
            xp.m_XP = xpValue;
            xp.m_MaximumPopulation = maxPop;
            xp.m_MaximumIncome = maxIncome;
            em.SetComponentData(e, xp);
        }
    }

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
