using System.Collections.Generic;
using Colossal.Mathematics;
using CS2MultiplayerMod.Core.Diagnostics;
using CS2MultiplayerMod.Game.Diagnostics;
using Game.Common;
using Game.Net;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;

namespace CS2MultiplayerMod.Game.Sync.Systems.Net
{
    // Commit orchestration for NetSyncSystem. A remote net operation includes the objects and areas
    // its native generation updates as side effects; the complete local preview graph is temporarily
    // Disabled so an unrelated tool can remain selected without either transaction consuming the
    // other one's entities.
    // Validating an armed object transaction: every temp's prefab, original, attachment, area and
    // owned buffers must point at something still live or at another member of the same batch.
    public partial class NetSyncSystem
    {
        /// <summary>
        /// Validate the exact union consumed by the object, net, and area apply passes. The checks
        /// intentionally run on the main thread immediately before scheduling those jobs because
        /// their observed runtime behaviour assumes owner/original/buffer references are live.
        /// </summary>
        private bool ValidateArmedObjectTransaction(out string reason)
        {
            _relinkedOwners = 0;
            NativeArray<Entity> temps = _objectTransactionTemps.ToEntityArray(Allocator.Temp);
            try
            {
                if (temps.Length == 0)
                {
                    reason = "the generated object transaction was empty";
                    return false;
                }

                var members = new HashSet<Entity>();
                for (int i = 0; i < temps.Length; i++)
                {
                    Entity entity = temps[i];
                    if (!EntityManager.Exists(entity) || !EntityManager.HasComponent<Temp>(entity) ||
                        EntityManager.HasComponent<Deleted>(entity) ||
                        EntityManager.HasComponent<Disabled>(entity))
                    {
                        reason = "the generated object transaction became partial before commit";
                        return false;
                    }
                    members.Add(entity);
                }
                HashSet<Entity> enabledTransactionConnections =
                    CollectEnabledTransactionConnections(members);

                int objectRoots = 0;
                int netStructures = 0;
                for (int i = 0; i < temps.Length; i++)
                {
                    Entity entity = temps[i];
                    Temp temp = EntityManager.GetComponentData<Temp>(entity);
                    bool isObject = EntityManager.HasComponent<global::Game.Objects.Object>(entity);
                    bool isNode = EntityManager.HasComponent<Node>(entity);
                    bool isEdge = EntityManager.HasComponent<Edge>(entity);
                    bool isLane = EntityManager.HasComponent<Lane>(entity);
                    bool isAggregate = EntityManager.HasComponent<Aggregate>(entity);
                    bool isArea = EntityManager.HasComponent<global::Game.Areas.Area>(entity);
                    if (isNode || isEdge) netStructures++;
                    if (!isObject && !isNode && !isEdge && !isLane && !isAggregate && !isArea)
                    {
                        reason = "the generated object transaction contains an unsupported Temp shape";
                        return false;
                    }

                    if (!ValidateTransactionOwner(entity, members, out reason)) return false;
                    if (!ValidateOwnedBuffers(entity, members, out reason)) return false;

                    if (isObject)
                    {
                        if (!EntityManager.HasComponent<Owner>(entity) ||
                            !members.Contains(EntityManager.GetComponentData<Owner>(entity).m_Owner))
                            objectRoots++;
                        if ((temp.m_Flags & TempFlags.Delete) == 0 &&
                            !ValidateObjectPrefabReference(entity, out reason)) return false;
                        if (!ValidateObjectOriginal(temp, out reason)) return false;
                        if (!ValidateAttachment(entity, members, out reason)) return false;
                    }

                    if (isNode || isEdge)
                    {
                        if (!ValidateTransactionOriginal(entity, temp, isNode, isEdge,
                                enabledTransactionConnections, out reason))
                            return false;
                        if (isNode && !ValidateTempNode(entity, temp, out reason)) return false;
                        if (isEdge && !ValidateTempEdge(entity, temp, members,
                                enabledTransactionConnections, out reason)) return false;
                    }

                    if (isLane && !ValidateLaneOriginal(temp, out reason)) return false;
                    if (isArea && !ValidateAreaEntity(entity, temp, out reason)) return false;

                    bool missingReplacementOriginal =
                        isEdge && (temp.m_Flags & (TempFlags.Replace | TempFlags.Combine)) != 0 ||
                        isLane && (temp.m_Flags & TempFlags.Replace) != 0;
                    if (missingReplacementOriginal && temp.m_Original == Entity.Null)
                    {
                        reason = "a generated object-graph replacement has no original entity";
                        return false;
                    }
                }

                if (_pendingTransactionKind == RemoteToolTransactionKind.AssetStampGraph)
                {
                    if (netStructures == 0)
                    {
                        reason = "the generated asset-stamp transaction has no network graph";
                        return false;
                    }
                }
                else if (objectRoots == 0)
                {
                    reason = "the generated object transaction has no top-level object";
                    return false;
                }

                reason = null;
                SyncLog.Trace(LogTopic.Nets, "object transaction validated temps=" + temps.Length +
                    (_relinkedOwners > 0 ? " ownersRelinked=" + _relinkedOwners : string.Empty));
                return true;
            }
            finally
            {
                temps.Dispose();
            }
        }

