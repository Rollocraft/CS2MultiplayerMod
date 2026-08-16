using Unity.Entities;
using Game.City;
using CS2MultiplayerMod.Core.Protocol;

using CS2MultiplayerMod.Game.Sync.Infrastructure;
namespace CS2MultiplayerMod.Game.Sync.Channels
{
    /// <summary>
    /// Replicates the city treasury (<see cref="Game.City.PlayerMoney"/>), a singleton
    /// component on the City entity. The host's value is authoritative; clients snap to
    /// it each snapshot so both players see the same budget while building together.
    /// </summary>
    public sealed class MoneyStateChannel : IStateChannel
    {
        public const byte Id = 1;
        public byte ChannelId => Id;

        private EntityQuery _query;
        private bool _ready;

        private void Ensure(EntityManager em)
        {
            if (_ready) return;
            _query = em.CreateEntityQuery(ComponentType.ReadWrite<PlayerMoney>());
            _ready = true;
        }

        public bool Capture(EntityManager em, NetworkWriter writer)
        {
            Ensure(em);
            if (_query.CalculateEntityCount() == 0) return false;

            PlayerMoney money = em.GetComponentData<PlayerMoney>(_query.GetSingletonEntity());
            writer.WriteInt(money.money); // m_Money is private; 'money' is its public getter
            writer.WriteBool(money.m_Unlimited);
            return true;
        }

        public void Apply(EntityManager em, NetworkReader reader)
        {
            Ensure(em);
            int amount = reader.ReadInt();
            bool unlimited = reader.ReadBool();
            if (_query.CalculateEntityCount() == 0) return;

            Entity entity = _query.GetSingletonEntity();
            PlayerMoney money = em.GetComponentData<PlayerMoney>(entity);

            // m_Money is private; reach the target value through the public Add/Subtract API.
            int delta = amount - money.money;
            if (delta > 0) money.Add(delta);
            else if (delta < 0) money.Subtract(-delta);

            money.m_Unlimited = unlimited;
            em.SetComponentData(entity, money);
        }
    }
}
