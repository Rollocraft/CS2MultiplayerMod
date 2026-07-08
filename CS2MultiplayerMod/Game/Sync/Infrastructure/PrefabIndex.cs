using System.Collections.Generic;
using Game.Prefabs;
using Unity.Collections;
using Unity.Entities;

namespace CS2MultiplayerMod.Game.Sync.Infrastructure
{
    /// <summary>
    /// Resolves a prefab's stable name back to its local prefab <see cref="Entity"/>.
    /// Prefab entity indices differ between machines, so placements travel by name and
    /// each receiver maps the name to its own prefab here. The name -> entity table is
    /// built lazily and rebuilt once on a miss (prefabs can load late).
    /// </summary>
    public sealed class PrefabIndex
    {
        private readonly PrefabSystem _prefabs;
        private readonly EntityQuery _allPrefabs;
        private readonly Dictionary<string, Entity> _byName = new Dictionary<string, Entity>();
        private bool _built;
        private int _builtCount = -1;

        public PrefabIndex(PrefabSystem prefabs, EntityQuery allPrefabs)
        {
            _prefabs = prefabs;
            _allPrefabs = allPrefabs;
        }

        public bool TryResolve(string name, out Entity prefab)
        {
            if (!_built) Build();
            if (_byName.TryGetValue(name, out prefab)) return true;

            // Late-loaded prefabs are the one legitimate reason for a miss; rebuild only
            // when the prefab table actually changed. Without this gate, a stream of
            // unknown names (a content mismatch between machines, or a hostile peer)
            // would force a full rescan of every prefab per message.
            if (_allPrefabs.CalculateEntityCount() == _builtCount) return false;
            Build();
            return _byName.TryGetValue(name, out prefab);
        }

        public string NameOf(Entity prefab) => _prefabs.GetPrefabName(prefab);

        private void Build()
        {
            _byName.Clear();
            NativeArray<Entity> prefabs = _allPrefabs.ToEntityArray(Allocator.Temp);
            try
            {
                _builtCount = prefabs.Length;
                for (int i = 0; i < prefabs.Length; i++)
                {
                    string name = _prefabs.GetPrefabName(prefabs[i]);
                    if (!string.IsNullOrEmpty(name)) _byName[name] = prefabs[i];
                }
            }
            finally
            {
                prefabs.Dispose();
            }
            _built = true;
        }
    }
}
