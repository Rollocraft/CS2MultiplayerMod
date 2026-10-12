using Game.Areas;
using Game.Prefabs;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using CS2MultiplayerMod.Core.Diagnostics;
using CS2MultiplayerMod.Core.Session;
using CS2MultiplayerMod.Game.Diagnostics;
using CS2MultiplayerMod.Game.Sync.Commands;

namespace CS2MultiplayerMod.Game.Sync.Systems
{
    public partial class AreaSyncSystem
    {
        private static float3 CentroidOf(float3[] ring)
        {
            float3 sum = float3.zero;
            for (int i = 0; i < ring.Length; i++) sum += ring[i];
            float3 center = sum / ring.Length;
            center.y = 0f;
            return center;
        }

        private static bool RingsEqual(float3[] a, float3[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
                if (math.distancesq(a[i], b[i]) > 0.01f) return false;
            return true;
        }

        private float3[] ReadRing(Entity entity)
        {
            DynamicBuffer<Node> nodes = EntityManager.GetBuffer<Node>(entity, true);
            var ring = new float3[nodes.Length];
            for (int i = 0; i < nodes.Length; i++) ring[i] = nodes[i].m_Position;
            return ring;
        }

        /// <summary>
        /// 1 Hz ring comparison: redraws do not reliably surface as Created/Deleted. A first sighting
        /// only records.
        /// </summary>
        private void ScanForEdits(MultiplayerSession session, long now)
        {
            if (now - _lastEditScanMs < EditScanIntervalMs) return;
            _lastEditScanMs = now;

            _nextRings.Clear();
            ScanForEdits(session, now, _liveAreas, ownedSpecialized: false);
            ScanForEdits(session, now, _ownedSpecializedAreas, ownedSpecialized: true);

            var swap = _knownRings;
            _knownRings = _nextRings;
            _nextRings = swap;
        }

        private void ScanForEdits(MultiplayerSession session, long now,
            EntityQuery query, bool ownedSpecialized)
        {
            NativeArray<Entity> entities = query.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < entities.Length; i++)
                {
                    Entity entity = entities[i];
                    Entity areaPrefab = EntityManager
                        .GetComponentData<PrefabRef>(entity).m_Prefab;
                    Entity ownerPrefab = Entity.Null;
                    global::Game.Objects.Transform ownerTransform =
                        default(global::Game.Objects.Transform);
                    if (ownedSpecialized &&
                        !TryGetOwnedAreaIdentity(entity, out areaPrefab,
                            out ownerPrefab, out ownerTransform)) continue;

                    float3[] ring = ReadRing(entity);
                    bool had = _knownRings.TryGetValue(entity, out float3[] old);
                    // Record seed-node lots too, or the player's first real draw reads as a first sighting.
                    _nextRings[entity] = ring;
                    if (ring.Length < 3 || !had || RingsEqual(old, ring)) continue;

                    string name = _prefabSystem.GetPrefabName(areaPrefab);
                    if (string.IsNullOrEmpty(name)) continue;
                    DynamicBuffer<Node> nodes = EntityManager.GetBuffer<Node>(entity, true);

                    if (ownedSpecialized)
                    {
                        // A specialized placement publishes its building and closed lot as one operation.
                        if (_buildSync != null && _buildSync.IsSpecializedAreaHeld(entity))
                        {
                            // Keep the old baseline so an abandoned handoff still owes this redraw.
                            _nextRings[entity] = old;
                            continue;
                        }

                        string ownerName = _prefabSystem.GetPrefabName(ownerPrefab);
                        if (string.IsNullOrEmpty(ownerName)) continue;
                        string key = OwnedAreaUpdateKey(name, ownerName,
                            ownerTransform.m_Position);
                        if (_guard.Consume(key, now)) continue;

                        var ownedCommand = new OwnedAreaSnapshotCommand
                        {
                            AreaPrefabName = name,
                            OwnerPrefabName = ownerName,
                            OwnerX = ownerTransform.m_Position.x,
                            OwnerY = ownerTransform.m_Position.y,
                            OwnerZ = ownerTransform.m_Position.z,
                            OwnerRotX = ownerTransform.m_Rotation.value.x,
                            OwnerRotY = ownerTransform.m_Rotation.value.y,
                            OwnerRotZ = ownerTransform.m_Rotation.value.z,
                            OwnerRotW = ownerTransform.m_Rotation.value.w,
                            NodeX = new float[ring.Length],
                            NodeY = new float[ring.Length],
                            NodeZ = new float[ring.Length],
                            NodeElevation = new float[ring.Length],
                        };
                        CopyNodes(nodes, ownedCommand.NodeX, ownedCommand.NodeY,
                            ownedCommand.NodeZ, ownedCommand.NodeElevation);
                        session.SendCommand(0, OwnedAreaSnapshotCommand.Id,
                            ownedCommand.Encode());
                        SyncLog.Detail(LogTopic.Land, "AreaSync captured owned redraw of '" + name +
                            "' on '" + ownerName + "' (" + ring.Length + " nodes).");
                        continue;
                    }

                    if (_guard.Consume(AreaUpdateKey(name, CentroidOf(ring)), now))
                        continue;

                    float3 oldCentroid = CentroidOf(old);
                    var command = new AreaUpdateCommand
                    {
                        PrefabName = name,
                        AnchorX = oldCentroid.x, AnchorY = oldCentroid.y, AnchorZ = oldCentroid.z,
                        NodeX = new float[ring.Length],
                        NodeY = new float[ring.Length],
                        NodeZ = new float[ring.Length],
                        NodeElevation = new float[ring.Length],
                    };
                    CopyNodes(nodes, command.NodeX, command.NodeY,
                        command.NodeZ, command.NodeElevation);
                    session.SendCommand(0, AreaUpdateCommand.Id, command.Encode());
                    SyncLog.Detail(LogTopic.Land, "AreaSync captured redraw of '" + name + "' (" +
                        ring.Length + " nodes).");
                }
            }
            finally
            {
                entities.Dispose();
            }
        }

