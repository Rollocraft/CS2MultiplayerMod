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
using CS2MultiplayerMod.Game.Sync.Infrastructure;

namespace CS2MultiplayerMod.Game.Sync.Systems
{
    // Turning one of the tool's definition entities into the intent that travels on the wire:
    // its prefab, transform, attachment and course positions.
    public partial class BuildSyncSystem
    {
        private bool TryCaptureObjectToolDefinition(Entity entity,
            out ObjectToolDefinitionIntent result)
        {
            result = null;
            CreationDefinition creation = EntityManager.GetComponentData<CreationDefinition>(entity);
            bool isObject = EntityManager.HasComponent<ObjectDefinition>(entity);
            bool isNet = EntityManager.HasComponent<NetCourse>(entity);
            bool isArea = EntityManager.HasBuffer<global::Game.Areas.Node>(entity);
            int shapeCount = (isObject ? 1 : 0) + (isNet ? 1 : 0) + (isArea ? 1 : 0);
            if (shapeCount != 1) return false;

            var value = new ObjectToolDefinitionIntent
            {
                Kind = isObject ? ObjectToolDefinitionKind.Object :
                    isNet ? ObjectToolDefinitionKind.NetCourse : ObjectToolDefinitionKind.Area,
                PrefabIsNull = creation.m_Prefab == Entity.Null,
                // Permanent is an execution-policy bit for definitions that are consumed on this
                // machine without ToolOutputSystem's transaction. It is not object intent. Sending
                // it made the receiver refuse the whole native batch because remote work must pass
                // through the isolated Temp/apply/drain lifecycle.
                CreationFlags = (uint)(creation.m_Flags & ~CreationFlags.Permanent),
                RandomSeed = creation.m_RandomSeed,
            };
            if (creation.m_Prefab != Entity.Null &&
                !TryPrefabName(creation.m_Prefab, out value.PrefabName)) return false;
            if (creation.m_SubPrefab != Entity.Null &&
                !TryPrefabName(creation.m_SubPrefab, out value.SubPrefabName)) return false;
            if (!TryCapturePortableRef(creation.m_Original, out value.Original) ||
                !TryCapturePortableRef(creation.m_Owner, out value.Owner) ||
                !TryCaptureAttachment(creation.m_Attached, creation.m_Prefab,
                    creation.m_Flags, out value.Attached,
                    out value.AttachedPrefabName)) return false;

            if (EntityManager.HasComponent<OwnerDefinition>(entity))
            {
                OwnerDefinition owner = EntityManager.GetComponentData<OwnerDefinition>(entity);
                if (owner.m_Prefab == Entity.Null ||
                    !TryPrefabName(owner.m_Prefab, out value.OwnerDefinitionPrefabName)) return false;
                value.HasOwnerDefinition = true;
                value.OwnerDefinitionX = owner.m_Position.x;
                value.OwnerDefinitionY = owner.m_Position.y;
                value.OwnerDefinitionZ = owner.m_Position.z;
                value.OwnerDefinitionRotX = owner.m_Rotation.value.x;
                value.OwnerDefinitionRotY = owner.m_Rotation.value.y;
                value.OwnerDefinitionRotZ = owner.m_Rotation.value.z;
                value.OwnerDefinitionRotW = owner.m_Rotation.value.w;
            }

            if (isObject)
            {
                ObjectDefinition data = EntityManager.GetComponentData<ObjectDefinition>(entity);
                value.Object = new ObjectDefinitionIntent
                {
                    PosX = data.m_Position.x, PosY = data.m_Position.y, PosZ = data.m_Position.z,
                    LocalX = data.m_LocalPosition.x, LocalY = data.m_LocalPosition.y,
                    LocalZ = data.m_LocalPosition.z,
                    ScaleX = data.m_Scale.x, ScaleY = data.m_Scale.y, ScaleZ = data.m_Scale.z,
                    RotX = data.m_Rotation.value.x, RotY = data.m_Rotation.value.y,
                    RotZ = data.m_Rotation.value.z, RotW = data.m_Rotation.value.w,
                    LocalRotX = data.m_LocalRotation.value.x,
                    LocalRotY = data.m_LocalRotation.value.y,
                    LocalRotZ = data.m_LocalRotation.value.z,
                    LocalRotW = data.m_LocalRotation.value.w,
                    Elevation = data.m_Elevation,
                    Intensity = data.m_Intensity,
                    Age = data.m_Age,
                    IsDecoration = data.m_IsDecoration,
                    ParentMesh = data.m_ParentMesh,
                    GroupIndex = data.m_GroupIndex,
                    Probability = data.m_Probability,
                    PrefabSubIndex = data.m_PrefabSubIndex,
                };
            }
            else if (isNet)
            {
                NetCourse data = EntityManager.GetComponentData<NetCourse>(entity);
                ObjectCoursePositionIntent start, end;
                if (!TryCaptureCoursePosition(data.m_StartPosition, out start) ||
                    !TryCaptureCoursePosition(data.m_EndPosition, out end)) return false;
                value.NetCourse = new ObjectNetCourseIntent
                {
                    Start = start,
                    End = end,
                    Ax = data.m_Curve.a.x, Ay = data.m_Curve.a.y, Az = data.m_Curve.a.z,
                    Bx = data.m_Curve.b.x, By = data.m_Curve.b.y, Bz = data.m_Curve.b.z,
                    Cx = data.m_Curve.c.x, Cy = data.m_Curve.c.y, Cz = data.m_Curve.c.z,
                    Dx = data.m_Curve.d.x, Dy = data.m_Curve.d.y, Dz = data.m_Curve.d.z,
                    ElevationLeft = data.m_Elevation.x,
                    ElevationRight = data.m_Elevation.y,
                    Length = data.m_Length,
                    FixedIndex = data.m_FixedIndex,
                };
            }
            else
            {
                DynamicBuffer<global::Game.Areas.Node> nodes =
                    EntityManager.GetBuffer<global::Game.Areas.Node>(entity, isReadOnly: true);
                if (nodes.Length == 0 ||
                    nodes.Length > ObjectToolOperationCommand.MaxAreaNodesPerDefinition) return false;
                value.AreaNodes = new ObjectAreaNodeIntent[nodes.Length];
                for (int i = 0; i < nodes.Length; i++)
                {
                    value.AreaNodes[i] = new ObjectAreaNodeIntent
                    {
                        X = nodes[i].m_Position.x,
                        Y = nodes[i].m_Position.y,
                        Z = nodes[i].m_Position.z,
                        Elevation = nodes[i].m_Elevation,
                    };
                }
            }

            if (EntityManager.HasComponent<Upgraded>(entity))
            {
                CompositionFlags flags = EntityManager.GetComponentData<Upgraded>(entity).m_Flags;
                value.HasUpgraded = true;
                value.UpgradeGeneral = (uint)flags.m_General;
                value.UpgradeLeft = (uint)flags.m_Left;
                value.UpgradeRight = (uint)flags.m_Right;
            }

            result = value;
            return true;
        }

