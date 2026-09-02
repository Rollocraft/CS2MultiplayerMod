using CS2MultiplayerMod.Game.Sync.Infrastructure;
using Game;
using Game.Buildings;
using Game.Common;
using Game.Prefabs;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;

namespace CS2MultiplayerMod.Game.Sync.Systems
{
    /// <summary>Reasserts the host fee input after native electricity dispatch writes it.</summary>
    public sealed partial class ResidentialElectricityFeeCorrectionSystem : GameSystemBase
    {
        private ResidentialOccupancySyncSystem _occupancy;
        private EntityQuery _changedProperties;

        protected override void OnCreate()
        {
            base.OnCreate();
            _occupancy = World.GetOrCreateSystemManaged<ResidentialOccupancySyncSystem>();
            _changedProperties = GetEntityQuery(new EntityQueryDesc
            {
                All = SyncQuery.ReadOnly<Building, ResidentialProperty, ElectricityConsumer,
                    PrefabRef>(),
                None = SyncQuery.ReadOnly<Deleted, Temp>(),
            });
            _changedProperties.SetChangedVersionFilter(
                ComponentType.ReadOnly<ElectricityConsumer>());
        }

        /// <summary>
        /// DispatchElectricitySystem's own interval. It is the only writer this pass reacts to, so
        /// on 127 of every 128 frames the changed-version query could only ever return what this
        /// system itself had already queued - and paying for that meant materialising an entity
        /// array over every residential consumer and hashing each one into the pending set.
        /// </summary>
        public override int GetUpdateInterval(SystemUpdatePhase phase) =>
            phase == SystemUpdatePhase.GameSimulation ? DispatchInterval : 1;

        /// <summary>Matches <c>DispatchElectricitySystem.GetUpdateInterval</c>.</summary>
        internal const int DispatchInterval = 128;

        protected override void OnUpdate()
        {
            if (_occupancy == null) return;
            if (!_occupancy.WantsPropertyFeeCorrection)
            {
                _occupancy.ClearPropertyFeeCorrections();
                return;
            }

            NativeArray<Entity> properties = default(NativeArray<Entity>);
            try
            {
                if (!_changedProperties.IsEmptyIgnoreFilter)
                {
                    properties = _changedProperties.ToEntityArray(Allocator.Temp);
                    if (properties.Length != 0)
                        _occupancy.QueueElectricityFeeCorrections(properties);
                }
            }
            finally
            {
                if (properties.IsCreated) properties.Dispose();
            }
            _occupancy.CorrectElectricityFeeInputsAfterLocalUpdate();
        }
    }

    /// <summary>Reasserts the host fee inputs after native water/sewage dispatch writes them.</summary>
    public sealed partial class ResidentialWaterFeeCorrectionSystem : GameSystemBase
    {
        private ResidentialOccupancySyncSystem _occupancy;
        private EntityQuery _changedProperties;

        protected override void OnCreate()
        {
            base.OnCreate();
            _occupancy = World.GetOrCreateSystemManaged<ResidentialOccupancySyncSystem>();
            _changedProperties = GetEntityQuery(new EntityQueryDesc
            {
                All = SyncQuery.ReadOnly<Building, ResidentialProperty, WaterConsumer, PrefabRef>(),
                None = SyncQuery.ReadOnly<Deleted, Temp>(),
            });
            _changedProperties.SetChangedVersionFilter(ComponentType.ReadOnly<WaterConsumer>());
        }

        /// <summary>DispatchWaterSystem's own interval; see the electricity twin above.</summary>
        public override int GetUpdateInterval(SystemUpdatePhase phase) =>
            phase == SystemUpdatePhase.GameSimulation
                ? ResidentialElectricityFeeCorrectionSystem.DispatchInterval : 1;

        protected override void OnUpdate()
        {
            if (_occupancy == null) return;
            if (!_occupancy.WantsPropertyFeeCorrection)
            {
                _occupancy.ClearPropertyFeeCorrections();
                return;
            }

            NativeArray<Entity> properties = default(NativeArray<Entity>);
            try
            {
                if (!_changedProperties.IsEmptyIgnoreFilter)
                {
                    properties = _changedProperties.ToEntityArray(Allocator.Temp);
                    if (properties.Length != 0)
                        _occupancy.QueueWaterFeeCorrections(properties);
                }
            }
            finally
            {
                if (properties.IsCreated) properties.Dispose();
            }
            _occupancy.CorrectWaterFeeInputsAfterLocalUpdate();
        }
    }
}