        private static void CopyNodes(DynamicBuffer<Node> nodes,
            float[] x, float[] y, float[] z, float[] elevation)
        {
            for (int i = 0; i < nodes.Length; i++)
            {
                x[i] = nodes[i].m_Position.x;
                y[i] = nodes[i].m_Position.y;
                z[i] = nodes[i].m_Position.z;
                elevation[i] = nodes[i].m_Elevation;
            }
        }

        private void CaptureCreated(MultiplayerSession session, long now)
        {
            if (_createdAreas.IsEmptyIgnoreFilter) return;

            NativeArray<Entity> entities = _createdAreas.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < entities.Length; i++)
                {
                    Entity entity = entities[i];
                    string name = _prefabSystem.GetPrefabName(EntityManager.GetComponentData<PrefabRef>(entity).m_Prefab);
                    if (string.IsNullOrEmpty(name)) continue;

                    DynamicBuffer<Node> nodes = EntityManager.GetBuffer<Node>(entity, true);
                    if (nodes.Length < 3) continue; // not a polygon (yet)

                    if (_guard.Consume(AreaKey(name, nodes[0].m_Position), now)) continue;

                    var command = new AreaCreateCommand
                    {
                        PrefabName = name,
                        NodeX = new float[nodes.Length],
                        NodeY = new float[nodes.Length],
                        NodeZ = new float[nodes.Length],
                        NodeElevation = new float[nodes.Length],
                    };
                    for (int n = 0; n < nodes.Length; n++)
                    {
                        command.NodeX[n] = nodes[n].m_Position.x;
                        command.NodeY[n] = nodes[n].m_Position.y;
                        command.NodeZ[n] = nodes[n].m_Position.z;
                        command.NodeElevation[n] = nodes[n].m_Elevation;
                    }
                    session.SendCommand(0, AreaCreateCommand.Id, command.Encode());
                    SyncLog.Detail(LogTopic.Land, "AreaSync captured '" + name + "' (" +
                        nodes.Length + " nodes).");
                }
            }
            finally
            {
                entities.Dispose();
            }
        }

        private void CaptureDeleted(MultiplayerSession session, long now)
        {
            if (_deletedAreas.IsEmptyIgnoreFilter) return;

            NativeArray<Entity> entities = _deletedAreas.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < entities.Length; i++)
                {
                    Entity entity = entities[i];
                    string name = _prefabSystem.GetPrefabName(EntityManager.GetComponentData<PrefabRef>(entity).m_Prefab);
                    if (string.IsNullOrEmpty(name)) continue;

                    DynamicBuffer<Node> nodes = EntityManager.GetBuffer<Node>(entity, true);
                    if (nodes.Length == 0) continue;
                    if (_guard.Consume(AreaDeleteKey(name, nodes[0].m_Position), now)) continue;

                    var command = new AreaDeleteCommand
                    {
                        PrefabName = name,
                        NodeX = nodes[0].m_Position.x,
                        NodeY = nodes[0].m_Position.y,
                        NodeZ = nodes[0].m_Position.z,
                        NodeCount = nodes.Length,
                    };
                    session.SendCommand(0, AreaDeleteCommand.Id, command.Encode());
                }
            }
            finally
            {
                entities.Dispose();
            }
        }
    }
}
