using System;
using CS2MultiplayerMod.Core.Diagnostics;
using CS2MultiplayerMod.Core.Session;
using CS2MultiplayerMod.Game.Diagnostics;
using CS2MultiplayerMod.Game.Sync.Commands;
using CS2MultiplayerMod.Game.Sync.Infrastructure;
using Game;
using Game.City;
using Game.Economy;
using Game.Prefabs;
using Game.Simulation;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;

namespace CS2MultiplayerMod.Game.Sync.Systems
{
    /// <summary>
    /// Reasserts host service-accounting records around the native collectors. The collectors stay
    /// enabled because they also maintain unrelated service and budget state; only their redundant
    /// client-side accounting output is replaced.
    /// </summary>
    public sealed partial class ServiceAccountingCorrectionSystem : GameSystemBase
    {
        private PrefabIndex _prefabIndex;
        private CityServiceBudgetSystem _budgetSystem;
        private ServiceAccountingSnapshot _snapshot;
        private bool _warned;
        private bool _shapeWarned;

        protected override void OnCreate()
        {
            base.OnCreate();
            var prefabs = World.GetOrCreateSystemManaged<PrefabSystem>();
            EntityQuery prefabQuery = GetEntityQuery(ComponentType.ReadOnly<PrefabData>());
            _prefabIndex = new PrefabIndex(prefabs, prefabQuery);
            _budgetSystem = World.GetOrCreateSystemManaged<CityServiceBudgetSystem>();
        }

        internal void Install(ServiceAccountingSnapshot snapshot)
        {
            _snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
            CorrectAll();
        }

        internal void ClearSnapshot()
        {
            _snapshot = null;
            _warned = false;
            _shapeWarned = false;
        }

        internal void MaintainAuthority()
        {
            if (_snapshot != null && !IsAuthoritativeClient()) ClearSnapshot();
        }

        internal void ApplyBeforeBudgetCollection()
        {
            CorrectAll();
        }

        /// <summary>
        /// Discard locally produced fee events at both sides of the native fee collector and put
        /// the host's terminal fee records back. Every native fee producer shares this queue.
        /// </summary>
        internal void ApplyFeeBoundary(ServiceFeeSystem feeSystem)
        {
            if (!IsAuthoritativeClient() || feeSystem == null) return;
            try
            {
                JobHandle deps;
                NativeQueue<ServiceFeeSystem.FeeEvent> queue = feeSystem.GetFeeQueue(out deps);
                deps.Complete();
                queue.Clear();
                ApplyFeeRecords();
            }
            catch (Exception ex)
            {
                WarnOnce("fee-event boundary", ex);
            }
        }

        protected override void OnUpdate()
        {
            CorrectAll();
        }

        protected override void OnDestroy()
        {
            ClearSnapshot();
            base.OnDestroy();
        }

        private void CorrectAll()
        {
            if (!IsAuthoritativeClient()) return;
            try
            {
                ApplyServiceRecords();
                ApplyAccountingArrays();
            }
            catch (Exception ex)
            {
                WarnOnce("correction", ex);
            }
        }

        private bool IsAuthoritativeClient()
        {
            if (_snapshot == null) return false;
            MultiplayerService service = Mod.Service;
            return service != null && service.GameplaySyncReady &&
                   service.Session.Role == SessionRole.Client;
        }

        private void ApplyServiceRecords()
        {
            for (int i = 0; i < _snapshot.Services.Count; i++)
            {
                ServiceAccountingService wanted = _snapshot.Services[i];
                Entity service;
                if (!_prefabIndex.TryResolve(wanted.PrefabName, IsServicePrefab, out service))
                {
                    WarnShapeOnce("could not resolve service prefab '" + wanted.PrefabName + "'");
                    continue;
                }

                ApplyBudgetRecord(service, wanted);
                ApplyUpkeepRecords(service, wanted);
                ApplyFeeRecords(service, wanted);
            }
        }

        private void ApplyFeeRecords()
        {
            for (int i = 0; i < _snapshot.Services.Count; i++)
            {
                ServiceAccountingService wanted = _snapshot.Services[i];
                Entity service;
                if (_prefabIndex.TryResolve(wanted.PrefabName, IsServicePrefab, out service))
                    ApplyFeeRecords(service, wanted);
            }
        }

        private bool IsServicePrefab(Entity entity) =>
            EntityManager.HasComponent<ServiceData>(entity) &&
            EntityManager.HasComponent<CollectedCityServiceBudgetData>(entity) &&
            EntityManager.HasBuffer<CollectedCityServiceUpkeepData>(entity);

