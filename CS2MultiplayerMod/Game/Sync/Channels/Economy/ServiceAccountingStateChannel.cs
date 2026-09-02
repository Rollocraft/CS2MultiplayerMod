using System;
using System.Collections.Generic;
using CS2MultiplayerMod.Core.Diagnostics;
using CS2MultiplayerMod.Core.Protocol;
using CS2MultiplayerMod.Game.Diagnostics;
using CS2MultiplayerMod.Game.Sync.Commands;
using CS2MultiplayerMod.Game.Sync.Infrastructure;
using CS2MultiplayerMod.Game.Sync.Systems;
using Game.Prefabs;
using Game.Simulation;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;

namespace CS2MultiplayerMod.Game.Sync.Channels
{
    /// <summary>
    /// Host-authoritative fee results and service upkeep aggregates. Fee slider values remain in
    /// the editable channel 8; this channel carries the simulation-owned counts, income/expense
    /// results and per-service records that the economy and service-detail panels actually read.
    /// </summary>
    internal sealed class ServiceAccountingStateChannel : IStateChannel, IPumpedStateChannel
    {
        public const byte Id = 24;
        public byte ChannelId => Id;

        private readonly ServiceAccountingCorrectionSystem _correction;
        private EntityQuery _services;
        private PrefabSystem _prefabs;
        private CityServiceBudgetSystem _budgets;
        private bool _ready;
        private bool _captureWarned;

        internal ServiceAccountingStateChannel(ServiceAccountingCorrectionSystem correction)
        {
            _correction = correction;
        }

        private void Ensure(EntityManager em)
        {
            if (_ready) return;
            _services = em.CreateEntityQuery(
                ComponentType.ReadOnly<PrefabData>(),
                ComponentType.ReadOnly<ServiceData>(),
                ComponentType.ReadOnly<CollectedCityServiceBudgetData>(),
                ComponentType.ReadOnly<CollectedCityServiceUpkeepData>());
            _prefabs = em.World.GetOrCreateSystemManaged<PrefabSystem>();
            _budgets = em.World.GetOrCreateSystemManaged<CityServiceBudgetSystem>();
            _ready = true;
        }

        public bool Capture(EntityManager em, NetworkWriter writer)
        {
            Ensure(em);
            try
            {
                var snapshot = new ServiceAccountingSnapshot();
                NativeArray<Entity> entities = _services.ToEntityArray(Allocator.Temp);
                var names = new HashSet<string>(StringComparer.Ordinal);
                try
                {
                    if (entities.Length > ServiceAccountingSnapshot.MaxServices)
                        throw new InvalidOperationException(
                            "Service prefab count exceeds the accounting channel cap.");

                    for (int i = 0; i < entities.Length; i++)
                    {
                        Entity entity = entities[i];
                        string name = PrefabIndex.SafeName(_prefabs, entity);
                        if (string.IsNullOrEmpty(name) || !names.Add(name))
                            throw new InvalidOperationException(
                                "Service accounting encountered a missing or duplicate prefab name.");

                        CollectedCityServiceBudgetData budget =
                            em.GetComponentData<CollectedCityServiceBudgetData>(entity);
                        var service = new ServiceAccountingService
                        {
                            PrefabName = name,
                            WorkplacesX = budget.m_Workplaces.x,
                            WorkplacesY = budget.m_Workplaces.y,
                            WorkplacesZ = budget.m_Workplaces.z,
                            Count = budget.m_Count,
                            Export = budget.m_Export,
                            BaseCost = budget.m_BaseCost,
                            Wages = budget.m_Wages,
                            FullWages = budget.m_FullWages,
                        };

                        if (em.HasBuffer<CollectedCityServiceFeeData>(entity))
                        {
                            DynamicBuffer<CollectedCityServiceFeeData> fees =
                                em.GetBuffer<CollectedCityServiceFeeData>(entity, true);
                            if (fees.Length > ServiceAccountingSnapshot.MaxFeesPerService)
                                throw new InvalidOperationException(
                                    "Service fee aggregate count exceeds its cap.");
                            service.Fees = new ServiceAccountingFee[fees.Length];
                            for (int f = 0; f < fees.Length; f++)
                            {
                                CollectedCityServiceFeeData fee = fees[f];
                                service.Fees[f] = new ServiceAccountingFee
                                {
                                    PlayerResource = fee.m_PlayerResource,
                                    Export = fee.m_Export,
                                    Import = fee.m_Import,
                                    Internal = fee.m_Internal,
                                    ExportCount = fee.m_ExportCount,
                                    ImportCount = fee.m_ImportCount,
                                    InternalCount = fee.m_InternalCount,
                                };
                            }
                            Array.Sort(service.Fees, (left, right) =>
                                left.PlayerResource.CompareTo(right.PlayerResource));
                        }

                        DynamicBuffer<CollectedCityServiceUpkeepData> upkeeps =
                            em.GetBuffer<CollectedCityServiceUpkeepData>(entity, true);
                        if (upkeeps.Length > ServiceAccountingSnapshot.MaxUpkeepsPerService)
                            throw new InvalidOperationException(
                                "Service upkeep aggregate count exceeds its cap.");
                        service.Upkeeps = new ServiceAccountingUpkeep[upkeeps.Length];
                        for (int u = 0; u < upkeeps.Length; u++)
                        {
                            CollectedCityServiceUpkeepData upkeep = upkeeps[u];
                            service.Upkeeps[u] = new ServiceAccountingUpkeep
                            {
                                Resource = unchecked((long)(ulong)upkeep.m_Resource),
                                FullCost = upkeep.m_FullCost,
                                Amount = upkeep.m_Amount,
                                Cost = upkeep.m_Cost,
                            };
                        }
                        Array.Sort(service.Upkeeps, (left, right) =>
                            left.Resource.CompareTo(right.Resource));
                        snapshot.Services.Add(service);
                    }
                }
                finally
                {
                    entities.Dispose();
                }

                snapshot.Services.Sort((left, right) =>
                    string.CompareOrdinal(left.PrefabName, right.PrefabName));

                JobHandle incomeDeps;
                JobHandle expenseDeps;
                NativeArray<int> incomes = _budgets.GetIncomeArray(out incomeDeps);
                NativeArray<int> expenses = _budgets.GetExpenseArray(out expenseDeps);
                JobHandle.CombineDependencies(incomeDeps, expenseDeps).Complete();
                for (int i = 0; i < ServiceAccountingSnapshot.FeeIncomeSources.Length; i++)
                {
                    var source = ServiceAccountingSnapshot.FeeIncomeSources[i];
                    int index = (int)source;
                    if (index < 0 || index >= incomes.Length)
                        throw new InvalidOperationException("Native service-income table is incomplete.");
                    snapshot.SetIncome(source, incomes[index]);
                }
                for (int i = 0;
                     i < ServiceAccountingSnapshot.FeeAndUpkeepExpenseSources.Length; i++)
                {
                    var source = ServiceAccountingSnapshot.FeeAndUpkeepExpenseSources[i];
                    int index = (int)source;
                    if (index < 0 || index >= expenses.Length)
                        throw new InvalidOperationException("Native service-expense table is incomplete.");
                    snapshot.SetExpense(source, expenses[index]);
                }

                snapshot.Write(writer);
                return true;
            }
            catch (Exception ex)
            {
                if (!_captureWarned)
                {
                    _captureWarned = true;
                    SyncLog.Warn(LogTopic.City,
                        "ServiceAccounting: capture failed; channel skipped (logged once): " +
                        ex.Message);
                }
                return false;
            }
        }

