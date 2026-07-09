using System.Collections.Generic;
using Colossal.Mathematics;
using Game.Net;
using Game.Objects;
using Game.Prefabs;
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
            if (_deletedObjects.IsEmptyIgnoreFilter) return;

            NativeArray<Entity> entities = _deletedObjects.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < entities.Length; i++)
                {
                    Entity prefab = EntityManager.GetComponentData<PrefabRef>(entities[i]).m_Prefab;
                    string name = _prefabSystem.GetPrefabName(prefab);
                    if (string.IsNullOrEmpty(name)) continue;

                    float3 pos = EntityManager.GetComponentData<Transform>(entities[i]).m_Position;
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

        private void CaptureDeletedEdges(MultiplayerSession session, long now)
        {
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
