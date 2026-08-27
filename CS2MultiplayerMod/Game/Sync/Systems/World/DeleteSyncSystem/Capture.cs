using System.Collections.Generic;
using Colossal.Mathematics;
using Game.Net;
using Game.Objects;
using Game.Prefabs;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using CS2MultiplayerMod.Core.Session;
using CS2MultiplayerMod.Game.Sync.Commands;
using CS2MultiplayerMod.Game.Sync.Infrastructure;

namespace CS2MultiplayerMod.Game.Sync.Systems
{
    public partial class DeleteSyncSystem
    {
        private void CaptureDeletedObjects(MultiplayerSession session, long now)
        {
            // A native object-tool transaction already contains every explicit delete in the
            // object/sub-net/area graph, and the receiver's generator reproduces its implicit
            // clear/split side effects. Do not turn that transaction output into a second command.
            BuildSyncSystem buildSync = World.GetExistingSystemManaged<BuildSyncSystem>();
            if ((buildSync != null && (buildSync.NativeLifecycleCapturedThisFrame ||
                                       buildSync.LocalObjectLifecycleAppliedThisFrame)) ||
                (_netSync != null && _netSync.DidCommitObjectGraphThisFrame)) return;

            CollectToolDeleteOriginals();
            SendObjectDeletes(session, now, _deletedObjects, ownedUpgrades: false);
            // Removing a single upgrade is not a bulldoze: the building's properties panel tags that
            // one owned entity Deleted. The query above excludes Owner (a root delete already carries
            // its owned graph), so a standalone upgrade removal was never captured at all.
            SendObjectDeletes(session, now, _deletedOwnedUpgrades, ownedUpgrades: true);
        }

        private void SendObjectDeletes(MultiplayerSession session, long now, EntityQuery query,
            bool ownedUpgrades)
        {
            if (query.IsEmptyIgnoreFilter) return;

            NativeArray<Entity> entities = query.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < entities.Length; i++)
                {
                    Entity entity = entities[i];
                    Entity prefab = EntityManager.GetComponentData<PrefabRef>(entity).m_Prefab;
                    string name = _prefabSystem.GetPrefabName(prefab);
                    if (string.IsNullOrEmpty(name)) continue;

                    // Each city grows and retires its own growables; BuildSync refuses to place
                    // them for the same reason, and a world resync is what reconciles the two.
                    // Sending these produced a delete the peer could never match (its lot holds a
                    // different building, or none), and when one did match it tore down a building
                    // the peer's own simulation considered healthy.
                    if (!ownedUpgrades && IsSimulationOwnedLifecycle(prefab) &&
                        !_toolDeleteOriginals.Contains(entity))
                    {
                        Mod.Verbose("[MP] DeleteSync: not replicating simulation-owned removal of '" +
                                    name + "'.");
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
        /// Records the entities a tool is removing this frame. Read at ModificationEnd, where the
        /// apply pass has already tagged the victim <see cref="global::Game.Common.Deleted"/> while
        /// its <see cref="Temp"/> is still standing (cleanup runs later).
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

        /// <summary>
        /// True for objects the simulation creates and retires on its own. Mirrors
        /// BuildSyncSystem's placement rule so creation and removal stay symmetric: neither
        /// direction of a growable's lifecycle travels on the wire.
        /// </summary>
        private bool IsSimulationOwnedLifecycle(Entity prefab)
        {
            if (prefab == Entity.Null || !EntityManager.Exists(prefab)) return true;
            if (EntityManager.HasComponent<MovingObjectData>(prefab)) return true;
            return EntityManager.HasComponent<SpawnableBuildingData>(prefab) &&
                   !EntityManager.HasComponent<SignatureBuildingData>(prefab);
        }

        /// <summary>
        /// True when this owned upgrade is being removed on its own, rather than disappearing with its
        /// host. A host delete already replicates as one root command whose realization walks the
        /// owned graph, so re-sending the children would fight that. Requiring
        /// <see cref="ServiceUpgradeData"/> also keeps simulation-owned lot content (a storage yard's
        /// container piles, which despawn constantly) off the wire.
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
            // Asset-stamp intersections and other object prefabs apply their whole owned network in
            // one native graph. A follow-up edge delete can otherwise tear down the freshly connected
            // receiver graph after its atomic commit. The same holds for an upgrade or relocation
            // whose footprint clears the host building's existing driveways: the receiver derives
            // those removals from the same action.
            BuildSyncSystem buildSync = World.GetExistingSystemManaged<BuildSyncSystem>();
            if ((buildSync != null && (buildSync.NativeLifecycleCapturedThisFrame ||
                                       buildSync.LocalObjectLifecycleAppliedThisFrame)) ||
                (_netSync != null && _netSync.DidCommitObjectGraphThisFrame)) return;
            if (_deletedEdges.IsEmptyIgnoreFilter) return;

            // Snapshot this frame's Created edges so we can distinguish a mid-span SPLIT from a real
            // bulldoze. A split deletes the original edge and creates two halves on its centreline;
            // replicating that delete would tear down the receiver's still-whole edge before its own
            // local split runs, leaving the new road disconnected ("not accessible"). So below we skip
            // deleting an edge whose same-prefab Created halves lie on its centreline in 3D AND cover
            // its whole span — the receiver reproduces the split locally from the drawn-edge command.
            // Height-mismatching pieces (span REBUILT at another elevation) or a coverage gap (part
            // of the span CONSUMED, e.g. by a roundabout placed on top) are no split: that delete IS
            // sent, and NetSyncSystem sends the kept pieces one frame behind it.
            NativeArray<Entity> createdEnts = _createdEdges.ToEntityArray(Allocator.Temp);
            NativeArray<Curve> createdCurves = _createdEdges.ToComponentDataArray<Curve>(Allocator.Temp);
            var createdPrefabs = new NativeArray<Entity>(createdEnts.Length, Allocator.Temp);
            for (int i = 0; i < createdEnts.Length; i++)
                createdPrefabs[i] = EntityManager.GetComponentData<PrefabRef>(createdEnts[i]).m_Prefab;

            // This frame's geometry-changed survivors, for the node-reduction test below. When a
            // bulldoze frees a node between two collinear same-prefab edges, the game merges them:
            // one neighbour is committed with the JOINED curve (Updated, covers the other's span),
            // the other is Deleted. Replicating that victim's delete would rip half the through-road
            // out of a receiver whose own reduction hasn't run yet (its own commit of the bulldoze
            // reproduces the merge natively) — the "street half-deleted / stub left behind" bug.
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

                    // A committing Temp transaction named this exact edge as an original immediately
                    // before Apply. Its deletion is already represented by that placement/delete/
                    // replace command, so it must not become a second bulldozer command. Geometry
                    // matching below remains the fallback for uncaptured and simulation-driven work.
                    if (_netSync != null && _netSync.ConsumeCommittedNetSideEffect(entities[i], now))
                    {
                        continue;
                    }

                    // A node-reduction victim, not a bulldoze — a same-prefab neighbour was extended
                    // over this edge's span this same frame. The receiver's own commit reproduces the
                    // merge, so this delete stays local.
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

        /// <summary>
        /// True when <paramref name="deleted"/> died to node reduction: same-prefab Updated edge
        /// now covers its 3D span (game joined two edges, this is leftover).
        /// </summary>
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
        /// True when <paramref name="deleted"/> is being split (not bulldozed/rebuilt/consumed):
        /// same-prefab Created edges match XZ and height AND jointly cover the whole span. A height
        /// mismatch (rebuild at new elevation) or a coverage gap (span partially consumed, e.g. by
        /// a roundabout placed on top) means the delete must replicate.
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
