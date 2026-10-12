using System.Collections.Generic;
using CS2MultiplayerMod.Core.Diagnostics;
using CS2MultiplayerMod.Game.Diagnostics;
using Game.Common;
using Game.Net;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;

namespace CS2MultiplayerMod.Game.Sync.Systems.Net
{
    // Validating an armed net transaction, its attached objects and every Temp's owner.
    public partial class NetSyncSystem
    {
        /// <summary>
        /// Verifies the generated transaction just before its apply: a local edit since arming may have
        /// invalidated an original, endpoint, owner or buffer. Rebuilt rather than applied unchecked.
        /// </summary>
        private bool ValidateArmedNetTransaction(out string reason)
        {
            _relinkedOwners = 0;
            NativeArray<Entity> temps = _netOperationTemps.ToEntityArray(Allocator.Temp);
            try
            {
                if (temps.Length == 0)
                {
                    reason = "the generated net transaction was empty";
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
                        reason = "the generated net transaction became partial before commit";
                        return false;
                    }
                    members.Add(entity);
                }
                HashSet<Entity> enabledTransactionConnections =
                    CollectEnabledTransactionConnections(members);

                int structuralEntities = 0;
                int attachedObjectRoots = 0;
                int areaEntities = 0;
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
                    if (!isObject && !isNode && !isEdge && !isLane && !isAggregate && !isArea)
                    {
                        reason = "the generated net transaction contains an unknown entity shape";
                        return false;
                    }
                    if (isNode || isEdge) structuralEntities++;
                    if (isArea) areaEntities++;

                    if (!ValidateTransactionOwner(entity, members, out reason)) return false;
                    if (!ValidateOwnedBuffers(entity, members, out reason)) return false;

                    if (isObject)
                    {
                        if ((temp.m_Flags & TempFlags.Delete) == 0 &&
                            !ValidateObjectPrefabReference(entity, out reason)) return false;
                        if (!ValidateObjectOriginal(temp, out reason)) return false;
                        if (!ValidateAttachment(entity, members, out reason)) return false;
                        if (!EntityManager.HasComponent<Owner>(entity))
                        {
                            if (!ValidateNetAttachedObjectRoot(entity, temp, members, out reason))
                                return false;
                            attachedObjectRoots++;
                        }
                    }

                    if (isNode || isEdge)
                    {
                        if (!ValidateTransactionOriginal(entity, temp, isNode, isEdge,
                                enabledTransactionConnections, out reason)) return false;
                    }
                    if (isNode && !ValidateTempNode(entity, temp, out reason)) return false;
                    if (isEdge && !ValidateTempEdge(entity, temp, members,
                            enabledTransactionConnections, out reason)) return false;
                    if (isLane && !ValidateLaneOriginal(temp, out reason)) return false;
                    if (isArea && !ValidateAreaEntity(entity, temp, out reason)) return false;

                    bool missingReplacementOriginal = Infrastructure.NativeReplacementPolicy.RequiresOriginal(
                        isEdge, isLane, (temp.m_Flags & TempFlags.Delete) != 0,
                        (temp.m_Flags & TempFlags.Cancel) != 0,
                        (temp.m_Flags & TempFlags.Replace) != 0,
                        (temp.m_Flags & TempFlags.Combine) != 0);
                    if (missingReplacementOriginal && temp.m_Original == Entity.Null)
                    {
                        reason = "a generated replacement has no original entity (entity=" + entity +
                            ", shape=" + (isEdge ? "edge" : "lane") + ", flags=" + temp.m_Flags + ")";
                        return false;
                    }
                }

                if (structuralEntities == 0)
                {
                    reason = "the generated net transaction has no node/edge root";
                    return false;
                }

