using Game;
using Game.Buildings;
using Game.Common;
using Game.Companies;
using Game.Prefabs;
using Game.Tools;
using CS2MultiplayerMod.Game.Sync.Infrastructure;
using Unity.Entities;

namespace CS2MultiplayerMod.Game.Sync.Systems
{
    /// <summary>On the job-matching cadence, well before the slower accounting partition.</summary>
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
            SyncQuery.WithEntities(_changedEmployees, _companies.CaptureEmployeeChanges);
            SyncQuery.WithEntities(_changedEfficiencies, _companies.CaptureEfficiencyChanges);
            _companies.ApplyClientStateBoundary();
        }
    }
}