        public void Apply(EntityManager em, NetworkReader reader)
        {
            Ensure(em);
            ServiceAccountingSnapshot snapshot = ServiceAccountingSnapshot.Read(reader);
            ValidateLocalShape(em, snapshot);
            if (_correction == null)
                throw new InvalidOperationException(
                    "Service-accounting correction boundary is unavailable.");
            _correction.Install(snapshot);
        }

        private void ValidateLocalShape(EntityManager em,
            ServiceAccountingSnapshot snapshot)
        {
            var wantedByName = new Dictionary<string, ServiceAccountingService>(
                snapshot.Services.Count, StringComparer.Ordinal);
            for (int i = 0; i < snapshot.Services.Count; i++)
                wantedByName.Add(snapshot.Services[i].PrefabName, snapshot.Services[i]);

            NativeArray<Entity> entities = _services.ToEntityArray(Allocator.Temp);
            try
            {
                if (entities.Length != snapshot.Services.Count)
                    throw new ProtocolException(
                        "Service-accounting prefab table differs from this game build.");

                var localNames = new HashSet<string>(StringComparer.Ordinal);
                for (int i = 0; i < entities.Length; i++)
                {
                    Entity entity = entities[i];
                    string name = PrefabIndex.SafeName(_prefabs, entity);
                    ServiceAccountingService wanted;
                    if (string.IsNullOrEmpty(name) || !localNames.Add(name) ||
                        !wantedByName.TryGetValue(name, out wanted))
                        throw new ProtocolException(
                            "Service-accounting prefab table differs from this game build.");

                    int localFeeCount = em.HasBuffer<CollectedCityServiceFeeData>(entity)
                        ? em.GetBuffer<CollectedCityServiceFeeData>(entity, true).Length
                        : 0;
                    if (localFeeCount != wanted.Fees.Length)
                        throw new ProtocolException(
                            "Service-accounting fee layout differs from this game build.");

                    if (localFeeCount == 0) continue;
                    DynamicBuffer<CollectedCityServiceFeeData> localFees =
                        em.GetBuffer<CollectedCityServiceFeeData>(entity, true);
                    var resources = new HashSet<int>();
                    for (int f = 0; f < localFees.Length; f++)
                    {
                        int resource = localFees[f].m_PlayerResource;
                        if (!resources.Add(resource) || !ContainsFee(wanted.Fees, resource))
                            throw new ProtocolException(
                                "Service-accounting fee layout differs from this game build.");
                    }
                }
            }
            finally
            {
                entities.Dispose();
            }
        }

        private static bool ContainsFee(ServiceAccountingFee[] fees, int resource)
        {
            for (int i = 0; i < fees.Length; i++)
                if (fees[i].PlayerResource == resource) return true;
            return false;
        }

        public void Pump(EntityManager em)
        {
            if (_correction != null) _correction.MaintainAuthority();
        }

        public void ResetPending()
        {
            _captureWarned = false;
            if (_correction != null) _correction.ClearSnapshot();
        }
    }
}