        private void ApplyBudgetRecord(Entity entity, ServiceAccountingService wanted)
        {
            var value = new CollectedCityServiceBudgetData
            {
                m_Workplaces = new int3(wanted.WorkplacesX, wanted.WorkplacesY,
                    wanted.WorkplacesZ),
                m_Count = wanted.Count,
                m_Export = wanted.Export,
                m_BaseCost = wanted.BaseCost,
                m_Wages = wanted.Wages,
                m_FullWages = wanted.FullWages,
            };
            CollectedCityServiceBudgetData current =
                EntityManager.GetComponentData<CollectedCityServiceBudgetData>(entity);
            if (!BudgetEquals(current, value)) EntityManager.SetComponentData(entity, value);
        }

        private void ApplyUpkeepRecords(Entity entity, ServiceAccountingService wanted)
        {
            DynamicBuffer<CollectedCityServiceUpkeepData> buffer =
                EntityManager.GetBuffer<CollectedCityServiceUpkeepData>(entity);
            bool equal = buffer.Length == wanted.Upkeeps.Length;
            if (equal)
            {
                for (int i = 0; i < buffer.Length; i++)
                {
                    CollectedCityServiceUpkeepData current = buffer[i];
                    ServiceAccountingUpkeep target = wanted.Upkeeps[i];
                    if (unchecked((long)(ulong)current.m_Resource) == target.Resource &&
                        current.m_FullCost == target.FullCost &&
                        current.m_Amount == target.Amount && current.m_Cost == target.Cost)
                        continue;
                    equal = false;
                    break;
                }
            }
            if (equal) return;

            buffer.ResizeUninitialized(wanted.Upkeeps.Length);
            for (int i = 0; i < wanted.Upkeeps.Length; i++)
            {
                ServiceAccountingUpkeep target = wanted.Upkeeps[i];
                buffer[i] = new CollectedCityServiceUpkeepData
                {
                    m_Resource = (Resource)unchecked((ulong)target.Resource),
                    m_FullCost = target.FullCost,
                    m_Amount = target.Amount,
                    m_Cost = target.Cost,
                };
            }
        }

        private void ApplyFeeRecords(Entity entity, ServiceAccountingService wanted)
        {
            bool hasBuffer = EntityManager.HasBuffer<CollectedCityServiceFeeData>(entity);
            if (!hasBuffer)
            {
                if (wanted.Fees.Length != 0)
                    WarnShapeOnce("service prefab '" + wanted.PrefabName +
                                  "' has no local fee aggregate buffer");
                return;
            }

            DynamicBuffer<CollectedCityServiceFeeData> buffer =
                EntityManager.GetBuffer<CollectedCityServiceFeeData>(entity);
            if (buffer.Length != wanted.Fees.Length)
            {
                WarnShapeOnce("service prefab '" + wanted.PrefabName +
                              "' has a different fee aggregate layout");
                return;
            }

            // Verify the whole key table before writing any value. The layout is prefab-owned,
            // so a mismatch means this record must be left untouched rather than half-corrected.
            for (int i = 0; i < wanted.Fees.Length; i++)
            {
                ServiceAccountingFee target = wanted.Fees[i];
                if (FindFee(buffer, target.PlayerResource) < 0)
                {
                    WarnShapeOnce("service prefab '" + wanted.PrefabName +
                                  "' is missing fee resource " + target.PlayerResource);
                    return;
                }
            }

            for (int i = 0; i < wanted.Fees.Length; i++)
            {
                ServiceAccountingFee target = wanted.Fees[i];
                int localIndex = FindFee(buffer, target.PlayerResource);
                CollectedCityServiceFeeData current = buffer[localIndex];
                if (FeeEquals(current, target)) continue;
                current.m_Export = target.Export;
                current.m_Import = target.Import;
                current.m_Internal = target.Internal;
                current.m_ExportCount = target.ExportCount;
                current.m_ImportCount = target.ImportCount;
                current.m_InternalCount = target.InternalCount;
                buffer[localIndex] = current;
            }
        }

