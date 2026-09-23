using CS2MultiplayerMod.Game.Sync.Infrastructure;
using Game;
using Game.Buildings;
using Game.Citizens;
using Game.Common;
using Game.Economy;
using Game.Prefabs;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;

namespace CS2MultiplayerMod.Game.Sync.Systems
{
    /// <summary>
    /// Ordered directly after one native writer: queues the entities it changed this interval, then
    /// reasserts the host values on them. Runs at the writer's interval so the game hands it the same
    /// update offset.
    /// </summary>
    public abstract partial class OccupancyCorrectionSystem : GameSystemBase
    {
        private EntityQuery _changed;

        protected ResidentialOccupancySyncSystem Occupancy { get; private set; }

        /// <summary>Profiler scope, in the residential zone.</summary>
        protected abstract string Scope { get; }
        protected abstract int SimulationInterval { get; }
        protected abstract bool Wanted { get; }

        protected abstract EntityQuery CreateChangedQuery();
        protected abstract void Clear();
        protected abstract void Queue(NativeArray<Entity> changed);

        /// <summary>Every update, also with nothing queued: it drains what a bounded pass retained.</summary>
        protected abstract void Correct();

        protected override void OnCreate()
        {
            base.OnCreate();
            Occupancy = World.GetOrCreateSystemManaged<ResidentialOccupancySyncSystem>();
            _changed = CreateChangedQuery();
        }

        public override int GetUpdateInterval(SystemUpdatePhase phase) =>
            phase == SystemUpdatePhase.GameSimulation ? SimulationInterval : 1;

        protected override void OnUpdate()
        {
            using (Diagnostics.SyncProfiler.Measure(Scope, Diagnostics.SyncZone.Residential))
            {
                if (Occupancy == null) return;
                if (!Wanted)
                {
                    Clear();
                    return;
                }
                SyncQuery.WithEntities(_changed, Queue);
                Correct();
            }
        }

        protected EntityQuery HouseholdQuery(bool withResources, params ComponentType[] changeFilter)
        {
            EntityQuery query = GetEntityQuery(new EntityQueryDesc
            {
                All = withResources
                    ? SyncQuery.ReadOnly<Household, PropertyRenter, Resources>()
                    : SyncQuery.ReadOnly<Household, PropertyRenter>(),
                None = SyncQuery.ReadOnly<Deleted, Temp, TouristHousehold, CommuterHousehold>(),
            });
            query.SetChangedVersionFilter(changeFilter);
            return query;
        }

        protected EntityQuery PropertyQuery<TConsumer>() where TConsumer : struct, IComponentData
        {
            EntityQuery query = GetEntityQuery(new EntityQueryDesc
            {
                All = SyncQuery.ReadOnly<Building, ResidentialProperty, TConsumer, PrefabRef>(),
                None = SyncQuery.ReadOnly<Deleted, Temp>(),
            });
            query.SetChangedVersionFilter(ComponentType.ReadOnly<TConsumer>());
            return query;
        }
    }

    /// <summary>
    /// Household chunks touched since the last update; households in one building do not share its
    /// update partition.
    /// </summary>
    public sealed partial class ResidentialHouseholdEconomyCorrectionSystem : OccupancyCorrectionSystem
    {
        protected override string Scope => "Occupancy.Economy";

        /// <summary>The followed writers run on 16- and 64-frame intervals.</summary>
        protected override int SimulationInterval => 16;
        protected override bool Wanted => Occupancy.WantsHouseholdEconomyCorrection;

        protected override EntityQuery CreateChangedQuery() =>
            HouseholdQuery(true, ComponentType.ReadOnly<Household>(), ComponentType.ReadOnly<Resources>());

        protected override void Clear() => Occupancy.ClearHouseholdEconomyCorrections();
        protected override void Queue(NativeArray<Entity> changed) => Occupancy.QueueHouseholdEconomyCorrections(changed);
        protected override void Correct() => Occupancy.CorrectHouseholdEconomyAfterLocalUpdate();
    }

