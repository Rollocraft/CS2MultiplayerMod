using CS2MultiplayerMod.Core.Diagnostics;
using CS2MultiplayerMod.Game.Sync.Commands;
using CS2MultiplayerMod.Game.Sync.Systems;

namespace CS2MultiplayerMod.Game.Sync.Channels
{
    /// <summary>Numeric rent per property. Applied between RentAdjustSystem and PropertyRenterSystem.</summary>
    internal sealed class PropertyRentStateChannel : PagedPropertyChannel<PropertyRentSnapshot>
    {
        public const byte Id = 20;

        public PropertyRentStateChannel(PropertyRentSyncSystem runtime)
            : base(Id, runtime, PropertyRentSnapshot.Read, LogTopic.Residential, "PropertyRent") { }
    }
}
