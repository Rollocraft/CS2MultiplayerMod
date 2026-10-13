using Unity.Entities;
using Game.Simulation;
using CS2MultiplayerMod.Core.Protocol;

using CS2MultiplayerMod.Game.Sync.Infrastructure;
namespace CS2MultiplayerMod.Game.Sync.Channels
{
    /// <summary>
    /// Weather and the climate date, written through <see cref="ClimateSystem"/>'s own setters so the
    /// client keeps evolving from a corrected baseline.
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
