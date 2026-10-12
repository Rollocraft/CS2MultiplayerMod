using System.Collections.Generic;
using CS2MultiplayerMod.Core.Diagnostics;
using CS2MultiplayerMod.Game.Diagnostics;
using CS2MultiplayerMod.Game.Sync.Infrastructure;
using Game.Common;
using Game.Net;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;

namespace CS2MultiplayerMod.Game.Sync.Systems.Net
{
    // Disabling, releasing and clearing tool Temps so a remote batch and a local tool never consume
    // each other's entities.
    public partial class NetSyncSystem
    {
        private readonly NetPreviewVisibility _isolatedLocalNetVisibility = new NetPreviewVisibility();

        private void DisableQueryEntities(EntityQuery query, List<Entity> destination)
        {
            NativeArray<Entity> entities = query.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < entities.Length; i++)
                {
                    Entity entity = entities[i];
                    if (!EntityManager.Exists(entity) || EntityManager.HasComponent<Disabled>(entity)) continue;
                    EntityManager.AddComponent<Disabled>(entity);
                    destination.Add(entity);
                    // Hidden on an original survives Disabled on its preview and hides the real road from EdgeIterator.
                    if (ReferenceEquals(destination, _isolatedLocalTemps))
                        _isolatedLocalNetVisibility.Suspend(EntityManager, entity);
                }
            }
            finally
            {
                entities.Dispose();
            }
        }

        private void ReleaseTrackedTemps(List<Entity> entities)
        {
            if (ReferenceEquals(entities, _isolatedLocalTemps))
                _isolatedLocalNetVisibility.Restore(EntityManager, entities);
            for (int i = 0; i < entities.Count; i++)
            {
                Entity entity = entities[i];
                if (EntityManager.Exists(entity) && EntityManager.HasComponent<Disabled>(entity))
                    EntityManager.RemoveComponent<Disabled>(entity);
            }
            entities.Clear();
        }

        private int ClearTrackedTemps(List<Entity> entities, bool clearPreview)
        {
            if (clearPreview && ReferenceEquals(entities, _isolatedLocalTemps))
                _isolatedLocalNetVisibility.Forget();
            int cleared = 0;
            for (int i = 0; i < entities.Count; i++)
            {
                Entity entity = entities[i];
                if (!EntityManager.Exists(entity)) continue;
                if (clearPreview && ClearTempEntity(entity)) cleared++;
                if (EntityManager.Exists(entity) && EntityManager.HasComponent<Disabled>(entity))
                    EntityManager.RemoveComponent<Disabled>(entity);
            }
            return cleared;
        }

        private bool ClearTempEntity(Entity e)
        {
            if (!EntityManager.Exists(e) || EntityManager.HasComponent<Deleted>(e) ||
                !EntityManager.HasComponent<Temp>(e)) return false;

            Temp temp = EntityManager.GetComponentData<Temp>(e);
            bool handledSubObject = false;
            Entity owner = Entity.Null;
            if (EntityManager.HasComponent<Owner>(e))
            {
                owner = EntityManager.GetComponentData<Owner>(e).m_Owner;
                handledSubObject = EntityManager.HasComponent<Lane>(e) ||
                    (EntityManager.HasComponent<global::Game.Objects.Object>(e) &&
                     !EntityManager.HasComponent<global::Game.Vehicles.Vehicle>(e) &&
                     !EntityManager.HasComponent<global::Game.Creatures.Creature>(e) &&
                     !EntityManager.HasComponent<global::Game.Buildings.Building>(e) &&
                     !EntityManager.HasComponent<global::Game.Buildings.ServiceUpgrade>(e));
            }

            // The tool-clear ownership rule: non-essential children go with their owner.
            bool deleteEntity = !handledSubObject || (temp.m_Flags & TempFlags.Essential) != 0 ||
                                owner == Entity.Null || !EntityManager.Exists(owner) ||
                                !EntityManager.HasComponent<Temp>(owner);

            if (deleteEntity && temp.m_Original != Entity.Null && EntityManager.Exists(temp.m_Original)
                && EntityManager.HasComponent<Hidden>(temp.m_Original))
            {
                EntityManager.RemoveComponent<Hidden>(temp.m_Original);
                EntityManager.AddComponent<BatchesUpdated>(temp.m_Original);
            }
            if (EntityManager.HasBuffer<AggregateElement>(e))
            {
                DynamicBuffer<AggregateElement> buffer =
                    EntityManager.GetBuffer<AggregateElement>(e, isReadOnly: true);
                var elements = new NativeArray<Entity>(
                    buffer.AsNativeArray().Reinterpret<Entity>(), Allocator.Temp);
                try
                {
                    for (int j = 0; j < elements.Length; j++)
                    {
                        if (!EntityManager.Exists(elements[j])) continue;
                        EntityManager.AddComponent<BatchesUpdated>(elements[j]);
                        if (EntityManager.HasComponent<Highlighted>(elements[j]))
                            EntityManager.RemoveComponent<Highlighted>(elements[j]);
                    }
                }
                finally
                {
                    elements.Dispose();
                }
            }
            if (deleteEntity) EntityManager.AddComponent<Deleted>(e);
            return deleteEntity;
        }

        /// <summary>
        /// Clears Temps as the game's clear pass does: restore hidden originals, drop street-name highlights,
        /// tag Deleted.
        /// </summary>
        private int ClearTempEntities(EntityQuery query)
        {
            if (query.IsEmptyIgnoreFilter) return 0;

            int cleared = 0;
            NativeArray<Entity> tempEntities = query.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < tempEntities.Length; i++)
                {
                    if (ClearTempEntity(tempEntities[i])) cleared++;
                }
            }
            finally
            {
                tempEntities.Dispose();
            }
            return cleared;
        }

        private void ProtectRemoteBatchForLocalToolOutput()
        {
            _localToolOutputProtectedThisFrame = false;
            if (!_pendingApply) return;

            global::Game.Tools.ToolBaseSystem tool = _toolSystem != null ? _toolSystem.activeTool : null;
            if (tool == null || tool.applyMode != global::Game.Tools.ApplyMode.Apply) return;

            _protectedRemoteNetTemps.Clear();
            DisableQueryEntities(ActiveTransactionQuery(), _protectedRemoteNetTemps);
            // A local Apply owns its whole preview; releasing part could split a building from its driveway.
            ReleaseTrackedTemps(_isolatedLocalTemps);
            _localToolOutputProtectedThisFrame = true;
            SyncLog.Trace(LogTopic.Nets, "net remote batch protected for local " + tool.applyMode +
                " (remote=" + _protectedRemoteNetTemps.Count + ")");
        }

        private EntityQuery ActiveTransactionQuery()
        {
            if (IsObjectGraphTransaction(_pendingTransactionKind))
                return _objectTransactionTemps;
            if (IsRouteTransaction(_pendingTransactionKind))
                return _routeTransactionTemps;
            return _netOperationTemps;
        }
    }
}
