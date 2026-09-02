using Game;
using Game.Buildings;
using Game.Citizens;
using Game.Common;
using Game.Economy;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using CS2MultiplayerMod.Game.Sync.Infrastructure;

namespace CS2MultiplayerMod.Game.Sync.Systems
{
    /// <summary>
    /// Corrects only household chunks touched since the preceding simulation update. This follows
    /// household-level writers instead of assuming that every family shares its building's update
    /// partition, which is not true for multi-unit residential buildings.
    /// </summary>
    public sealed partial class ResidentialHouseholdEconomyCorrectionSystem : GameSystemBase
    {
        private ResidentialOccupancySyncSystem _occupancy;
        private EntityQuery _changedHouseholds;

        protected override void OnCreate()
        {
            base.OnCreate();
            _occupancy = World.GetOrCreateSystemManaged<ResidentialOccupancySyncSystem>();
            _changedHouseholds = GetEntityQuery(new EntityQueryDesc
            {
                All = SyncQuery.ReadOnly<Household, PropertyRenter, Resources>(),
                None = SyncQuery.ReadOnly<Deleted, Temp, TouristHousehold, CommuterHousehold>(),
            });
            _changedHouseholds.SetChangedVersionFilter(new[]
            {
                ComponentType.ReadOnly<Household>(),
                ComponentType.ReadOnly<Resources>(),
            });
        }

        /// <summary>
        /// The household writers this pass follows run on 16- and 64-frame intervals, and the
        /// bounded drain has capacity to spare at this cadence: its identical twin
        /// <see cref="ResidentialHouseholdPurchaseCorrectionSystem"/> measured 531 ms per 30 s
        /// against this system's 4,951 ms for the same work on the same queue, purely because it
        /// was not rebuilding the changed-household array on every single frame.
        /// </summary>
        public override int GetUpdateInterval(SystemUpdatePhase phase) =>
            phase == SystemUpdatePhase.GameSimulation ? 16 : 1;

        protected override void OnUpdate()
        {
            using (Diagnostics.SyncProfiler.Measure("Occupancy.Economy", Diagnostics.SyncZone.Residential))
            {
                if (_occupancy == null) return;
                if (!_occupancy.WantsHouseholdEconomyCorrection)
                {
                    _occupancy.ClearHouseholdEconomyCorrections();
                    return;
                }
                NativeArray<Entity> households = default(NativeArray<Entity>);
                try
                {
                    if (!_changedHouseholds.IsEmptyIgnoreFilter)
                    {
                        households = _changedHouseholds.ToEntityArray(Allocator.Temp);
                        if (households.Length != 0)
                            _occupancy.QueueHouseholdEconomyCorrections(households);
                    }
                }
                finally
                {
                    if (households.IsCreated) households.Dispose();
                }
                // Drain even on a frame whose changed-version query is empty: those are the
                // retained entities that did not fit the previous frame's bounded correction.
                _occupancy.CorrectHouseholdEconomyAfterLocalUpdate();
            }
        }
    }
}
