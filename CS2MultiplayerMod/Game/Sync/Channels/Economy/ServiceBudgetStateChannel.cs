using System.Collections.Generic;
using Game.Prefabs;
using Game.Simulation;
using Unity.Collections;
using Unity.Entities;
using CS2MultiplayerMod.Core.Protocol;

using CS2MultiplayerMod.Game.Sync.Infrastructure;
namespace CS2MultiplayerMod.Game.Sync.Channels
{
    /// <summary>
    /// Per-service budget sliders, set through <see cref="CityServiceBudgetSystem.SetServiceBudget"/> and
    /// keyed by service prefab name. Editable; sorted by name so the diff in
    /// <see cref="CityStateSyncSystem"/> is order-independent.
    /// </summary>
    public sealed class ServiceBudgetStateChannel : IStateChannel
    {
        public const byte Id = 15;
        public byte ChannelId => Id;

        private EntityQuery _query;
        private PrefabSystem _prefabSystem;
        private CityServiceBudgetSystem _budgetSystem;
        private bool _ready;

        private void Ensure(EntityManager em)
        {
            if (_ready) return;
            _query = em.CreateEntityQuery(ComponentType.ReadOnly<ServiceBudgetData>());
            _prefabSystem = em.World.GetOrCreateSystemManaged<PrefabSystem>();
            _budgetSystem = em.World.GetOrCreateSystemManaged<CityServiceBudgetSystem>();
            _ready = true;
        }

        public bool Capture(EntityManager em, NetworkWriter writer)
        {
            Ensure(em);
            if (_query.CalculateEntityCount() == 0) return false;

            DynamicBuffer<ServiceBudgetData> budgets = em.GetBuffer<ServiceBudgetData>(_query.GetSingletonEntity(), true);
            var entries = new List<(string name, int budget)>(budgets.Length);
            for (int i = 0; i < budgets.Length; i++)
            {
                string name = _prefabSystem.GetPrefabName(budgets[i].m_Service);
                if (string.IsNullOrEmpty(name)) continue;
                entries.Add((name, budgets[i].m_Budget));
            }
            entries.Sort((a, b) => string.CompareOrdinal(a.name, b.name));

            writer.WriteShort((short)entries.Count);
            for (int i = 0; i < entries.Count; i++)
            {
                writer.WriteString(entries[i].name);
                writer.WriteInt(entries[i].budget);
            }
            return true;
        }

        public void Apply(EntityManager em, NetworkReader reader)
        {
            Ensure(em);
            int count = reader.ReadShort();
            if (count < 0) throw new ProtocolException("Negative service-budget count: " + count + ".");
            var wanted = new (string name, int budget)[count];
            for (int i = 0; i < count; i++)
                wanted[i] = (reader.ReadString(), reader.ReadInt());

            if (_query.CalculateEntityCount() == 0) return;

            for (int i = 0; i < count; i++)
            {
                Entity service = ResolveService(em, wanted[i].name);
                if (service == Entity.Null) continue;
                if (_budgetSystem.GetServiceBudget(service) != wanted[i].budget)
                    _budgetSystem.SetServiceBudget(service, wanted[i].budget);
            }
        }

        private EntityQuery _prefabQuery;
        private bool _prefabQueryReady;
        private readonly Dictionary<string, Entity> _serviceByName = new Dictionary<string, Entity>();

        private Entity ResolveService(EntityManager em, string name)
        {
            if (_serviceByName.TryGetValue(name, out Entity entity)) return entity;

            // Cheap path: the budget buffer already references the service prefab entities.
            DynamicBuffer<ServiceBudgetData> budgets = em.GetBuffer<ServiceBudgetData>(_query.GetSingletonEntity(), true);
            for (int i = 0; i < budgets.Length; i++)
            {
                string candidate = _prefabSystem.GetPrefabName(budgets[i].m_Service);
                if (!string.IsNullOrEmpty(candidate)) _serviceByName[candidate] = budgets[i].m_Service;
            }
            if (_serviceByName.TryGetValue(name, out entity)) return entity;

            // A never-adjusted service is not in the buffer; look up real service prefabs only.
            if (!_prefabQueryReady)
            {
                _prefabQuery = em.CreateEntityQuery(
                    ComponentType.ReadOnly<PrefabData>(),
                    ComponentType.ReadOnly<ServiceData>());
                _prefabQueryReady = true;
            }
            NativeArray<Entity> prefabs = _prefabQuery.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < prefabs.Length; i++)
                {
                    string candidate = PrefabIndex.SafeName(_prefabSystem, prefabs[i]);
                    if (!string.IsNullOrEmpty(candidate) && !_serviceByName.ContainsKey(candidate))
                        _serviceByName[candidate] = prefabs[i];
                }
            }
            finally { prefabs.Dispose(); }

            return _serviceByName.TryGetValue(name, out entity) ? entity : Entity.Null;
        }
    }
}