        private void ApplyAccountingArrays()
        {
            JobHandle incomeDeps;
            JobHandle expenseDeps;
            NativeArray<int> incomes = _budgetSystem.GetIncomeArray(out incomeDeps);
            NativeArray<int> expenses = _budgetSystem.GetExpenseArray(out expenseDeps);
            JobHandle.CombineDependencies(incomeDeps, expenseDeps).Complete();

            for (int i = 0; i < ServiceAccountingSnapshot.FeeIncomeSources.Length; i++)
            {
                int source = ServiceAccountingSnapshot.FeeIncomeSources[i];
                int index = source;
                if (index >= 0 && index < incomes.Length)
                    incomes[index] = _snapshot.GetIncome(source);
            }
            for (int i = 0;
                 i < ServiceAccountingSnapshot.FeeAndUpkeepExpenseSources.Length; i++)
            {
                int source =
                    ServiceAccountingSnapshot.FeeAndUpkeepExpenseSources[i];
                int index = source;
                if (index >= 0 && index < expenses.Length)
                    expenses[index] = _snapshot.GetExpense(source);
            }
        }

        private static int FindFee(DynamicBuffer<CollectedCityServiceFeeData> buffer,
            int resource)
        {
            for (int i = 0; i < buffer.Length; i++)
                if (buffer[i].m_PlayerResource == resource) return i;
            return -1;
        }

        private static bool BudgetEquals(CollectedCityServiceBudgetData left,
            CollectedCityServiceBudgetData right) =>
            left.m_Workplaces.Equals(right.m_Workplaces) && left.m_Count == right.m_Count &&
            left.m_Export == right.m_Export && left.m_BaseCost == right.m_BaseCost &&
            left.m_Wages == right.m_Wages && left.m_FullWages == right.m_FullWages;

        private static bool FeeEquals(CollectedCityServiceFeeData current,
            ServiceAccountingFee wanted) =>
            current.m_PlayerResource == wanted.PlayerResource &&
            current.m_Export.Equals(wanted.Export) &&
            current.m_Import.Equals(wanted.Import) &&
            current.m_Internal.Equals(wanted.Internal) &&
            current.m_ExportCount.Equals(wanted.ExportCount) &&
            current.m_ImportCount.Equals(wanted.ImportCount) &&
            current.m_InternalCount.Equals(wanted.InternalCount);

        private void WarnOnce(string stage, Exception ex)
        {
            if (_warned) return;
            _warned = true;
            SyncLog.Warn(LogTopic.City, "ServiceAccounting: " + stage +
                " failed (logged once): " + ex.Message);
        }

        private void WarnShapeOnce(string detail)
        {
            if (_shapeWarned) return;
            _shapeWarned = true;
            SyncLog.Warn(LogTopic.City, "ServiceAccounting: " + detail +
                "; that service record was not corrected (logged once).");
        }
    }

    /// <summary>Installs the host records immediately before the native budget collector reads.</summary>
    public sealed partial class ServiceAccountingInputSystem : GameSystemBase
    {
        private ServiceAccountingCorrectionSystem _correction;

        protected override void OnCreate()
        {
            base.OnCreate();
            _correction = World.GetOrCreateSystemManaged<ServiceAccountingCorrectionSystem>();
        }

        protected override void OnUpdate()
        {
            if (_correction != null) _correction.ApplyBeforeBudgetCollection();
        }
    }

    /// <summary>Clears fee events produced before the native collector's own update.</summary>
    public sealed partial class ServiceFeeIngressBoundarySystem : GameSystemBase
    {
        private ServiceAccountingCorrectionSystem _correction;
        private ServiceFeeSystem _fees;

        protected override void OnCreate()
        {
            base.OnCreate();
            _correction = World.GetOrCreateSystemManaged<ServiceAccountingCorrectionSystem>();
            _fees = World.GetOrCreateSystemManaged<ServiceFeeSystem>();
        }

        protected override void OnUpdate()
        {
            if (_correction != null) _correction.ApplyFeeBoundary(_fees);
        }
    }

    /// <summary>Clears trade and utility fee events produced after the native collector.</summary>
    public sealed partial class ServiceFeeEgressBoundarySystem : GameSystemBase
    {
        private ServiceAccountingCorrectionSystem _correction;
        private ServiceFeeSystem _fees;

        protected override void OnCreate()
        {
            base.OnCreate();
            _correction = World.GetOrCreateSystemManaged<ServiceAccountingCorrectionSystem>();
            _fees = World.GetOrCreateSystemManaged<ServiceFeeSystem>();
        }

        protected override void OnUpdate()
        {
            if (_correction != null) _correction.ApplyFeeBoundary(_fees);
        }
    }
}
