using Game;
using Game.Buildings;
using Game.Common;
using Game.Companies;
using Game.Prefabs;
using Game.Tools;
using CS2MultiplayerMod.Game.Sync.Infrastructure;
using Unity.Collections;
using Unity.Entities;

namespace CS2MultiplayerMod.Game.Sync.Systems
{
    /// <summary>
    /// Fast client boundary for host company state. It runs on the native job-matching cadence so
    /// an arrived name/economy page and any locally changed Employee/Worker link are repaired long
    /// before the company's slower accounting partition comes around.
    /// </summary>
    public sealed partial class CompanyStateBoundarySystem : GameSystemBase
    {
        private CompanyStatsSyncSystem _companies;
        private EntityQuery _changedEmployees;
        private EntityQuery _changedEfficiencies;

        protected override void OnCreate()
        {
            base.OnCreate();
            _companies = World.GetOrCreateSystemManaged<CompanyStatsSyncSystem>();
            _changedEmployees = GetEntityQuery(new EntityQueryDesc
            {
                All = SyncQuery.ReadOnly<CompanyData, Employee, PropertyRenter>(),
                None = SyncQuery.ReadOnly<Created, Deleted, Temp>(),
            });
            _changedEmployees.SetChangedVersionFilter(ComponentType.ReadOnly<Employee>());
            _changedEfficiencies = GetEntityQuery(new EntityQueryDesc
            {
                All = SyncQuery.ReadOnly<Building, Efficiency, Renter, PrefabRef>(),
                Any = SyncQuery.ReadOnly<CommercialProperty, IndustrialProperty, OfficeProperty,
                    StorageProperty, ExtractorProperty>(),
                None = SyncQuery.ReadOnly<Deleted, Temp>(),
            });
            _changedEfficiencies.SetChangedVersionFilter(ComponentType.ReadOnly<Efficiency>());
        }

        public override int GetUpdateInterval(SystemUpdatePhase phase) =>
            phase == SystemUpdatePhase.GameSimulation ? 16 : 1;

        protected override void OnUpdate()
        {
            if (_companies == null) return;
            NativeArray<Entity> companies = default(NativeArray<Entity>);
            NativeArray<Entity> properties = default(NativeArray<Entity>);
            try
            {
                if (!_changedEmployees.IsEmptyIgnoreFilter)
                {
                    companies = _changedEmployees.ToEntityArray(Allocator.Temp);
                    _companies.CaptureEmployeeChanges(companies);
                }
                if (!_changedEfficiencies.IsEmptyIgnoreFilter)
                {
                    properties = _changedEfficiencies.ToEntityArray(Allocator.Temp);
                    _companies.CaptureEfficiencyChanges(properties);
                }
            }
            finally
            {
                if (companies.IsCreated) companies.Dispose();
                if (properties.IsCreated) properties.Dispose();
            }
            _companies.ApplyClientStateBoundary();
        }
    }
}
