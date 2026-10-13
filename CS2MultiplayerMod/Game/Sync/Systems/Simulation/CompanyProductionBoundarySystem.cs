using Game;
using Game.Buildings;
using Game.Common;
using Game.Companies;
using Game.Economy;
using Game.Prefabs;
using Game.Tools;
using CS2MultiplayerMod.Game.Sync.Infrastructure;
using Unity.Entities;

namespace CS2MultiplayerMod.Game.Sync.Systems
{
    /// <summary>
    /// Runs right after the native processing pass, at its interval and offset, and repairs the
    /// efficiency factors it just rewrote from local goods and a random draw. The panel multiplies
    /// every factor each UI frame, so the repair lands in the same frame.
    /// </summary>
    public sealed partial class CompanyProcessingBoundarySystem : GameSystemBase
    {
        private CompanyStatsSyncSystem _companies;
        private EntityQuery _changedEfficiencies;

        protected override void OnCreate()
        {
            base.OnCreate();
            _companies = World.GetOrCreateSystemManaged<CompanyStatsSyncSystem>();
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
            phase == SystemUpdatePhase.GameSimulation
                ? 262144 / (EconomyUtils.kCompanyUpdatesPerDay * 16)
                : 1;

        protected override void OnUpdate()
        {
            if (_companies == null || !_companies.WantsProductionBoundary) return;
            SyncQuery.WithEntities(_changedEfficiencies, _companies.ApplyProductionBoundary);
        }
    }

    /// <summary>
    /// The extraction half, which also restores <c>m_LastUpdateProduce</c>: read straight off the
    /// company and derived from local depletion and a random draw.
    /// </summary>
    public sealed partial class CompanyExtractorBoundarySystem : GameSystemBase
    {
        private CompanyStatsSyncSystem _companies;
        private EntityQuery _changedEfficiencies;
        private EntityQuery _changedProduce;

        protected override void OnCreate()
        {
            base.OnCreate();
            _companies = World.GetOrCreateSystemManaged<CompanyStatsSyncSystem>();
            _changedEfficiencies = GetEntityQuery(new EntityQueryDesc
            {
                All = SyncQuery.ReadOnly<Building, Efficiency, Renter, PrefabRef>(),
                Any = SyncQuery.ReadOnly<IndustrialProperty, ExtractorProperty,
                    StorageProperty>(),
                None = SyncQuery.ReadOnly<Deleted, Temp>(),
            });
            _changedEfficiencies.SetChangedVersionFilter(ComponentType.ReadOnly<Efficiency>());
            _changedProduce = GetEntityQuery(new EntityQueryDesc
            {
                All = SyncQuery.ReadOnly<global::Game.Companies.ExtractorCompany,
                    CompanyStatisticData, PropertyRenter>(),
                None = SyncQuery.ReadOnly<Created, Deleted, Temp>(),
            });
            _changedProduce.SetChangedVersionFilter(
                ComponentType.ReadOnly<CompanyStatisticData>());
        }

        public override int GetUpdateInterval(SystemUpdatePhase phase) =>
            phase == SystemUpdatePhase.GameSimulation
                ? 262144 / (EconomyUtils.kCompanyUpdatesPerDay * 16)
                : 1;

        protected override void OnUpdate()
        {
            if (_companies == null || !_companies.WantsExtractorProduceBoundary) return;
            SyncQuery.WithEntities(_changedProduce, _companies.CaptureExtractorProduceChanges);
            if (_companies.WantsProductionBoundary)
                SyncQuery.WithEntities(_changedEfficiencies, _companies.ApplyProductionBoundary);
        }
    }
}
