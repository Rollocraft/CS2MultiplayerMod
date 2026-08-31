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
        private readonly Dictionary<string, List<Entity>> _allByName =
            new Dictionary<string, List<Entity>>();
        // Reading PrefabBase.name is a native call that returns a freshly allocated string every
        // time. Capture paths ask for the same few thousand prefab names thousands of times a
        // second, so hold the answer for as long as the name -> entity table itself is valid.
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

            // Late-loaded prefabs are the one legitimate reason for a miss; rebuild only
            // when the prefab table actually changed. Without this gate, a stream of
            // unknown names (a content mismatch between machines, or a hostile peer)
            // would force a full rescan of every prefab per message.
            if (_allPrefabs.CalculateEntityCount() == _builtCount) return false;
            Build();
            return _byName.TryGetValue(name, out prefab);
        }

        /// <summary>
        /// Resolve a name to a prefab of the required category. Multiple prefab collections can
        /// expose the same display name; callers that know whether they need an object, net, area,
        /// or stamp must not depend on entity iteration order.
        /// </summary>
        public bool TryResolve(string name, Predicate<Entity> compatible, out Entity prefab)
        {
            if (!_built) Build();
            if (TryResolveBuilt(name, compatible, out prefab)) return true;

            if (_allPrefabs.CalculateEntityCount() == _builtCount) return false;
            Build();
            return TryResolveBuilt(name, compatible, out prefab);
        }

        /// <summary>
        /// The cached form of <see cref="SafeName(PrefabSystem, Entity)"/>. Entity handles carry a
        /// version, so a recycled prefab index cannot read another prefab's cached name, and the
        /// table is dropped whenever the catalogue is rebuilt. A miss is never cached: a name that
        /// could not be read is either a retired prefab or a torn-down asset, and both can change.
        /// </summary>
        public string NameOf(Entity prefab)
        {
            if (prefab == Entity.Null) return null;
            string name;
            if (_namesByPrefab.TryGetValue(prefab, out name)) return name;
            name = SafeName(_prefabs, prefab);
            if (!string.IsNullOrEmpty(name)) _namesByPrefab[prefab] = name;
            return name;
        }

        /// <summary>
        /// The prefab's name, or null when nothing usable stands behind the entity.
        /// The catalogue outlives its assets: switching game mode (editor, map, main menu)
        /// tears down content that no world entity holds, and the prefab entity survives
        /// pointing at an asset that is already gone. Reading the name off one of those
        /// faults inside the engine, so ask whether the asset is alive first - the null
        /// test sees a torn-down asset, the name property does not.
        /// </summary>
        public static string SafeName(PrefabSystem prefabs, Entity prefab)
        {
            bool tornDown;
            return SafeName(prefabs, prefab, out tornDown);
        }

        /// <summary>
        /// <paramref name="tornDown"/> separates the two ways a name can be missing: a prefab
        /// the game retired properly (harmless, it is simply gone) versus one still registered
        /// with a destroyed asset behind it - only the second faults on <c>.name</c>.
        /// </summary>
        public static string SafeName(PrefabSystem prefabs, Entity prefab, out bool tornDown)
        {
            tornDown = false;
            try
            {
                PrefabBase asset;
                if (!prefabs.TryGetPrefab(prefab, out asset)) return null;
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
                    bool tornDown;
                    string name = SafeName(_prefabs, prefabs[i], out tornDown);
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
                    List<Entity> matches;
                    if (!_allByName.TryGetValue(name, out matches))
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
            List<Entity> matches;
            if (!_allByName.TryGetValue(name, out matches)) return false;
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
