using CS2MultiplayerMod.Core.Diagnostics;
using CS2MultiplayerMod.Game.Sync.Commands;
using CS2MultiplayerMod.Game.Sync.Systems;

namespace CS2MultiplayerMod.Game.Sync.Channels
{
    /// <summary>Households and residents per residential building.</summary>
    internal sealed class ResidentialOccupancyChannel : PagedPropertyChannel<ResidentialOccupancySnapshot>
    {
        public const byte Id = 21;

        public ResidentialOccupancyChannel(ResidentialOccupancySyncSystem runtime)
            : base(Id, runtime, ResidentialOccupancySnapshot.Read, LogTopic.Residential, "Occupancy") { }
    }
}
