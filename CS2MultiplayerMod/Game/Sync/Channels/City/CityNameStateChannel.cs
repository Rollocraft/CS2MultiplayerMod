using Game.City;
using Unity.Entities;
using CS2MultiplayerMod.Core.Protocol;

using CS2MultiplayerMod.Game.Sync.Infrastructure;
namespace CS2MultiplayerMod.Game.Sync.Channels
{
    /// <summary>
    /// The city's own name: city configuration rather than an entity, so it travels as editable city
    /// state instead of through <see cref="Systems.NameSyncSystem"/>.
    /// </summary>
    public sealed class CityNameStateChannel : IStateChannel
    {
        public const byte Id = 17;
        public byte ChannelId => Id;

        /// <summary>The rename field is free text, so it is sanitized and clamped, not rejected.</summary>
        private const int MaxCityNameLength = 64;

        private CityConfigurationSystem _configuration;

        private CityConfigurationSystem Resolve(EntityManager em) =>
            _configuration ?? (_configuration =
                em.World.GetOrCreateSystemManaged<CityConfigurationSystem>());

        public bool Capture(EntityManager em, NetworkWriter writer)
        {
            string name = Resolve(em).cityName;
            // No city loaded yet: staying silent keeps a client from adopting an empty name.
            if (string.IsNullOrEmpty(name)) return false;
            writer.WriteString(WireGuard.SanitizeText(name, MaxCityNameLength));
            return true;
        }

        public void Apply(EntityManager em, NetworkReader reader)
        {
            string name = WireGuard.SanitizeText(reader.ReadString(), MaxCityNameLength);
            if (name.Length == 0) return;

            CityConfigurationSystem configuration = Resolve(em);
            if (configuration.cityName == name) return;
            configuration.cityName = name;
        }
    }
}
