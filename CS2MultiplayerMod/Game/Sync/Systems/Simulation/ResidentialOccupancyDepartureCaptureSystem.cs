using CS2MultiplayerMod.Game.Sync.Infrastructure;
using Game;
using Game.Common;
using Game.Tools;
using Unity.Entities;

namespace CS2MultiplayerMod.Game.Sync.Systems
{
    /// <summary>
    /// Just before the move-away executor: samples departures on a host, strips local lifecycle
    /// decisions on a client. State lives in ResidentialOccupancySyncSystem.
    /// </summary>
    public sealed partial class ResidentialOccupancyDepartureCaptureSystem : GameSystemBase
    {
        private ResidentialOccupancySyncSystem _occupancy;
        // Owned here so its change filter spans exactly the ticks since this scan last ran.
        private EntityQuery _changedDepartures;

        protected override void OnCreate()
        {
            base.OnCreate();
            _occupancy = World.GetOrCreateSystemManaged<ResidentialOccupancySyncSystem>();
            _changedDepartures = GetEntityQuery(new EntityQueryDesc
            {
                All = SyncQuery.ReadOnly<global::Game.Citizens.Household,
                    global::Game.Agents.MovingAway>(),
                None = SyncQuery.ReadOnly<Deleted, Temp, global::Game.Citizens.TouristHousehold,
                    global::Game.Citizens.CommuterHousehold>(),
            });
            _changedDepartures.SetChangedVersionFilter(
                ComponentType.ReadOnly<global::Game.Agents.MovingAway>());
        }

        /// <summary>
        /// HouseholdMoveAwaySystem's interval; an equal interval inherits its update offset, so both tick on
        /// the same frame.
        /// </summary>
        public override int GetUpdateInterval(SystemUpdatePhase phase) =>
            phase == SystemUpdatePhase.GameSimulation ? 16 : 1;

        protected override void OnUpdate()
        {
            using (Diagnostics.SyncProfiler.Measure("Occupancy.Lifecycle", Diagnostics.SyncZone.Residential))
            {
                if (_occupancy != null) _occupancy.ProcessHouseholdLifecycleBoundary(_changedDepartures);
            }
        }
    }
}
