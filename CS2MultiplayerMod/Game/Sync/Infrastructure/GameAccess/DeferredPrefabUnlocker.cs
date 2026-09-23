using System.Collections.Generic;
using Game;
using Game.Prefabs;
using Unity.Entities;

namespace CS2MultiplayerMod.Game.Sync.Infrastructure
{
    /// <summary>Queues unlocks where the game's unlock pipeline consumes them and cascades requirements.</summary>
    internal sealed class DeferredPrefabUnlocker
    {
        private readonly EntityManager _entities;
        private readonly EndFrameBarrier _barrier;
        private readonly EntityArchetype _eventArchetype;
        private readonly HashSet<Entity> _pending = new HashSet<Entity>();
        private readonly List<Entity> _completed = new List<Entity>();

        public DeferredPrefabUnlocker(EntityManager entities)
        {
            _entities = entities;
            _barrier = entities.World.GetOrCreateSystemManaged<EndFrameBarrier>();
            _eventArchetype = entities.CreateArchetype(
                ComponentType.ReadWrite<global::Game.Common.Event>(),
                ComponentType.ReadWrite<Unlock>());
        }

        /// <summary>Never queues a prefab twice, so a one-time charge can follow a true return.</summary>
        public bool TryQueue(Entity prefab)
        {
            if (!IsLocked(prefab))
            {
                _pending.Remove(prefab);
                return false;
            }
            if (!_pending.Add(prefab)) return false;

            try
            {
                EntityCommandBuffer ecb = _barrier.CreateCommandBuffer();
                Entity unlockEvent = ecb.CreateEntity(_eventArchetype);
                ecb.SetComponent(unlockEvent, new Unlock(prefab));
                return true;
            }
            catch
            {
                _pending.Remove(prefab);
                throw;
            }
        }

        /// <summary>Release requests after the barrier's unlock has become visible.</summary>
        public void PruneCompleted()
        {
            _completed.Clear();
            foreach (Entity prefab in _pending)
            {
                if (!IsLocked(prefab)) _completed.Add(prefab);
            }
            for (int i = 0; i < _completed.Count; i++) _pending.Remove(_completed[i]);
            _completed.Clear();
        }

        /// <summary>Forget requests belonging to the city/session that just ended.</summary>
        public void Reset()
        {
            _pending.Clear();
            _completed.Clear();
        }

        private bool IsLocked(Entity prefab)
        {
            return prefab != Entity.Null && _entities.Exists(prefab) &&
                   _entities.HasComponent<Locked>(prefab) &&
                   _entities.IsComponentEnabled<Locked>(prefab);
        }
    }
}
