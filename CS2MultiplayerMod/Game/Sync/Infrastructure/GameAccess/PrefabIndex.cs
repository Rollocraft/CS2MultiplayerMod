using System;
using System.Collections.Generic;
using CS2MultiplayerMod.Core.Diagnostics;
using CS2MultiplayerMod.Game.Diagnostics;
using Game.Prefabs;
using Unity.Collections;
using Unity.Entities;

namespace CS2MultiplayerMod.Game.Sync.Infrastructure
{
    /// <summary>
    /// Prefab name to local prefab entity (indices differ per machine). Built lazily, rebuilt once on a
    /// miss since prefabs can load late.
    /// </summary>
    public sealed class PrefabIndex
    {
        private readonly PrefabSystem _prefabs;
        private readonly EntityQuery _allPrefabs;
        private readonly Dictionary<string, Entity> _byName = new Dictionary<string, Entity>();
        private readonly Dictionary<string, List<Entity>> _allByName =
            new Dictionary<string, List<Entity>>();
        // PrefabBase.name allocates on every native read; cache it while the table is valid.
        private readonly Dictionary<Entity, string> _namesByPrefab =
            new Dictionary<Entity, string>();
        private bool _built;
        private bool _warnedUnusable;
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

            // Rebuild only when the prefab table changed, or unknown names force a rescan per message.
            if (_allPrefabs.CalculateEntityCount() == _builtCount) return false;
            Build();
            return _byName.TryGetValue(name, out prefab);
        }

        /// <summary>Resolves within a category: collections can share a display name.</summary>
        public bool TryResolve(string name, Predicate<Entity> compatible, out Entity prefab)
        {
            if (!_built) Build();
            if (TryResolveBuilt(name, compatible, out prefab)) return true;

            if (_allPrefabs.CalculateEntityCount() == _builtCount) return false;
            Build();
            return TryResolveBuilt(name, compatible, out prefab);
        }

        /// <summary>
        /// Cached <see cref="SafeName(PrefabSystem, Entity)"/>; handles are versioned and misses are never
        /// cached.
        /// </summary>
        public string NameOf(Entity prefab)
        {
            if (prefab == Entity.Null) return null;
            if (_namesByPrefab.TryGetValue(prefab, out string name)) return name;
            name = SafeName(_prefabs, prefab);
            if (!string.IsNullOrEmpty(name)) _namesByPrefab[prefab] = name;
            return name;
        }

        /// <summary>
        /// The prefab's name, or null. A prefab entity can outlive its torn-down asset (after a mode switch),
        /// and reading <c>.name</c> then faults natively, so the asset is null-checked first.
        /// </summary>
        public static string SafeName(PrefabSystem prefabs, Entity prefab) =>
            SafeName(prefabs, prefab, out bool tornDown);

        /// <summary><paramref name="tornDown"/>: registered with a destroyed asset, the case that faults.</summary>
        public static string SafeName(PrefabSystem prefabs, Entity prefab, out bool tornDown)
        {
            tornDown = false;
            try
            {
                if (!prefabs.TryGetPrefab(prefab, out PrefabBase asset)) return null;
                if (asset != null) return asset.name;
                tornDown = true;
                return null;
            }
            catch (Exception)
            {
                tornDown = true;
                return null;
            }
        }

        private void Build()
        {
            _byName.Clear();
            _allByName.Clear();
            _namesByPrefab.Clear();
            NativeArray<Entity> prefabs = _allPrefabs.ToEntityArray(Allocator.Temp);
            try
            {
                _builtCount = prefabs.Length;
                int retired = 0, tornDownCount = 0, firstTornDown = -1;
                for (int i = 0; i < prefabs.Length; i++)
                {
                    string name = SafeName(_prefabs, prefabs[i], out bool tornDown);
                    if (string.IsNullOrEmpty(name))
                    {
                        if (tornDown)
                        {
                            if (firstTornDown < 0) firstTornDown = prefabs[i].Index;
                            tornDownCount++;
                        }
                        else retired++;
                        continue;
                    }
                    _byName[name] = prefabs[i];
                    _namesByPrefab[prefabs[i]] = name;
                    if (!_allByName.TryGetValue(name, out List<Entity> matches))
                    {
                        matches = new List<Entity>(1);
                        _allByName[name] = matches;
                    }
                    matches.Add(prefabs[i]);
                }

                if (tornDownCount > 0 && !_warnedUnusable)
                {
                    _warnedUnusable = true;
                    SyncLog.Warn(LogTopic.Pipeline, "PrefabIndex: " + tornDownCount + " of " +
                        prefabs.Length +
                        " catalogue entries still point at a torn-down asset (first entity " +
                        firstTornDown + "); skipped. " + retired + " more were retired normally.");
                }
            }
            finally
            {
                prefabs.Dispose();
            }
            _built = true;
        }

        private bool TryResolveBuilt(string name, Predicate<Entity> compatible,
            out Entity prefab)
        {
            prefab = Entity.Null;
            if (compatible == null) return _byName.TryGetValue(name, out prefab);
            if (!_allByName.TryGetValue(name, out List<Entity> matches)) return false;
            for (int i = 0; i < matches.Count; i++)
            {
                if (!compatible(matches[i])) continue;
                prefab = matches[i];
                return true;
            }
            return false;
        }
    }
}
