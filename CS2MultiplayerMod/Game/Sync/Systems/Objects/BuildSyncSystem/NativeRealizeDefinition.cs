using System.Collections.Generic;
using Colossal.Mathematics;
using Game.Common;
using Game.Net;
using Game.Prefabs;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using CS2MultiplayerMod.Game.Sync.Commands;

namespace CS2MultiplayerMod.Game.Sync.Systems
{
    // Building the game's own tool-definition entities from a remote operation's intents, and
    // recognising an operation whose result this peer already has.
    public partial class BuildSyncSystem
    {
        private Entity CreateObjectToolDefinition(ObjectToolDefinitionIntent source,
            ResolvedObjectDefinition resolved)
        {
            Entity entity = EntityManager.CreateEntity();
            EntityManager.AddComponentData(entity, new CreationDefinition
            {
                m_Prefab = resolved.Prefab,
                m_SubPrefab = resolved.SubPrefab,
                m_Original = resolved.Original,
                m_Owner = resolved.Owner,
                m_Attached = resolved.Attached,
                // Defense in depth: remote definitions always enter through the coordinator's
                // isolated transaction, even if a future caller skips the decode normalization.
                m_Flags = (CreationFlags)source.CreationFlags & ~CreationFlags.Permanent,
                m_RandomSeed = source.RandomSeed,
            });
            if (source.HasOwnerDefinition)
            {
                EntityManager.AddComponentData(entity, new OwnerDefinition
                {
                    m_Prefab = resolved.OwnerDefinitionPrefab,
                    m_Position = new float3(source.OwnerDefinitionX,
                        source.OwnerDefinitionY, source.OwnerDefinitionZ),
                    m_Rotation = new quaternion(source.OwnerDefinitionRotX,
                        source.OwnerDefinitionRotY, source.OwnerDefinitionRotZ,
                        source.OwnerDefinitionRotW),
                });
            }

            if (source.Kind == ObjectToolDefinitionKind.Object)
            {
                ObjectDefinitionIntent value = source.Object;
                EntityManager.AddComponentData(entity, new ObjectDefinition
                {
                    m_Position = new float3(value.PosX, value.PosY, value.PosZ),
                    m_LocalPosition = new float3(value.LocalX, value.LocalY, value.LocalZ),
                    m_Scale = new float3(value.ScaleX, value.ScaleY, value.ScaleZ),
                    m_Rotation = new quaternion(value.RotX, value.RotY, value.RotZ, value.RotW),
                    m_LocalRotation = new quaternion(value.LocalRotX, value.LocalRotY,
                        value.LocalRotZ, value.LocalRotW),
                    m_Elevation = value.Elevation,
                    m_Intensity = value.Intensity,
                    m_Age = value.Age,
                    m_IsDecoration = value.IsDecoration,
                    m_ParentMesh = value.ParentMesh,
                    m_GroupIndex = value.GroupIndex,
                    m_Probability = value.Probability,
                    m_PrefabSubIndex = value.PrefabSubIndex,
                });
            }
            else if (source.Kind == ObjectToolDefinitionKind.NetCourse)
            {
                ObjectNetCourseIntent value = source.NetCourse;
                EntityManager.AddComponentData(entity, new NetCourse
                {
                    m_StartPosition = CreateCoursePos(value.Start, resolved.StartEntity),
                    m_EndPosition = CreateCoursePos(value.End, resolved.EndEntity),
                    m_Curve = new Bezier4x3
                    {
                        a = new float3(value.Ax, value.Ay, value.Az),
                        b = new float3(value.Bx, value.By, value.Bz),
                        c = new float3(value.Cx, value.Cy, value.Cz),
                        d = new float3(value.Dx, value.Dy, value.Dz),
                    },
                    m_Elevation = new float2(value.ElevationLeft, value.ElevationRight),
                    m_Length = value.Length,
                    m_FixedIndex = value.FixedIndex,
                });
            }
            else
            {
                DynamicBuffer<global::Game.Areas.Node> nodes =
                    EntityManager.AddBuffer<global::Game.Areas.Node>(entity);
                ObjectAreaNodeIntent[] sourceNodes = source.AreaNodes;
                nodes.ResizeUninitialized(sourceNodes.Length);
                for (int i = 0; i < sourceNodes.Length; i++)
                    nodes[i] = new global::Game.Areas.Node(
                        new float3(sourceNodes[i].X, sourceNodes[i].Y, sourceNodes[i].Z),
                        sourceNodes[i].Elevation);
            }

            if (source.HasUpgraded)
            {
                EntityManager.AddComponentData(entity, new Upgraded
                {
                    m_Flags = new CompositionFlags(
                        (CompositionFlags.General)source.UpgradeGeneral,
                        (CompositionFlags.Side)source.UpgradeLeft,
                        (CompositionFlags.Side)source.UpgradeRight),
                });
            }
            EntityManager.AddComponent<Updated>(entity);
            EntityManager.AddComponent<Deleted>(entity);
            return entity;
        }

