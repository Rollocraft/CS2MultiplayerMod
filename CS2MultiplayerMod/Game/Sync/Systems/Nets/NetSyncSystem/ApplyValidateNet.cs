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
    // Validating an armed net transaction, including the objects attached to the net being built
    // and the owner every temp in the batch resolves to.
    public partial class NetSyncSystem
    {
        /// <summary>
        /// Verify the complete generated net transaction immediately before scheduling its apply.
        /// Split targets and reuse nodes were resolved a frame earlier; a concurrent local edit may
        /// have invalidated an original, endpoint, owner, or connectivity buffer in the meantime.
        /// Partial work is discarded and rebuilt rather than passed to an unchecked apply path.
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

                    bool missingReplacementOriginal =
                        isEdge && (temp.m_Flags & (TempFlags.Replace | TempFlags.Combine)) != 0 ||
                        isLane && (temp.m_Flags & TempFlags.Replace) != 0;
                    if (missingReplacementOriginal && temp.m_Original == Entity.Null)
                    {
                        reason = "a generated replacement has no original entity";
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
        /// An owner-less object in a net transaction must be the native update copy of an existing
        /// object attached to a touched node/edge. This excludes an unrelated placement preview from
        /// the net apply pass while retaining the exact path that recentres roundabout islands.
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
            // An unset owner is a normal intermediate state, not corruption. Native generation
            // leaves it unset on a sub-element whose owner is described by prefab + transform, and
            // the resolution pass a phase later fills it in by an exact transform match. That match
            // is one-shot - the description is consumed whether or not it hit - so a single miss is
            // permanent. Re-link from the description this batch still holds rather than discarding
            // a graph whose ownership the batch itself can state.
            Entity relinked;
            if (owner == Entity.Null && TryRelinkGeneratedOwner(entity, members, out relinked))
            {
                // Owner is already present, so this writes a value without changing the archetype:
                // the enclosing member array and set stay valid.
                EntityManager.SetComponentData(entity, new Owner { m_Owner = relinked });
                // One line per orphan would be hundreds on a large placement; the pass reports a
                // total, and the first member is enough to identify which graph needed repair.
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
                // Generated child entities may still point at an isolated preview copy of an
                // existing owner. The apply passes patch that reference to Temp.m_Original before
                // consuming the child. Accept exactly that resolvable form; a new/replacement Temp
                // owner outside this transaction would leave the child attached to discarded work.
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
