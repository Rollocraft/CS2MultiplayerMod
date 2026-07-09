using Colossal.Mathematics;
using Game.Common;
using Game.Net;
using Game.Objects;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace CS2MultiplayerMod.Game.Sync.Infrastructure
{
    /// <summary>
    /// A net object - a roundabout central island on a node, a "no right turn" sign on an edge -
    /// acts on the road purely through the composition flags of the net entity it hangs off, and
    /// those flags are re-selected only while that entity carries <see cref="Updated"/>. The game's
    /// AttachSystem maintains the parent link but tags nothing; in a normal placement the tool's
    /// apply pass supplies the tags.
    ///
    /// A direct create/delete has no apply pass, so it must tag the parent itself - otherwise the
    /// object renders as an inert prop on an untouched road (or its effect survives a bulldoze).
    /// See docs/internals/roundabout-placement.md.
    /// </summary>
    internal static class NetAttachment
    {
        /// <summary>The road node or edge this object hangs off, or Null when it is free-standing.</summary>
        public static Entity GetNetParent(EntityManager em, Entity obj)
        {
            if (!em.HasComponent<Attached>(obj)) return Entity.Null;

            Entity parent = em.GetComponentData<Attached>(obj).m_Parent;
            if (parent == Entity.Null || !em.Exists(parent)) return Entity.Null;
            return em.HasComponent<Node>(parent) || em.HasComponent<Edge>(parent) ? parent : Entity.Null;
        }

        /// <summary>
        /// Describe an object's attachment for the wire. The anchor is the parent's own world
        /// position (node) or the point on its centreline the object hangs at (edge) - entity ids
        /// differ per machine, positions do not.
        /// </summary>
        public static bool TryGetAttachment(EntityManager em, Entity obj, out bool isNode, out float3 anchor)
        {
            isNode = false;
            anchor = default;

            Entity parent = GetNetParent(em, obj);
            if (parent == Entity.Null) return false;

            if (em.HasComponent<Node>(parent))
            {
                isNode = true;
                anchor = em.GetComponentData<Node>(parent).m_Position;
                return true;
            }

            // An edge-attached object sits off the centreline, at the curb. Anchoring on its own
            // transform would let a neighbouring road win the match, so anchor on the projection
            // the game itself stored: Attached.m_CurvePosition along the parent curve.
            if (!em.HasComponent<Curve>(parent)) return false;
            float curvePosition = em.GetComponentData<Attached>(obj).m_CurvePosition;
            anchor = MathUtils.Position(em.GetComponentData<Curve>(parent).m_Bezier, curvePosition);
            return true;
        }

        /// <summary>
        /// Tag a parent so its compositions re-derive this frame. A node is selected through the
        /// edges meeting it; an edge carries its own composition plus the node-side ones at each end.
        /// </summary>
        public static void TagParentUpdated(EntityManager em, Entity parent)
        {
            if (parent == Entity.Null || !em.Exists(parent)) return;

            if (em.HasComponent<Node>(parent)) { TagNodeUpdated(em, parent); return; }
            if (em.HasComponent<Edge>(parent)) TagEdgeUpdated(em, parent);
        }

        private static void TagNodeUpdated(EntityManager em, Entity node)
        {
            NativeArray<ConnectedEdge> connected = default;
            if (em.HasBuffer<ConnectedEdge>(node))
                connected = em.GetBuffer<ConnectedEdge>(node, isReadOnly: true).ToNativeArray(Allocator.Temp);

            // Tagging is a structural change and invalidates the buffer, hence the copy above.
            Tag(em, node);
            if (!connected.IsCreated) return;
            try
            {
                for (int i = 0; i < connected.Length; i++) Tag(em, connected[i].m_Edge);
            }
            finally
            {
                connected.Dispose();
            }
        }

        private static void TagEdgeUpdated(EntityManager em, Entity edge)
        {
            Edge ends = em.GetComponentData<Edge>(edge);
            Tag(em, edge);
            Tag(em, ends.m_Start);
            Tag(em, ends.m_End);
        }

        private static void Tag(EntityManager em, Entity entity)
        {
            if (entity == Entity.Null || !em.Exists(entity)) return;
            if (em.HasComponent<Deleted>(entity) || em.HasComponent<Temp>(entity)) return;
            if (!em.HasComponent<Updated>(entity)) em.AddComponent<Updated>(entity);
        }
    }
}
