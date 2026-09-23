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
    /// A net object (roundabout island, turn sign) acts through its parent's composition flags, which
    /// re-select only when the parent is Updated. A direct create or delete has no apply pass to tag it,
    /// so the parent is tagged here.
    /// </summary>
    internal static class NetAttachment
    {
        /// <summary>The road node or edge this object hangs off, or Null when it is free-standing.</summary>
        public static Entity GetNetParent(EntityManager em, Entity obj)
        {
            if (!em.HasComponent<Attached>(obj)) return Entity.Null;

            Entity parent = em.GetComponentData<Attached>(obj).m_Parent;
            return NormalizeNetParent(em, parent);
        }

        /// <summary>A snap target to its node or edge; it may be the net or an attached object.</summary>
        public static Entity NormalizeNetParent(EntityManager em, Entity candidate)
        {
            if (candidate == Entity.Null || !em.Exists(candidate)) return Entity.Null;
            if (em.HasComponent<Node>(candidate) || em.HasComponent<Edge>(candidate)) return candidate;
            if (!em.HasComponent<Attached>(candidate)) return Entity.Null;

            Entity parent = em.GetComponentData<Attached>(candidate).m_Parent;
            if (parent == Entity.Null || !em.Exists(parent)) return Entity.Null;
            return em.HasComponent<Node>(parent) || em.HasComponent<Edge>(parent)
                ? parent
                : Entity.Null;
        }

        /// <summary>
        /// The anchor: the node's position, or the point on the edge centreline the object hangs at.
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

            // Anchor on Attached.m_CurvePosition, not the curb-side transform a neighbour road could win.
            if (!em.HasComponent<Curve>(parent)) return false;
            float curvePosition = em.GetComponentData<Attached>(obj).m_CurvePosition;
            anchor = MathUtils.Position(em.GetComponentData<Curve>(parent).m_Bezier, curvePosition);
            return true;
        }

        /// <summary>A node or edge as a portable anchor, projected onto the centreline for edges.</summary>
        public static bool TryDescribeParent(EntityManager em, Entity candidate, float3 nearPosition,
            out bool isNode, out float3 anchor)
        {
            isNode = false;
            anchor = default;
            Entity parent = NormalizeNetParent(em, candidate);
            if (parent == Entity.Null) return false;

            if (em.HasComponent<Node>(parent))
            {
                isNode = true;
                anchor = em.GetComponentData<Node>(parent).m_Position;
                return true;
            }

            if (!em.HasComponent<Curve>(parent)) return false;
            Bezier4x3 curve = em.GetComponentData<Curve>(parent).m_Bezier;
            MathUtils.Distance(curve, nearPosition, out float curvePosition);
            anchor = MathUtils.Position(curve, curvePosition);
            return true;
        }

        /// <summary>Tags a parent so its compositions re-derive, including the node sides of an edge.</summary>
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
