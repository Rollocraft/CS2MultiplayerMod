using Game.Simulation;
using Unity.Collections;
using Unity.Entities;
using CS2MultiplayerMod.Core.Protocol;
using CS2MultiplayerMod.Game.Sync.Infrastructure;

namespace CS2MultiplayerMod.Game.Sync.Channels
{
    /// <summary>
    /// The complete 92-entry native tax-rate table (area offsets plus education and resource rates).
    /// Editable: a client proposes a whole bounded table, the host applies it atomically.
    /// </summary>
    public sealed class TaxStateChannel : IStateChannel
    {
        public const byte Id = 6;
        public byte ChannelId => Id;

        private const int MaxTaxRateEntries = 128;
        private const int MaxRawTaxOffset = 100;

        private TaxSystem _taxSystem;

        private TaxSystem Resolve(EntityManager em) =>
            _taxSystem ?? (_taxSystem = em.World.GetOrCreateSystemManaged<TaxSystem>());

        public bool Capture(EntityManager em, NetworkWriter writer)
        {
            TaxSystem tax = Resolve(em);
            tax.Readers.Complete();
            NativeArray<int> rates = tax.GetTaxRates();
            if (!rates.IsCreated || rates.Length <= 0 || rates.Length > MaxTaxRateEntries)
                return false;

            writer.WriteShort((short)rates.Length);
            for (int i = 0; i < rates.Length; i++) writer.WriteInt(rates[i]);
            return true;
        }

        public void Apply(EntityManager em, NetworkReader reader)
        {
            TaxSystem tax = Resolve(em);
            int count = reader.ReadShort();
            if (count <= 0 || count > MaxTaxRateEntries)
                throw new ProtocolException("Invalid tax-rate table length " + count + ".");

            var incoming = new int[count];
            for (int i = 0; i < count; i++)
            {
                int value = reader.ReadInt();
                // Raw offsets, not percentages; a generous bound stops overflow-sized forged values.
                if (value < -MaxRawTaxOffset || value > MaxRawTaxOffset)
                    throw new ProtocolException("Invalid raw tax-rate value " + value + ".");
                incoming[i] = value;
            }
            if (reader.Remaining != 0)
                throw new ProtocolException("Trailing bytes in tax-rate state.");

            tax.Readers.Complete();
            NativeArray<int> rates = tax.GetTaxRates();
            if (!rates.IsCreated || rates.Length != count)
                throw new ProtocolException("Tax-rate table length differs from this game build (" +
                    count + " on wire, " + (rates.IsCreated ? rates.Length : 0) + " locally).");

            // Copy only after the whole payload validated.
            for (int i = 0; i < count; i++) rates[i] = incoming[i];
        }
    }
}
