using System.Text;
using Colossal.Mathematics;
using Game.Common;
using Game.Prefabs;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using CS2MultiplayerMod.Game.Sync.Commands;

namespace CS2MultiplayerMod.Game.Sync.Systems
{
    public partial class BuildSyncSystem
    {
        /// <summary>
        /// A same-prefab object (or one spawned this frame) with the same replay identity within
        /// <see cref="DuplicateRadiusSq"/>, or at the exact transform. Snapshot once per realizing frame.
        /// </summary>
        private bool AlreadyStandsAt(ObjectPlacementCommand command, Entity prefab,
            float3 position, quaternion rotation)
        {
            for (int i = 0; i < _rzRealizedThisFrame.Count; i++)
            {
                if (_rzRealizedThisFrame[i].prefab != prefab ||
                    _rzRealizedThisFrame[i].attachKind != command.AttachKind) continue;
                float3 p = _rzRealizedThisFrame[i].position;
                float rotationDot = math.abs(math.dot(
                    _rzRealizedThisFrame[i].rotation.value, rotation.value));
                // Both players picked the identical transform: keep one rather than stack two.
                if (math.distancesq(p, position) <= ExactDuplicateDistanceSq &&
                    (command.AttachKind != ObjectAttachKind.None || rotationDot >= 0.99999f))
                    return true;
                if (math.distancesq(p.xz, position.xz) >= DuplicateRadiusSq ||
                    math.abs(p.y - position.y) > DuplicateMaxDy ||
                    unchecked((ushort)_rzRealizedThisFrame[i].randomSeed) !=
                    unchecked((ushort)command.RandomSeed)) continue;
                if (command.AttachKind == ObjectAttachKind.None && rotationDot < 0.9999f) continue;
                return true;
            }

            if (!_dupSnapshotTaken)
            {
                _dupEntities = _liveStaticObjects.ToEntityArray(Allocator.Temp);
                _dupTransforms = _liveStaticObjects.ToComponentDataArray<global::Game.Objects.Transform>(Allocator.Temp);
                _dupPrefabs = _liveStaticObjects.ToComponentDataArray<PrefabRef>(Allocator.Temp);
                _dupSnapshotTaken = true;
            }
            for (int i = 0; i < _dupTransforms.Length; i++)
            {
                if (_dupPrefabs[i].m_Prefab != prefab) continue;
                float3 p = _dupTransforms[i].m_Position;
                if (math.distancesq(p.xz, position.xz) >= DuplicateRadiusSq ||
                    math.abs(p.y - position.y) > DuplicateMaxDy) continue;

                Entity candidate = _dupEntities[i];
                bool attached = EntityManager.HasComponent<global::Game.Objects.Attached>(candidate);
                if ((command.AttachKind != ObjectAttachKind.None) != attached) continue;
                if (attached)
                {
                    Entity parent = EntityManager
                        .GetComponentData<global::Game.Objects.Attached>(candidate).m_Parent;
                    bool parentMatchesKind = parent != Entity.Null && EntityManager.Exists(parent) &&
                        (command.AttachKind == ObjectAttachKind.NetNode
                            ? EntityManager.HasComponent<global::Game.Net.Node>(parent)
                            : EntityManager.HasComponent<global::Game.Net.Edge>(parent));
                    if (!parentMatchesKind) continue;
                }

                float rotationDot = attached ? 1f : math.abs(math.dot(
                    math.normalizesafe(_dupTransforms[i].m_Rotation.value,
                        new float4(0f, 0f, 0f, 1f)), rotation.value));
                if (math.distancesq(p, position) <= ExactDuplicateDistanceSq &&
                    rotationDot >= 0.99999f) return true;

                // Proximity alone is not a replay; without a seed, keep the command.
                if (!EntityManager.HasComponent<PseudoRandomSeed>(candidate) ||
                    unchecked((ushort)EntityManager
                        .GetComponentData<PseudoRandomSeed>(candidate).m_Seed) !=
                    unchecked((ushort)command.RandomSeed)) continue;

                // Attachment can rotate a prop; free-standing ones must also match orientation.
                if (!attached && rotationDot < 0.9999f) continue;
                return true;
            }
            return false;
        }

