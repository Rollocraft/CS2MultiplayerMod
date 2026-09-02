using System.Collections.Concurrent;
using System.Collections.Generic;
using CS2MultiplayerMod.Core.Session;
using Game.Buildings;
using Unity.Collections;
using Unity.Entities;

namespace CS2MultiplayerMod.Game.Sync.Systems
{
    public partial class ResidentialOccupancySyncSystem
    {
        private const int MaxPropertyFeeCorrectionsPerFrame = 1024;

        private readonly ConcurrentQueue<Entity> _electricityFeeCorrectionQueue =
            new ConcurrentQueue<Entity>();
        private readonly HashSet<Entity> _electricityFeeCorrectionMembers = new HashSet<Entity>();
        private readonly ConcurrentQueue<Entity> _waterFeeCorrectionQueue =
            new ConcurrentQueue<Entity>();
        private readonly HashSet<Entity> _waterFeeCorrectionMembers = new HashSet<Entity>();

        internal bool WantsPropertyFeeCorrection
        {
            get
            {
                MultiplayerService service = Mod.Service;
                return service != null && service.GameplaySyncReady &&
                       service.Session.Role == SessionRole.Client && _cache.Count != 0;
            }
        }

        /// <summary>
        /// Apply only the quantities used by ResidentsSection's fee calculation. Wanted demand,
        /// graph connectivity, cooldowns and warning flags remain products of the receiver's real
        /// utility simulation.
        /// </summary>
        private void ApplyPropertyFeeInputs(Entity property, CachedProperty cached)
        {
            ApplyElectricityFeeInput(property, cached);
            ApplyWaterFeeInput(property, cached);
        }

        private void ApplyElectricityFeeInput(Entity property, CachedProperty cached)
        {
            if (!EntityManager.HasComponent<ElectricityConsumer>(property)) return;
            ElectricityConsumer current =
                EntityManager.GetComponentData<ElectricityConsumer>(property);
            int wanted = cached.HasElectricityConsumer
                ? cached.ElectricityFulfilledConsumption : 0;
            if (current.m_FulfilledConsumption == wanted) return;
            current.m_FulfilledConsumption = wanted;
            EntityManager.SetComponentData(property, current);
            _feeInputCorrections++;
        }

        private void ApplyWaterFeeInput(Entity property, CachedProperty cached)
        {
            if (!EntityManager.HasComponent<WaterConsumer>(property)) return;
            WaterConsumer current = EntityManager.GetComponentData<WaterConsumer>(property);
            int wantedFresh = cached.HasWaterConsumer ? cached.WaterFulfilledFresh : 0;
            int wantedSewage = cached.HasWaterConsumer ? cached.WaterFulfilledSewage : 0;
            if (current.m_FulfilledFresh == wantedFresh &&
                current.m_FulfilledSewage == wantedSewage) return;
            current.m_FulfilledFresh = wantedFresh;
            current.m_FulfilledSewage = wantedSewage;
            EntityManager.SetComponentData(property, current);
            _feeInputCorrections++;
        }

        // The dispatch systems write a consumer on every building they serve, so these arrays
        // arrive holding the whole residential city. Only a property this peer holds a host page
        // for can be corrected at all - the drain drops the rest again - so test that first and
        // keep the uncorrectable majority out of the pending set entirely.
        internal void QueueElectricityFeeCorrections(NativeArray<Entity> properties)
        {
            for (int i = 0; i < properties.Length; i++)
            {
                Entity property = properties[i];
                if (!_cache.ContainsKey(property)) continue;
                if (_electricityFeeCorrectionMembers.Add(property))
                    _electricityFeeCorrectionQueue.Enqueue(property);
            }
        }

        internal void QueueWaterFeeCorrections(NativeArray<Entity> properties)
        {
            for (int i = 0; i < properties.Length; i++)
            {
                Entity property = properties[i];
                if (!_cache.ContainsKey(property)) continue;
                if (_waterFeeCorrectionMembers.Add(property))
                    _waterFeeCorrectionQueue.Enqueue(property);
            }
        }

        internal void CorrectElectricityFeeInputsAfterLocalUpdate()
        {
            if (!WantsPropertyFeeCorrection)
            {
                ClearPropertyFeeCorrections();
                return;
            }

            int examine = _electricityFeeCorrectionQueue.Count <
                          MaxPropertyFeeCorrectionsPerFrame
                ? _electricityFeeCorrectionQueue.Count : MaxPropertyFeeCorrectionsPerFrame;
            for (int i = 0; i < examine; i++)
            {
                Entity property;
                if (!_electricityFeeCorrectionQueue.TryDequeue(out property)) break;
                _electricityFeeCorrectionMembers.Remove(property);
                CachedProperty cached;
                if (!_cache.TryGetValue(property, out cached) ||
                    !MatchesCachedProperty(property, cached)) continue;
                ApplyElectricityFeeInput(property, cached);
            }
            if (_electricityFeeCorrectionQueue.Count != 0)
                _feeInputDeferred += _electricityFeeCorrectionQueue.Count;
        }

        internal void CorrectWaterFeeInputsAfterLocalUpdate()
        {
            if (!WantsPropertyFeeCorrection)
            {
                ClearPropertyFeeCorrections();
                return;
            }

            int examine = _waterFeeCorrectionQueue.Count < MaxPropertyFeeCorrectionsPerFrame
                ? _waterFeeCorrectionQueue.Count : MaxPropertyFeeCorrectionsPerFrame;
            for (int i = 0; i < examine; i++)
            {
                Entity property;
                if (!_waterFeeCorrectionQueue.TryDequeue(out property)) break;
                _waterFeeCorrectionMembers.Remove(property);
                CachedProperty cached;
                if (!_cache.TryGetValue(property, out cached) ||
                    !MatchesCachedProperty(property, cached)) continue;
                ApplyWaterFeeInput(property, cached);
            }
            if (_waterFeeCorrectionQueue.Count != 0)
                _feeInputDeferred += _waterFeeCorrectionQueue.Count;
        }

        internal void ClearPropertyFeeCorrections()
        {
            Entity discarded;
            while (_electricityFeeCorrectionQueue.TryDequeue(out discarded)) { }
            _electricityFeeCorrectionMembers.Clear();
            while (_waterFeeCorrectionQueue.TryDequeue(out discarded)) { }
            _waterFeeCorrectionMembers.Clear();
        }
    }
}