        private bool ValidateObjectPrefabReference(Entity entity, out string reason)
        {
            reason = null;
            if (!EntityManager.HasComponent<global::Game.Prefabs.PrefabRef>(entity))
            {
                reason = "a generated object has no prefab reference";
                return false;
            }
            Entity prefab = EntityManager.GetComponentData<global::Game.Prefabs.PrefabRef>(entity).m_Prefab;
            if (prefab == Entity.Null || !EntityManager.Exists(prefab) ||
                !EntityManager.HasComponent<global::Game.Prefabs.PrefabData>(prefab) ||
                !EntityManager.HasComponent<global::Game.Prefabs.ObjectData>(prefab))
            {
                reason = "a generated object references an invalid object prefab";
                return false;
            }
            return true;
        }

        private bool ValidateObjectOriginal(Temp temp, out string reason)
        {
            reason = null;
            if (temp.m_Original == Entity.Null) return true;
            Entity original = temp.m_Original;
            if (!EntityManager.Exists(original) || EntityManager.HasComponent<Deleted>(original) ||
                EntityManager.HasComponent<Temp>(original) ||
                !EntityManager.HasComponent<global::Game.Objects.Object>(original))
            {
                reason = "an object definition references a stale or non-object original";
                return false;
            }
            return ValidateOwnedBuffers(original, null, out reason);
        }

        /// <summary>
        /// A lane Temp naming an original reaches the apply pass's lane update, which adds the
        /// apply-updated component set to that original with no existence test - and its delete and
        /// replace branches only null-check it. A destroyed original therefore becomes a command
        /// buffer entry that faults when the tool barrier plays it back, with the process ending
        /// inside the playback rather than at the system that recorded it.
        ///
        /// Nodes, edges, objects and areas were already checked here. Lanes were not, and they are
        /// the bulk of every large batch - 573 of 732 members in one observed fatal commit.
        /// </summary>
        private bool ValidateLaneOriginal(Temp temp, out string reason)
        {
            reason = null;
            Entity original = temp.m_Original;
            if (original == Entity.Null) return true;
            if (!EntityManager.Exists(original) || EntityManager.HasComponent<Deleted>(original) ||
                EntityManager.HasComponent<Temp>(original) ||
                !EntityManager.HasComponent<Lane>(original))
            {
                reason = "a generated lane references a stale original";
                return false;
            }
            return true;
        }