        // Prefab-name digest for the per-frame flight note, e.g. " [WaterPumpingStation x3]".
        private void AppendRealizedNames(StringBuilder note)
        {
            if (_rzRealizedThisFrame.Count == 0) return;
            note.Append(" [");
            int written = 0;
            for (int i = 0; i < _rzRealizedThisFrame.Count; i++)
            {
                Entity prefab = _rzRealizedThisFrame[i].prefab;
                bool seen = false;
                int count = 0;
                for (int j = 0; j < _rzRealizedThisFrame.Count; j++)
                {
                    if (_rzRealizedThisFrame[j].prefab != prefab) continue;
                    if (j < i) { seen = true; break; }
                    count++;
                }
                if (seen) continue;
                if (written > 0) note.Append(", ");
                note.Append(_prefabSystem.GetPrefabName(prefab));
                if (count > 1) note.Append(" x").Append(count);
                written++;
            }
            note.Append(']');
        }

        /// <summary>The local net entity this command's object hangs off, or Null (also when unattached).</summary>
        private Entity FindAttachTarget(ObjectPlacementCommand command)
        {
            return ResolveNetAttachment(command.AttachKind,
                new float3(command.AttachX, command.AttachY, command.AttachZ));
        }

        /// <summary>One resolver for placement and relocation, so both choose the same piece of road.</summary>
        internal Entity ResolveNetAttachment(ObjectAttachKind kind, float3 anchor)
        {
            switch (kind)
            {
                case ObjectAttachKind.NetNode: return FindAttachNode(anchor);
                case ObjectAttachKind.NetEdge: return FindAttachEdge(anchor);
                default: return Entity.Null;
            }
        }

        /// <summary>
        /// The edge whose centreline is closest to the anchor; 3D distance keeps a bridge overhead out.
        /// </summary>
        private Entity FindAttachEdge(float3 anchor)
        {
            Entity best = Entity.Null, bestOwned = Entity.Null;
            float bestDist = AttachEdgeTol, bestOwnedDist = AttachEdgeTol;

            NativeArray<Entity> entities = _liveEdges.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < entities.Length; i++)
                {
                    Bezier4x3 curve = EntityManager.GetComponentData<global::Game.Net.Curve>(entities[i]).m_Bezier;

                    // The control hull bounds the curve and rejects almost every edge cheaply.
                    Bounds3 bounds = MathUtils.Bounds(curve);
                    if (math.any(anchor < bounds.min - AttachEdgeTol) ||
                        math.any(anchor > bounds.max + AttachEdgeTol)) continue;

                    float dist = MathUtils.Distance(curve, anchor, out float t);

                    // A driveway ties with its road at the centreline; owned sub-nets win only when nothing else is near.
                    if (EntityManager.HasComponent<Owner>(entities[i]))
                    {
                        if (dist >= bestOwnedDist) continue;
                        bestOwned = entities[i];
                        bestOwnedDist = dist;
                        continue;
                    }

                    if (dist >= bestDist) continue;
                    best = entities[i];
                    bestDist = dist;
                }
            }
            finally
            {
                entities.Dispose();
            }
            return best != Entity.Null ? best : bestOwned;
        }

        /// <summary>The live road node closest to <paramref name="wanted"/>, or Null when none is near.</summary>
        private Entity FindAttachNode(float3 wanted)
        {
            Entity best = Entity.Null;
            float bestDistSq = AttachNodeTolSq;

            NativeArray<Entity> entities = _liveNodes.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < entities.Length; i++)
                {
                    float3 pos = EntityManager.GetComponentData<global::Game.Net.Node>(entities[i]).m_Position;
                    // A bridge node stacked over a junction sits at the same XZ - never match it.
                    if (math.abs(pos.y - wanted.y) > AttachNodeMaxDy) continue;

                    float distSq = math.distancesq(pos.xz, wanted.xz);
                    if (distSq >= bestDistSq) continue;
                    best = entities[i];
                    bestDistSq = distSq;
                }
            }
            finally
            {
                entities.Dispose();
            }
            return best;
        }
    }
}
