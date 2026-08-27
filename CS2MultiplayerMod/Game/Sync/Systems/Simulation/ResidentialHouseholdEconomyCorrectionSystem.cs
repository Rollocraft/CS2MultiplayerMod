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
                All = new[]
                {
                    ComponentType.ReadOnly<Household>(),
                    ComponentType.ReadOnly<PropertyRenter>(),
                    ComponentType.ReadOnly<Resources>(),
                },
                None = new[]
                {
                    ComponentType.ReadOnly<Deleted>(),
                    ComponentType.ReadOnly<Temp>(),
                    ComponentType.ReadOnly<TouristHousehold>(),
                    ComponentType.ReadOnly<CommuterHousehold>(),
                },
            });
            _changedHouseholds.SetChangedVersionFilter(new[]
            {
                ComponentType.ReadOnly<Household>(),
                ComponentType.ReadOnly<Resources>(),
            });
        }

        protected override void OnUpdate()
        {
            if (_occupancy == null || _changedHouseholds.IsEmptyIgnoreFilter) return;
            NativeArray<Entity> households = default(NativeArray<Entity>);
            try
            {
                households = _changedHouseholds.ToEntityArray(Allocator.Temp);
                if (households.Length != 0)
                    _occupancy.CorrectHouseholdEconomyAfterLocalUpdate(households);
            }
            finally
            {
                if (households.IsCreated) households.Dispose();
            }
        }
    }
}
