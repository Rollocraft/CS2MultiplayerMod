using Game.City;
using Unity.Entities;
using CS2MultiplayerMod.Core.Protocol;

using CS2MultiplayerMod.Game.Sync.Infrastructure;
namespace CS2MultiplayerMod.Game.Sync.Channels
{
    /// <summary>
    /// Replicates the service-fee sliders (electricity/water price etc.): the
    /// <see cref="ServiceFee"/> buffer on the city entity, keyed by
    /// <see cref="PlayerResource"/> which is a stable enum (same on every machine).
    /// Player-editable - every player may move the sliders; the host arbitrates.
    /// </summary>
    public sealed class ServiceFeeStateChannel : IStateChannel
    {
        public const byte Id = 8;
        public byte ChannelId => Id;

        private EntityQuery _cityQuery;
        private bool _ready;

        private void Ensure(EntityManager em)
        {
            if (_ready) return;
            _cityQuery = em.CreateEntityQuery(ComponentType.ReadWrite<PlayerMoney>());
            _ready = true;
        }

        public bool Capture(EntityManager em, NetworkWriter writer)
        {
            Ensure(em);
            if (_cityQuery.CalculateEntityCount() == 0) return false;
            Entity city = _cityQuery.GetSingletonEntity();
            if (!em.HasBuffer<ServiceFee>(city)) return false;

            DynamicBuffer<ServiceFee> fees = em.GetBuffer<ServiceFee>(city, true);
            writer.WriteByte((byte)fees.Length);
            for (int i = 0; i < fees.Length; i++)
            {
                writer.WriteByte((byte)fees[i].m_Resource);
                writer.WriteFloat(fees[i].m_Fee);
            }
            return true;
        }

        public void Apply(EntityManager em, NetworkReader reader)
        {
            Ensure(em);
            int count = reader.ReadByte();
            var wanted = new (byte resource, float fee)[count];
            for (int i = 0; i < count; i++)
                wanted[i] = (reader.ReadByte(), reader.ReadFloat());

            if (_cityQuery.CalculateEntityCount() == 0) return;
            Entity city = _cityQuery.GetSingletonEntity();
            if (!em.HasBuffer<ServiceFee>(city)) return;

            DynamicBuffer<ServiceFee> fees = em.GetBuffer<ServiceFee>(city);
            for (int i = 0; i < count; i++)
            {
                for (int f = 0; f < fees.Length; f++)
                {
                    if ((byte)fees[f].m_Resource != wanted[i].resource) continue;
                    if (!fees[f].m_Fee.Equals(wanted[i].fee))
                    {
                        ServiceFee fee = fees[f];
                        fee.m_Fee = wanted[i].fee;
                        fees[f] = fee;
                    }
                    break;
                }
            }
        }
    }
}