        private static CoursePos CreateCoursePos(ObjectCoursePositionIntent source, Entity target)
        {
            return new CoursePos
            {
                m_Entity = target,
                m_Position = new float3(source.PosX, source.PosY, source.PosZ),
                m_Rotation = new quaternion(source.RotX, source.RotY, source.RotZ, source.RotW),
                m_Elevation = new float2(source.ElevationLeft, source.ElevationRight),
                m_CourseDelta = source.CourseDelta,
                m_SplitPosition = source.SplitPosition,
                m_Flags = (CoursePosFlags)source.Flags,
                m_ParentMesh = source.ParentMesh,
            };
        }

        private void DestroyDefinitions(List<Entity> definitions)
        {
            for (int i = 0; i < definitions.Count; i++)
                if (EntityManager.Exists(definitions[i])) EntityManager.DestroyEntity(definitions[i]);
        }

        private bool EquivalentObjectOperationAlreadyExists(ObjectToolOperationCommand command,
            ResolvedObjectDefinition[] resolved)
        {
            // A stamp has no root object identity. Replay suppression is handled by OperationId;
            // geometry proximity would incorrectly suppress two intentional adjacent stamps.
            if (command.IsAssetStamp) return false;
            ObjectToolDefinitionIntent root = command.Definitions[command.RootIndex];
            if (root.Kind != ObjectToolDefinitionKind.Object ||
                root.Original.Kind != PortableEntityKind.None) return false;
            ObjectDefinitionIntent data = root.Object;
            PortableEntityRef wantedIdentity = default(PortableEntityRef);
            if (root.Owner.Kind != PortableEntityKind.None)
            {
                wantedIdentity.OwnerPrefabName = root.Owner.PrefabName;
                wantedIdentity.OwnerX = root.Owner.PosX;
                wantedIdentity.OwnerY = root.Owner.PosY;
                wantedIdentity.OwnerZ = root.Owner.PosZ;
            }
            else if (root.HasOwnerDefinition)
            {
                wantedIdentity.OwnerPrefabName = root.OwnerDefinitionPrefabName;
                wantedIdentity.OwnerX = root.OwnerDefinitionX;
                wantedIdentity.OwnerY = root.OwnerDefinitionY;
                wantedIdentity.OwnerZ = root.OwnerDefinitionZ;
            }
            return FindEquivalentPlacedObject(resolved[command.RootIndex].Prefab, data,
                root.RandomSeed, wantedIdentity) != Entity.Null;
        }

        /// <summary>
        /// Geometry alone is not a placement identity. Two players can legitimately place the same
        /// prefab close together, especially small roadside buildings and props. Require the
        /// generated variant seed as well, plus orientation for a free-standing object, before
        /// treating a different operation as an already-committed replay.
        /// </summary>
        private Entity FindEquivalentPlacedObject(Entity prefab, ObjectDefinitionIntent source,
            int randomSeed, PortableEntityRef identity)
        {
            List<Entity> candidates = Candidates(_objectCandidates, _portableObjects, prefab);
            float3 position = new float3(source.PosX, source.PosY, source.PosZ);
            float4 rotation = math.normalizesafe(new float4(source.RotX, source.RotY,
                    source.RotZ, source.RotW),
                new float4(0f, 0f, 0f, 1f));
            Entity best = Entity.Null;
            float bestDistance = 4f;
            for (int i = 0; i < candidates.Count; i++)
            {
                Entity candidate = candidates[i];
                if (!MatchesPortableOwner(candidate, identity)) continue;

                global::Game.Objects.Transform transform = EntityManager
                    .GetComponentData<global::Game.Objects.Transform>(candidate);
                float distance = math.distancesq(transform.m_Position, position);
                if (distance >= bestDistance) continue;

                // Attachment resolution may rotate the committed instance relative to its source
                // definition. For unattached objects, orientation is stable and distinguishes two
                // intentional close placements that happen to share a variant seed.
                bool attached = EntityManager.HasComponent<global::Game.Objects.Attached>(candidate);
                float rotationDot = attached ? 1f : math.abs(math.dot(
                    math.normalizesafe(transform.m_Rotation.value,
                        new float4(0f, 0f, 0f, 1f)), rotation));
                // Exact overlap can happen when two players click before receiving one another's
                // edit. It is a genuine collision even though their independently advanced seeds
                // differ; do not feed an impossible stacked graph into the native apply pipeline.
                if (distance <= ExactDuplicateDistanceSq && rotationDot >= 0.99999f)
                    return candidate;
                if (!EntityManager.HasComponent<PseudoRandomSeed>(candidate) ||
                    unchecked((ushort)EntityManager
                        .GetComponentData<PseudoRandomSeed>(candidate).m_Seed) !=
                    unchecked((ushort)randomSeed) || (!attached && rotationDot < 0.9999f)) continue;
                best = candidate;
                bestDistance = distance;
            }
            return best;
        }
    }
}
