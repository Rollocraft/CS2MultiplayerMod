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
    /// Point lookups against the game's static-object search tree.
    ///
    /// Sync systems resolve a remote command's target by position, and retry every frame while it
    /// stays unmatched. Walking the object domain to do that cost a main-thread component lookup
    /// per object in the city per frame — a 250k-object city spent ~15 ms a frame there and sat at
    /// half frame rate for the whole retry window. The tree answers the same question in
    /// log time and is the index the game's own tools search.
    /// </summary>
    public sealed class ObjectSearch
    {
        private readonly global::Game.Objects.SearchSystem _search;

        public ObjectSearch(global::Game.Objects.SearchSystem search)
        {
            _search = search;
        }

        /// <summary>
        /// Fills <paramref name="results"/> with every live static object whose bounds reach a box
        /// of <paramref name="radius"/> around <paramref name="position"/>. Bounds are built around
        /// an object's transform, so this reaches everything whose pivot lies inside that box.
        /// Callers still filter the candidates: the tree holds owned sub-objects too, and its
        /// entries are only as fresh as the last search-tree update.
        /// </summary>
        public void CollectNear(float3 position, float radius, NativeList<Entity> results)
        {
            BeginBatch().CollectNear(position, radius, results);
        }

        /// <summary>
        /// Holds the tree across a run of queries. Acquiring it per query re-enters the job system
        /// for every point, which for a batch of thousands is thousands of round trips to answer
        /// one question. Valid for the rest of the calling system's update: component writes are
        /// fine in between, structural changes are not.
        /// </summary>
        public Batch BeginBatch()
        {
            JobHandle dependencies;
            NativeQuadTree<Entity, QuadTreeBoundsXZ> tree =
                _search.GetStaticSearchTree(readOnly: true, out dependencies);
            // Read on the main thread: the callers make structural changes straight afterwards,
            // which would sync these jobs anyway.
            dependencies.Complete();
            return new Batch(tree);
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
                _tree.Iterate(ref iterator);
            }
        }

        private struct NearbyIterator :
            INativeQuadTreeIterator<Entity, QuadTreeBoundsXZ>,
            IUnsafeQuadTreeIterator<Entity, QuadTreeBoundsXZ>
        {
            public Bounds3 m_Bounds;
            public NativeList<Entity> m_Results;

            public bool Intersect(QuadTreeBoundsXZ bounds)
            {
                return MathUtils.Intersect(bounds.m_Bounds.xz, m_Bounds.xz);
            }

            public void Iterate(QuadTreeBoundsXZ bounds, Entity item)
            {
                if (MathUtils.Intersect(bounds.m_Bounds.xz, m_Bounds.xz)) m_Results.Add(item);
            }
        }
    }
}
