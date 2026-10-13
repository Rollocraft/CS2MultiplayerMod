using System.Collections.Generic;
using Colossal.Mathematics;
using Game.Net;
using Game.Objects;
using Game.Prefabs;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using CS2MultiplayerMod.Core.Diagnostics;
using CS2MultiplayerMod.Core.Session;
using CS2MultiplayerMod.Game.Diagnostics;
using CS2MultiplayerMod.Game.Sync.Commands;
using CS2MultiplayerMod.Game.Sync.Infrastructure;

namespace CS2MultiplayerMod.Game.Sync.Systems
{
    public partial class DeleteSyncSystem
    {
        private void CaptureDeletedObjects(MultiplayerSession session, long now)
        {
            // A native object transaction already carries its deletes; the receiver regenerates them.
            BuildSyncSystem buildSync = World.GetExistingSystemManaged<BuildSyncSystem>();
            if ((buildSync != null && ObjectBrushCapture.SuppressDeletes(
                    buildSync.NativeLifecycleCapturedThisFrame,
                    buildSync.LocalObjectLifecycleAppliedThisFrame,
                    buildSync.LocalObjectBrushAppliedThisFrame)) ||
                (_netSync != null && _netSync.DidCommitObjectGraphThisFrame)) return;

            CollectToolDeleteOriginals();
            SendObjectDeletes(session, now, _deletedObjects, ownedUpgrades: false);
            // A single upgrade removed from the properties panel: the root query above excludes Owner.
            SendObjectDeletes(session, now, _deletedOwnedUpgrades, ownedUpgrades: true);
        }

