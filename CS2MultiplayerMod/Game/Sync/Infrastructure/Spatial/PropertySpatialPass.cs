using System;
using System.Collections.Generic;
using Unity.Entities;

namespace CS2MultiplayerMod.Game.Sync.Infrastructure
{
    /// <summary>Property metadata shared within one read-only channel run only.</summary>
    internal sealed class PropertySpatialPass : IDisposable
    {
        internal static PropertySpatialPass Current { get; private set; }
        private readonly Dictionary<Entity, PropertyEntitySnapshot> _properties =
            new Dictionary<Entity, PropertyEntitySnapshot>();
        private readonly Dictionary<global::Game.Objects.SearchSystem, ObjectSearch.Batch> _batches =
            new Dictionary<global::Game.Objects.SearchSystem, ObjectSearch.Batch>();
        private EntityManager _manager;

        public PropertySpatialPass Begin(EntityManager manager)
        {
            if (Current != null) throw new InvalidOperationException("Nested property spatial pass.");
            _manager = manager;
            Current = this;
            return this;
        }

        public void Invalidate()
        {
            _properties.Clear();
            _batches.Clear();
        }

        internal bool TryRead(EntityManager manager, Entity entity, out PropertyEntitySnapshot snapshot)
        {
            if (!manager.Equals(_manager))
                return PropertyEntitySnapshot.TryRead(manager, entity, out snapshot);
            if (_properties.TryGetValue(entity, out snapshot)) return true;
            if (!PropertyEntitySnapshot.TryRead(manager, entity, out snapshot)) return false;
            if (_properties.Count < 4096) _properties.Add(entity, snapshot);
            return true;
        }

        internal ObjectSearch.Batch Acquire(ObjectSearch search)
        {
            if (!_batches.TryGetValue(search.System, out ObjectSearch.Batch batch))
            {
                batch = search.BeginBatch();
                _batches.Add(search.System, batch);
            }
            return batch;
        }

        public void Dispose()
        {
            Invalidate();
            Current = null;
        }
    }

    /// <summary>The pump only reads property ECS state; structural work runs in simulation.</summary>
    internal interface IPropertyStateChannel : IPumpedStateChannel { }
}
