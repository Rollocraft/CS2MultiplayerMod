using Game.City;
using Unity.Entities;
using CS2MultiplayerMod.Core.Protocol;

using CS2MultiplayerMod.Game.Sync.Infrastructure;
namespace CS2MultiplayerMod.Game.Sync.Channels
{
    /// <summary>
    /// Replicates the city's own name. Unlike every other name in the game this one is not on an
    /// entity - it is city configuration - so it travels as city state rather than through
    /// <see cref="Systems.NameSyncSystem"/>. Editable: any player may rename the city, the host
    /// applies the edit and re-broadcasts it.
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