        private void SendObjectDeletes(MultiplayerSession session, long now, EntityQuery query,
            bool ownedUpgrades)
        {
            if (query.IsEmptyIgnoreFilter) return;
            if (!ownedUpgrades && TrySendObjectBrushDeletes(session, now, query)) return;

            NativeArray<Entity> entities = query.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < entities.Length; i++)
                {
                    Entity entity = entities[i];
                    Entity prefab = EntityManager.GetComponentData<PrefabRef>(entity).m_Prefab;
                    string name = _prefabSystem.GetPrefabName(prefab);
                    if (string.IsNullOrEmpty(name)) continue;

                    // Each city grows and retires its own growables, so a simulation removal cannot match on the
                    // peer. A player's bulldoze travels, unless growables are not synced this session.
                    bool playerRemoved = _toolDeleteOriginals.Contains(entity) &&
                                         Mod.Service != null && Mod.Service.SimulationSyncEnabled;
                    if (!ownedUpgrades && IsSimulationOwnedLifecycle(prefab) && !playerRemoved)
                    {
                        SyncLog.Trace(LogTopic.Buildings,
                            "DeleteSync: not replicating simulation-owned removal of '" + name +
                            "'.");
                        continue;
                    }

                    if (ownedUpgrades && !IsStandaloneUpgradeRemoval(entity, prefab)) continue;

                    float3 pos = EntityManager.GetComponentData<Transform>(entity).m_Position;
                    if (_guard.Consume(DeleteKey(name, pos), now)) continue;

                    var command = new ObjectDeleteCommand
                    {
                        PrefabName = name,
                        PosX = pos.x, PosY = pos.y, PosZ = pos.z,
                    };
                    session.SendCommand(0, ObjectDeleteCommand.Id, command.Encode());
                }
            }
            finally
            {
                entities.Dispose();
            }
        }

        /// <summary>
        /// Entities a tool removes this frame: at ModificationEnd the victim is Deleted while its
        /// <see cref="Temp"/> still stands.
        /// </summary>
        private void CollectToolDeleteOriginals()
        {
            _toolDeleteOriginals.Clear();
            if (_toolDeleteTemps.IsEmptyIgnoreFilter) return;

            NativeArray<Entity> temps = _toolDeleteTemps.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < temps.Length; i++)
                {
                    Temp temp = EntityManager.GetComponentData<Temp>(temps[i]);
                    if ((temp.m_Flags & TempFlags.Delete) == 0) continue;
                    if (temp.m_Original != Entity.Null) _toolDeleteOriginals.Add(temp.m_Original);
                }
            }
            finally
            {
                temps.Dispose();
            }
        }

        /// <summary>Simulation-owned lifecycle; mirrors BuildSync's placement rule.</summary>
        private bool IsSimulationOwnedLifecycle(Entity prefab)
        {
            if (prefab == Entity.Null || !EntityManager.Exists(prefab)) return true;
            if (EntityManager.HasComponent<MovingObjectData>(prefab)) return true;
            return EntityManager.HasComponent<SpawnableBuildingData>(prefab) &&
                   !EntityManager.HasComponent<SignatureBuildingData>(prefab);
        }

        /// <summary>
        /// An owned upgrade removed on its own, not with its host (a host delete already carries its
        /// graph). <see cref="ServiceUpgradeData"/> keeps simulation lot content off the wire.
        /// </summary>
        private bool IsStandaloneUpgradeRemoval(Entity entity, Entity prefab)
        {
            if (!EntityManager.HasComponent<ServiceUpgradeData>(prefab)) return false;
            Entity owner = EntityManager
                .GetComponentData<global::Game.Common.Owner>(entity).m_Owner;
            return owner != Entity.Null && EntityManager.Exists(owner) &&
                   !EntityManager.HasComponent<global::Game.Common.Deleted>(owner) &&
                   !EntityManager.HasComponent<global::Game.Tools.Temp>(owner);
        }

        private void CaptureDeletedEdges(MultiplayerSession session, long now)
        {
            // Stamps, upgrades and relocations clear their own networks natively on the receiver; a
            // follow-up edge delete would tear down the freshly committed graph.
            BuildSyncSystem buildSync = World.GetExistingSystemManaged<BuildSyncSystem>();
            if ((buildSync != null && (buildSync.NativeLifecycleCapturedThisFrame ||
                                       buildSync.LocalObjectLifecycleAppliedThisFrame)) ||
                (_netSync != null && _netSync.DidCommitObjectGraphThisFrame)) return;
            if (_deletedEdges.IsEmptyIgnoreFilter) return;

            // A mid-span split deletes the original and creates two covering halves; the receiver splits
            // locally, so that delete stays local. A height mismatch (rebuild) or coverage gap (consumed
            // span) is not a split and is sent.
            NativeArray<Entity> createdEnts = _createdEdges.ToEntityArray(Allocator.Temp);
            NativeArray<Curve> createdCurves = _createdEdges.ToComponentDataArray<Curve>(Allocator.Temp);
            var createdPrefabs = new NativeArray<Entity>(createdEnts.Length, Allocator.Temp);
            for (int i = 0; i < createdEnts.Length; i++)
                createdPrefabs[i] = EntityManager.GetComponentData<PrefabRef>(createdEnts[i]).m_Prefab;

            // Node reduction: a freed node merges two collinear edges, one Updated over the other's span and
            // the other Deleted. The receiver's own commit reproduces it, so the victim's delete stays local.
            NativeArray<Entity> updatedEnts = _updatedEdges.ToEntityArray(Allocator.Temp);
            NativeArray<Curve> updatedCurves = _updatedEdges.ToComponentDataArray<Curve>(Allocator.Temp);
            var updatedPrefabs = new NativeArray<Entity>(updatedEnts.Length, Allocator.Temp);
            for (int i = 0; i < updatedEnts.Length; i++)
                updatedPrefabs[i] = EntityManager.GetComponentData<PrefabRef>(updatedEnts[i]).m_Prefab;

            NativeArray<Entity> entities = _deletedEdges.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < entities.Length; i++)
                {
                    Entity prefab = EntityManager.GetComponentData<PrefabRef>(entities[i]).m_Prefab;
                    string name = _prefabSystem.GetPrefabName(prefab);
                    if (string.IsNullOrEmpty(name) || name.StartsWith("Invisible")) continue;

                    Bezier4x3 b = EntityManager.GetComponentData<Curve>(entities[i]).m_Bezier;

                    // Already represented by the committing transaction that named it as an original.
                    if (_netSync != null && _netSync.ConsumeCommittedNetSideEffect(entities[i], now))
                    {
                        continue;
                    }

                    // Node-reduction victim: the receiver's own commit reproduces the merge.
                    if (IsReductionVictim(b, prefab, updatedPrefabs, updatedCurves))
                    {
                        continue;
                    }

                    // A split, not a bulldoze — let the receiver split its own copy locally.
                    if (IsBeingSplit(b, prefab, createdPrefabs, createdCurves))
                    {
                        continue;
                    }

                    if (_guard.Consume(DeleteKey(name, b.a), now))
                    {
                        continue;
                    }

                    var command = new NetDeleteCommand
                    {
                        PrefabName = name,
                        Ax = b.a.x, Ay = b.a.y, Az = b.a.z,
                        Bx = b.b.x, By = b.b.y, Bz = b.b.z,
                        Cx = b.c.x, Cy = b.c.y, Cz = b.c.z,
                        Dx = b.d.x, Dy = b.d.y, Dz = b.d.z,
                    };
                    session.SendCommand(0, NetDeleteCommand.Id, command.Encode());
                }
            }
            finally
            {
                entities.Dispose();
                createdEnts.Dispose();
                createdCurves.Dispose();
                createdPrefabs.Dispose();
                updatedEnts.Dispose();
                updatedCurves.Dispose();
                updatedPrefabs.Dispose();
            }
        }

        /// <summary>A same-prefab Updated edge now covers <paramref name="deleted"/>'s 3D span.</summary>
        private static bool IsReductionVictim(Bezier4x3 deleted, Entity prefab,
            NativeArray<Entity> updatedPrefabs, NativeArray<Curve> updatedCurves)
        {
            for (int i = 0; i < updatedCurves.Length; i++)
            {
                if (updatedPrefabs[i] != prefab) continue;
                if (SplitMatch.IsSubCurve3D(deleted, updatedCurves[i].m_Bezier)) return true;
            }
            return false;
        }

        /// <summary>
        /// Same-prefab Created edges match <paramref name="deleted"/> in XZ and height and cover its whole
        /// span. A height mismatch or coverage gap means the delete must replicate.
        /// </summary>
        private static bool IsBeingSplit(Bezier4x3 deleted, Entity prefab,
            NativeArray<Entity> createdPrefabs, NativeArray<Curve> createdCurves)
        {
            List<Bezier4x3> pieces = null;
            for (int i = 0; i < createdCurves.Length; i++)
            {
                if (createdPrefabs[i] != prefab) continue;
                Bezier4x3 c = createdCurves[i].m_Bezier;
                if (!SplitMatch.FollowsXZ(c, deleted)) continue;
                if (!SplitMatch.HeightMatches(c, deleted)) return false; // rebuilt at a new height
                (pieces ?? (pieces = new List<Bezier4x3>())).Add(c);
            }
            return SplitMatch.CoverWholeSpan(pieces, deleted);
        }
    }
}