                reason = null;
                if (attachedObjectRoots > 0 || areaEntities > 0 || _relinkedOwners > 0)
                    SyncLog.Trace(LogTopic.Nets, "net side-effect graph validated temps=" +
                        temps.Length + " attachedRoots=" + attachedObjectRoots + " areas=" +
                        areaEntities +
                        (_relinkedOwners > 0 ? " ownersRelinked=" + _relinkedOwners : string.Empty));
                return true;
            }
            finally
            {
                temps.Dispose();
            }
        }

        /// <summary>
        /// An owner-less object must be the update copy of an object attached to a touched node or edge
        /// (e.g. a recentred roundabout island), never an unrelated preview.
        /// </summary>
        private bool ValidateNetAttachedObjectRoot(Entity entity, Temp temp,
            HashSet<Entity> members, out string reason)
        {
            reason = null;
            const TempFlags incompatible = TempFlags.Create | TempFlags.Dragging |
                TempFlags.Select | TempFlags.Modify | TempFlags.Replace | TempFlags.Upgrade |
                TempFlags.Combine | TempFlags.Cancel | TempFlags.Duplicate;
            if (temp.m_Original == Entity.Null ||
                (temp.m_Flags & TempFlags.Essential) == 0 ||
                (temp.m_Flags & incompatible) != 0)
            {
                reason = "the net transaction contains an unrelated top-level object Temp";
                return false;
            }

            Entity original = temp.m_Original;
            if (!EntityManager.HasComponent<global::Game.Objects.Attached>(entity) ||
                !EntityManager.HasComponent<global::Game.Objects.Attached>(original) ||
                !EntityManager.HasComponent<global::Game.Prefabs.PrefabRef>(entity) ||
                !EntityManager.HasComponent<global::Game.Prefabs.PrefabRef>(original))
            {
                reason = "a generated net-side object is not an attached-object update";
                return false;
            }

            global::Game.Prefabs.PrefabRef prefab =
                EntityManager.GetComponentData<global::Game.Prefabs.PrefabRef>(entity);
            global::Game.Prefabs.PrefabRef originalPrefab =
                EntityManager.GetComponentData<global::Game.Prefabs.PrefabRef>(original);
            if (prefab.m_Prefab != originalPrefab.m_Prefab)
            {
                reason = "a generated net-side object changed prefab unexpectedly";
                return false;
            }

            global::Game.Objects.Attached attached =
                EntityManager.GetComponentData<global::Game.Objects.Attached>(entity);
            global::Game.Objects.Attached originalAttached =
                EntityManager.GetComponentData<global::Game.Objects.Attached>(original);
            bool deletesWithoutParent = (temp.m_Flags & TempFlags.Delete) != 0 &&
                                         attached.m_Parent == Entity.Null;
            if ((!deletesWithoutParent &&
                 !ValidateNetAttachmentParent(attached.m_Parent, members,
                     "generated attachment parent", out reason)) ||
                !ValidateNetAttachmentParent(originalAttached.m_Parent, members,
                    "original attachment parent", out reason)) return false;

            return true;
        }

        private bool ValidateNetAttachmentParent(Entity parent, HashSet<Entity> members,
            string label, out string reason)
        {
            if (parent == Entity.Null)
            {
                reason = label + " is null";
                return false;
            }
            if (!ValidateLiveOrMemberReference(parent, members, label, out reason)) return false;
            if (!EntityManager.HasComponent<Node>(parent) && !EntityManager.HasComponent<Edge>(parent))
            {
                reason = label + " is not a network node or edge";
                return false;
            }
            return true;
        }

        private bool ValidateTransactionOwner(Entity entity, HashSet<Entity> members, out string reason)
        {
            reason = null;
            if (!EntityManager.HasComponent<Owner>(entity)) return true;

            Entity owner = EntityManager.GetComponentData<Owner>(entity).m_Owner;
            // An unset owner is normal: resolution is one-shot, so a miss is permanent. Re-link from the
            // batch's own description.
            if (owner == Entity.Null && TryRelinkGeneratedOwner(entity, members, out Entity relinked))
            {
                // No archetype change: the member arrays stay valid.
                EntityManager.SetComponentData(entity, new Owner { m_Owner = relinked });
                if (_relinkedOwners++ == 0)
                    SyncLog.Trace(LogTopic.Nets, "transaction owner re-linked " +
                        DescribeTransactionEntity(entity) + " owner=#" + relinked.Index);
                owner = relinked;
            }
            if (owner == Entity.Null || !EntityManager.Exists(owner) ||
                EntityManager.HasComponent<Deleted>(owner))
            {
                reason = "a generated net entity has a missing owner " +
                         DescribeOwnerFailure(entity, owner, members);
                return false;
            }
            if (EntityManager.HasComponent<Temp>(owner) &&
                (!members.Contains(owner) || EntityManager.HasComponent<Disabled>(owner)))
            {
                // A child may point at an isolated preview copy of an existing owner, which the apply passes patch
                // to Temp.m_Original. Any other Temp owner outside this transaction is refused.
                Temp ownerTemp = EntityManager.GetComponentData<Temp>(owner);
                Entity original = ownerTemp.m_Original;
                bool resolvesToLiveOriginal = original != Entity.Null &&
                    (ownerTemp.m_Flags & (TempFlags.Replace | TempFlags.Combine)) == 0 &&
                    EntityManager.Exists(original) &&
                    !EntityManager.HasComponent<Deleted>(original) &&
                    !EntityManager.HasComponent<Temp>(original);
                if (!resolvesToLiveOriginal)
                {
                    reason = "a generated net entity is separated from an unresolved Temp owner";
                    return false;
                }
            }
            return true;
        }
    }
}
