using System.Collections.Generic;
using CS2MultiplayerMod.Game.Sync.Infrastructure;
using Game;
using Game.Buildings;
using Game.Citizens;
using Game.Common;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;

namespace CS2MultiplayerMod.Game.Sync.Systems
{
    /// <summary>
    /// Watches household buffers after citizen initialization; cost follows lifecycle work, and the flag
    /// cache drops HealthProblem writes that changed nothing.
    /// </summary>
    public sealed partial class ResidentialHouseholdLifecycleObservationSystem : GameSystemBase
    {
        private readonly Dictionary<Entity, byte> _healthFlags = new Dictionary<Entity, byte>();
        private ResidentialOccupancySyncSystem _occupancy;
        private EntityQuery _changedRosters;
        private EntityQuery _changedHealthProblems;

        protected override void OnCreate()
        {
            base.OnCreate();
            _occupancy = World.GetOrCreateSystemManaged<ResidentialOccupancySyncSystem>();
            _changedRosters = GetEntityQuery(new EntityQueryDesc
            {
                All = SyncQuery.ReadOnly<Household, HouseholdCitizen, PropertyRenter>(),
                None = SyncQuery.ReadOnly<Deleted, Temp, TouristHousehold, CommuterHousehold>(),
            });
            _changedRosters.SetChangedVersionFilter(
                ComponentType.ReadOnly<HouseholdCitizen>());

            _changedHealthProblems = GetEntityQuery(new EntityQueryDesc
            {
                All = SyncQuery.ReadOnly<Citizen, HouseholdMember, HealthProblem>(),
                None = SyncQuery.ReadOnly<Deleted, Temp>(),
            });
            _changedHealthProblems.SetChangedVersionFilter(
                ComponentType.ReadOnly<HealthProblem>());
        }

        protected override void OnUpdate()
        {
            using (Diagnostics.SyncProfiler.Measure("Occupancy.Lifecycle",
                       Diagnostics.SyncZone.Residential))
            {
                if (_occupancy == null) return;
                NativeArray<Entity> households = default(NativeArray<Entity>);
                NativeArray<Entity> healthCitizens = default(NativeArray<Entity>);
                NativeList<Entity> changedHealth = default(NativeList<Entity>);
                try
                {
                    if (!_changedRosters.IsEmptyIgnoreFilter)
                        households = _changedRosters.ToEntityArray(Allocator.Temp);
                    if (!_changedHealthProblems.IsEmptyIgnoreFilter)
                    {
                        healthCitizens = _changedHealthProblems.ToEntityArray(Allocator.Temp);
                        changedHealth = new NativeList<Entity>(healthCitizens.Length,
                            Allocator.Temp);
                        for (int i = 0; i < healthCitizens.Length; i++)
                        {
                            Entity citizen = healthCitizens[i];
                            HealthProblem problem = EntityManager
                                .GetComponentData<HealthProblem>(citizen);
                            byte flags = (byte)(0x80 | ((byte)problem.m_Flags & 0x7F));
                            if (_healthFlags.TryGetValue(citizen, out byte previous) &&
                                previous == flags) continue;
                            _healthFlags[citizen] = flags;
                            changedHealth.Add(citizen);
                        }
                    }

                    NativeArray<Entity> changedHealthArray = changedHealth.IsCreated
                        ? changedHealth.AsArray() : default(NativeArray<Entity>);
                    if (households.IsCreated || changedHealthArray.IsCreated)
                        _occupancy.ProcessObservedHouseholdLifecycleChanges(
                            households, changedHealthArray);
                }
                finally
                {
                    if (changedHealth.IsCreated) changedHealth.Dispose();
                    if (healthCitizens.IsCreated) healthCitizens.Dispose();
                    if (households.IsCreated) households.Dispose();
                }
            }
        }
    }
}
