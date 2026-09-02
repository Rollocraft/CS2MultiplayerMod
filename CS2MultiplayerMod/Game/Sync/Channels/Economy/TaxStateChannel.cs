using Game.Simulation;
using Unity.Collections;
using Unity.Entities;
using CS2MultiplayerMod.Core.Protocol;
using CS2MultiplayerMod.Game.Sync.Infrastructure;

namespace CS2MultiplayerMod.Game.Sync.Channels
{
    /// <summary>
    /// Replicates the complete native tax-rate table. The first version of this channel sent only
    /// the four area totals; Cities: Skylines II stores those as offsets into a 92-entry table whose
    /// remaining entries contain the residential education-level and commercial/industrial/office
    /// resource rates. Consequently two panels could show the same four headings while every
    /// expanded row - and every demand calculation using it - still differed.
    ///
    /// This remains player-editable. A client proposes one complete, bounded table, the host applies
    /// it atomically and the next snapshot confirms the result to everyone.
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
                // These are offsets, not the displayed final percentages. Vanilla's limits are
                // much narrower; this generous bound admits current/future game settings while a
                // forged editable payload cannot install overflow-sized values into native jobs.
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

            // Copy only after the complete payload has passed validation, so a malformed edit can
            // never leave the host with a half-updated table.
            for (int i = 0; i < count; i++) rates[i] = incoming[i];
        }
    }
}
