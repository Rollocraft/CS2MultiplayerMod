using System.Collections.Generic;
using Game.City;
using Game.Policies;
using Game.Prefabs;
using Unity.Collections;
using Unity.Entities;
using CS2MultiplayerMod.Core.Protocol;

using CS2MultiplayerMod.Game.Sync.Infrastructure;
namespace CS2MultiplayerMod.Game.Sync.Channels
{
    /// <summary>
    /// Replicates the city-wide policy list (the <see cref="Policy"/> buffer on the city
    /// entity). Policies are identified by prefab name; flags + slider adjustment travel
    /// along. Player-editable - any player may toggle policies; the host arbitrates.
    /// District and transport-line policies are a follow-up (needs a cross-machine
    /// district identity first).
    /// </summary>
    public sealed class CityPolicyStateChannel : IStateChannel
    {
        public const byte Id = 7;
        public byte ChannelId => Id;

        private EntityQuery _cityQuery;
        private PrefabSystem _prefabSystem;
        private bool _ready;

        private void Ensure(EntityManager em)
        {
            if (_ready) return;
            // PlayerMoney is a known singleton on the city entity (same trick as MoneyStateChannel).
            _cityQuery = em.CreateEntityQuery(ComponentType.ReadWrite<PlayerMoney>());
            _prefabSystem = em.World.GetOrCreateSystemManaged<PrefabSystem>();
            _ready = true;
        }

        public bool Capture(EntityManager em, NetworkWriter writer)
        {
            Ensure(em);
            if (_cityQuery.CalculateEntityCount() == 0) return false;
            Entity city = _cityQuery.GetSingletonEntity();
            if (!em.HasBuffer<Policy>(city)) return false;

            DynamicBuffer<Policy> policies = em.GetBuffer<Policy>(city, true);
            var entries = new List<(string name, ushort flags, float adjustment)>();
            for (int i = 0; i < policies.Length; i++)
            {
                string name = _prefabSystem.GetPrefabName(policies[i].m_Policy);
                if (string.IsNullOrEmpty(name)) continue;
                entries.Add((name, (ushort)policies[i].m_Flags, policies[i].m_Adjustment));
            }

            writer.WriteShort((short)entries.Count);
            for (int i = 0; i < entries.Count; i++)
            {
                writer.WriteString(entries[i].name);
                writer.WriteShort((short)entries[i].flags);
                writer.WriteFloat(entries[i].adjustment);
            }
            return true;
        }

        public void Apply(EntityManager em, NetworkReader reader)
        {
            Ensure(em);
            int count = reader.ReadShort();
            if (count < 0) throw new ProtocolException("Negative policy count: " + count + ".");
            var wanted = new List<(string name, ushort flags, float adjustment)>(count);
            for (int i = 0; i < count; i++)
            {
                string name = reader.ReadString();
                ushort flags = (ushort)reader.ReadShort();
                float adjustment = reader.ReadFloat();
                wanted.Add((name, flags, adjustment));
            }

            if (_cityQuery.CalculateEntityCount() == 0) return;
            Entity city = _cityQuery.GetSingletonEntity();

            // Resolve names -> local prefab entities; skip unknown (mod mismatch).
            var resolved = new List<Policy>(wanted.Count);
            foreach (var entry in wanted)
            {
                Entity prefab = ResolvePolicyPrefab(em, entry.name);
                if (prefab == Entity.Null) continue;
                resolved.Add(new Policy
                {
                    m_Policy = prefab,
                    m_Flags = (PolicyFlags)entry.flags,
                    m_Adjustment = entry.adjustment,
                });
            }

            DynamicBuffer<Policy> buffer = em.HasBuffer<Policy>(city)
                ? em.GetBuffer<Policy>(city)
                : em.AddBuffer<Policy>(city);

            // Skip the write (and the Updated churn) when nothing differs.
            bool same = buffer.Length == resolved.Count;
            for (int i = 0; same && i < resolved.Count; i++)
                same = buffer[i].m_Policy == resolved[i].m_Policy &&
                       buffer[i].m_Flags == resolved[i].m_Flags &&
                       buffer[i].m_Adjustment.Equals(resolved[i].m_Adjustment);
            if (same) return;

            buffer.Clear();
            for (int i = 0; i < resolved.Count; i++) buffer.Add(resolved[i]);
            em.AddComponent<global::Game.Common.Updated>(city);
        }

        private EntityQuery _policyPrefabs;
        private bool _policyQueryReady;
        private readonly Dictionary<string, Entity> _policyByName = new Dictionary<string, Entity>();

        private Entity ResolvePolicyPrefab(EntityManager em, string name)
        {
            Entity entity;
            if (_policyByName.TryGetValue(name, out entity)) return entity;

            if (!_policyQueryReady)
            {
                _policyPrefabs = em.CreateEntityQuery(ComponentType.ReadOnly<PrefabData>());
                _policyQueryReady = true;
            }

            // Lazy full index: one scan caches every prefab name we encounter.
            NativeArray<Entity> prefabs = _policyPrefabs.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < prefabs.Length; i++)
                {
                    string candidate = _prefabSystem.GetPrefabName(prefabs[i]);
                    if (!string.IsNullOrEmpty(candidate) && !_policyByName.ContainsKey(candidate))
                        _policyByName[candidate] = prefabs[i];
                }
            }
            finally
            {
                prefabs.Dispose();
            }

            return _policyByName.TryGetValue(name, out entity) ? entity : Entity.Null;
        }
    }
}
