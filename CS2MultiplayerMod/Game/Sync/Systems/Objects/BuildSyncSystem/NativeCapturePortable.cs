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
    // Naming an entity to a peer that shares none of our entity ids: a portable reference is the
    // prefab, the kind, and the path of owners down from a top-level object, each step described
    // well enough that the other side can walk the same path through its own graph.
    public partial class BuildSyncSystem
    {
        private bool TryCapturePortableRef(Entity entity, out PortableEntityRef value)
        {
            value = new PortableEntityRef { Kind = PortableEntityKind.None };
            if (!TryGetStablePortableEntity(entity, out entity)) return false;
            if (entity == Entity.Null) return true;
            if (!EntityManager.Exists(entity) || !EntityManager.HasComponent<PrefabRef>(entity))
                return false;

            Entity prefab = EntityManager.GetComponentData<PrefabRef>(entity).m_Prefab;
            if (!TryPrefabName(prefab, out value.PrefabName)) return false;
            value.RotW = 1f;

            if (EntityManager.HasComponent<global::Game.Net.Edge>(entity) &&
                EntityManager.HasComponent<global::Game.Net.Curve>(entity))
            {
                value.Kind = PortableEntityKind.NetEdge;
                Bezier4x3 curve = EntityManager.GetComponentData<global::Game.Net.Curve>(entity).m_Bezier;
                value.Ax = curve.a.x; value.Ay = curve.a.y; value.Az = curve.a.z;
                value.Bx = curve.b.x; value.By = curve.b.y; value.Bz = curve.b.z;
                value.Cx = curve.c.x; value.Cy = curve.c.y; value.Cz = curve.c.z;
                value.Dx = curve.d.x; value.Dy = curve.d.y; value.Dz = curve.d.z;
                float3 midpoint = MathUtils.Position(curve, 0.5f);
                value.PosX = midpoint.x; value.PosY = midpoint.y; value.PosZ = midpoint.z;
            }
            else if (EntityManager.HasComponent<global::Game.Net.Node>(entity))
            {
                value.Kind = PortableEntityKind.NetNode;
                float3 position = EntityManager.GetComponentData<global::Game.Net.Node>(entity).m_Position;
                value.PosX = position.x; value.PosY = position.y; value.PosZ = position.z;
            }
            else if (EntityManager.HasComponent<global::Game.Areas.Area>(entity) &&
                     EntityManager.HasBuffer<global::Game.Areas.Node>(entity))
            {
                value.Kind = PortableEntityKind.Area;
                DynamicBuffer<global::Game.Areas.Node> nodes =
                    EntityManager.GetBuffer<global::Game.Areas.Node>(entity, isReadOnly: true);
                if (nodes.Length == 0) return false;
                value.PosX = nodes[0].m_Position.x;
                value.PosY = nodes[0].m_Position.y;
                value.PosZ = nodes[0].m_Position.z;
            }
            else if (EntityManager.HasComponent<global::Game.Objects.Transform>(entity))
            {
                value.Kind = PortableEntityKind.Object;
                global::Game.Objects.Transform transform =
                    EntityManager.GetComponentData<global::Game.Objects.Transform>(entity);
                value.PosX = transform.m_Position.x; value.PosY = transform.m_Position.y;
                value.PosZ = transform.m_Position.z;
                value.RotX = transform.m_Rotation.value.x; value.RotY = transform.m_Rotation.value.y;
                value.RotZ = transform.m_Rotation.value.z; value.RotW = transform.m_Rotation.value.w;
            }
            else return false;

            if (EntityManager.HasComponent<NetData>(prefab))
            {
                NetData netData = EntityManager.GetComponentData<NetData>(prefab);
                value.RequiredLayers = (uint)netData.m_RequiredLayers;
                value.ConnectLayers = (uint)netData.m_ConnectLayers;
            }

            Entity topOwner;
            if (!TryFindTopOwner(entity, out topOwner) || topOwner == Entity.Null) return true;
            if (!EntityManager.HasComponent<PrefabRef>(topOwner) ||
                !EntityManager.HasComponent<global::Game.Objects.Transform>(topOwner)) return false;
            Entity ownerPrefab = EntityManager.GetComponentData<PrefabRef>(topOwner).m_Prefab;
            if (!TryPrefabName(ownerPrefab, out value.OwnerPrefabName)) return false;
            global::Game.Objects.Transform ownerTransform =
                EntityManager.GetComponentData<global::Game.Objects.Transform>(topOwner);
            value.OwnerX = ownerTransform.m_Position.x;
            value.OwnerY = ownerTransform.m_Position.y;
            value.OwnerZ = ownerTransform.m_Position.z;
            value.OwnerRotX = ownerTransform.m_Rotation.value.x;
            value.OwnerRotY = ownerTransform.m_Rotation.value.y;
            value.OwnerRotZ = ownerTransform.m_Rotation.value.z;
            value.OwnerRotW = ownerTransform.m_Rotation.value.w;
            PortableOwnerPathStep[] ownerPath;
            if (TryCaptureOwnerPath(entity, topOwner, out ownerPath))
                value.OwnerPath = ownerPath;
            return true;
        }

        private bool TryCaptureOwnerPath(Entity entity, Entity topOwner,
            out PortableOwnerPathStep[] result)
        {
            result = null;
            if (entity == Entity.Null || topOwner == Entity.Null || entity == topOwner)
                return false;

            var reversed = new List<PortableOwnerPathStep>();
            Entity cursor = entity;
            while (cursor != topOwner)
            {
                if (reversed.Count >= ObjectToolOperationCommand.MaxOwnerPathDepth ||
                    !EntityManager.HasComponent<Owner>(cursor)) return false;
                Entity owner = EntityManager.GetComponentData<Owner>(cursor).m_Owner;
                if (owner == Entity.Null || owner == cursor || !EntityManager.Exists(owner))
                    return false;
                PortableOwnerPathStep step;
                if (!TryCaptureOwnerPathStep(owner, cursor, out step)) return false;
                reversed.Add(step);
                cursor = owner;
            }

            reversed.Reverse();
            result = reversed.ToArray();
            return result.Length != 0;
        }

        private bool TryCaptureOwnerPathStep(Entity owner, Entity child,
            out PortableOwnerPathStep step)
        {
            step = default(PortableOwnerPathStep);
            if (!EntityManager.HasComponent<PrefabRef>(child)) return false;
            Entity childPrefab = EntityManager.GetComponentData<PrefabRef>(child).m_Prefab;
            string childPrefabName;
            PortableEntityKind childKind;
            if (!TryPrefabName(childPrefab, out childPrefabName) ||
                !TryGetPortableEntityKind(child, out childKind)) return false;

            if (EntityManager.HasBuffer<global::Game.Buildings.InstalledUpgrade>(owner))
            {
                DynamicBuffer<global::Game.Buildings.InstalledUpgrade> buffer =
                    EntityManager.GetBuffer<global::Game.Buildings.InstalledUpgrade>(
                        owner, isReadOnly: true);
                int ordinal = 0;
                for (int i = 0; i < buffer.Length; i++)
                {
                    Entity candidate = buffer[i].m_Upgrade;
                    bool same = MatchesOwnerPathSibling(owner, candidate, childPrefab, childKind);
                    if (candidate == child)
                    {
                        step = CreateOwnerPathStep(PortableOwnerPathKind.InstalledUpgrade,
                            childKind, childPrefabName, i, ordinal);
                        return true;
                    }
                    if (same) ordinal++;
                }
            }
            if (EntityManager.HasBuffer<global::Game.Objects.SubObject>(owner))
            {
                DynamicBuffer<global::Game.Objects.SubObject> buffer =
                    EntityManager.GetBuffer<global::Game.Objects.SubObject>(
                        owner, isReadOnly: true);
                int ordinal = 0;
                for (int i = 0; i < buffer.Length; i++)
                {
                    Entity candidate = buffer[i].m_SubObject;
                    bool same = MatchesOwnerPathSibling(owner, candidate, childPrefab, childKind);
                    if (candidate == child)
                    {
                        step = CreateOwnerPathStep(PortableOwnerPathKind.SubObject,
                            childKind, childPrefabName, i, ordinal);
                        return true;
                    }
                    if (same) ordinal++;
                }
            }
            if (EntityManager.HasBuffer<global::Game.Net.SubNet>(owner))
            {
                DynamicBuffer<global::Game.Net.SubNet> buffer =
                    EntityManager.GetBuffer<global::Game.Net.SubNet>(
                        owner, isReadOnly: true);
                int ordinal = 0;
                for (int i = 0; i < buffer.Length; i++)
                {
                    Entity candidate = buffer[i].m_SubNet;
                    bool same = MatchesOwnerPathSibling(owner, candidate, childPrefab, childKind);
                    if (candidate == child)
                    {
                        step = CreateOwnerPathStep(PortableOwnerPathKind.SubNet,
                            childKind, childPrefabName, i, ordinal);
                        return true;
                    }
                    if (same) ordinal++;
                }
            }
            if (EntityManager.HasBuffer<global::Game.Areas.SubArea>(owner))
            {
                DynamicBuffer<global::Game.Areas.SubArea> buffer =
                    EntityManager.GetBuffer<global::Game.Areas.SubArea>(
                        owner, isReadOnly: true);
                int ordinal = 0;
                for (int i = 0; i < buffer.Length; i++)
                {
                    Entity candidate = buffer[i].m_Area;
                    bool same = MatchesOwnerPathSibling(owner, candidate, childPrefab, childKind);
                    if (candidate == child)
                    {
                        step = CreateOwnerPathStep(PortableOwnerPathKind.SubArea,
                            childKind, childPrefabName, i, ordinal);
                        return true;
                    }
                    if (same) ordinal++;
                }
            }
            return false;
        }

        private static PortableOwnerPathStep CreateOwnerPathStep(
            PortableOwnerPathKind bufferKind, PortableEntityKind entityKind, string prefabName,
            int bufferIndex, int prefabOrdinal)
        {
            return new PortableOwnerPathStep
            {
                BufferKind = bufferKind,
                EntityKind = entityKind,
                PrefabName = prefabName,
                BufferIndex = bufferIndex,
                PrefabOrdinal = prefabOrdinal,
            };
        }

        private bool MatchesOwnerPathSibling(Entity owner, Entity candidate, Entity prefab,
            PortableEntityKind kind)
        {
            if (candidate == Entity.Null || !EntityManager.Exists(candidate) ||
                EntityManager.HasComponent<Temp>(candidate) ||
                EntityManager.HasComponent<Deleted>(candidate) ||
                !EntityManager.HasComponent<PrefabRef>(candidate) ||
                EntityManager.GetComponentData<PrefabRef>(candidate).m_Prefab != prefab ||
                !EntityManager.HasComponent<Owner>(candidate) ||
                EntityManager.GetComponentData<Owner>(candidate).m_Owner != owner)
                return false;
            PortableEntityKind candidateKind;
            return TryGetPortableEntityKind(candidate, out candidateKind) &&
                   candidateKind == kind;
        }

        private bool TryGetPortableEntityKind(Entity entity, out PortableEntityKind kind)
        {
            if (EntityManager.HasComponent<global::Game.Net.Edge>(entity) &&
                EntityManager.HasComponent<global::Game.Net.Curve>(entity))
            {
                kind = PortableEntityKind.NetEdge;
                return true;
            }
            if (EntityManager.HasComponent<global::Game.Net.Node>(entity))
            {
                kind = PortableEntityKind.NetNode;
                return true;
            }
            if (EntityManager.HasComponent<global::Game.Areas.Area>(entity))
            {
                kind = PortableEntityKind.Area;
                return true;
            }
            if (EntityManager.HasComponent<global::Game.Objects.Object>(entity))
            {
                kind = PortableEntityKind.Object;
                return true;
            }
            kind = PortableEntityKind.None;
            return false;
        }

        private bool TryFindTopOwner(Entity entity, out Entity topOwner)
        {
            topOwner = Entity.Null;
            Entity cursor = entity;
            for (int depth = 0; depth < 64 && EntityManager.HasComponent<Owner>(cursor); depth++)
            {
                Entity next = EntityManager.GetComponentData<Owner>(cursor).m_Owner;
                if (next == Entity.Null || next == cursor || !EntityManager.Exists(next)) return false;
                topOwner = next;
                cursor = next;
            }
            return cursor == entity || !EntityManager.HasComponent<Owner>(cursor);
        }

        private bool TryPrefabName(Entity prefab, out string name)
        {
            name = prefab != Entity.Null ? _prefabSystem.GetPrefabName(prefab) : null;
            return !string.IsNullOrEmpty(name);
        }
    }
}
