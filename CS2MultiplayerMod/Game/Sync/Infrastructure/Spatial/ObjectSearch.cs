using Colossal.Collections;
using Colossal.Mathematics;
using Game.Common;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;

namespace CS2MultiplayerMod.Game.Sync.Infrastructure
{
    /// <summary>
    /// Point lookups against the game's static-object search tree, instead of walking the object domain
    /// per retry frame.
    /// </summary>
    public sealed class ObjectSearch
    {
        private readonly global::Game.Objects.SearchSystem _search;
        internal global::Game.Objects.SearchSystem System => _search;

        public ObjectSearch(global::Game.Objects.SearchSystem search)
        {
            _search = search;
        }

        /// <summary>
        /// Live static objects whose bounds reach a box of <paramref name="radius"/> around
        /// <paramref name="position"/>. Callers still filter: the tree holds owned sub-objects and may be stale.
        /// </summary>
        public void CollectNear(float3 position, float radius, NativeList<Entity> results) =>
            BeginBatch().CollectNear(position, radius, results);

        /// <summary>
        /// Holds the tree across many queries. Valid for the caller's update: component writes are fine,
        /// structural changes are not.
        /// </summary>
        public Batch BeginBatch()
        {
            using (Diagnostics.SyncProfiler.Measure("Search.Acquire"))
            {
                NativeQuadTree<Entity, QuadTreeBoundsXZ> tree =
                    _search.GetStaticSearchTree(readOnly: true, out JobHandle dependencies);
                dependencies.Complete();
                return new Batch(tree);
            }
        }

        public struct Batch
        {
            private NativeQuadTree<Entity, QuadTreeBoundsXZ> _tree;

            internal Batch(NativeQuadTree<Entity, QuadTreeBoundsXZ> tree)
            {
                _tree = tree;
            }

            /// <summary>See <see cref="ObjectSearch.CollectNear"/>.</summary>
            public void CollectNear(float3 position, float radius, NativeList<Entity> results)
            {
                results.Clear();
                var iterator = new NearbyIterator
                {
                    m_Bounds = new Bounds3(position - radius, position + radius),
                    m_Results = results,
                };
                using (Diagnostics.SyncProfiler.Measure("Search.CollectNear"))
                    _tree.Iterate(ref iterator);
            }
        }

        private struct NearbyIterator :
            INativeQuadTreeIterator<Entity, QuadTreeBoundsXZ>,
            IUnsafeQuadTreeIterator<Entity, QuadTreeBoundsXZ>
        {
            public Bounds3 m_Bounds;
            public NativeList<Entity> m_Results;

            public bool Intersect(QuadTreeBoundsXZ bounds) => MathUtils.Intersect(bounds.m_Bounds.xz, m_Bounds.xz);

            public void Iterate(QuadTreeBoundsXZ bounds, Entity item)
            {
                if (MathUtils.Intersect(bounds.m_Bounds.xz, m_Bounds.xz)) m_Results.Add(item);
            }
        }
    }

    /// <summary>
    /// Collects every entity whose 3D bounds reach <see cref="Bounds"/> (the tree itself only prunes in
    /// XZ); callers filter.
    /// </summary>
    internal struct Bounds3Collector :
        INativeQuadTreeIterator<Entity, QuadTreeBoundsXZ>,
        IUnsafeQuadTreeIterator<Entity, QuadTreeBoundsXZ>
    {
        public Bounds3 Bounds;
        public NativeList<Entity> Results;

        public bool Intersect(QuadTreeBoundsXZ bounds) => MathUtils.Intersect(bounds.m_Bounds, Bounds);

        public void Iterate(QuadTreeBoundsXZ bounds, Entity item)
        {
            if (MathUtils.Intersect(bounds.m_Bounds, Bounds)) Results.Add(item);
        }
    }
}
