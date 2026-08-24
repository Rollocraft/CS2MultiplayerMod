using Unity.Entities;
using Game.Simulation;
using CS2MultiplayerMod.Core.Protocol;

using CS2MultiplayerMod.Game.Sync.Infrastructure;
namespace CS2MultiplayerMod.Game.Sync.Channels
{
    /// <summary>
    /// Replicates weather and the climate calendar so both players see the same sky and
    /// season. The host's <see cref="ClimateSystem"/> is authoritative: its climate date
    /// (which drives the season) and the current temperature / precipitation /
    /// cloudiness are snapshotted at 1 Hz; clients write them back through the system's
    /// own <c>value</c> setters, so the client sim keeps evolving naturally between
    /// snapshots from a continuously corrected baseline (no hard override lock).
    /// </summary>
    public sealed class WeatherStateChannel : IStateChannel
    {
        public const byte Id = 13;
        public byte ChannelId => Id;

        public bool Capture(EntityManager em, NetworkWriter writer)
        {
            ClimateSystem climate = em.World.GetExistingSystemManaged<ClimateSystem>();
            if (climate == null) return false;

            writer.WriteFloat(climate.currentDate.value);
            writer.WriteFloat(climate.temperature.value);
            writer.WriteFloat(climate.precipitation.value);
            writer.WriteFloat(climate.cloudiness.value);
            return true;
        }

        public void Apply(EntityManager em, NetworkReader reader)
        {
            float date = reader.ReadFloat();
            float temperature = reader.ReadFloat();
            float precipitation = reader.ReadFloat();
            float cloudiness = reader.ReadFloat();

            ClimateSystem climate = em.World.GetExistingSystemManaged<ClimateSystem>();
            if (climate == null) return;

            climate.currentDate.value = date;
            climate.temperature.value = temperature;
            climate.precipitation.value = precipitation;
            climate.cloudiness.value = cloudiness;
        }
    }
}