        private bool ValidateAttachment(Entity entity, HashSet<Entity> members, out string reason)
        {
            reason = null;
            if (!EntityManager.HasComponent<global::Game.Objects.Attached>(entity)) return true;
            global::Game.Objects.Attached attached =
                EntityManager.GetComponentData<global::Game.Objects.Attached>(entity);
            return ValidateLiveOrMemberReference(attached.m_Parent, members, "attachment parent", out reason) &&
                   ValidateLiveOrMemberReference(attached.m_OldParent, members, "old attachment parent", out reason);
        }

        private bool ValidateAreaEntity(Entity entity, Temp temp, out string reason)
        {
            reason = null;
            if ((temp.m_Flags & TempFlags.Delete) == 0)
            {
                if (!EntityManager.HasComponent<global::Game.Prefabs.PrefabRef>(entity))
                {
                    reason = "a generated area has no prefab reference";
                    return false;
                }
                Entity prefab = EntityManager.GetComponentData<global::Game.Prefabs.PrefabRef>(entity).m_Prefab;
                if (prefab == Entity.Null || !EntityManager.Exists(prefab) ||
                    !EntityManager.HasComponent<global::Game.Prefabs.AreaData>(prefab) ||
                    !EntityManager.HasBuffer<global::Game.Areas.Node>(entity))
                {
                    reason = "a generated area is missing prefab or node data";
                    return false;
                }
            }
            if (temp.m_Original != Entity.Null &&
                (!EntityManager.Exists(temp.m_Original) ||
                 EntityManager.HasComponent<Deleted>(temp.m_Original) ||
                 !EntityManager.HasComponent<global::Game.Areas.Area>(temp.m_Original) ||
                 !EntityManager.HasBuffer<global::Game.Areas.Node>(temp.m_Original)))
            {
                reason = "an area definition references a stale original";
                return false;
            }
            return true;
        }

        private bool ValidateOwnedBuffers(Entity entity, HashSet<Entity> members, out string reason)
        {
            reason = null;
            if (EntityManager.HasBuffer<global::Game.Objects.SubObject>(entity))
            {
                DynamicBuffer<global::Game.Objects.SubObject> buffer =
                    EntityManager.GetBuffer<global::Game.Objects.SubObject>(entity, isReadOnly: true);
                for (int i = 0; i < buffer.Length; i++)
                    if (!ValidateLiveOrMemberReference(buffer[i].m_SubObject, members,
                            "SubObject", out reason)) return false;
            }
            if (EntityManager.HasBuffer<global::Game.Net.SubNet>(entity))
            {
                DynamicBuffer<global::Game.Net.SubNet> buffer =
                    EntityManager.GetBuffer<global::Game.Net.SubNet>(entity, isReadOnly: true);
                for (int i = 0; i < buffer.Length; i++)
                    if (!ValidateLiveOrMemberReference(buffer[i].m_SubNet, members,
                            "SubNet", out reason)) return false;
            }
            if (EntityManager.HasBuffer<global::Game.Areas.SubArea>(entity))
            {
                DynamicBuffer<global::Game.Areas.SubArea> buffer =
                    EntityManager.GetBuffer<global::Game.Areas.SubArea>(entity, isReadOnly: true);
                for (int i = 0; i < buffer.Length; i++)
                    if (!ValidateLiveOrMemberReference(buffer[i].m_Area, members,
                            "SubArea", out reason)) return false;
            }
            return true;
        }

        private bool ValidateLiveOrMemberReference(Entity referenced, HashSet<Entity> members,
            string label, out string reason)
        {
            reason = null;
            if (referenced == Entity.Null) return true;
            if (!EntityManager.Exists(referenced) || EntityManager.HasComponent<Deleted>(referenced))
            {
                reason = label + " contains a stale entity reference";
                return false;
            }
            if (EntityManager.HasComponent<Temp>(referenced) &&
                (members == null || !members.Contains(referenced) ||
                 EntityManager.HasComponent<Disabled>(referenced)))
            {
                reason = label + " points outside the enabled transaction";
                return false;
            }
            return true;
        }
    }
}