    /// <summary>
    /// Restores host income in the frame the local pass recomputes it; its consumers run before the
    /// economy correction does.
    /// </summary>
    public sealed partial class ResidentialHouseholdIncomeBoundarySystem : OccupancyCorrectionSystem
    {
        protected override string Scope => "Occupancy.Income";

        /// <summary>The writer's own interval.</summary>
        protected override int SimulationInterval =>
            262144 / (global::Game.Simulation.HouseholdBehaviorSystem.kUpdatesPerDay * 16);

        protected override bool Wanted => Occupancy.WantsHouseholdEconomyCorrection;

        // Income lives on Household only: do not wake on a Resources-only change.
        protected override EntityQuery CreateChangedQuery() =>
            HouseholdQuery(false, ComponentType.ReadOnly<Household>());

        protected override void Clear() => Occupancy.ClearHouseholdIncomeCorrections();
        protected override void Queue(NativeArray<Entity> changed) => Occupancy.QueueHouseholdIncomeCorrections(changed);
        protected override void Correct() => Occupancy.CorrectHouseholdIncomeAfterLocalUpdate();
    }

    /// <summary>Household money after ResourceBuyerSystem; shoppers and SaleEvents stay real.</summary>
    public sealed partial class ResidentialHouseholdPurchaseCorrectionSystem : OccupancyCorrectionSystem
    {
        protected override string Scope => "Occupancy.Purchases";

        /// <summary>ResourceBuyerSystem's own interval.</summary>
        protected override int SimulationInterval => 16;
        protected override bool Wanted => Occupancy.WantsHouseholdEconomyCorrection;

        // A sale writes Resources; Household-only changes belong to the earlier boundaries.
        protected override EntityQuery CreateChangedQuery() =>
            HouseholdQuery(true, ComponentType.ReadOnly<Resources>());

        protected override void Clear() => Occupancy.ClearHouseholdEconomyCorrections();
        protected override void Queue(NativeArray<Entity> changed) => Occupancy.QueueHouseholdEconomyCorrections(changed);
        protected override void Correct() => Occupancy.CorrectHouseholdEconomyAfterLocalUpdate();
    }

    /// <summary>The host fee input after native electricity dispatch writes it.</summary>
    public sealed partial class ResidentialElectricityFeeCorrectionSystem : OccupancyCorrectionSystem
    {
        /// <summary>Matches <c>DispatchElectricitySystem.GetUpdateInterval</c>, as does water dispatch.</summary>
        internal const int DispatchInterval = 128;

        protected override string Scope => "Occupancy.ElectricityFees";
        protected override int SimulationInterval => DispatchInterval;
        protected override bool Wanted => Occupancy.WantsPropertyFeeCorrection;
        protected override EntityQuery CreateChangedQuery() => PropertyQuery<ElectricityConsumer>();
        protected override void Clear() => Occupancy.ClearPropertyFeeCorrections();
        protected override void Queue(NativeArray<Entity> changed) => Occupancy.QueueElectricityFeeCorrections(changed);
        protected override void Correct() => Occupancy.CorrectElectricityFeeInputsAfterLocalUpdate();
    }

    /// <summary>The host fee inputs after native water/sewage dispatch writes them.</summary>
    public sealed partial class ResidentialWaterFeeCorrectionSystem : OccupancyCorrectionSystem
    {
        protected override string Scope => "Occupancy.WaterFees";
        protected override int SimulationInterval => ResidentialElectricityFeeCorrectionSystem.DispatchInterval;
        protected override bool Wanted => Occupancy.WantsPropertyFeeCorrection;
        protected override EntityQuery CreateChangedQuery() => PropertyQuery<WaterConsumer>();
        protected override void Clear() => Occupancy.ClearPropertyFeeCorrections();
        protected override void Queue(NativeArray<Entity> changed) => Occupancy.QueueWaterFeeCorrections(changed);
        protected override void Correct() => Occupancy.CorrectWaterFeeInputsAfterLocalUpdate();
    }
}
