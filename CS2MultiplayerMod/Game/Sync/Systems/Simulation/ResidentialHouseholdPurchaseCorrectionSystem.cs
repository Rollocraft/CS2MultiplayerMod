using CS2MultiplayerMod.Game.Sync.Infrastructure;
using Game;
using Game.Buildings;
using Game.Citizens;
using Game.Common;
using Game.Economy;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;

namespace CS2MultiplayerMod.Game.Sync.Systems
{
    /// <summary>
    /// Captures household money/resource-cost writes made by ResourceBuyerSystem after the earlier
    /// daily-economy boundary. Native shoppers and their SaleEvents remain real and keep running;
    /// only the resulting host-owned accounting scalars are corrected.
    /// </summary>
    public sealed partial class ResidentialHouseholdPurchaseCorrectionSystem : GameSystemBase
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
        /// ResourceBuyerSystem's own interval, which is what this pass exists to follow. The query
        /// here is identical to the one in
        /// <see cref="ResidentialHouseholdEconomyCorrectionSystem"/>, and both enqueue into the
        /// same retained correction queue that the same bounded drain empties - so the only thing
        /// full rate bought was building the same entity array a second time every frame.
        /// </summary>
        public override int GetUpdateInterval(SystemUpdatePhase phase) =>
            phase == SystemUpdatePhase.GameSimulation ? 16 : 1;

        protected override void OnUpdate()
        {
            using (Diagnostics.SyncProfiler.Measure("Occupancy.Purchases",
                       Diagnostics.SyncZone.Residential))
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
                _occupancy.CorrectHouseholdEconomyAfterLocalUpdate();
            }
        }
    }
}
