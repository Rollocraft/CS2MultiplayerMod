using Game;
using Game.Buildings;
using Game.Common;
using Game.Companies;
using Game.Economy;
using Game.Prefabs;
using Game.Tools;
using CS2MultiplayerMod.Game.Sync.Infrastructure;
using Unity.Collections;
using Unity.Entities;

namespace CS2MultiplayerMod.Game.Sync.Systems
{
    /// <summary>
    /// The two boundaries that keep the selected-building Production figure in step. Each runs
    /// directly after one native production pass, at that pass's own interval, so it inherits its
    /// update offset and sees exactly the buildings that pass just wrote.
    ///
    /// <para>Both native passes rewrite the property's efficiency factors on the machine they run
    /// on. Processing businesses get <c>LackResources</c>, a binary factor taken from the goods the
    /// company happens to be holding locally and from a random rounding draw; extraction adds
    /// <c>NaturalResources</c> from the local area's depletion state. The panel multiplies every
    /// factor, so a single locally derived zero shows a producing business as zero. Offices never
    /// meet either factor - their output is weightless, which is what makes the native production
    /// count large enough that the rounding draw is never zero, and they consume no materials -
    /// which is why the panel already agreed for offices and never did for industry.</para>
    ///
    /// <para>The panel recalculates on every UI frame, so the repair has to land in the same
    /// simulation frame as the local write rather than on the slower company boundary.</para>
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
            if (_companies == null || !_companies.WantsProductionBoundary ||
                _changedEfficiencies.IsEmptyIgnoreFilter) return;
            NativeArray<Entity> properties = _changedEfficiencies.ToEntityArray(Allocator.Temp);
            try { _companies.ApplyProductionBoundary(properties); }
            finally { properties.Dispose(); }
        }
    }

    /// <summary>
    /// The extraction half of the same boundary. Besides the efficiency factors it also holds
    /// <c>CompanyStatisticData.m_LastUpdateProduce</c>, the one production figure the panel reads
    /// straight off the company instead of recalculating it - and which the native extractor pass
    /// derives from area depletion and a random rounding draw, so it can never converge on its own.
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
            NativeArray<Entity> companies = default(NativeArray<Entity>);
            NativeArray<Entity> properties = default(NativeArray<Entity>);
            try
            {
                if (!_changedProduce.IsEmptyIgnoreFilter)
                {
                    companies = _changedProduce.ToEntityArray(Allocator.Temp);
                    _companies.CaptureExtractorProduceChanges(companies);
                }
                if (_companies.WantsProductionBoundary &&
                    !_changedEfficiencies.IsEmptyIgnoreFilter)
                {
                    properties = _changedEfficiencies.ToEntityArray(Allocator.Temp);
                    _companies.ApplyProductionBoundary(properties);
                }
            }
            finally
            {
                if (companies.IsCreated) companies.Dispose();
                if (properties.IsCreated) properties.Dispose();
            }
        }
    }
}
