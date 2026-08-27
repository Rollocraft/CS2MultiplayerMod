using Game.Simulation;
using Unity.Entities;
using CS2MultiplayerMod.Core.Protocol;

using CS2MultiplayerMod.Game.Sync.Infrastructure;
namespace CS2MultiplayerMod.Game.Sync.Channels
{
    /// <summary>
    /// Replicates the four headline tax rates (residential/commercial/industrial/office)
    /// via <see cref="TaxSystem"/>. Registered as player-editable in
    /// <see cref="CityStateSyncSystem"/>: any player may change them, the host applies
    /// the edit and re-broadcasts it to everyone. Per-job-level and per-resource
    /// sub-rates are a follow-up (same mechanism, longer payload).
    /// </summary>
    public sealed class TaxStateChannel : IStateChannel
    {
        public const byte Id = 6;
        public byte ChannelId => Id;

        private static readonly TaxAreaType[] Areas =
        {
            TaxAreaType.Residential, TaxAreaType.Commercial, TaxAreaType.Industrial, TaxAreaType.Office,
        };

        private TaxSystem _taxSystem;

        private TaxSystem Resolve(EntityManager em) =>
            _taxSystem ?? (_taxSystem = em.World.GetOrCreateSystemManaged<TaxSystem>());

        public bool Capture(EntityManager em, NetworkWriter writer)
        {
            TaxSystem tax = Resolve(em);
            writer.WriteByte((byte)Areas.Length);
            for (int i = 0; i < Areas.Length; i++)
                writer.WriteInt(tax.GetTaxRate(Areas[i]));
            return true;
        }

        public void Apply(EntityManager em, NetworkReader reader)
        {
            TaxSystem tax = Resolve(em);
            int count = reader.ReadByte();
            for (int i = 0; i < count && i < Areas.Length; i++)
            {
                int rate = reader.ReadInt();
                if (tax.GetTaxRate(Areas[i]) != rate) tax.SetTaxRate(Areas[i], rate);
            }
        }
    }
}