        private bool TryCaptureAttachment(Entity attached, Entity objectPrefab,
            CreationFlags flags, out PortableEntityRef portable, out string prefabName)
        {
            portable = new PortableEntityRef { Kind = PortableEntityKind.None };
            prefabName = null;
            if (attached == Entity.Null) return true;

            // Placeholder facilities emit their visible level-one building as a second object
            // definition whose attachment target is the placeholder prefab entity itself. That is
            // a local prefab relationship, not a live-world entity reference.
            if (EntityManager.Exists(attached) &&
                EntityManager.HasComponent<PrefabData>(attached))
            {
                if ((flags & CreationFlags.Attach) == 0 ||
                    objectPrefab == Entity.Null ||
                    !EntityManager.Exists(objectPrefab) ||
                    !EntityManager.HasComponent<SpawnableBuildingData>(objectPrefab) ||
                    !EntityManager.HasComponent<PlaceholderBuildingData>(attached) ||
                    !TryPrefabName(attached, out prefabName))
                    return false;
                return true;
            }

            return TryCapturePortableRef(attached, out portable);
        }

        private bool TryCaptureCoursePosition(CoursePos data,
            out ObjectCoursePositionIntent value)
        {
            value = new ObjectCoursePositionIntent();
            PortableEntityRef target;
            if (!TryCaptureCourseTarget(data.m_Entity, out target)) return false;
            value.Entity = target;
            value.PosX = data.m_Position.x; value.PosY = data.m_Position.y;
            value.PosZ = data.m_Position.z;
            value.RotX = data.m_Rotation.value.x; value.RotY = data.m_Rotation.value.y;
            value.RotZ = data.m_Rotation.value.z; value.RotW = data.m_Rotation.value.w;
            value.ElevationLeft = data.m_Elevation.x;
            value.ElevationRight = data.m_Elevation.y;
            value.CourseDelta = data.m_CourseDelta;
            value.SplitPosition = data.m_SplitPosition;
            value.Flags = (uint)data.m_Flags;
            value.ParentMesh = data.m_ParentMesh;
            return true;
        }

        private bool TryCaptureCourseTarget(Entity entity, out PortableEntityRef value)
        {
            return TryCapturePortableRef(entity, out value);
        }

        private bool TryGetStablePortableEntity(Entity entity, out Entity stable)
        {
            stable = entity;
            // Standing definitions can reference the previous preview's Temp graph. Follow its
            // live original; a preview-only target is represented as None and regenerated from the
            // transmitted definition on the receiver.
            const int maxTempDepth = 16;
            for (int depth = 0; stable != Entity.Null && depth < maxTempDepth; depth++)
            {
                if (!EntityManager.Exists(stable)) return false;
                if (!EntityManager.HasComponent<Temp>(stable))
                {
                    return !EntityManager.HasComponent<Deleted>(stable);
                }

                Entity original = EntityManager.GetComponentData<Temp>(stable).m_Original;
                if (original == stable) return false;
                stable = original;
            }

            return stable == Entity.Null;
        }
    }
}
